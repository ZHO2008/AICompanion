using System.IO;
using System.Windows.Threading;
using AICompanion.App.Animation;
using AICompanion.Core.Abstractions;
using AICompanion.Core.Asr;
using AICompanion.Core.Audio;
using AICompanion.Core.Configuration;
using AICompanion.Core.Context;
using AICompanion.Core.Gpu;
using AICompanion.Core.Health;
using AICompanion.Core.Interop;
using AICompanion.Core.Knowledge;
using AICompanion.Core.Llm;
using AICompanion.Core.Logging;
using AICompanion.Core.Monitor;
using AICompanion.Core.Pipeline;
using AICompanion.Core.Refine;
using AICompanion.Core.Scheduling;
using AICompanion.Core.Tts;

namespace AICompanion.App.Services;

/// <summary>
/// 组合根：按手册 §1.6 的启动顺序与 §6 的依赖校验把整个系统接起来。
///
/// 启动顺序（由 <see cref="StartAsync"/> 保证）：
///   1. 加载/生成 config.yaml（缺失即生成并提示）
///   2. 建日志
///   3. 知识库（加载 + 热重载 + 清理过期候选）
///   4. 计算 -ngl 并**由主程序拉起 llama-server**，轮询 /health（决策 Q5）
///   5. TTS 引擎初始化（含 Chatterbox 不可用时的降级）
///   6. ASR 初始化（模型缺失则置 Faulted 并提示改用文字输入）
///   7. HealthMonitor 立即全量探活（手册 §1.6 步骤 7 的二次确认）
///   8. GameMonitor 通信服务 + 游戏模式协调器
///   9. 空闲任务调度器
///  10. 对话管线
///
/// 注意：手册 §1.6 让 start_all.bat 启动 llama-server，而决策 Q5 改为由主程序拥有其生命周期。
/// 因此 bat 只做残留清理与启动 GameMonitor/主程序，见 start_all.bat 中的注释。
/// </summary>
public sealed class AppHost : IAsyncDisposable
{
    private readonly BubbleNotifier _bubbles;
    private readonly List<string> _startupProblems = new();

    /// <summary>
    /// 启动期旁路任务（待审核提醒）的取消与句柄。
    ///
    /// 它们捕获了 this：若在 <see cref="DisposeAsync"/> 关掉日志 sink 之后才跑完，
    /// 往里写一行日志就会把旧目录的日志文件重新打开（日志分流 + 多一个句柄）。
    /// 因此 DisposeAsync 必须先 Cancel + 限时等它结束，再关日志。
    /// </summary>
    private readonly CancellationTokenSource _backgroundCts = new();
    private Task? _pendingNotifyTask;

    private FileLogger? _rootLogger;

    /// <summary>
    /// config.yaml 里 live2d.default_model_path 的**原值**。
    ///
    /// 界面把 Live2D 关掉时会把内存里的 DefaultModelPath 清空（与启动时的折算一致），
    /// 之后用户在设置面板重新打开 Live2D，要靠它把路径恢复回去 —— 否则只能重启。
    /// 只记在内存，config.yaml 一个字都不改（准则 9）。
    /// </summary>
    private string _configuredLive2dModelPath = string.Empty;

    /// <param name="dispatcher">
    /// UI 线程调度器。AppHost 自身不做封送（需要 UI 线程的组件在各自内部处理，
    /// 例如 BubbleNotifier），因此这里**只保留参数、不再存字段**：
    /// 原来的 _dispatcher 字段只赋值、全仓库零读取。
    /// </param>
    /// <param name="sharedSink">
    /// 由调用方（App.OnStartup）预先创建并共享的日志 sink。
    ///
    /// 为什么必须共享：BubbleNotifier 需要日志器，而它在 AppHost 之前就构造。
    /// 如果 App 自己建一个 sink、AppHost 又建一个指向同一文件，
    /// 同一个进程里就有两个 StreamWriter 写同一个文件 —— 两者各自缓冲、
    /// 刷盘时机不同，实测会把两行日志交错成一行（时间戳与分类标签互相插入，
    /// 甚至把一个多字节中文字符从中间切开）。
    /// 传 null 时退回到"自己建一个"，便于测试或单独使用。
    /// </param>
    public AppHost(Dispatcher dispatcher, BubbleNotifier bubbles, LogSink? sharedSink = null)
    {
        _ = dispatcher;   // 保留参数只为调用方签名稳定：AppHost 不直接往 UI 线程封送。
        _bubbles = bubbles;

        /*
         * 构造阶段就必须提供一个可用的日志器。
         *
         * 原因：MainWindow 在 AppHost.StartAsync() 之前就被构造，其 LoadAvatar()
         * 会调用 _host.Logger。如果 Logger 到 StartAsync 才赋值，就会在启动最早期
         * 抛 NullReferenceException —— 而且因为 Logger 是 null，连异常都记不下来，
         * 只在气泡里留一句"启动失败"，无法排查。
         * 因此这里先用工作目录下的默认日志目录建一个引导日志器，
         * StartAsync 读到配置后再换成配置指定的目录。
         */
        BootstrapSink = sharedSink ?? new LogSink(LogRoot.AppLogDirectory());
        Logger = new FileLogger(BootstrapSink, "Bootstrap");
    }

    /// <summary>引导日志（配置加载前的日志都写这里）。</summary>
    public LogSink BootstrapSink { get; }

    /// <summary>是否已完成 <see cref="StartAsync"/>。UI 在启动过程中据此避免访问尚未构造的组件。</summary>
    public bool IsStarted { get; private set; }

    public AppConfig Config { get; private set; } = new();

    public IAppLogger Logger { get; private set; } = null!;

    public LogSink? LogSink { get; private set; }

    public UnifiedKnowledgeManager Knowledge { get; private set; } = null!;

    public RuntimeModeState RuntimeMode { get; private set; } = null!;

    public GPUMemoryManager Gpu { get; private set; } = null!;

    public NvidiaSmi NvidiaSmi { get; private set; } = null!;

    public LlamaServerController Llama { get; private set; } = null!;

    public TTSManager Tts { get; private set; } = null!;

    public ASRManager Asr { get; private set; } = null!;

    /// <summary>按住说话按键监听（PTT 模式才有）。</summary>
    private PushToTalkMonitor? _pushToTalk;

