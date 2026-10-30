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
