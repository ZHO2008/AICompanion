using AICompanion.Core.Abstractions;

namespace AICompanion.App.Services;

/// <summary>混合显卡一致性判定结果。</summary>
public enum GpuVerdict
{
    /// <summary>信息不足 / 厂商识别不出来，不做判定（也不写注册表，只提示）。</summary>
    Unknown = 0,

    /// <summary>GL 与桌面合成在同一块显卡上，正常。</summary>
    Consistent = 1,

    /// <summary>不一致：共享纹理路径不可用，Live2D 会"渲染成功但看不见"。</summary>
    Mismatch = 2,

    /// <summary>
    /// 可能不一致：同厂商双卡但型号明显不同（例如 Intel 核显 + Intel Arc 独显）。
    /// 没有实测证据表明这种组合一定失败，因此**只记录、只提示，不写注册表**。
    /// </summary>
    MaybeMismatch = 3,
}

/// <summary>判定结论 + 中文理由（日志与界面共用同一句，避免两套说法）。</summary>
public sealed record GpuDecision(GpuVerdict Verdict, string Reason)
{
    public bool IsMismatch => Verdict == GpuVerdict.Mismatch;

    public bool IsMaybeMismatch => Verdict == GpuVerdict.MaybeMismatch;

    /// <summary>是否应该自动修（只有确定的不一致才写注册表）。</summary>
    public bool NeedsFix => Verdict == GpuVerdict.Mismatch;
}

/// <summary>
/// 混合显卡（iGPU + dGPU）下 Live2D 不可见的自动检测与自动修复。
///
/// 背景（本机实测，2026-10）：
///   AMD Radeon 780M（核显）+ NVIDIA GeForce RTX 5060 Laptop。
///   GL 上下文落在 AMD 上时，渲染器就绪、帧数在涨、FBO 探针也能读到 15.6% 不透明像素，
///   但窗口里什么都看不到（角色区像素 0.31%）。给本 exe 写入 GpuPreference=2（高性能）
///   重启后 GL 落到 NVIDIA，同一份代码角色区像素 13.8%，正常显示。
///
/// 判定为什么不是"GL 厂商 != 主显示适配器厂商"这么简单：
///   在本机实测（EnumDisplayMonitors + EnumDisplayDevices）主显示适配器就是 **AMD 780M**，
///   而 GL 默认也落在 AMD 780M —— 两者厂商相同，纯按"GL 与显示厂商是否一致"判会得出
///   "一致，无需处理"，而这个组合恰恰是坏的。真正的差异在**共享纹理的互操作路径**上：
///   GLWpfControl 用 WGL_NV_DX_interop（wglDXOpenDeviceNV/wglDXRegisterObjectNV，见
///   GLWpfControl.dll 里的导入名）把 GL 纹理交给 WPF 的 D3DImage，这条扩展只有 NVIDIA 驱动实现；
///   GL 落在 AMD/Intel 上时它静默不可用 —— 于是"画出来了，但合成不进去"。
///   所以判定表里"有 NVIDIA 卡、GL 却不在 NVIDIA 上"就是不一致（第 4 条）；
///   "GL 与主显示适配器厂商不同"不能再单独当唯一依据，否则本机会永久性误报
///   （GL=NVIDIA、显示=AMD 是可用的正常组合，实测角色可见），它作为第 5 条只覆盖
///   "没有 NVIDIA 卡的跨厂商双卡"（Intel 核显 + AMD 独显）。
///
/// 判定表（纯函数 <see cref="Decide"/>，可脱离 WPF 单测）：
///   1. 没有 GL 信息                       → Unknown（等第一帧，不写注册表）
///   2. 识别到的显卡 ≤ 1 块                → Consistent（纯 Intel / 纯 AMD 核显机器一律不误报）
///   3. GL 厂商识别不出来                  → Unknown（Microsoft Basic Render Driver / 空串；
///                                          不写注册表、只提示，避免误伤）
///   4. 有 NVIDIA 卡、GL 却不在它上面       → Mismatch（本机实测的失败条件，自动修复）
///   5. GL 不在 NVIDIA 上但厂商 ≠ 显示厂商    → Mismatch（Intel 核显 GL vs AMD 独显合成等，自动修复）
///   6. 厂商相同但型号明显不同              → MaybeMismatch（同厂双卡；只记录、只提示，不写注册表）
///   7. 其余                               → Consistent（含 GL=NVIDIA + 核显出屏的 Optimus 正常组合）
///
/// 线程契约：<see cref="Start"/> / <see cref="StatusLine"/> 在 UI 线程调用，
/// <see cref="ReportGlAdapter"/> 由 Live2D 的渲染线程回调；内部全部加锁，不抛异常。
/// </summary>
public sealed class HybridGpuAdvisor
{
    /// <summary>一次性气泡的 key（同一 key 重复 Show 只覆盖，不会堆成墙）。</summary>
    public const string BubbleKey = "gpu-hybrid-preference";