    public ContextManager Context { get; private set; } = null!;

    public ConversationRefiner Refiner { get; private set; } = null!;

    public IdleTaskScheduler Idle { get; private set; } = null!;

    public HealthMonitor Health { get; private set; } = null!;

    public MonitorServer Monitor { get; private set; } = null!;

    public GameModeCoordinator GameMode { get; private set; } = null!;

    public GameCooldownTracker Cooldown { get; private set; } = null!;

    public ConversationPipeline Pipeline { get; private set; } = null!;

    public IAudioPlayer Audio { get; private set; } = null!;

    /// <summary>带播放事件的播放器（动作调度与口型同步订阅它）。</summary>
    public ObservableAudioPlayer ObservableAudio { get; private set; } = null!;

    /// <summary>
    /// 设置面板改完外观项后的"立即应用"通知。MainWindow 在构造时订阅、关闭时退订。
    ///
    /// 为什么放在 AppHost 而不是让设置面板直接持有 MainWindow：
    ///   - 设置面板不必知道窗口类型（托盘里打开面板时也一样）；
    ///   - 主窗口已关闭 / 还没就绪时这里没有订阅者，天然就降级为"下次启动生效"。
    /// </summary>
    public event EventHandler<AppearanceChange>? AppearanceChangeRequested;

    /// <summary>是否有主窗口在监听外观变更（设置面板据此如实标注"已生效"还是"下次启动生效"）。</summary>
    public bool HasAppearanceTarget => AppearanceChangeRequested is not null;

    /// <summary>
    /// 请求把外观设置立即应用到已打开的桌宠窗口。
    /// 没有订阅者（主窗口未就绪或已关闭）时只记 Debug，绝不抛出 —— 改设置不该让程序崩。
    /// </summary>
    public void RequestAppearanceChange(AppearanceChange change)
    {
        var handler = AppearanceChangeRequested;

        if (handler is null)
        {
            Logger.Debug($"外观设置变更（{change}）没有可用的桌宠窗口接收，降级为下次启动生效。");
            return;
        }

        try
        {
            handler(this, change);
        }
        catch (Exception ex)
        {
            // 订阅者（主窗口）已经各自吞异常，这里再兜一层，保证设置面板的调用栈干净。
            Logger.Warn($"应用外观设置变更失败（{change}，不影响运行，下次启动仍会生效）：{ex.Message}");
        }
    }

    /// <summary>启动过程中发现的问题（用于启动后一次性提示与设置面板展示）。</summary>
    public IReadOnlyList<string> StartupProblems => _startupProblems;

    /// <summary>llama-server 是否成功启动。</summary>
    public bool LlmReady { get; private set; }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        // ---------- 1. 配置：缺失则从嵌入资源生成 ----------
        var load = ConfigLoader.Load(explicitPath: null, Logger);
        Config = load.Config;

        // ---------- 2. 真正的日志器（目录来自配置）----------
        var level = ConfigLoader.ParseLogLevel(Config.Logging.MinimumLevel);
        var appLogDirectory = AppPaths.Resolve(Config.Logging.AppLogDir);

