using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using AICompanion.App.Animation;
using AICompanion.App.Services;
using AICompanion.Core.Abstractions;
using AICompanion.Core.Asr;
using AICompanion.Core.Configuration;
using AICompanion.Core.Pipeline;

namespace AICompanion.App;

/// <summary>
/// 桌宠主窗口（手册 §1.1）。
///
/// 表现层实现要点：
///   - 透明无边框窗口 + 鼠标穿透（Win32 扩展样式 WS_EX_TRANSPARENT）；
///   - 角色形象走三级兜底：角色图片 → 全局默认贴图 → 纯色背景 + 角色名（准则 10）；
///   - "动画指令"在静态图降级方案下落为**表情贴图切换 + 角标提示**，
///     这样上游（知识条目 / LLM）给出的动画指令在降级模式下依然可见、可验证；
///   - UI 线程上没有任何阻塞等待（准则 1），所有交互都是 await 到后台。
/// </summary>
public partial class MainWindow : Window
{
    private const int GwlExStyle = -20;
    private const int WsExTransparent = 0x00000020;
    private const int WsExLayered = 0x00080000;

    private readonly AppHost _host;
    private readonly BubbleNotifier _bubbles;

    /// <summary>
    /// 是否处于"透明"（鼠标穿透）模式。
    ///
    /// 默认取 config.yaml 的 ui.click_through，而它的默认值已改为 false ——
    /// 也就是启动时处于"覆盖"（可交互）模式。这是必须的：
    /// 穿透状态下窗口收不到任何鼠标事件，左键/右键交互根本无从触发。
    /// </summary>
    private bool _clickThrough;

    private bool _hostReady;
    private string? _currentExpression;

    // 拖动与"点击"的区分状态。
    //
    // 参照系统一用**屏幕坐标（物理像素）**，不能用 e.GetPosition(this)：
    // 那个坐标是相对于窗口的，而拖动时窗口自己也在动 ——
    // "移动窗口 → 相对坐标随之变化 → 又算出新的位移" 会形成反馈回路，
    // 表现就是窗口来回抽搐。
    private bool _dragActive;
    private bool _isDragging;
    private Point _dragStartWindowPos;
    private int _dragStartCursorX;
    private int _dragStartCursorY;

    private TaskBarWindow? _taskBar;

    /// <summary>动作调度器（线程安全，后台服务只提交请求）。</summary>
    private AnimationManager? _animations;

    /// <summary>口型同步驱动（后台每 50ms 上报振幅，UI 线程写模型参数）。</summary>
    private LipSyncDriver? _lipSync;

    /// <summary>是否因为 TTS 播放而暂停了 ASR（播放结束后要恢复）。</summary>
    private bool _asrPausedForPlayback;

    /// <summary>播放结束后延迟恢复 ASR 的计时器（等扬声器余音散去）。</summary>
    private DispatcherTimer? _asrResumeTimer;

    /// <summary>Live2D 渲染器；为 null 表示走静态图降级方案。</summary>
    private Live2DRenderer? _live2d;

    /// <summary>角色右上角的「透明/覆盖」切换按钮（独立窗口，两种模式下都可点）。</summary>
    private CornerButton? _cornerButton;

    // ---- 角色大小（DPI / 分辨率自适应）----
    /// <summary>倍率 1.0 时的窗口基准尺寸（与 XAML 里的初始 Width/Height 一致）。</summary>
    private const double BaseWidth = 340;
    private const double BaseHeight = 460;

    private const double MinScale = 0.5;
    private const double MaxScale = 2.5;

    private readonly ScaleTransform _rootScale = new(1, 1);
    private double _scale = 1.0;

    /// <summary>
    /// 透明（穿透）态下角色整体的不透明度。
    /// 覆盖态恒为 1.0，透明态降到此值 —— 让两种模式**一眼可辨**。
    /// 来源优先级：设置面板保存的值 → config.yaml 的 ui.transparent_opacity。
    /// </summary>
    private double _transparentOpacity = 0.5;

    // ---- 透明态悬停切换 ----
    private readonly DispatcherTimer _hoverTimer;

    /// <summary>表情停留计时：到期后回到默认立绘（否则角色会永远停在最后一次的表情上）。</summary>
    private readonly DispatcherTimer _expressionTimer;
    private DateTime? _hoverSince;
    private DateTime? _awaySince;

    /// <summary>当前「覆盖」态是否是悬停自动切过来的（只有这种才会自动退回透明）。</summary>
    private bool _hoverSwitched;

    /// <summary>
    /// 悬停触发是否"已解除待发"。
    ///
    /// 只有光标**先离开**过角色，之后再移上来才允许触发切换。
    ///
    /// 没有这个状态会出现什么：右键切到「透明」时光标通常正压在角色身上，
    /// 250ms 后悬停逻辑立刻把它切回「覆盖」；用户再右键、又被切回……
    /// 实测 55 秒内来回切了 25 次（13 次透明 / 12 次覆盖），
    /// 每次都改窗口样式并弹气泡 —— 表现就是界面剧烈抽搐。
    /// </summary>
    private bool _hoverArmed;

    // 悬停判定的诊断字段（仅用于排查命中问题）
    private DateTime _lastHoverDiag = DateTime.MinValue;
    private string _hoverDiag = "(尚未采样)";

    public MainWindow(AppHost host, BubbleNotifier bubbles)
    {
        _host = host;
        _bubbles = bubbles;
        _clickThrough = _host.Config.Ui.ClickThrough;

        InitializeComponent();

        // 在代码里挂缩放变换：XAML 内联的 ScaleTransform 会被 WPF 冻结，运行时改不了。
        RootGrid.LayoutTransform = _rootScale;

        // 悬停检测定时器。穿透态下收不到任何鼠标事件，只能靠轮询。
        _hoverTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(100),
        };
        _hoverTimer.Tick += (_, _) => OnHoverTick();