    /// <summary>写不了注册表时给出的可操作步骤，全文只此一处，避免两处文案漂移。</summary>
    public const string ManualSteps = "设置 → 系统 → 屏幕 → 显示卡 → 图形设置 → 选择本程序 → 高性能";

    private readonly object _sync = new();
    private readonly IBubbleNotifier _bubbles;

    private IAppLogger _logger;

    private IReadOnlyList<GpuAdapterInfo>? _adapters;
    private GpuAdapterInfo? _displayAdapter;

    private string? _glVendor;
    private string? _glRenderer;

    private GpuDecision? _decision;
    private GpuPreferenceOutcome? _preference;

    /// <summary>修复只做一次：写入（或确认用户已设过）之后，同一会话不再重复尝试。</summary>
    private bool _fixAttempted;

    private bool _bubbleShown;
    private bool _inventoryLogged;

    public HybridGpuAdvisor(IAppLogger logger, IBubbleNotifier bubbles)
    {
        _logger = logger.ForCategory("Gpu");
        _bubbles = bubbles;
    }

    /// <summary>
    /// 换掉内部日志器：启动早期（配置还没解析）用的是引导日志器，
    /// 之后日志目录可能切换。改绑后每次写日志都走当前 sink，
    /// 与 <see cref="BubbleNotifier.SetLogger"/> 同样的理由。
    /// </summary>
    public void SetLogger(IAppLogger logger)
    {
        if (logger is null)
        {
            return;
        }

        _logger = logger.ForCategory("Gpu");
    }

    /// <summary>当前判定结论（还没拿到 GL 信息时为 null）。</summary>
    public GpuDecision? Decision
    {
        get
        {
            lock (_sync)
            {
                EnsureProbe();
                return _decision;
            }
        }
    }

    /// <summary>本次会话对注册表做了什么（只读展示用）。</summary>
    public GpuPreferenceOutcome? PreferenceOutcome
    {
        get
        {
            lock (_sync)
            {
                return _preference;
            }
        }
    }

    /// <summary>
    /// 设置面板「诊断」页那一行的文本，形如：
    /// <c>GL=AMD Radeon 780M Graphics / 显示=AMD Radeon 780M Graphics（不一致，已自动修复，重启生效）</c>
    /// </summary>
    public string StatusLine
    {
        get
        {
            lock (_sync)
            {
                EnsureProbe();

                var displayName = _displayAdapter?.Name ?? "未识别";
                var glName = string.IsNullOrWhiteSpace(_glVendor) && string.IsNullOrWhiteSpace(_glRenderer)
                    ? "等待 OpenGL 上下文"
                    : GlAdapterName(_glVendor, _glRenderer);

                return $"GL={glName} / 显示={displayName}（{VerdictText()}）";
            }
        }
    }

    /// <summary>
    /// 启动自检：枚举显卡 + 主显示适配器并记日志。
    /// 不依赖 GL（GL 信息要等 Live2D 画出第一帧，由 <see cref="ReportGlAdapter"/> 回填）。
    /// </summary>
    public void Start()
    {
        lock (_sync)
        {
            EnsureProbe();
            LogInventory();
        }
    }