        if (string.Equals(
                Path.GetFullPath(appLogDirectory).TrimEnd(Path.DirectorySeparatorChar),
                Path.GetFullPath(BootstrapSink.DirectoryPath).TrimEnd(Path.DirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase))
        {
            /*
             * 配置目录与引导目录相同（默认就是同一个），此时**复用**引导 sink。
             *
             * 不能在这里新建第二个 sink 指向同一个文件：两个 StreamWriter 各自缓冲，
             * 刷盘时机不同，实测会出现两行日志交错成一行（时间戳与分类标签互相插入）。
             * 复用同一个 sink 既避免交错，也避免重复打开同一个文件。
             */
            LogSink = BootstrapSink;
            _rootLogger = new FileLogger(LogSink, "App");
            Logger = _rootLogger;

            // 气泡的 notifier 在引导期就建好了，它的日志器仍指着引导 sink。
            // 统一改绑到真正的根日志器：否则切换目录后每条气泡日志都会把旧目录的文件重新打开。
            _bubbles.SetLogger(Logger);

            Logger.Info($"沿用引导日志目录：{LogSink.DirectoryPath}");
        }
        else
        {
            LogSink = new LogSink(appLogDirectory, level, Config.Logging.MaxLogDays);
            _rootLogger = new FileLogger(LogSink, "App");
            Logger = _rootLogger;

            // 同上：先改绑气泡日志，再关闭引导句柄，避免"改绑前写一条、落到旧目录"。
            _bubbles.SetLogger(Logger);

            // 目录不同：先关掉引导句柄再用新的，避免两个句柄同时存在。
            BootstrapSink.Close();
            Logger.Info($"日志已切换到配置目录：{LogSink.DirectoryPath}");
        }

        LogSink.CleanupOldFiles();
        Logger.Info("================ AI 伴侣启动 ================");
        Logger.Info($"配置：{Config.ConfigFilePath}");
        Logger.Info($"运行目录：{AppPaths.Root}");

        if (load.ConfigFileGenerated)
        {
            _bubbles.Show(
                "config-generated",
                "已生成默认配置文件，请按需修改后重启。",
                BubbleLevel.Warning,
                TimeSpan.FromSeconds(12));
        }

        if (load.ParseError is not null)
        {
            _startupProblems.Add($"config.yaml 解析失败（{load.ParseError}），已回退到默认配置。");
            _bubbles.Show(
                "config-parse-error",
                $"config.yaml 解析失败，已使用默认配置运行。请检查文件格式。（{load.ParseError}）",
                BubbleLevel.Error,
                TimeSpan.FromSeconds(15));
        }

        foreach (var warning in load.Warnings)
        {
            _startupProblems.Add(warning);
        }

        if (load.Warnings.Count > 0)
        {
            _bubbles.Show(
                "config-fallback",
                $"有 {load.Warnings.Count} 项配置缺失或非法，已使用默认值。详见设置面板与日志。",
                BubbleLevel.Info,
                TimeSpan.FromSeconds(10));
        }

        // ---------- 3. 核心组件（构造顺序按依赖关系） ----------
        ApplyTtsEnginePreference();
        ApplyAiTogglePreference();
        ApplySettingsPanelPreferences();

        // 「拓展」页启用的 tts / asr 模块接进运行时：必须赶在 TTSManager / ASR 构造之前，
        // 因为它们会在构造时读取 tts.engine、tts.piper.*、asr.model_path 等配置。
        ApplyModuleRuntimePreferences();

        // 对话后端的兜底发现：配置里的 llama-server.exe 不存在时（旧模板的 bin\ 路径、
        // 用户改坏、只留了 cuda 模块……），按 vulkan → cuda → 其他 自动挑一个能用的。
        // 必须在 LlamaServerController 构造与 StartAsync 之前做，日志顺序才如实。
        ApplyLlmServerDiscovery();

        NvidiaSmi = new NvidiaSmi(Logger);
        RuntimeMode = new RuntimeModeState();

        Knowledge = new UnifiedKnowledgeManager(Config, Logger, _bubbles);
        await Knowledge.StartAsync(cancellationToken).ConfigureAwait(false);

        Gpu = new GPUMemoryManager(Config, NvidiaSmi, RuntimeMode, Logger, _bubbles);
        Llama = new LlamaServerController(Config, Gpu, RuntimeMode, Logger, _bubbles);

        Tts = new TTSManager(Config, Logger, _bubbles);

        // 用装饰器包一层，把"开始/结束播放"暴露成事件：动作调度与口型同步都靠它触发。
        // 核心层的 IAudioPlayer 接口保持不变（它不该知道 VPet/Live2D 的细节）。
        var innerPlayer = new WaveOutPlayer(Logger);
        ObservableAudio = new ObservableAudioPlayer(innerPlayer, Logger);
        Audio = ObservableAudio;

        Context = new ContextManager(Config, Logger, _bubbles, Knowledge, Llama);
        Refiner = new ConversationRefiner(Config, Logger, _bubbles, Llama, NvidiaSmi, Knowledge.Pending);
        Idle = new IdleTaskScheduler(Config, Logger, _bubbles, Context, Refiner, Tts);

        Pipeline = new ConversationPipeline(Config, Logger, _bubbles, Knowledge, Context, Llama, Tts, Audio, Idle);

        // ---------- 4. LLM：算 ngl 并拉起 llama-server ----------
        var startResult = await Llama.StartAsync(cancellationToken).ConfigureAwait(false);
        LlmReady = startResult.Success;

        if (startResult.Outcome == LlamaStartOutcome.Disabled)
        {
            // 用户主动关掉对话：既不算启动问题，也不弹红色告警 —— 这是他要的。
            Logger.Info("对话功能已关闭（llm.enabled=false），跳过 LLM 健康检查告警。");
        }
        else if (!LlmReady)
        {
            var fatal = startResult.Outcome == LlamaStartOutcome.ModelFileMissing;
            _startupProblems.Add(startResult.Message);

            // 手册 §6：致命缺失必须阻断启动并清晰提示。
            // 这里"阻断"的是 LLM 服务本身，而保留 UI 运行 ——
            // 这样用户能打开设置面板查看原因并修正路径，而不是面对一个直接退出的程序。
            _bubbles.Show(
                fatal ? "llm-model-missing" : "llm-offline",
                fatal
                    ? "LLM 模型文件缺失或路径错误，对话功能不可用。请在设置面板检查 llm.model_path。"
                    : $"LLM 服务未就绪：{startResult.Message}",
                BubbleLevel.Error);
        }

        // ---------- 5. TTS ----------
        await Tts.InitializeAsync(cancellationToken).ConfigureAwait(false);

        // ---------- 6. ASR ----------
        /*
         * 关掉语音输入时**不能**再走工厂：WhisperCppRecognizer 在构造时就
         * 把 140MB 的模型读进内存并初始化引擎（日志里那行"whisper 模型已加载"就是它）。
         * 用户既然关了语音输入，就不该还占这些资源和初始化时间。
         */
        var recognizer = Config.Asr.Enabled
            ? SpeechRecognizerFactory.Create(Config, Logger)
            : new UnavailableSpeechRecognizer("语音输入已关闭（asr.enabled=false）。");
        var capture = AudioCaptureFactory.Create(Config.Asr.Capture, Logger);
        Asr = new ASRManager(Config, Logger, _bubbles, recognizer, capture);
        await Asr.InitializeAsync(cancellationToken).ConfigureAwait(false);

        /*
         * 两种工作模式互斥：
         *   - 按住说话（push_to_talk）：只在按住按键时录音，松开后整段识别。
         *     不会进入常开监听，因此不采环境音、不误收旁人对话、也不会和 TTS 回声打架。
         *   - 常开监听（auto_start）：老的 VAD 分段模式，靠静音切句。
         */
        if (!Config.Asr.Enabled)
        {
            // 语音输入被关掉：连热键监听都不装，避免占用全局快捷键却什么也不做。
            Logger.Info("语音输入已关闭（asr.enabled=false），跳过热键监听与常开监听。");
        }
        else if (Config.Asr.PushToTalk)
        {
            try
            {
                _pushToTalk = new PushToTalkMonitor(
                    Config.Asr.PushToTalkKey,
                    Logger,
                    onPress: Asr.BeginPushToTalk,
                    onRelease: Asr.EndPushToTalk,
                    dispatcher: System.Windows.Application.Current?.Dispatcher);

                _pushToTalk.Start();
                Logger.Info($"ASR 已按配置进入按住说话模式（按键 {Config.Asr.PushToTalkKey}，采集源={Config.Asr.Capture}）。");
            }
            catch (Exception ex)
            {
                _pushToTalk?.Dispose();
                _pushToTalk = null;
                Logger.Warn($"按住说话启动失败，回退到常开监听：{ex.Message}");

                if (Config.Asr.AutoStart)
                {
                    try
                    {
                        Asr.StartListening();
                        Logger.Info($"ASR 回退为常开监听，状态={Asr.State}。");
                    }
                    catch (Exception inner)
                    {
                        Logger.Warn($"ASR 自动启动失败（不影响其他功能）：{inner.Message}");
                    }
                }
            }
        }
        else if (Config.Asr.AutoStart)
        {
            try
            {
                Asr.StartListening();
                Logger.Info($"ASR 已按配置自动启动（采集源={Config.Asr.Capture}），状态={Asr.State}。");
            }
            catch (Exception ex)
            {
                Logger.Warn($"ASR 自动启动失败（不影响其他功能）：{ex.Message}");
            }
        }

        // ---------- 7. 健康监控（立即做一次全量检查） ----------
        Health = new HealthMonitor(Config, Logger, _bubbles, () => Tts.IsChatterboxActive)
        {
            ServiceRestartCallback = RestartServiceAsync,
        };
        await Health.StartAsync(cancellationToken).ConfigureAwait(false);

        // ---------- 8. GameMonitor 通信 + 游戏模式 ----------
        Cooldown = new GameCooldownTracker();
        Monitor = new MonitorServer(Config, Logger, _bubbles, Cooldown)
        {
            RestartMonitorProcessCallback = RestartGameMonitorAsync,
        };
        GameMode = new GameModeCoordinator(Config, Logger, _bubbles, Llama, Tts, Monitor, Cooldown)
        {
            RestartChatterboxCallback = RestartChatterboxAsync,
        };

        try
        {
            await Monitor.StartAsync(cancellationToken).ConfigureAwait(false);
            GameMode.Attach();
        }
        catch (Exception ex)
        {
            Logger.Error($"GameMonitor 通信服务启动失败（不影响其它功能）：{ex.Message}", ex);
            _startupProblems.Add($"GameMonitor 通信服务启动失败：{ex.Message}");
        }

        // ---------- 9. 空闲调度 ----------
        Idle.Start();

        // ---------- 10. 待审核提醒 ----------
        /*
         * 这里是"启动即走"的旁路任务，但绝不能裸 fire-and-forget。
         *
         * 它捕获了 this；若 DisposeAsync 已经把日志 sink 关掉，它随后再写一行
         * `Logger.Warn`，LogSink.EnsureWriter 就会把已关闭的日志文件重新打开
         * （日志分流到旧目录 + 多一个句柄）。所以要把 Task 存下来，退出时先取消并限时等待。
         */
        _pendingNotifyTask = Task.Run(
            async () =>
            {
                try
                {
                    await Knowledge.Pending.NotifyIfNeededAsync().ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    // 已经在退出就不再写日志：此时 sink 可能已关闭，写进去会把旧文件重新打开。
                    if (!_backgroundCts.IsCancellationRequested)
                    {
                        Logger.Warn($"待审核提醒失败：{ex.Message}");
                    }
                }
            },
            _backgroundCts.Token);

        /*
         * 汇总行要如实反映"关掉的"：
         * 之前即使 tts.enabled=false 也照样打印 TTS=gpt_sovits，看起来像引擎起来了却没声音，
         * 排查时会被误导。关掉的一律写"已关闭"。
         */
        var llmLabel = Config.Llm.Enabled ? $"LLM 就绪={LlmReady}" : "LLM=已关闭";
        var ttsLabel = Config.Tts.Enabled ? $"TTS={Tts.CurrentEngineName}（降级={Tts.IsDegraded}）" : "TTS=已关闭";
        var asrLabel = Config.Asr.Enabled ? $"ASR={Asr.State}" : "ASR=已关闭";
        var modeLabel = Config.Llm.Enabled
            ? RuntimeMode.Describe()
            : "未计算（对话已关闭，不占显存）";

        Logger.Info(
            $"启动完成：运行模式={modeLabel}，{llmLabel}，{ttsLabel}，{asrLabel}，" +
            $"知识库 {Knowledge.Count} 条，启动问题 {_startupProblems.Count} 项。");

        IsStarted = true;
    }