        _expressionTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher)
        {
            Interval = TimeSpan.FromSeconds(8),
        };
        _expressionTimer.Tick += (_, _) => RevertExpression();

        _bubbles.Changed += (_, _) => Dispatcher.BeginInvoke(new Action(RefreshBubbles));
        RefreshBubbles();

        // Live2D 三级兜底在窗口构造阶段就要算出来，否则启动早期会显示空白。
        // 注意此时 config.yaml 还没加载，AppPaths 仍指向 exe 目录，
        // 因此先做一次 provisional 预览（不告警），OnHostReady 里再用真实根目录正式加载。
        LoadAvatar(provisional: true);

        SourceInitialized += (_, _) =>
        {
            _clickThrough = _host.Config.Ui.ClickThrough;
            ApplyClickThrough(_clickThrough);
        };
    }

    /// <summary>启动完成后挂接核心层事件。</summary>
    public void OnHostReady()
    {
        if (_hostReady)
        {
            return;
        }


        // 记录 WPF 渲染层级：Tier 0 = 软件渲染（整窗都会发虚），Tier 1/2 = 硬件
        try
        {
            var tier = System.Windows.Media.RenderCapability.Tier >> 16;
            _host.Logger.Info(
                $"WPF 渲染层级：Tier {tier}（0=软件，1/2=硬件），" +
                $"进程渲染模式={System.Windows.Media.RenderOptions.ProcessRenderMode}");
        }
        catch (Exception ex)
        {
            _host.Logger.Debug($"读取渲染层级失败：{ex.Message}");
        }

        _hostReady = true;

        _host.Pipeline.StatusChanged += (_, status) => Dispatcher.BeginInvoke(new Action(() => OnPipelineStatus(status)));
        _host.Pipeline.ReplyReady += (_, message) => Dispatcher.BeginInvoke(new Action(() => OnReplyReady(message)));
        _host.Pipeline.UserMessageAccepted += (_, _) => Dispatcher.BeginInvoke(new Action(RefreshBubbles));

        _host.Asr.SpeechRecognized += (_, text) => Dispatcher.BeginInvoke(new Action(() => _ = SendAsync(text)));
        _host.Asr.StateChanged += (_, state) => Dispatcher.BeginInvoke(new Action(() => UpdateStageText()));

        // ---- 动作调度 + 口型同步 ----
        /*
         * 1) 先解析模型文件（config.live2d.default_model_path）。
         *    解析出来的动作组名用于"动作是否真的存在于模型"这一层兜底 ——
         *    之前这里只能是 TODO，现在有模型就能真查了。
         */
        var modelPath = AppPaths.Resolve(_host.Config.Live2d.DefaultModelPath);
        var modelInfo = Live2dModelInfo.TryLoad(modelPath, _host.Logger);

        if (modelInfo is not null)
        {
            _host.Logger.Info(
                $"Live2D 模型已解析：{Path.GetFileName(modelInfo.ModelPath)}，" +
                $"动作组 [{string.Join(", ", modelInfo.MotionGroups)}]，" +
                $"口型参数 {modelInfo.LipSyncParameter ?? "(未声明)"}");
        }
        else
        {
            // 官方的公开分发包**不含** Live2D 的模型与原生库（它们需各自授权），
            // 所以"刚解压完就启动"必然落到这里。直接说清缺什么、去哪补，比只说"没解析到"有用。
            var hasCore = File.Exists(Path.Combine(AppContext.BaseDirectory, "Live2DCubismCore.dll"));
            _host.Logger.Info($"未解析到 Live2D 模型（{modelPath}），动作存在性检查降级为「只看映射是否存在」。");

            if (!hasCore)
            {
                _host.Logger.Warn(
                    "未启用 Live2D：既没有模型文件，也没有 Live2DCubismCore.dll。" +
                    "二者都需按 Live2D 的授权自行取得（不随包分发），放到程序目录后重启即可启用；" +
                    "缺省会用静态立绘或纯色背景代替，功能不受影响。详见同目录的「素材说明.md」。");
            }
        }

        /*
         * 2) 角色包配置优先从**模型所在目录**读：模型与它的 AvatarProfile.json 放一起，
         *    换模型时整个文件夹替换即可，不需要改代码。
         */
        var modelDirectory = modelInfo is not null
            ? Path.GetDirectoryName(modelInfo.ModelPath) ?? AppPaths.Resolve("Assets/Default")
            : AppPaths.Resolve("Assets/Default");

        var profile = AvatarProfile.Load(modelDirectory, _host.Logger, out var profileSource);
        _host.Logger.Info($"角色包动作配置来源：{profileSource}");

        // 口型参数两处都声明了，不一致时以模型为准并告警（模型才是事实）
        if (modelInfo?.LipSyncParameter is { } modelLipSync &&
            !string.Equals(modelLipSync, profile.MouthOpenParameter, StringComparison.OrdinalIgnoreCase))
        {
            _host.Logger.Warn(
                $"口型参数不一致：AvatarProfile.json 写的是 {profile.MouthOpenParameter}，" +
                $"模型声明的是 {modelLipSync}，以模型为准。");
        }

        // 真实的存在性检查：模型里没有该动作组 → 自动降级到 Fallback。
        // 用显式委托变量：方法组不能直接接 ?.，也不能和 null 做条件表达式推断。
        Func<string, bool>? motionExists = modelInfo is null ? null : modelInfo.HasMotion;

        /*
         * 3) 真渲染：模型可用时创建 Live2D 渲染器并顶掉静态图；
         *    任何一步失败都返回 null，退回原来的静态图（决策 Q4 的降级链保持完整）。
         */
        if (modelInfo is not null)
        {
            // 用户滑块优先；没设过就用角色包里的默认值（都为空 → 自动居中）
            var initialOffset = AppearanceStore.LoadHorizontalOffset() ?? profile.HorizontalOffset;
            _live2d = Live2DRenderer.TryCreate(modelInfo.ModelPath, initialOffset, _host.Logger);

            if (_live2d is not null)
            {
                _live2d.Start();
                Live2DHost.Child = _live2d.View;
                Live2DHost.Visibility = Visibility.Visible;
                AvatarImage.Visibility = Visibility.Collapsed;

                _host.Logger.Info(
                    $"已切换到 Live2D 渲染：{_live2d.ModelName}（动作 {_live2d.Motions.Count} 个，口型 {_live2d.MouthParameter}）");
            }
            else
            {
                // 最常见的失败原因是缺 Live2DCubismCore.dll——它由 Live2D 授权、不随包分发，
                // 用户拿到的是"没有 Live2D 的包"，所以这里要说清怎么补，而不只是"失败了"。
                var corePath = Path.Combine(AppContext.BaseDirectory, "Live2DCubismCore.dll");
                if (!File.Exists(corePath))
                {
                    _host.Logger.Warn(
                        "Live2D 不可用：程序目录缺少 Live2DCubismCore.dll（该文件需按 Live2D 授权自行取得，不随包分发）。" +
                        "从 Live2D 官网的 Cubism SDK for Native 取 x64 版放到程序目录即可启用，详见同目录的「素材说明.md」。");
                }
                else
                {
                    _host.Logger.Info("Live2D 渲染器创建失败，继续使用静态图。");
                }
            }
        }

        _animations = new AnimationManager(
            Dispatcher,
            _host.Logger,
            profile,
            ApplyAnimation,
            motionExists);

        _lipSync = new LipSyncDriver(Dispatcher, ApplyMouthOpen, _host.Logger);

        // ASR 进入 Running（倾听中）→ Listen（StateChange）
        _host.Asr.StateChanged += (_, state) =>
        {
            if (state == AsrState.Running)
            {
                _animations.Request(AnimationType.Listen, AnimationPriority.StateChange, "ASR", TimeSpan.FromSeconds(10));
            }
        };

        // TTS 开始播放 → Speak + 启动口型跟随 + 暂停 ASR
        _host.ObservableAudio.PlaybackStarted += (_, wav) =>
        {
            /*
             * 回声抑制：TTS 一开口就停掉 ASR。
             *
             * 不这么做的话，麦克风（以及更严重的：系统音频回环）会把角色自己的声音再识别一遍，
             * 形成「识别 → 回复 → TTS → 再识别」的自激循环。实测回环模式下几秒就会无限对话。
             */
            SuspendAsrForPlayback();

            _animations.Request(AnimationType.Speak, AnimationPriority.Speak, "TTS");
            _lipSync?.Start(wav);
        };

        // 播放结束/被打断 → 嘴立刻归零 + 回到 Idle + 恢复 ASR
        _host.ObservableAudio.PlaybackStopped += (_, _) =>
        {
            _lipSync?.Stop(resetMouth: true, reason: "playback-stopped");
            _animations.ResetToIdle("playback-stopped");
            ScheduleAsrResume();
        };

        // TTS 引擎崩溃降级 → Confused（Critical，强行打断）
        _host.Tts.EngineFellBack += (_, engine) =>
            _animations.Request(AnimationType.Confused, AnimationPriority.Critical, $"TTS→{engine}");

        // config.yaml 已加载，AppPaths 现在指向真实根目录：
        // 重新算一次形象（这次会带上"更换外观"的覆盖值），并同步启动模式。
        LoadAvatar();
        _clickThrough = _host.Config.Ui.ClickThrough;
        ApplyUserAppearanceSettings();

        _taskBar = new TaskBarWindow();
        _taskBar.SettingsRequested += (_, _) => OpenSettings();
        _taskBar.AppearanceRequested += (_, _) => ChangeAppearance();
        _taskBar.ResetAppearanceRequested += (_, _) => ResetAppearance();
        _taskBar.ChatRequested += (_, _) => OpenChat();
        _taskBar.SmallerRequested += (_, _) => AdjustScale(-0.05);
        _taskBar.LargerRequested += (_, _) => AdjustScale(+0.05);

        // 右上角模式切换按钮：独立窗口，因此透明态下依然可点
        _cornerButton = new CornerButton();
        _cornerButton.ToggleRequested += (_, _) => ToggleClickThrough();
        _cornerButton.ApplyScale(_scale);
        _cornerButton.SetMode(_clickThrough);
        _cornerButton.ShowAt(this);

        LogDisplayEnvironment();
        ApplyInitialScale();

        UpdateStageText();

        // 配置就绪，开始悬停检测（穿透态下自动切回覆盖的能力依赖它）
        _hoverTimer.Start();
        _host.Logger.Info(
            $"悬停切换已{( _host.Config.Ui.TransparentHoverSwitch ? "启用" : "禁用")}：" +
            $"悬停 {_host.Config.Ui.TransparentHoverDelayMs} ms 切到覆盖，" +
            $"离开 {_host.Config.Ui.TransparentAutoRevertSeconds} 秒自动退回透明。");

        // 交互方式没有可见入口，必须主动告知一次（内容需与当前交互模型一致：
        // 右键开功能栏、右上角按钮切模式、透明态悬停自动切回）。
        _bubbles.Show(
            "interaction-hint",
            "右键点我打开功能栏；右上角「覆/透」按钮切换 透明↔覆盖；透明态下把鼠标移到我身上也会自动切回覆盖。",
            BubbleLevel.Info,
            TimeSpan.FromSeconds(12));
    }

    /// <summary>是否处于"可交互"状态（鼠标穿透关闭）。</summary>
    public bool IsInteractive => !_clickThrough;

    /// <summary>切换交互模式。托盘菜单与"输入文字…"都走这里。</summary>
    public void SetInteractive(bool interactive)
    {
        _clickThrough = !interactive;
        ApplyClickThrough(_clickThrough);

        if (interactive)
        {
            Activate();
            InputBox.Focus();
        }
        else
        {
            _taskBar?.Hide();
        }
    }

    /// <summary>切换显示/隐藏（托盘"显示/隐藏角色"）。</summary>
    public void ToggleVisibility()
    {
        if (IsVisible)
        {
            _taskBar?.Hide();
            _cornerButton?.Hide();
            Hide();
        }
        else
        {
            Show();
            Activate();
            _cornerButton?.ShowAt(this);
        }
    }

    // ------------------------------------------------------------------ 气泡

    private void RefreshBubbles()
    {
        BubbleList.ItemsSource = _bubbles.Current;
    }

    // ------------------------------------------------------------------ 角色形象三级兜底

    /// <param name="provisional">
    /// true 表示这是**构造阶段**的预览加载：此时 config.yaml 还没读，
    /// AppPaths 仍指向 exe 目录，找不到素材是必然的。
    /// 因此这个阶段不弹告警、只记 Debug，避免每次启动都误报"形象全部缺失"。
    /// 配置加载完成后 <see cref="OnHostReady"/> 会用真实根目录再做一次正式加载。
    /// </param>
    private void LoadAvatar(bool provisional = false)
    {
        var log = _host.Logger;
        var ui = _host.Config.Ui;
        var live2d = _host.Config.Live2d;

        // 第零级：用户通过"更换外观"自己挑的图片（存在 Assets\appearance.json）。
        // 它优先于 config.yaml，但不修改 config.yaml 本身（准则 9）。
        var overridePath = AppearanceStore.LoadImagePath();
        if (!string.IsNullOrWhiteSpace(overridePath))
        {
            if (TryLoadImage(overridePath, out var overridden, out var overrideError))
            {
                SetAvatarImage(overridden!);
                log.Info($"角色形象：使用用户指定外观 {AppPaths.Resolve(overridePath)}");
                return;
            }

            log.Warn($"用户指定外观不可用（{AppPaths.Resolve(overridePath)}：{overrideError}），回退到配置外观。");
        }

        // 第一级：config.yaml 明确指定的角色图片（文件存在就用）。
        if (TryLoadImage(ui.PetImagePath, out var bitmap, out var firstError))
        {
            SetAvatarImage(bitmap!);
            log.Info($"角色形象：使用角色图片 {AppPaths.Resolve(ui.PetImagePath)}");
            LogLive2dPresence(log, live2d);
            return;
        }

        /*
         * 第二级：扫描 Assets\Default 目录，**任意文件名、任意受支持的图片格式**。
         *
         * 这是用户最自然的用法："把图丢进 Default 目录就该生效"。
         * 因此这里不绑定 default.png 这类固定名字，也不要求特定格式 ——
         * config.yaml 里 ui.pet_image_path 指错了名字（默认值就是 default.png）
         * 也不影响：上面那一级没命中就落到这里，按目录里的实际文件来。
         */
        if (TryFindImageInDefaultFolder(out var scannedPath, out var scanDetail) &&
            TryLoadImage(scannedPath, out var scanned, out _))
        {
            SetAvatarImage(scanned!);
            log.Info($"角色形象：使用 Assets\\Default 中的图片 {scannedPath}（{scanDetail}）");
            LogLive2dPresence(log, live2d);
            return;
        }

        // 第三级：全局默认贴图。
        if (TryLoadImage(live2d.DefaultTexturePath, out bitmap, out var secondError))
        {
            SetAvatarImage(bitmap!);
            log.Warn($"角色目录未命中（{firstError}；{scanDetail}），已降级为全局默认贴图 {AppPaths.Resolve(live2d.DefaultTexturePath)}。");

            if (!provisional)
            {
                _bubbles.Show(
                    "avatar-degraded",
                    "未找到角色形象，已使用默认贴图。",
                    BubbleLevel.Info,
                    TimeSpan.FromSeconds(_host.Config.Ui.BubbleDurationSeconds));
            }

            return;
        }

        // 第四级：纯色背景 + 角色名。
        ShowFallbackAvatar(
            $"角色形象缺失，已使用纯色背景。\n角色图片：{firstError}\n角色目录：{scanDetail}\n默认贴图：{secondError}");

        if (provisional)
        {
            // 构造阶段 AppPaths 还指向 exe 目录，此时找不到素材是**必然**的，
            // 不是真的缺失。这里不告警也不弹气泡，等配置加载后 OnHostReady 再正式判定。
            log.Debug($"（构造阶段预览）暂未找到形象素材，等配置加载后重试：{firstError}");
            return;
        }

        log.Warn($"角色形象全部缺失（角色图片：{firstError}；角色目录：{scanDetail}；默认贴图：{secondError}），已降级为纯色背景。");
        _bubbles.Show(
            "avatar-fallback",
            "未找到任何角色形象素材，已使用纯色背景显示。把图片放进 Assets\\Default 目录即可。",
            BubbleLevel.Warning,
            TimeSpan.FromSeconds(_host.Config.Ui.BubbleDurationSeconds * 2));
    }

    /// <summary>
    /// WPF 自带解码器支持的图片格式。WebP 不在其中（需要额外编解码器），
    /// 所以这里不列 .webp，避免"文件在目录里却被跳过"的困惑。
    /// </summary>
    private static readonly string[] SupportedImageExtensions =
    {
        ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".tif", ".tiff",
    };

    /// <summary>
    /// 在 Assets\Default 里找角色图片，不关心文件名与格式。
    ///
    /// 选择规则（多张时保证结果确定且可解释）：
    ///   1. 名字以 default 开头的优先（兼容老约定）；
    ///   2. 否则取**最后修改时间最新**的那张；
    ///   3. 命中的文件名与取舍理由会写进日志，避免"为什么用这张"说不清。
    /// </summary>
    private bool TryFindImageInDefaultFolder(out string path, out string detail)
    {
        path = string.Empty;
        detail = string.Empty;

        var folder = AppPaths.Resolve("Assets/Default");

        try
        {
            if (!Directory.Exists(folder))
            {
                detail = $"目录不存在：{folder}";
                return false;
            }

            var candidates = Directory
                .GetFiles(folder, "*", SearchOption.TopDirectoryOnly)
                .Where(f => SupportedImageExtensions.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase))
                .ToList();

            if (candidates.Count == 0)
            {
                detail = $"目录里没有图片：{folder}";
                return false;
            }

            var chosen = candidates
                .OrderByDescending(f => Path.GetFileNameWithoutExtension(f)
                    .StartsWith("default", StringComparison.OrdinalIgnoreCase))
                .ThenByDescending(File.GetLastWriteTimeUtc)
                .First();

            var names = string.Join("、", candidates.Select(Path.GetFileName));

            detail = candidates.Count == 1
                ? "目录中唯一的图片"
                : $"目录中有 {candidates.Count} 张图片（{names}），选了 {Path.GetFileName(chosen)}";

            path = chosen;
            return true;
        }
        catch (Exception ex)
        {
            detail = $"扫描失败：{ex.Message}";
            return false;
        }
    }

    /// <summary>记录 Live2D 模型是否存在：将来接入真 Live2D 时可以直接接管。</summary>
    private static void LogLive2dPresence(IAppLogger log, Live2dConfig live2d)
    {
        var modelPath = AppPaths.Resolve(live2d.DefaultModelPath);
        log.Info(File.Exists(modelPath)
            ? $"检测到 Live2D 模型文件：{modelPath}（有渲染器时用它，否则静态图兜底）"
            : $"未检测到 Live2D 模型文件：{modelPath}，继续使用静态图。");
    }

    private static bool TryLoadImage(string? configuredPath, out BitmapImage? bitmap, out string error)
    {
        bitmap = null;
        error = string.Empty;

        if (string.IsNullOrWhiteSpace(configuredPath))
        {
            error = "未配置路径。";
            return false;
        }

        var path = AppPaths.Resolve(configuredPath);
        if (!File.Exists(path))
        {
            error = $"文件不存在：{path}";
            return false;
        }

        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            // OnLoad + 完全缓存：避免文件被占用，也避免后续渲染时再去读盘。
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.UriSource = new Uri(path, UriKind.Absolute);
            image.EndInit();
            image.Freeze();

            bitmap = image;
            return true;
        }
        catch (Exception ex)
        {
            error = $"加载失败：{ex.Message}";
            return false;
        }
    }

    private void SetAvatarImage(BitmapSource bitmap)
    {
        AvatarImage.Source = bitmap;

        // 有 Live2D 渲染器时静态图只作为后备，不能显示出来盖在模型上
        AvatarImage.Visibility = _live2d is null ? Visibility.Visible : Visibility.Collapsed;
        FallbackBackground.Visibility = Visibility.Collapsed;
    }

    private void ShowFallbackAvatar(string detail)
    {
        AvatarImage.Source = null;
        AvatarImage.Visibility = Visibility.Collapsed;

        FallbackText.Text = $"{_host.Config.Ui.FallbackDisplayName}\n\n{detail}";
        FallbackBackground.Visibility = Visibility.Visible;

        if (TryParseColor(_host.Config.Ui.FallbackBackgroundColor, out var color))
        {
            FallbackBackground.Background = new SolidColorBrush(color);
        }
    }

    private static bool TryParseColor(string? value, out Color color)
    {
        color = Colors.Transparent;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        try
        {
            color = (Color)ColorConverter.ConvertFromString(value);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 表情切换：静态图降级方案下，动画指令映射为 ExpressionDirectory 下的同名图片。
    /// 找不到对应表情时保留当前形象并提示，而不是静默失败（准则 10：任何一层缺失都不应静默）。
    /// </summary>
    private void ApplyExpression(string? animation)
    {
        if (string.IsNullOrWhiteSpace(animation))
        {
            return;
        }

        AnimationText.Text = animation;
        AnimationBadge.Visibility = Visibility.Visible;

        if (string.Equals(_currentExpression, animation, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var directory = AppPaths.Resolve(_host.Config.Live2d.ExpressionDirectory);
        var candidates = new[]
        {
            Path.Combine(directory, animation + ".png"),
            Path.Combine(directory, animation + ".jpg"),
        };

        foreach (var candidate in candidates)
        {
            if (TryLoadImage(candidate, out var bitmap, out _))
            {
                SetAvatarImage(bitmap!);
                _currentExpression = animation;
                ScheduleExpressionRevert();
                _host.Logger.Info($"已切换到表情：{animation}");
                return;
            }
        }

        // 当前用的是哪一级形象就保留哪一级，不做破坏性切换。
        _host.Logger.Info($"没有找到表情贴图 \"{animation}\"，保留当前形象（目录：{directory}）。");
        _currentExpression = animation;
    }

    /// <summary>启动表情回退计时（时长取 ui.expression_hold_seconds，0 表示不回退）。</summary>
    private void ScheduleExpressionRevert()
    {
        _expressionTimer.Stop();

        var hold = _host.Config.Ui.ExpressionHoldSeconds;
        if (hold <= 0)
        {
            return;
        }

        _expressionTimer.Interval = TimeSpan.FromSeconds(hold);
        _expressionTimer.Start();
    }

    /// <summary>回到默认立绘。表情图是整张替换，不回退的话角色会停在最后一个表情上。</summary>
    private void RevertExpression()
    {
        _expressionTimer.Stop();

        if (_currentExpression is null)
        {
            return;
        }

        _currentExpression = null;
        AnimationBadge.Visibility = Visibility.Collapsed;

        // 走默认加载链（含"更换外观"的覆盖值），不直接用 ui.pet_image_path，
        // 否则用户自选的外观会被表情回退冲掉。
        LoadAvatar();
        _host.Logger.Debug("表情已到期，恢复到默认立绘。");
    }

    // ------------------------------------------------------------------ 管线事件

    /// <summary>窗口关闭时释放渲染资源。GL 上下文与模型不释放会留下线程/显存。</summary>
    protected override void OnClosed(EventArgs e)
    {
        try
        {
            _live2d?.Dispose();
            _lipSync?.Dispose();
        }
        catch (Exception ex)
        {
            _host.Logger.Warn($"关闭时释放渲染资源失败：{ex.Message}");
        }

        base.OnClosed(e);
    }

    private void OnPipelineStatus(PipelineStatus status)
    {
        StageText.Text = status.Message;

        // 手册 §4 步骤 5：进入"推理中"时激活"停止生成"按钮。
        StopButton.Visibility = status.CanStopGeneration ? Visibility.Visible : Visibility.Collapsed;

        // LLM 等待首 Token（Thinking）→ Think 动作（StateChange）。
        // 这是"在等模型开口"的唯一可靠信号，比按时间猜要准。
        if (status.Stage == PipelineStage.Thinking)
        {
            _animations?.Request(AnimationType.Think, AnimationPriority.StateChange, "LLM", TimeSpan.FromSeconds(10));
        }

        if (status.Stage == PipelineStage.Idle)
        {
            AnimationBadge.Visibility = Visibility.Collapsed;
        }
    }

    // ------------------------------------------------------------------ 动作调度与口型

    /// <summary>
    /// 把动作请求真正落地到表现层。**只在 UI 线程执行**（由 AnimationManager 保证）。
    ///
    /// <paramref name="motionName"/> 是已经过映射与存在性校验的动作名。
    ///
    /// 当前项目是"静态图 + 表情差分"（决策 Q4），所以这里的实现是：
    /// 按**逻辑动作名**找同名表情图（happy.png / sad.png …），有就切，没有就只更新角标。
    ///
    /// TODO: 接真 Live2D 后替换为
    ///   live2dModel.SetMotion(motionName) 或 VPet 对应的动作播放 API。
    /// </summary>
    private void ApplyAnimation(AnimationType type, string motionName)
    {
        AnimationText.Text = type.ToString();
        AnimationBadge.Visibility = Visibility.Visible;

        // 1) 有真模型 → 直接播动作（motionName 已由 AvatarProfile 映射并校验过存在性）
        if (_live2d is not null)
        {
            _live2d.PlayMotion(motionName);
            _host.Logger.Debug($"Live2D 播放动作：{type} → {motionName}");
            return;
        }

        // 2) 静态图降级：按逻辑动作名找同名表情差分图（happy.png / sad.png …）
        var expressionName = type.ToString().ToLowerInvariant();
        var directory = AppPaths.Resolve(_host.Config.Live2d.ExpressionDirectory);
        var candidate = Path.Combine(directory, expressionName + ".png");

        if (File.Exists(candidate))
        {
            ApplyExpression(expressionName);
        }
        else
        {
            _host.Logger.Debug($"动作 {type}→{motionName} 无对应表情图，仅更新角标。");
        }
    }

    /// <summary>
    /// 把口型开合值写入模型。**只在 UI 线程、每 50ms 最多一次**（由 LipSyncDriver 保证）。
    ///
    /// 有 Live2D 模型时写进模型的嘴型参数；否则静态图没有可动的嘴，只保留数值。
    /// </summary>
    /// <summary>TTS 开始播放时暂停 ASR（回声抑制）。</summary>
    private void SuspendAsrForPlayback()
    {
        try
        {
            /*
             * 按住说话模式下不做回声抑制：
             * 录音只在按住期间进行，松开就结束，不存在"持续监听吃进 TTS 回声"的路径；
             * 反而如果这里把正在进行的录音停掉，用户会丢掉自己那半句话。
             */
            if (_host.Config.Asr.PushToTalk)
            {
                return;
            }

            if (_host.Asr.State != AsrState.Running)
            {
                return;
            }

            _asrPausedForPlayback = true;
            _host.Asr.StopListening();
            _host.Logger.Info("TTS 播放中：已暂停 ASR（回声抑制）。");
        }
        catch (Exception ex)
        {
            _host.Logger.Warn($"暂停 ASR 失败：{ex.Message}");
        }
    }

    /// <summary>播放结束后延迟 400ms 恢复 ASR，避开扬声器余音。</summary>
    private void ScheduleAsrResume()
    {
        // PTT 模式下没有"恢复常开监听"这回事，等用户下一次按键即可。
        if (_host.Config.Asr.PushToTalk)
        {
            _asrPausedForPlayback = false;
            return;
        }

        if (!_asrPausedForPlayback)
        {
            return;
        }

        _asrPausedForPlayback = false;

        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (_asrResumeTimer is null)
            {
                _asrResumeTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher)
                {
                    Interval = TimeSpan.FromMilliseconds(400),
                };
                _asrResumeTimer.Tick += OnAsrResumeTick;
            }

            _asrResumeTimer.Stop();
            _asrResumeTimer.Start();
        }));
    }

    private void OnAsrResumeTick(object? sender, EventArgs e)
    {
        _asrResumeTimer?.Stop();

        try
        {
            if (_host.Asr.State != AsrState.Running)
            {
                _host.Asr.StartListening();
                _host.Logger.Info("TTS 播放结束：已恢复 ASR。");
            }
        }
        catch (Exception ex)
        {
            _host.Logger.Warn($"恢复 ASR 失败：{ex.Message}");
        }
    }
    private void ApplyMouthOpen(double value)
    {
        if (_live2d is not null)
        {
            _live2d.SetMouthOpen(value);
            return;
        }

        var parameter = _animations?.MouthOpenParameter ?? "ParamMouthOpenY";

        if (value <= 0.001)
        {
            _host.Logger.Debug($"MouthOpen({parameter}) → 0（静音/播放结束）。");
        }
    }

    private void OnReplyReady(Core.Models.ChatMessage message)
    {
        ApplyExpression(message.Animation);
        StageText.Text = $"伴侣：{Truncate(message.Content, 60)}";
    }

    private void UpdateStageText()
    {
        var asrText = _host.Config.Asr.PushToTalk
            ? (_host.Asr.State == AsrState.Running
                ? $"按住说话（录音中）"
                : $"按住说话（{_host.Config.Asr.PushToTalkKey}）")
            : _host.Asr.State.ToString();

        StageText.Text = $"运行模式：{_host.RuntimeMode.Mode}｜TTS：{_host.Tts.CurrentEngineName}｜ASR：{asrText}";
    }

    // ------------------------------------------------------------------ 交互

    private async void OnSendClick(object sender, RoutedEventArgs e) => await SendAsync(InputBox.Text);

    private async void OnInputBoxKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            await SendAsync(InputBox.Text);
        }
    }

    private async Task SendAsync(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        InputBox.Clear();
        _host.Idle.NotifyUserActivity();

        try
        {
            var result = await _host.Pipeline.ProcessUserTextAsync(text);
            RefreshBubbles();

            if (!result.Success && !string.IsNullOrWhiteSpace(result.Error))
            {
                StageText.Text = $"未完成：{result.Error}";
            }
        }
        catch (Exception ex)
        {
            // UI 事件处理器里必须兜住异常，否则会以未处理异常结束进程。
            _host.Logger.Error($"发送消息失败：{ex.Message}", ex);
            StageText.Text = "发送失败，详见日志。";
        }
    }

    private async void OnStopClick(object sender, RoutedEventArgs e)
    {
        try
        {
            await _host.Pipeline.StopGenerationAsync();
        }
        catch (Exception ex)
        {
            _host.Logger.Error($"停止生成失败：{ex.Message}", ex);
        }
    }

    /// <summary>
    /// 按下左键：记录起点并捕获鼠标。
    ///
    /// 这里没有用 WPF 的 <c>DragMove()</c>：DragMove 会一直阻塞到鼠标释放，
    /// 释放后收不到 MouseLeftButtonUp，也就无法区分"用户是想拖动"还是"只想点一下"。
    /// 改为手动记录起点 + 在 MouseMove 里按位移阈值判断，两者才能共存。
    ///
    /// 起点记的是**屏幕坐标**而不是 <c>e.GetPosition(this)</c>，
    /// 详见字段声明处的注释（相对坐标会形成反馈回路，导致拖动抽搐）。
    /// </summary>
    private void OnWindowMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_clickThrough || e.ButtonState != MouseButtonState.Pressed)
        {
            return;
        }

        if (!GetCursorPos(out var cursor))
        {
            return;
        }

        _dragStartCursorX = cursor.X;
        _dragStartCursorY = cursor.Y;
        _dragStartWindowPos = new Point(Left, Top);
        _dragActive = true;
        _isDragging = false;

        CaptureMouse();
    }

    // ------------------------------------------------------------------ 角色大小（DPI / 分辨率自适应）

    /// <summary>
    /// 把运行环境的 DPI 与分辨率记进日志。
    ///
    /// 这是"换机器就出问题"的第一手证据：同一块 2560×1440 屏，
    /// 100% 缩放时逻辑分辨率是 2560×1440，175% 时只有 1463×823 ——
    /// 同一个 340×460 DIP 的窗口，占屏比例差一倍多。
    /// </summary>
    private void LogDisplayEnvironment()
    {
        try
        {
            var dpi = VisualTreeHelper.GetDpi(this);
            var work = SystemParameters.WorkArea;

            _host.Logger.Info(
                $"显示环境：DPI {dpi.DpiScaleX * 100:F0}%（{dpi.DpiScaleX:F2}×），" +
                $"主屏 {SystemParameters.PrimaryScreenWidth:F0}×{SystemParameters.PrimaryScreenHeight:F0} DIP" +
                $"（物理约 {SystemParameters.PrimaryScreenWidth * dpi.DpiScaleX:F0}×{SystemParameters.PrimaryScreenHeight * dpi.DpiScaleY:F0} px），" +
                $"工作区 {work.Width:F0}×{work.Height:F0} DIP，虚拟屏 {SystemParameters.VirtualScreenWidth:F0}×{SystemParameters.VirtualScreenHeight:F0} DIP");
        }
        catch (Exception ex)
        {
            _host.Logger.Warn($"读取显示环境失败：{ex.Message}");
        }
    }

    /// <summary>
    /// 按"屏幕自动适配"算一个合适的倍率：
    /// 让角色高度约占工作区的 45%，同时不超过宽度的 60%。
    /// </summary>
    private static double ComputeAutoScale()
    {
        var work = SystemParameters.WorkArea;

        var byHeight = work.Height * 0.45 / BaseHeight;
        var byWidth = work.Width * 0.60 / BaseWidth;

        return Math.Clamp(Math.Min(byHeight, byWidth), MinScale, 1.5);
    }

    /// <summary>
    /// 启动时确定初始倍率，优先级：
    /// 用户在界面上调过的值 → config.yaml 的 ui.scale → 按屏幕自动适配。
    /// </summary>
    private void ApplyInitialScale()
    {
        double scale;
        string source;

        var stored = AppearanceStore.LoadScale();
        var configured = _host.Config.Ui.Scale;

        if (stored is { } userScale)
        {
            scale = userScale;
            source = "用户上次调整（Assets\\appearance.json）";
        }
        else if (configured > 0)
        {
            scale = configured;
            source = "config.yaml 的 ui.scale";
        }
        else
        {
            scale = ComputeAutoScale();
            source = "按屏幕自动适配";
        }

        _host.Logger.Info($"角色大小：{scale:P0}（来源={source}）");
        ApplyScale(scale, persist: false, showBubble: false);
    }

    /// <summary>调整倍率（−/＋ 按钮与 Ctrl+滚轮）。</summary>
    private void AdjustScale(double delta)
    {
        ApplyScale(_scale + delta, persist: true, showBubble: true);
    }

    /// <summary>
    /// 应用倍率：缩放整棵可视树 + 同步窗口尺寸 + 把窗口拉回工作区。
    ///
    /// 缩放以**底边中点**为锚（见下方注释），因此功能栏重新贴到角色正下方时
    /// 位置是稳定的，不需要额外传锚点。
    ///
    /// 上限还受屏幕约束 —— 放大到窗口超出工作区就没有意义了，
    /// 那样角色会有一半在屏幕外，用户也拖不回来。
    /// </summary>
    private void ApplyScale(double scale, bool persist, bool showBubble)
    {
        var work = SystemParameters.WorkArea;

        var maxByScreen = Math.Min(work.Width * 0.95 / BaseWidth, work.Height * 0.95 / BaseHeight);
        var effectiveMax = Math.Max(MinScale, Math.Min(MaxScale, maxByScreen));

        var requested = Math.Round(scale, 2);
        var clamped = Math.Clamp(requested, MinScale, effectiveMax);

        _scale = clamped;

        _rootScale.ScaleX = clamped;
        _rootScale.ScaleY = clamped;


        /*
         * 以**底边中点**为锚缩放：
         * 底边固定不动，于是贴在底边下方的功能栏不需要跟着重新定位；
         * 角色向上、向两侧长大（而不是向右下角长）。
         */
        var bottomCenterX = Left + Width / 2.0;
        var bottomCenterY = Top + Height;

        Width = BaseWidth * clamped;
        Height = BaseHeight * clamped;

        Left = bottomCenterX - Width / 2.0;
        Top = bottomCenterY - Height;

        // 缩放后可能跑出屏幕，拉回工作区
        Left = Math.Clamp(Left, work.Left, Math.Max(work.Left, work.Right - Width));
        Top = Math.Clamp(Top, work.Top, Math.Max(work.Top, work.Bottom - Height));

        /*
         * 功能栏：重新贴到角色正下方。
         *
         * 因为角色是**以底边中点为锚**缩放的（底边 Y 与中心 X 都不变），
         * 重新贴一次得到的位置和之前一样 —— 视觉上功能栏纹丝不动，
         * 按钮也不会跑掉，同时它始终紧贴角色，不会错位。
         */
        _taskBar?.ApplyScale(clamped);

        if (_taskBar is { IsVisible: true })
        {
            _taskBar.ShowBelow(this);
        }

        // 右上角按钮也要跟着倍率缩放，否则角色缩小时它显得特别大
        _cornerButton?.ApplyScale(clamped);
        _cornerButton?.ShowAt(this);

        if (persist)
        {
            AppearanceStore.SaveScale(clamped);
        }

        if (showBubble)
        {
            var note = Math.Abs(clamped - requested) > 0.001 ? "（已受屏幕尺寸限制）" : string.Empty;
            _bubbles.Show("scale", $"角色大小：{clamped:P0}{note}", BubbleLevel.Info, TimeSpan.FromSeconds(2));
        }

        _host.Logger.Info(
            $"角色大小设为 {clamped:P0}，窗口 {Width:F0}×{Height:F0} DIP（{(persist ? "已记住" : "未持久化")}）");
    }

    /// <summary>Ctrl + 滚轮调整角色大小。</summary>
    private void OnWindowMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (_clickThrough)
        {
            return;
        }

        if ((Keyboard.Modifiers & ModifierKeys.Control) == 0)
        {
            return;
        }

        e.Handled = true;
        AdjustScale(e.Delta > 0 ? 0.05 : -0.05);
    }

    /// <summary>拖动：按**屏幕坐标**的位移移动窗口。</summary>
    private void OnWindowMouseMove(object sender, MouseEventArgs e)
    {
        if (!_dragActive)
        {
            return;
        }

        // 鼠标在窗口外松开时收不到 ButtonUp，这里兜底
        if (e.LeftButton != MouseButtonState.Pressed)
        {
            EndDrag();
            return;
        }

        if (!GetCursorPos(out var cursor))
        {
            return;
        }

        // 物理像素 → DIP。每次重新取，跨不同缩放率的显示器时也能跟上。
        var dpi = VisualTreeHelper.GetDpi(this);
        var dpiX = dpi.DpiScaleX <= 0 ? 1 : dpi.DpiScaleX;
        var dpiY = dpi.DpiScaleY <= 0 ? 1 : dpi.DpiScaleY;

        var dx = (cursor.X - _dragStartCursorX) / dpiX;
        var dy = (cursor.Y - _dragStartCursorY) / dpiY;

        // 小于阈值视为手抖，不算拖动 —— 否则轻点一下也会把窗口挪走
        if (!_isDragging && Math.Abs(dx) + Math.Abs(dy) < 4)
        {
            return;
        }

        _isDragging = true;
        Left = _dragStartWindowPos.X + dx;
        Top = _dragStartWindowPos.Y + dy;

        // 软钳制：拖动过程中就保证至少 30% 可见。
        // 不这么做的话，拖到屏幕边缘时窗口会先跑到外面、松手再被拉回来 —— 松手瞬间会"跳"一下。
        // 这里可以安全地每帧钳制，是因为位移用的是绝对屏幕坐标（不会形成反馈回路）。
        ClampKeepVisible();

        // 拖动期间只挪位置：不 Show、不 Activate。
        // 每帧 Activate 会高频抢焦点导致闪烁。
        if (_taskBar is { IsVisible: true })
        {
            _taskBar.MoveBelow(this);
        }

        _cornerButton?.MoveTo(this);
    }

    private void OnWindowMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_dragActive)
        {
            return;
        }

        EndDrag();
    }

    /// <summary>结束拖动：区分"拖动"与"点击"，并只在这一刻做边界钳制。</summary>
    private void EndDrag()
    {
        var wasDragging = _isDragging;

        _dragActive = false;
        _isDragging = false;

        if (IsMouseCaptured)
        {
            ReleaseMouseCapture();
        }

        if (wasDragging)
        {
            ClampKeepVisible();

            if (_taskBar is { IsVisible: true })
            {
                _taskBar.ShowBelow(this);
            }
        }
        else
        {
            /*
             * 没有拖动过的左键单击：现在不做任何事。
             *
             * 打开功能栏改成**右键**（与该桌宠参考实现一致：覆盖态右键出按键栏）。
             * 左键只负责拖动，避免一次误点就把功能栏弹出来。
             */
        }
    }

    /// <summary>
    /// 拖动用的软钳制：只保证窗口至少有 30% 落在工作区内。
    ///
    /// 与 <see cref="ClampIntoWorkArea"/> 的区别：那个要求整个窗口都在工作区内，
    /// 用在改大小的时候；拖动时若也这样，拖到边缘会先跑出去、松手再被拉回来，
    /// 表现为松手瞬间"跳一下"。软钳制允许贴边甚至半出屏，但永远不会让角色丢失。
    /// </summary>
    private void ClampKeepVisible()
    {
        var work = SystemParameters.WorkArea;

        var keepX = Math.Min(Width * 0.3, Width);
        var keepY = Math.Min(Height * 0.3, Height);

        Left = Math.Clamp(Left, work.Left - Width + keepX, work.Right - keepX);
        Top = Math.Clamp(Top, work.Top - Height + keepY, work.Bottom - keepY);
    }

    /// <summary>把窗口拉回工作区，避免拖到屏幕外后找不回来。</summary>
    private void ClampIntoWorkArea()
    {
        var work = SystemParameters.WorkArea;

        Left = Math.Clamp(Left, work.Left, Math.Max(work.Left, work.Right - Width));
        Top = Math.Clamp(Top, work.Top, Math.Max(work.Top, work.Bottom - Height));
    }

    /// <summary>
    /// 右键：弹出/收起功能栏（设置 / 更换外观 / 对话 / 大小 / 恢复默认）。
    ///
    /// 模式切换不再是右键，改由**角色右上角的按钮**负责（独立窗口，透明态下也点得到）。
    /// 这样两种操作各有一个明确入口，不会互相误触。
    /// </summary>
    private void OnWindowMouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_clickThrough)
        {
            // 穿透模式下收不到事件，这里只是防御
            return;
        }

        e.Handled = true;
        ToggleTaskBar();
    }

    /// <summary>切换「透明（穿透）」与「覆盖（可交互）」。</summary>
    public void ToggleClickThrough() => SetClickThrough(!_clickThrough, fromHover: false);

    /// <summary>
    /// 设置交互模式。
    /// </summary>
    /// <param name="fromHover">
    /// 是否由悬停自动触发。只有悬停触发的切换才会在鼠标离开后自动退回透明；
    /// 用户手动切换的模式不会被自动改掉。
    /// </param>
    private void SetClickThrough(bool clickThrough, bool fromHover)
    {
        if (_clickThrough == clickThrough)
        {
            return;
        }

        _clickThrough = clickThrough;
        ApplyClickThrough(_clickThrough);

        if (fromHover)
        {
            _hoverSwitched = clickThrough == false;
        }
        else
        {
            // 这是用户的明确选择，取消"自动退回"的资格
            _hoverSwitched = false;
            _hoverSince = null;
            _awaySince = null;
        }

        if (_clickThrough)
        {
            // 切到透明：重置待发状态。用户此时多半正把光标停在角色上，
            // 必须先移开、再移回来才允许悬停切回覆盖，否则会与手动切换互相打架。
            _hoverArmed = false;
            _hoverSince = null;

            // 穿透后角色点不到，功能栏留着没有意义
            _taskBar?.Hide();
        }

        // 右上角按钮的文字随模式变化；两种模式下它都保持可点
        _cornerButton?.SetMode(_clickThrough);
        _cornerButton?.ShowAt(this);

        /*
         * 不再为模式切换弹气泡。
         *
         * 原因：切换已经有三个**可视化**反馈了 —— 右上角按钮文字从「覆」变「透」、
         * 整体不透明度 100% ↔ 50%、以及点击行为本身。
         * 再弹一条气泡纯属噪音，而且悬停切换很频繁时会刷屏。
         * 切换事实仍然写日志，排障不受影响。
         */
        _host.Logger.Info(
            $"交互模式切换为{(_clickThrough ? "透明（鼠标穿透）" : "覆盖（可交互）")}，来源={(fromHover ? "悬停" : "手动")}");

        // 切换后报一次窗口的屏幕矩形：既是排障线索，也方便自动化验证瞄准
        if (_clickThrough)
        {
            var rect = TryGetWindowScreenRect();
            _host.Logger.Info(rect is null
                ? "角色窗口矩形：暂不可用"
                : $"角色窗口矩形（屏幕物理坐标）：({rect.Value.Left:F0},{rect.Value.Top:F0})-({rect.Value.Right:F0},{rect.Value.Bottom:F0})，" +
                  $"中心=({(rect.Value.Left + rect.Value.Right) / 2:F0},{(rect.Value.Top + rect.Value.Bottom) / 2:F0})");
        }
    }

    /// <summary>
    /// 取角色窗口在屏幕上的矩形（物理像素）。
    ///
    /// 判定范围已改为整窗，因此这里直接报窗口矩形，不再算图片的 Uniform 绘制区域 ——
    /// 后者与用户看到的、能点到的范围不一致，正是"判定范围不对"的来源之一。
    /// </summary>
    private Rect? TryGetWindowScreenRect()
    {
        try
        {
            var topLeft = PointToScreen(new Point(0, 0));
            var bottomRight = PointToScreen(new Point(ActualWidth, ActualHeight));
            return new Rect(topLeft, bottomRight);
        }
        catch
        {
            return null;
        }
    }

    // ------------------------------------------------------------------ 透明态悬停检测

    /// <summary>
    /// 悬停检测循环。
    ///
    /// 之所以要轮询而不是订阅 MouseEnter：<c>WS_EX_TRANSPARENT</c> 会让窗口对鼠标完全透明，
    /// 连 WM_MOUSEMOVE/WM_MOUSELEAVE 都不会收到，WPF 的 MouseEnter 也就永远不会触发。
    /// 因此这里每 100ms 取一次光标位置，做**整窗**命中判定（与用户看到的窗口边界一致）。
    /// </summary>
    private void OnHoverTick()
    {
        if (!IsVisible)
        {
            return;
        }

        var ui = _host.Config.Ui;
        if (!ui.TransparentHoverSwitch)
        {
            return;
        }

        bool over;
        try
        {
            over = IsCursorOnCharacter();
        }
        catch (Exception ex)
        {
            _hoverDiag = $"异常：{ex.Message}";
            return;
        }

        var now = DateTime.UtcNow;

        // 透明态下每 2 秒打一条诊断，便于定位"为什么没触发"。
        // 用 Debug 级别：正常使用时不该刷屏，排查时把 logging.minimum_level 调成 debug 即可。
        if (_clickThrough && (now - _lastHoverDiag).TotalSeconds >= 2)
        {
            _lastHoverDiag = now;
            _host.Logger.Debug($"悬停诊断（透明态）：over={over}，{_hoverDiag}");
        }

        if (_clickThrough)
        {
            _awaySince = null;

            if (!over)
            {
                // 光标离开角色 → 解除待发，之后再次移上来才允许悬停切换。
                _hoverArmed = true;
                _hoverSince = null;
                return;
            }

            if (!_hoverArmed)
            {
                // 还没离开过（例如刚用右键切到透明，光标仍压在角色上）：
                // 不触发切换，否则会和用户的手动切换互相打架。
                _hoverSince = null;
                return;
            }

            _hoverSince ??= now;

            if ((now - _hoverSince.Value).TotalMilliseconds >= Math.Max(0, ui.TransparentHoverDelayMs))
            {
                _hoverSince = null;
                SetClickThrough(false, fromHover: true);
            }

            return;
        }

        // 覆盖态：只有"悬停切过来的"才会在鼠标离开后自动退回透明
        _hoverSince = null;

        if (!_hoverSwitched)
        {
            return;
        }

        var revertSeconds = ui.TransparentAutoRevertSeconds;
        if (revertSeconds <= 0)
        {
            return;
        }

        if (over)
        {
            _awaySince = null;
            return;
        }

        _awaySince ??= now;

        if ((now - _awaySince.Value).TotalSeconds >= revertSeconds)
        {
            _awaySince = null;
            SetClickThrough(true, fromHover: true);
        }
    }

    /// <summary>
    /// 光标是否落在角色窗口内 —— **整窗判定**，不做像素级 alpha 判定。
    ///
    /// 之所以从像素级改成整窗：用户反馈"判定范围不对"。
    /// 像素级判定只把图片里的不透明像素算作"碰到角色"，可角色周围有大量透明留边，
    /// 视觉上明明贴着窗口边缘却不算命中，鼠标移上去没反应，非常反直觉。
    /// 现在只要光标进了窗口矩形就算命中，与用户看到的窗口边界一致。
    /// </summary>
    private bool IsCursorOnCharacter()
    {
        if (!IsVisible)
        {
            _hoverDiag = "窗口不可见";
            return false;
        }

        if (!GetCursorPos(out var screen))
        {
            _hoverDiag = "GetCursorPos 失败";
            return false;
        }

        // PointFromScreen 负责物理像素 → 窗口 DIP 的换算（含 DPI 缩放）
        Point windowPoint;
        try
        {
            windowPoint = PointFromScreen(new Point(screen.X, screen.Y));
        }
        catch (Exception ex)
        {
            _hoverDiag = $"PointFromScreen 失败：{ex.Message}";
            return false;
        }

        var width = ActualWidth;
        var height = ActualHeight;
        if (width <= 0 || height <= 0)
        {
            return false;
        }

        var over = windowPoint.X >= 0 && windowPoint.Y >= 0 &&
                   windowPoint.X < width && windowPoint.Y < height;

        _hoverDiag = $"cursor={screen.X},{screen.Y} window={windowPoint.X:F0},{windowPoint.Y:F0} " +
                     $"size={width:F0}x{height:F0} → {(over ? "窗口内" : "窗口外")}";

        return over;
    }

    private void ToggleTaskBar()
    {
        if (_taskBar is null)
        {
            return;
        }

        if (_taskBar.IsVisible)
        {
            _taskBar.Hide();
            return;
        }

        // 打开前按当前倍率重建尺寸：缩放期间功能栏是冻结的（见 ApplyScale），
        // 这里补上最新倍率，避免长期停留在旧尺寸上。
        _taskBar.ApplyScale(_scale);
        _taskBar.ShowBelow(this);
    }

    private void OpenSettings()
    {
        try
        {
            var settings = new SettingsWindow(_host, _bubbles, ApplyUserAppearanceSettings)
            {
                Owner = IsVisible ? this : null,
            };

            settings.Show();
            settings.Activate();
        }
        catch (Exception ex)
        {
            _host.Logger.Error($"打开设置面板失败：{ex.Message}", ex);
            _bubbles.Show("settings-failed", "打开设置面板失败，详见日志。", BubbleLevel.Error);
        }
    }

    /// <summary>
    /// 应用"外观"类用户设置：气泡显示时长 + 透明态不透明度。
    ///
    /// 来源优先级统一为"设置面板保存的值 → config.yaml"，
    /// 这样设置面板改完能立刻生效，而删掉 Assets\appearance.json 又能回到配置。
    /// </summary>
    public void ApplyUserAppearanceSettings()
    {
        var bubbleSeconds = AppearanceStore.LoadBubbleSeconds() ?? _host.Config.Ui.BubbleDurationSeconds;
        _bubbles.DefaultDuration = TimeSpan.FromSeconds(Math.Max(1, bubbleSeconds));

        _transparentOpacity = Math.Clamp(
            AppearanceStore.LoadTransparentOpacity() ?? _host.Config.Ui.TransparentOpacity,
            0.1,
            1.0);

        // 重新套一次当前模式（不透明度是在 ApplyClickThrough 里生效的）
        ApplyClickThrough(_clickThrough);

        _host.Logger.Info(
            $"外观设置：气泡 {_bubbles.DefaultDuration.TotalSeconds:F0} 秒，透明态不透明度 {_transparentOpacity:P0}");
    }

    /// <summary>更换外观：选一张图片，存到 Assets\appearance.json（不动 config.yaml）。</summary>
    private void ChangeAppearance()
    {
        try
        {
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Title = "选择角色形象图片",
                Filter = "图片文件|*.png;*.jpg;*.jpeg;*.bmp;*.gif|所有文件|*.*",
                CheckFileExists = true,
            };

            var assets = AppPaths.Resolve("Assets");
            if (Directory.Exists(assets))
            {
                dialog.InitialDirectory = assets;
            }

            if (dialog.ShowDialog(this) != true)
            {
                return;
            }

            if (!AppearanceStore.SaveImagePath(dialog.FileName))
            {
                _bubbles.Show("appearance-save-failed", "外观保存失败，详见日志。", BubbleLevel.Error);
                return;
            }

            LoadAvatar();
            _host.Logger.Info($"用户更换外观：{dialog.FileName}");

            _bubbles.Show(
                "appearance-changed",
                $"外观已更换为 {Path.GetFileName(dialog.FileName)}。可在功能栏点「↺」恢复默认。",
                BubbleLevel.Info,
                TimeSpan.FromSeconds(8));
        }
        catch (Exception ex)
        {
            _host.Logger.Error($"更换外观失败：{ex.Message}", ex);
            _bubbles.Show("appearance-failed", "更换外观失败，详见日志。", BubbleLevel.Error);
        }
    }

    private void ResetAppearance()
    {
        try
        {
            AppearanceStore.SaveImagePath(null);

            // 大小也一起恢复：用户的直觉是"恢复默认"，不该只恢复一半
            AppearanceStore.SaveScale(null);

            LoadAvatar();

            var configured = _host.Config.Ui.Scale;
            ApplyScale(configured > 0 ? configured : ComputeAutoScale(), persist: false, showBubble: false);

            _host.Logger.Info($"用户恢复默认外观与大小（已清除 Assets\\appearance.json 中的覆盖），当前大小 {_scale:P0}。");
            _bubbles.Show(
                "appearance-reset",
                $"已恢复为 config.yaml 里配置的默认外观，大小 {_scale:P0}。",
                BubbleLevel.Info,
                TimeSpan.FromSeconds(6));
        }
        catch (Exception ex)
        {
            _host.Logger.Error($"恢复默认外观失败：{ex.Message}", ex);
        }
    }

    /// <summary>对话：确保可交互并把焦点放到输入框。</summary>
    private void OpenChat()
    {
        if (_clickThrough)
        {
            SetInteractive(true);
        }

        InputPanel.Visibility = Visibility.Visible;
        InputBox.Focus();

        _bubbles.Show(
            "chat-ready",
            "在角色下方的输入框里输入内容，按回车发送。",
            BubbleLevel.Info,
            TimeSpan.FromSeconds(8));
    }

    private static string Truncate(string value, int max)
        => value.Length <= max ? value : value[..max] + "…";

    // ------------------------------------------------------------------ Win32 鼠标穿透

    private void ApplyClickThrough(bool clickThrough)
    {
        try
        {
            var handle = new WindowInteropHelper(this).Handle;
            if (handle == IntPtr.Zero)
            {
                return;
            }

            var exStyle = GetWindowLong(handle, GwlExStyle);

            if (clickThrough)
            {
                exStyle |= WsExTransparent | WsExLayered;
            }
            else
            {
                exStyle &= ~WsExTransparent;
                exStyle |= WsExLayered;
            }

            SetWindowLong(handle, GwlExStyle, exStyle);

            /*
             * 穿透时隐藏输入区。
             *
             * 必须用 Hidden 而不是 Collapsed：
             * 输入区在 Grid 的最后一行（Height=Auto），Collapsed 会让那一行高度归零，
             * 上面角色所在的 * 行随之变高，图片被 Uniform 拉伸 ——
             * 表现为"右键切一下模式，角色就变大变小一下"。
             * Hidden 保留占位、只是不绘制，角色尺寸在两种模式下完全一致。
             */
            InputPanel.Visibility = clickThrough ? Visibility.Hidden : Visibility.Visible;

            /*
             * 用不透明度让两种模式一眼可辨：
             *   覆盖 = 完全不透明（1.0），透明 = 降到 _transparentOpacity（默认 0.5）。
             * 只改窗口样式的话两种模式看起来完全一样，用户要点一下才知道自己在哪种模式。
             */
            RootGrid.Opacity = clickThrough
                ? Math.Clamp(_transparentOpacity, 0.1, 1.0)
                : 1.0;

            _host.Logger.Info($"鼠标穿透已{(clickThrough ? "开启" : "关闭")}。");

            // 记录实际布局尺寸：用于确认"切换模式不会改变角色大小"。
            // 角色图片是 Uniform 拉伸的，一旦所在行的高度变了就会跟着缩放。
            _host.Logger.Info(
                $"布局：角色图片元素 {AvatarImage.ActualWidth:F0}×{AvatarImage.ActualHeight:F0}，" +
                $"输入区{(InputPanel.Visibility == Visibility.Visible ? "显示" : "隐藏但占位")}（高 {InputPanel.ActualHeight:F0}），" +
                $"气泡区高 {(BubbleList.Parent as FrameworkElement)?.ActualHeight ?? 0:F0}，" +
                $"整体不透明度 {RootGrid.Opacity:P0}。");
        }
        catch (Exception ex)
        {
            _host.Logger.Warn($"设置鼠标穿透失败：{ex.Message}");
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    /// <summary>取光标屏幕坐标（物理像素）。穿透态下收不到鼠标事件，只能主动查。</summary>
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out NativePoint point);
}