    /// <summary>
    /// 由 Live2DRenderer 在渲染线程上回调，带上已解析的 GL_VENDOR / GL_RENDERER。
    /// 第一次回调时完成判定；修复动作整个进程只做一次。
    /// </summary>
    public void ReportGlAdapter(string glVendor, string glRenderer)
    {
        lock (_sync)
        {
            _glVendor = glVendor;
            _glRenderer = glRenderer;

            if (_decision is not null)
            {
                // Live2D 换模型/重建时可能再来一次：只记 Debug，不重复判定、不重复弹气泡。
                _logger.Debug(
                    $"GL 适配器再次上报（Vendor={glVendor}，Renderer={glRenderer}），已有结论，跳过判定。");
                return;
            }

            EnsureProbe();
            Evaluate();
        }
    }

    // ================================================================== 判定

    /// <summary>
    /// 纯函数判定表（不碰注册表、不碰 WPF），可直接喂字符串做单元级验证。
    ///
    /// 判定表（从上到下第一条命中即返回）：
    ///   1. 没有 GL 信息                          → Unknown（等第一帧，不写注册表）
    ///   2. 识别到的显卡 ≤ 1 块                   → Consistent（纯核显机器：只有 Intel/AMD 核显时一律不写）
    ///   3. GL 厂商识别不出来（MS Basic Render/空）→ Unknown（只提示，不写；避免误伤）
    ///   4. 本机有 NVIDIA 卡、GL 却不在它上面      → Mismatch（本机实测的失败条件，见类注释）
    ///   5. GL 不在 NVIDIA 上、且与显示适配器不同厂 → Mismatch（如 Intel 核显 GL + AMD 独显合成）
    ///   6. 厂商相同但型号明显不同                 → MaybeMismatch（同厂双卡：只记录、只提示，不写）
    ///   7. 其余                                  → Consistent（含 GL=NVIDIA + 核显出屏的 Optimus 组合）
    ///
    /// 厂商比较同时用 GL_VENDOR + GL_RENDERER 两处字符串（见 <see cref="GpuInventory.VendorOf"/>），
    /// 型号比较用 <see cref="GpuInventory.SameDevice"/>（模糊匹配，容忍 (R)/(TM)/PNP 后缀差异）。
    /// </summary>
    public static GpuDecision Decide(
        IReadOnlyList<GpuAdapterInfo> adapters,
        GpuAdapterInfo? displayAdapter,
        string? glVendor,
        string? glRenderer)
    {
        // 1. 还没有 GL 信息
        if (string.IsNullOrWhiteSpace(glVendor) && string.IsNullOrWhiteSpace(glRenderer))
        {
            return new GpuDecision(GpuVerdict.Unknown, "尚未取得 OpenGL 上下文（Live2D 还没画出第一帧）");
        }

        // 两处字符串一起看：只匹配一处会在"厂商与渲染器自报不一致"的机器上误判。
        var gl = GpuInventory.VendorOf($"{glVendor} {glRenderer}");
        var glName = GlAdapterName(glVendor, glRenderer);
        var glLabel = GpuInventory.VendorLabel(gl);
        var displayName = displayAdapter?.Name ?? "（未识别）";
        var displayVendor = displayAdapter?.Vendor ?? GpuVendor.Unknown;
        var displayLabel = GpuInventory.VendorLabel(displayVendor);

        // 2. 单显卡：GL 与桌面合成必然在同一适配器上（纯 Intel / 纯 AMD 核显机器绝不能误报）
        if (adapters.Count <= 1)
        {
            return new GpuDecision(
                GpuVerdict.Consistent,
                $"本机只识别到 {adapters.Count} 块显卡（{GpuInventory.DescribeAdapters(adapters)}），" +
                $"GL 落在 {glName} 上也只可能在同一块适配器上，无需处理");
        }

        // 3. 识别不出厂商：不猜、不写注册表，只提示（Microsoft Basic Render Driver / 空串都走这里）
        if (gl is GpuVendor.Unknown or GpuVendor.Other)
        {
            return new GpuDecision(
                GpuVerdict.Unknown,
                $"GL 厂商无法识别（GL_VENDOR=\"{glVendor}\"，GL_RENDERER=\"{glRenderer}\"，归为{glLabel}），" +
                "未做自动修复，避免误伤；若角色不显示，请手动设置高性能显卡");
        }

        var nvidia = adapters.FirstOrDefault(a => a.IsNvidia);

        // 4. 有 NVIDIA 卡、GL 却不在它上面 —— 本机实测的失败条件
        if (nvidia is not null && gl != GpuVendor.Nvidia)
        {
            var reason = displayVendor == GpuVendor.Nvidia
                ? $"GL 落在 {glName}（{glLabel}）上，而主显示适配器是 {displayName}（{displayLabel}）—— " +
                  "GL 与合成不在同一块显卡，跨适配器共享纹理（GL → D3DImage）会静默失败"
                : $"本机有 NVIDIA 显卡（{nvidia.Name}），但 GL 落在 {glName}（{glLabel}）上 —— " +
                  "GLWpfControl 通过 WGL_NV_DX_interop 把 GL 纹理共享给 D3DImage，这条扩展只有 NVIDIA 驱动提供；" +
                  "此时渲染器与 FBO 探针都正常，但窗口里看不到角色";

            return new GpuDecision(GpuVerdict.Mismatch, reason);
        }

        // 5. 没有 NVIDIA 卡（或 GL 已在 NVIDIA 上）时，看"GL 厂商 ≠ 主显示适配器厂商"：
        //    典型是 Intel 核显 GL + AMD 独显合成。
        //    注意排除 GL 已在 NVIDIA 上的情况：NVIDIA 渲染 + 核显出屏（Optimus）是**可用**的，
        //    本机实测（GL=NVIDIA、显示=AMD 780M）角色正常可见，判成不一致会永久误报。
        if (gl != GpuVendor.Nvidia &&
            displayVendor is not (GpuVendor.Unknown or GpuVendor.Other) &&
            displayVendor != gl)
        {
            return new GpuDecision(
                GpuVerdict.Mismatch,
                $"GL 落在 {glName}（{glLabel}）上，主显示适配器却是 {displayName}（{displayLabel}）—— " +
                "跨厂商合成，共享纹理（GL → D3DImage）不可靠");
        }

        // 6. 厂商相同但型号明显不同：同厂双卡，如实记为"可能不一致"，不自动写注册表
        if (displayVendor == gl && !GpuInventory.SameDevice(glName, displayName))
        {
            return new GpuDecision(
                GpuVerdict.MaybeMismatch,
                $"GL 落在 {glName} 上，主显示适配器是 {displayName}：同属 {glLabel} 但型号不同（同厂双卡），" +
                "可能不一致；没有实测证据表明这种组合一定失败，因此只提示不自动修改");
        }

        // 7. 一致
        // 注意措辞：GL 在 NVIDIA、显示在核显（Optimus）时两块适配器确实不同厂商，
        // 但这个组合实测可用，不能写"与主显示适配器一致"（会自相矛盾）。
        if (gl == GpuVendor.Nvidia && displayVendor != GpuVendor.Nvidia)
        {
            return new GpuDecision(
                GpuVerdict.Consistent,
                $"GL 落在 NVIDIA 显卡上（{glName}）；主显示适配器是 {displayName}（{displayLabel}），" +
                "属于「独显渲染 + 核显出屏」的常规组合，共享纹理路径可用");
        }

        return new GpuDecision(
            GpuVerdict.Consistent,
            $"GL 落在 {glName}（{glLabel}）上，与主显示适配器 {displayName}（{displayLabel}）匹配，共享纹理路径可用");
    }