    /// <summary>
    /// 设置面板选的引擎存在 Assets\appearance.json，优先级高于 config.yaml 的 tts.engine。
    /// 只改内存里的配置对象，不回写文件（准则 9）。
    /// </summary>
    private void ApplyTtsEnginePreference()
    {
        var preferred = AppearanceStore.LoadTtsEngine();
        if (preferred is null)
        {
            return;
        }

        if (Array.IndexOf(Core.Tts.TTSEngineFactory.UserSelectable, preferred) < 0)
        {
            Logger.Warn(
                $"Assets\\appearance.json 里的 ttsEngine \"{preferred}\" 无法识别，忽略该偏好，" +
                $"按 config.yaml 的 {Config.Tts.Engine} 运行。");
            return;
        }

        if (string.Equals(preferred, Config.Tts.Engine, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        Logger.Info(
            $"按界面偏好把 TTS 引擎由 config.yaml 的 \"{Config.Tts.Engine}\" 覆盖为 \"{preferred}\"" +
            "（来源 Assets\\appearance.json，未改动 config.yaml）。");
        Config.Tts.Engine = preferred;
    }

    /// <summary>
    /// 应用设置面板里的三项 AI 功能开关（存 Assets\appearance.json，不改 config.yaml）。
    ///
    /// 必须赶在任何组件初始化**之前**调用：关掉的功能压根不该被创建，
    /// 而不是建好了再停 —— 那样模型照样进内存、外部服务照样被拉起。
    /// </summary>
    private void ApplyAiTogglePreference()
    {
        try
        {
            var toggle = AppearanceStore.LoadAiToggles();

            Apply("llm", toggle.Llm, Config.Llm.Enabled, value => Config.Llm.Enabled = value);
            Apply("tts", toggle.Tts, Config.Tts.Enabled, value => Config.Tts.Enabled = value);
            Apply("asr", toggle.Asr, Config.Asr.Enabled, value => Config.Asr.Enabled = value);
        }
        catch (Exception ex)
        {
            Logger.Warn($"读取 AI 功能开关偏好失败（不影响启动，按 config.yaml 运行）：{ex.Message}");
        }

        void Apply(string name, bool? preferred, bool fromConfig, Action<bool> set)
        {
            if (preferred is null || preferred.Value == fromConfig)
            {
                return;
            }

            set(preferred.Value);
            Logger.Info(
                $"按界面偏好把 {name}.enabled 由 config.yaml 的 {fromConfig} 覆盖为 {preferred.Value}" +
                "（来源 Assets\\appearance.json，未改动 config.yaml）。");
        }
    }

    /// <summary>
    /// 应用设置面板保存的其余偏好（都存在 Assets\appearance.json）。
    ///
    /// 这里只改内存里的 Config，绝不回写 config.yaml（准则 9）。
    /// 必须赶在 NvidiaSmi / Llama / Tts / Asr 构造之前调用：
    /// 模型路径、上下文长度、采集源、Live2D 开关都会被这些组件在构造时读取。
    /// </summary>
    private void ApplySettingsPanelPreferences()
    {
        try
        {
            // 先记下 config.yaml 的原值：后面的 ReapplyLive2dPreference 可能把它清空。
            _configuredLive2dModelPath = Config.Live2d.DefaultModelPath;

            ApplyText("llm.model_path", AppearanceStore.LoadLlmModelPath(), Config.Llm.ModelPath,
                value => Config.Llm.ModelPath = value);

            if (AppearanceStore.LoadLlmContextLength() is { } contextLength &&
                contextLength != Config.Llm.ContextLength)
            {
                Logger.Info(
                    $"按界面偏好把 llm.context_length 由 {Config.Llm.ContextLength} 覆盖为 {contextLength}" +
                    "（来源 Assets\\appearance.json，未改动 config.yaml）。");
                Config.Llm.ContextLength = contextLength;
            }

            if (AppearanceStore.LoadGpuReservedMb() is { } reservedMb && reservedMb != Config.Gpu.ReservedMb)
            {
                Logger.Info(
                    $"按界面偏好把 gpu.reserved_mb 由 {Config.Gpu.ReservedMb} 覆盖为 {reservedMb}" +
                    "（来源 Assets\\appearance.json，未改动 config.yaml）。");
                Config.Gpu.ReservedMb = reservedMb;
            }

            ApplyText("tts.default_voice", AppearanceStore.LoadTtsVoice(), Config.Tts.DefaultVoice,
                value => Config.Tts.DefaultVoice = value);

            if (AppearanceStore.LoadTtsSpeed() is { } speed &&
                Math.Abs(speed - Config.Tts.GptSoVits.SpeedFactor) > 0.001)
            {
                Logger.Info(
                    $"按界面偏好把 tts.gpt_sovits.speed_factor 由 {Config.Tts.GptSoVits.SpeedFactor:F2} 覆盖为 {speed:F2}" +
                    "（来源 Assets\\appearance.json，未改动 config.yaml）。");
                Config.Tts.GptSoVits.SpeedFactor = speed;
            }

            ApplyText("asr.capture", AppearanceStore.LoadAsrCapture(), Config.Asr.Capture,
                value => Config.Asr.Capture = value);

            ApplyFlag("ui.transparent_hover_switch", AppearanceStore.LoadHoverSwitch(),
                Config.Ui.TransparentHoverSwitch, value => Config.Ui.TransparentHoverSwitch = value);

            ApplyFlag("ui.click_through", AppearanceStore.LoadClickThrough(),
                Config.Ui.ClickThrough, value => Config.Ui.ClickThrough = value);

            ApplyFlag("asr.push_to_talk", AppearanceStore.LoadPushToTalk(),
                Config.Asr.PushToTalk, value => Config.Asr.PushToTalk = value);

            if (AppearanceStore.LoadIdleMinutes() is { } idleMinutes &&
                Math.Abs(idleMinutes - Config.Idle.IdleMinutes) > 0.001)
            {
                Logger.Info($"按界面偏好把 idle.idle_minutes 由 {Config.Idle.IdleMinutes:N0} 覆盖为 {idleMinutes:N0} 分钟。");
                Config.Idle.IdleMinutes = idleMinutes;
            }

            if (AppearanceStore.LoadMaintenanceCooldownMinutes() is { } cooldown &&
                Math.Abs(cooldown - Config.Idle.MaintenanceCooldownMinutes) > 0.001)
            {
                Logger.Info(
                    $"按界面偏好把 idle.maintenance_cooldown_minutes 由 " +
                    $"{Config.Idle.MaintenanceCooldownMinutes:N0} 覆盖为 {cooldown:N0} 分钟。");
                Config.Idle.MaintenanceCooldownMinutes = cooldown;
            }

            // Live2D 开关：关掉就把模型路径清空，MainWindow 会按既有降级链落到静态立绘。
            ReapplyLive2dPreference();

            // 启用中的对话模块：入口可执行文件替换 config.yaml 的 llm.server_executable_path。
            var moduleEntry = ModuleRegistry.ResolveEnabledEntryRelative(ModuleKind.Llm);
            if (!string.IsNullOrWhiteSpace(moduleEntry) &&
                !string.Equals(moduleEntry, Config.Llm.ServerExecutablePath, StringComparison.OrdinalIgnoreCase))
            {
                Logger.Info(
                    $"按界面偏好把 llm.server_executable_path 覆盖为 {moduleEntry}" +
                    "（来源 Assets\\appearance.json 里启用的模块，未改动 config.yaml）。");
                Config.Llm.ServerExecutablePath = moduleEntry;
            }
        }
        catch (Exception ex)
        {
            Logger.Warn($"读取设置面板偏好失败（不影响启动，按 config.yaml 运行）：{ex.Message}");
        }

        void ApplyText(string name, string? preferred, string current, Action<string> set)
        {
            if (string.IsNullOrWhiteSpace(preferred) ||
                string.Equals(preferred, current, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            set(preferred);
            Logger.Info(
                $"按界面偏好把 {name} 由 \"{current}\" 覆盖为 \"{preferred}\"" +
                "（来源 Assets\\appearance.json，未改动 config.yaml）。");
        }

        void ApplyFlag(string name, bool? preferred, bool current, Action<bool> set)
        {
            if (preferred is null || preferred.Value == current)
            {
                return;
            }

            set(preferred.Value);
            Logger.Info(
                $"按界面偏好把 {name} 由 {current} 覆盖为 {preferred.Value}" +
                "（来源 Assets\\appearance.json，未改动 config.yaml）。");
        }
    }

    /// <summary>
    /// 按 Assets\appearance.json 重算 Live2D 的两项内存配置（开关 → 模型路径）。
    ///
    /// 两个调用点：启动时（<see cref="ApplySettingsPanelPreferences"/>），
    /// 以及设置面板改完「外观 → Live2D 开关 / 模型」之后（MainWindow 收到
    /// <see cref="RequestAppearanceChange"/> 先调这里，再走既有的 LoadAvatar 重建链）。
    ///
    /// 只改内存，绝不回写 config.yaml（准则 9）。
    /// </summary>
    public void ReapplyLive2dPreference()
    {
        try
        {
            if (AppearanceStore.LoadLive2dEnabled() is false)
            {
                if (!string.IsNullOrEmpty(Config.Live2d.DefaultModelPath))
                {
                    Logger.Info("按界面偏好关闭 Live2D 渲染，改用静态立绘（仅改内存，config.yaml 未改动）。");
                }

                Config.Live2d.DefaultModelPath = string.Empty;
                return;
            }

            var preferred = AppearanceStore.LoadLive2dModelPath();
            var target = string.IsNullOrWhiteSpace(preferred) ? _configuredLive2dModelPath : preferred!;

            if (string.IsNullOrWhiteSpace(target))
            {
                // 连 config.yaml 都没配模型路径：保持现状，让 MainWindow 走它自己的降级链。
                return;
            }

            var current = Config.Live2d.DefaultModelPath;
            if (string.Equals(target, current, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            Config.Live2d.DefaultModelPath = target;

            Logger.Info(string.IsNullOrEmpty(current)
                ? $"按界面偏好重新启用 Live2D 渲染，模型路径 \"{target}\"（仅改内存，config.yaml 未改动）。"
                : $"按界面偏好把 live2d.default_model_path 由 \"{current}\" 覆盖为 \"{target}\"" +
                  "（来源 Assets\\appearance.json，未改动 config.yaml）。");
        }
        catch (Exception ex)
        {
            // 外观偏好算不出来不该影响启动/继续运行：保持当前内存配置即可。
            Logger.Warn($"重算 Live2D 外观偏好失败（不影响启动，沿用当前值）：{ex.Message}");
        }
    }

    /// <summary>
    /// 把「拓展」页里启用的 tts / asr 模块接进运行时。
    ///
    /// 之前这两类模块只影响设置面板的门禁，运行时完全不看它们 —— 用户"启用了 TTS 模块"，
    /// 实际出声的还是 config.yaml 里的引擎，且日志里没有任何说明。
    ///
    /// 现在：
    ///   - tts 模块有可用入口 → 引擎标识改成 <c>module:&lt;目录名&gt;</c>，并把 piper 的入口/模型
    ///     指向模块目录（只改内存）；没有入口 → 保持内置引擎，记一条 Info 说明原因；
    ///   - asr 模块带模型 → 优先用模块里的模型路径；带 ai_asr 动态库入口 → 优先用它；
    ///     两者都没有 → 保持现状并说明原因。
    ///
    /// 任何异常都只记 Warn，绝不让启动失败（模块是可选增强，不是必需件）。
    /// </summary>
    private void ApplyModuleRuntimePreferences()
    {
        try
        {
            ApplyTtsModulePreference();
        }
        catch (Exception ex)
        {
            Logger.Warn($"接入语音合成模块失败（不影响启动，继续使用内置引擎 {Config.Tts.Engine}）：{ex.Message}");
        }

        try
        {
            ApplyAsrModulePreference();
        }
        catch (Exception ex)
        {
            Logger.Warn($"接入语音识别模块失败（不影响启动，继续使用内置 ASR）：{ex.Message}");
        }
    }

    /// <summary>
    /// 对话后端兜底发现：<c>llm.server_executable_path</c> 指向的文件不存在时，
    /// 扫描 <c>modules\*\module.json</c>，按 vulkan → cuda → 其他 llm 模块 的顺序
    /// 取第一个入口真实存在的模块，覆盖**内存里**的配置（绝不回写 config.yaml）。
    ///
    /// 为什么在 AppHost 也做一遍（LlamaServerController.StartAsync 里有同样的兜底）：
    ///   - 这里更早，日志顺序与用户看到的启动流程一致；
    ///   - 设置面板「跟随 config.yaml（…）」显示的是 Config.Llm.ServerExecutablePath，
    ///     若只在 StartAsync 里改，用户先看到的是那条不存在的路径；
    ///   - 启动横幅（"启动完成"那行）之前就有结论，排查时不用翻两层日志。
    /// 两处调用同一个 <see cref="LlmServerDiscovery"/>，判断口径不会分叉。
    /// </summary>
    private void ApplyLlmServerDiscovery()
    {
        try
        {
            var configured = Config.Llm.ServerExecutablePath;
            var resolved = AppPaths.Resolve(configured);

            if (File.Exists(resolved))
            {
                // 正常路径：只记一条 Info，证明"包内后端就位"，便于冒烟日志断言。
                Logger.Info($"对话后端就位：{resolved}（llm.server_executable_path={configured}）。");
                return;
            }

            var fallback = LlmServerDiscovery.TryFind(out var discovery);

            if (fallback is null)
            {
                // 不在这里报错：交给 LlamaServerController 走既有"缺 exe"分支，
                // 那条路径会弹气泡、写 Error、并计入启动问题项。
                Logger.Warn(
                    $"配置的对话后端不存在：{resolved}；兜底发现也没找到可用后端（{discovery}）。" +
                    "对话将不可用，其余功能不受影响。");
                return;
            }

            Config.Llm.ServerExecutablePath = fallback.RelativePath;

            Logger.Warn(
                $"配置的对话后端不存在：{resolved}；已自动改用 {fallback.RelativePath}（{fallback.RankLabel}）。" +
                $"发现过程：{discovery}。本次仅内存生效，config.yaml 未改动。");
        }
        catch (Exception ex)
        {
            Logger.Warn($"对话后端兜底发现失败（不影响启动，将按配置路径继续）：{ex.Message}");
        }
    }

    private void ApplyTtsModulePreference()
    {
        var (binding, detail) = ModuleRegistry.ResolveEnabledRuntime(ModuleKind.Tts);

        if (binding?.EntryRelativePath is null)
        {
            Logger.Info(
                $"语音合成模块未接入运行时（{detail}），继续使用内置 TTS 引擎 \"{Config.Tts.Engine}\"。");
            return;
        }

        var previousEngine = Config.Tts.Engine;

        /*
         * 设置面板「合成引擎」下拉框与「拓展」页的 TTS 模块是两个入口，后者优先。
         * 用户如果两边都动过，实际生效的是模块 —— 之前这件事只体现在一行 Info 里，
         * 从下拉框看是"我明明选了 GPT-SoVITS"，排查时会被误导。这里明确记一条 Warn。
         */
        var explicitChoice = AppearanceStore.LoadTtsEngine();
        if (explicitChoice is not null &&
            !string.Equals(explicitChoice, binding.EngineId, StringComparison.OrdinalIgnoreCase))
        {
            Logger.Warn(
                $"设置面板里选过的 TTS 引擎 \"{explicitChoice}\" 被「拓展」页启用的语音合成模块覆盖：" +
                $"实际使用 {binding.EngineId}。要回到 \"{explicitChoice}\"，请在设置 → 拓展模块里停用该模块。");
        }

        // 复用 Piper 实现：模块提供的是 piper 兼容的命令行入口（piper.exe 或自定义 exe），
        // 因此不改 TTS 的对外接口与状态机，只把入口/模型路径指到模块目录。
        Config.Tts.Piper.ExecutablePath = binding.EntryRelativePath;

        if (binding.ModelRelativePath is { } moduleModel)
        {
            Config.Tts.Piper.ModelPath = moduleModel;
        }

        Config.Tts.Engine = binding.EngineId;

        var engineNote = string.Equals(previousEngine, binding.EngineId, StringComparison.OrdinalIgnoreCase)
            ? string.Empty
            : $"（原配置引擎 \"{previousEngine}\"）";

        Logger.Info(
            $"语音合成模块已接入运行时：{detail}；引擎标识 {binding.EngineId}{engineNote}。" +
            "实现复用内置 Piper，入口须接受 piper 的 --model / --output_file 参数，" +
            "否则合成失败时会按既有降级链继续降到 SAPI。仅改内存，config.yaml 未改动。");
    }

    private void ApplyAsrModulePreference()
    {
        var (binding, detail) = ModuleRegistry.ResolveEnabledRuntime(ModuleKind.Asr);

        if (binding is null)
        {
            Logger.Info(
                $"语音识别模块未接入运行时（{detail}），继续使用内置 ASR（库 {Config.Asr.LibraryPath}，" +
                $"模型 {Config.Asr.ModelPath}）。");
            return;
        }

        var applied = new List<string>();

        if (binding.ModelRelativePath is { } moduleModel &&
            !string.Equals(moduleModel, Config.Asr.ModelPath, StringComparison.OrdinalIgnoreCase))
        {
            var previousModel = Config.Asr.ModelPath;
            Config.Asr.ModelPath = moduleModel;
            applied.Add($"模型 \"{previousModel}\" → \"{moduleModel}\"");
        }

        if (binding.EntryRelativePath is { } moduleEntry)
        {
            if (moduleEntry.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            {
                if (!string.Equals(moduleEntry, Config.Asr.LibraryPath, StringComparison.OrdinalIgnoreCase))
                {
                    var previousLibrary = Config.Asr.LibraryPath;
                    Config.Asr.LibraryPath = moduleEntry;
                    applied.Add($"动态库 \"{previousLibrary}\" → \"{moduleEntry}\"");
                }
            }
            else
            {
                /*
                 * 可执行程序入口（如 whisper-cli.exe）当前 ASR 用不上：
                 * 运行时走的是 ai_asr.dll 桥接 + ggml 模型，没有"拉起外部识别进程"的实现。
                 * 与其猜它的命令行格式，不如保持内置行为，并在日志里说清楚为什么没用它。
                 */
                Logger.Info(
                    $"语音识别模块的入口 {moduleEntry} 是可执行程序，当前实现只支持 ai_asr 动态库 + ggml 模型，" +
                    "已忽略该入口（保持内置行为）。");
            }
        }

        if (applied.Count == 0)
        {
            Logger.Info(
                $"语音识别模块已启用但无需改动运行配置（{detail}），继续使用内置 ASR" +
                $"（库 {Config.Asr.LibraryPath}，模型 {Config.Asr.ModelPath}）。");
            return;
        }

        Logger.Info(
            $"语音识别模块已接入运行时：{detail}；" + string.Join("；", applied) +
            "（来源 Assets\\appearance.json 里启用的模块，未改动 config.yaml）。");
    }

    /// <summary>HealthMonitor 在休眠唤醒后用它尝试重启一次失败的服务。</summary>
    private async Task<bool> RestartServiceAsync(string serviceKey, CancellationToken cancellationToken)
    {
        try
        {
            switch (serviceKey)
            {
                case "llm":
                    // 按手册只尝试一次；不做重试循环。
                    await Llama.StopAsync(cancellationToken).ConfigureAwait(false);
                    var result = await Llama.StartAsync(cancellationToken).ConfigureAwait(false);
                    LlmReady = result.Success;
                    return result.Success;

                case "chatterbox":
                    return await RestartChatterboxAsync(cancellationToken).ConfigureAwait(false);

                default:
                    return false;
            }
        }
        catch (Exception ex)
        {
            Logger.Error($"重启服务 {serviceKey} 失败：{ex.Message}", ex);
            return false;
        }
    }

    /// <summary>
    /// 按进程名判断是否已有实例在运行（查询失败一律当作"没有"，不影响后面的拉起逻辑）。
    /// 用于拉起前先去重：否则每点一次"重连/重启"就会多一个常驻进程。
    /// </summary>
    private static bool IsProcessRunning(string processName)
    {
        try
        {
            var running = System.Diagnostics.Process.GetProcessesByName(processName);

            try
            {
                return running.Length > 0;
            }
            finally
            {
                // 每个 Process 对象都是一个进程句柄，不 Dispose 就等着终结器回收。
                foreach (var process in running)
                {
                    process.Dispose();
                }
            }
        }
        catch
        {
            return false;
        }
    }

    /// <summary>按配置的启动命令重新拉起 chatterbox-api。</summary>
    private async Task<bool> RestartChatterboxAsync(CancellationToken cancellationToken)
    {
        var command = Config.Tts.Chatterbox.LaunchCommand;
        if (string.IsNullOrWhiteSpace(command))
        {
            Logger.Warn("未配置 tts.chatterbox.launch_command，无法自动拉起 Chatterbox，请手动启动。");
            _bubbles.Show(
                "chatterbox-manual",
                "未配置 Chatterbox 启动命令，请手动启动后点击\"重启语音服务\"。",
                BubbleLevel.Warning);
            return false;
        }

        // 拉起前先看服务是否已经在线：已在线还去执行命令，每次重连都会多一个常驻的 chatterbox 进程。
        // 这里用健康状态而不是进程名：启动命令形如 `cmd /c <任意命令>`，真实进程可能是
        // python/node/xxx.exe，按名字查既可能漏判也可能误判（把用户自己的 python 当成服务）。
        var existing = Health.Snapshots.FirstOrDefault(s => s.Key == "chatterbox");
        if (existing?.State == Core.Health.ServiceState.Online)
        {
            Logger.Info("Chatterbox 已在线，跳过重复拉起。");
            return true;
        }

        try
        {
            Logger.Info($"按配置启动 Chatterbox：{command}");

            // 用 cmd /c 执行，允许用户在配置里写带参数或重定向的完整命令。
            var psi = new System.Diagnostics.ProcessStartInfo("cmd.exe", $"/c {command}")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = AppPaths.Root,
            };

            // using 只为不泄漏进程句柄（不 WaitForExit，否则会把启动流程阻塞到命令结束）。
            using var process = System.Diagnostics.Process.Start(psi);

            // 给它一点启动时间，然后全量探活确认。
            for (var i = 0; i < 10; i++)
            {
                await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false);
                await Health.CheckAllAsync(cancellationToken).ConfigureAwait(false);

                var chatterbox = Health.Snapshots.FirstOrDefault(s => s.Key == "chatterbox");
                if (chatterbox?.State == Core.Health.ServiceState.Online)
                {
                    return true;
                }
            }

            Logger.Warn("Chatterbox 启动后 20 秒内未就绪。");
            return false;
        }
        catch (Exception ex)
        {
            Logger.Error($"启动 Chatterbox 失败：{ex.Message}", ex);
            return false;
        }
    }

    /// <summary>重新拉起 GameMonitor.exe（托盘"重连游戏检测"按钮）。</summary>
    private Task<bool> RestartGameMonitorAsync(CancellationToken cancellationToken)
    {
        try
        {
            var exe = Path.Combine(AppPaths.Root, "GameMonitor.exe");
            if (!File.Exists(exe))
            {
                // 开发期输出目录也在候选之内。
                exe = Path.Combine(AppContext.BaseDirectory, "GameMonitor.exe");
            }

            if (!File.Exists(exe))
            {
                Logger.Error($"未找到 GameMonitor.exe，无法重连。已尝试 {AppPaths.Root} 与 {AppContext.BaseDirectory}");
                return Task.FromResult(false);
            }

            // 已经在跑就不要再拉一个：托盘"重连游戏检测"可能被连点，
            // 每次都会留下一个常驻的 GameMonitor.exe（它自己会重连，不需要新实例）。
            if (IsProcessRunning("GameMonitor"))
            {
                Logger.Info("GameMonitor 已在运行，跳过重复拉起（它自己会重连主程序）。");
                return Task.FromResult(true);
            }

            var psi = new System.Diagnostics.ProcessStartInfo(exe)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetDirectoryName(exe) ?? AppPaths.Root,
            };

            // using 只为不泄漏进程句柄（不 WaitForExit：GameMonitor 是常驻进程）。
            using var process = System.Diagnostics.Process.Start(psi);
            Logger.Info($"已重新启动 GameMonitor：{exe}");
            return Task.FromResult(true);
        }
        catch (Exception ex)
        {
            Logger.Error($"启动 GameMonitor 失败：{ex.Message}", ex);
            return Task.FromResult(false);
        }
    }

    public async ValueTask DisposeAsync()
    {
        Logger?.Info("正在关闭 AI 伴侣…");

        /*
         * 先收掉启动期的旁路任务，再拆组件、最后关日志 —— 顺序不能反。
         *
         * 那个任务捕获了 this：若它在日志 sink 关闭之后才写日志，
         * LogSink.EnsureWriter 会把已关闭的日志文件重新打开（日志分流到旧目录 + 多一个句柄）。
         */
        try
        {
            _backgroundCts.Cancel();

            var pending = _pendingNotifyTask;
            if (pending is not null && !pending.Wait(TimeSpan.FromSeconds(2)))
            {
                // 不无参等：它可能正卡在读知识库文件上，无限等待会吃光 OnExit 的 10 秒退出预算。
                Logger?.Warn("待审核提醒任务 2 秒内未结束，继续退出。");
            }
        }
        catch (AggregateException ex)
        {
            Logger?.Warn($"待审核提醒任务异常结束：{ex.InnerException?.Message ?? ex.Message}");
        }
        catch
        {
            // 其它异常都不该阻断退出流程。
        }
        finally
        {
            _backgroundCts.Dispose();
        }

        try
        {
            Idle?.Dispose();
        }
        catch
        {
            // 忽略。
        }

        try
        {
            GameMode?.Detach();
        }
        catch
        {
            // 忽略。
        }

        try
        {
            if (Monitor is not null)
            {
                await Monitor.DisposeAsync().ConfigureAwait(false);
            }
        }
        catch
        {
            // 忽略。
        }

        try
        {
            if (Health is not null)
            {
                await Health.DisposeAsync().ConfigureAwait(false);
            }
        }
        catch
        {
            // 忽略。
        }

        // 每个组件独立 try：任何一个 Dispose 抛异常，后面的都不能被跳过。
        // （之前四行挤在一个 try 里，ASR 抛一次就漏掉 TTS/知识库/音频的释放。）
        try
        {
            // 先停 PTT：它的 60ms 定时器会调 Asr.BeginPushToTalk，必须在 ASR 释放前停。
            _pushToTalk?.Dispose();
            _pushToTalk = null;
        }
        catch
        {
            // 忽略。
        }

        try
        {
            Asr?.Dispose();
        }
        catch
        {
            // 忽略。
        }

        try
        {
            Tts?.Dispose();
        }
        catch
        {
            // 忽略。
        }

        try
        {
            Knowledge?.Dispose();
        }
        catch
        {
            // 忽略。
        }

        try
        {
            (Audio as IDisposable)?.Dispose();
        }
        catch
        {
            // 忽略。
        }

        try
        {
            Idle?.Dispose();
        }
        catch
        {
            // 忽略。
        }

        try
        {
            if (Llama is not null)
            {
                await Llama.DisposeAsync().ConfigureAwait(false);
            }
        }
        catch
        {
            // 忽略。
        }

        Logger?.Info("已退出。");

        // 关闭日志句柄，确保最后几行确实落盘。
        try
        {
            LogSink?.Close();
            BootstrapSink.Close();
        }
        catch
        {
            // 忽略。
        }
    }
}