    /// <summary>GL 适配器的可读名字：去掉 Renderer 里 <c>/PCIe/SSE2</c> 这类后缀。</summary>
    public static string GlAdapterName(string? glVendor, string? glRenderer)
    {
        if (!string.IsNullOrWhiteSpace(glRenderer))
        {
            var slash = glRenderer.IndexOf('/');

            if (slash > 0)
            {
                return glRenderer[..slash].Trim();
            }

            return glRenderer.Trim();
        }

        return string.IsNullOrWhiteSpace(glVendor) ? "（未识别）" : glVendor.Trim();
    }

    // ================================================================== 修复

    private void Evaluate()
    {
        var adapters = _adapters ?? Array.Empty<GpuAdapterInfo>();
        var decision = Decide(adapters, _displayAdapter, _glVendor, _glRenderer);
        _decision = decision;

        var glVendor = GpuInventory.VendorOf($"{_glVendor} {_glRenderer}");
        var glName = GlAdapterName(_glVendor, _glRenderer);
        var displayName = _displayAdapter?.Name ?? "（未识别）";
        var displayLabel = _displayAdapter is null
            ? GpuInventory.VendorLabel(GpuVendor.Unknown)
            : GpuInventory.VendorLabel(_displayAdapter.Vendor);

        // 日志里一律写全厂商名（Intel / NVIDIA / AMD），不直接甩驱动自报名
        // ——后者是 "ATI Technologies Inc." 这种，用户看不懂。
        var summary =
            $"GL 适配器={glName}（{GpuInventory.VendorLabel(glVendor)}）｜主显示适配器={displayName}（{displayLabel}）";

        switch (decision.Verdict)
        {
            case GpuVerdict.Consistent:
                _logger.Info($"一致性判定：{summary} ⇒ 一致，无需处理。依据：{decision.Reason}");
                return;

            case GpuVerdict.Unknown:
                _logger.Warn($"一致性判定：{summary} ⇒ 无法判定，未自动修复。依据：{decision.Reason}");

                // "只提示"：多显卡机器上识别不出 GL 厂商时，给用户手动步骤，绝不写注册表。
                if (adapters.Count > 1)
                {
                    ShowBubbleOnce(
                        BubbleLevel.Warning,
                        "检测到多块显卡，但没能识别出角色用的是哪一块（未自动修改任何设置）。" +
                        $"若角色不显示，请到 {ManualSteps}，然后重启程序。",
                        TimeSpan.FromSeconds(18));
                }

                return;

            case GpuVerdict.MaybeMismatch:
                _logger.Warn($"一致性判定：{summary} ⇒ 可能不一致（同厂双卡，型号不同）。依据：{decision.Reason}");
                ShowBubbleOnce(
                    BubbleLevel.Warning,
                    "检测到双显卡，角色可能没有落在显示用的那块显卡上（同厂商、不同型号）。" +
                    $"程序未自动修改设置；若角色不显示，请到 {ManualSteps}，然后重启程序。",
                    TimeSpan.FromSeconds(18));
                return;

            default:
                _logger.Warn($"一致性判定：{summary} ⇒ 不一致：{decision.Reason}");
                ApplyFix();
                return;
        }
    }

    private void ApplyFix()
    {
        if (_fixAttempted)
        {
            return;
        }

        _fixAttempted = true;

        var exePath = UserGpuPreferences.CurrentExecutablePath();
        var outcome = UserGpuPreferences.EnsureHighPerformance(exePath);
        _preference = outcome;

        switch (outcome.State)
        {
            case GpuPreferenceState.Written:
                _logger.Info(
                    $"已为当前程序设置高性能显卡偏好：{UserGpuPreferences.HighPerformanceValue} " +
                    $@"（HKCU\{UserGpuPreferences.KeyPath} → {outcome.ValuePath}），重启后 GL 会落到 NVIDIA 显卡，" +
                    "角色即可正常显示。本次运行仍按原来的显卡渲染，不需要重启进程。");
                ShowBubbleOnce(
                    BubbleLevel.Info,
                    "检测到双显卡：已为程序设置「高性能显卡」，重启后角色即可正常显示。",
                    TimeSpan.FromSeconds(15));
                break;

            case GpuPreferenceState.AlreadyHighPerformance:
                _logger.Info(
                    $"该程序的显卡偏好已存在相同值（{outcome.ExistingValue}），本次不重复写入。" +
                    "如果本次 OpenGL 仍不在 NVIDIA 上，请重启程序后再确认（图形偏好只在进程启动时生效）。");
                ShowBubbleOnce(
                    BubbleLevel.Info,
                    "检测到双显卡：本程序已是「高性能显卡」偏好，重启程序后角色即可正常显示。",
                    TimeSpan.FromSeconds(12));
                break;

            case GpuPreferenceState.SetByUser:
                _logger.Info(
                    $"该程序的显卡偏好已存在（{outcome.ExistingValue}），判定为用户手动设置，跳过不覆盖。" +
                    $"如需自动修复，请把它改成「高性能（GpuPreference=2;）」，或手动走：{ManualSteps}。");
                ShowBubbleOnce(
                    BubbleLevel.Warning,
                    $"检测到双显卡，但本程序的显卡偏好已由你手动设置（{outcome.ExistingValue}），程序没有覆盖它。" +
                    $"若角色不显示，请到 {ManualSteps}，然后重启程序。",
                    TimeSpan.FromSeconds(18));
                break;

            default:
                _logger.Warn(
                    $"写入显卡偏好失败（{outcome.Error ?? "未知原因"}），已降级为提示，不影响启动。" +
                    $"请手动设置：{ManualSteps}，然后重启程序。");
                ShowBubbleOnce(
                    BubbleLevel.Warning,
                    $"检测到双显卡，但自动设置显卡偏好失败（{outcome.Error ?? "未知原因"}）。" +
                    $"请手动设置：{ManualSteps}，然后重启程序。",
                    TimeSpan.FromSeconds(20));
                break;
        }
    }

    private void ShowBubbleOnce(BubbleLevel level, string message, TimeSpan duration)
    {
        if (_bubbleShown)
        {
            return;
        }

        _bubbleShown = true;

        try
        {
            _bubbles.Show(BubbleKey, message, level, duration);
        }
        catch (Exception ex)
        {
            _logger.Warn($"弹出显卡提示气泡失败（不影响运行）：{ex.Message}");
        }
    }

    // ================================================================== 探测

    /// <summary>枚举适配器与主显示适配器，只做一次。</summary>
    private void EnsureProbe()
    {
        if (_adapters is not null)
        {
            return;
        }

        try
        {
            _adapters = GpuInventory.ListAdapters();
            _displayAdapter = GpuInventory.FindPrimaryDisplayAdapter();
        }
        catch (Exception ex)
        {
            // 探测失败 = 清单为空，后续判定会走"≤1 块 → 一致"，不会写注册表。
            _logger.Warn($"显卡探测失败（不影响其他功能）：{ex.Message}");
            _adapters = Array.Empty<GpuAdapterInfo>();
            _displayAdapter = null;
        }
    }

    private void LogInventory()
    {
        if (_inventoryLogged)
        {
            return;
        }

        _inventoryLogged = true;

        try
        {
            var adapters = _adapters ?? Array.Empty<GpuAdapterInfo>();
            var displayName = _displayAdapter?.Name ?? "未识别";
            var displayVendor = _displayAdapter is null
                ? "未知厂商"
                : GpuInventory.VendorLabel(_displayAdapter.Vendor);

            if (adapters.Count == 0)
            {
                _logger.Warn("显卡自检：没能识别到任何显卡（注册表与 GDI 两条路都读不到），跳过一致性判定。");
                return;
            }

            _logger.Info(
                $"显卡自检：识别到 {adapters.Count} 块显卡 —— {GpuInventory.DescribeAdapters(adapters)}；" +
                $"主显示适配器：{displayName}（{displayVendor}）。");

            if (adapters.Count <= 1)
            {
                _logger.Info(
                    "显卡自检：只有一块显卡（单核显 / 单独显），GL 与桌面合成必然在同一适配器上，" +
                    "不需要自动修复，也不会写注册表。");
                return;
            }

            _logger.Debug(
                "显卡自检：多显卡机器，一致性判定要等 Live2D 的第一帧（GL_VENDOR/GL_RENDERER）回填后才能下结论。");
        }
        catch (Exception ex)
        {
            _logger.Warn($"显卡自检日志写入失败（不影响运行）：{ex.Message}");
        }
    }

    private string VerdictText()
    {
        if (_decision is null)
        {
            return "判定进行中";
        }

        if (_decision.Verdict == GpuVerdict.Consistent)
        {
            return "一致，无需处理";
        }

        if (_decision.Verdict == GpuVerdict.MaybeMismatch)
        {
            return "可能不一致（同厂双卡，型号不同；只提示，未自动修复）";
        }

        if (!_decision.IsMismatch)
        {
            return "GL 厂商无法识别，未自动修复";
        }

        return _preference?.State switch
        {
            GpuPreferenceState.Written => "不一致，已自动修复，重启生效",
            GpuPreferenceState.AlreadyHighPerformance => "不一致，偏好已存在，重启生效",
            GpuPreferenceState.SetByUser => "不一致，已由用户设置偏好，未覆盖",
            GpuPreferenceState.WriteFailed => "不一致，自动修复失败，请在图形设置里手动选「高性能」",
            _ => "不一致",
        };
    }
}
