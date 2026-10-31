using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Media;
using Live2DCSharpSDK.App;
using Live2DCSharpSDK.Framework;
using Live2DCSharpSDK.Framework.Motion;
using Live2DCSharpSDK.OpenGL;
using Live2DCSharpSDK.WPF;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;
using OpenTK.Wpf;
using AICompanion.Core.Abstractions;

namespace AICompanion.App.Animation;

/// <summary>
/// Live2D 渲染器：把 Cubism 模型渲进一个可以放进 WPF 可视树的控件。
///
/// 设计约束：
///   1. **失败必须可退**：任何一步出错都返回 null / 不抛出，主窗口继续用静态图（决策 Q4 的降级链完整保留）；
///   2. **只在 UI 线程使用**：GL 控件与模型操作都由主窗口在 UI 线程调用；
///   3. 口型与动作只暴露两个方法：<see cref="PlayMotion"/> / <see cref="SetMouthOpen"/>，
///      正好替换 AnimationManager 里那两个 TODO。
///
/// 与 tools/Live2DPoc 的差别：这里不自己开窗口、不画诊断文字，只提供 View。
/// </summary>
public sealed class Live2DRenderer : IDisposable
{
    /// <summary>CubismFramework.StartUp 全进程只能调一次。</summary>
    private static bool _frameworkStarted;
    private static readonly object FrameworkSync = new();

    private readonly IAppLogger _logger;
    private readonly GLWpfControl _control;
    private readonly LAppDelegate _lapp;
    private readonly LAppModel _model;
    private readonly string _mouthParameter;

    /// <summary>模型是否已经画过至少一帧（贴图 id 之后才有效）。</summary>
    private bool _drawnOnce;

    /// <summary>是否已经给贴图补过 mipmap。</summary>
    private bool _texturesOptimized;

    /// <summary>是否已经打印过 GL 环境信息。</summary>
    private bool _glInfoLogged;

    /// <summary>最小帧间隔（毫秒）：约 30 fps（桌宠够用，CPU 也降一半）。</summary>
    private const double MinFrameIntervalMs = 33.0;

    /// <summary>
    /// <see cref="MinFrameIntervalMs"/> 换算成 <see cref="Stopwatch"/> 计时单位，只在类型初始化时算一次。
    /// 以前是每帧现算 <c>(now - last) * 1000.0 / Frequency</c>（两次 double 运算 + 一次除法）。
    /// </summary>
    private static readonly long MinFrameTicks = (long)(MinFrameIntervalMs / 1000.0 * Stopwatch.Frequency);

    /// <summary>每帧都要用的清屏色。Color4 是结构体，但静态字段省掉每帧重新构造四个分量。</summary>
    private static readonly Color4 TransparentColor = new(0f, 0f, 0f, 0f);

    /// <summary>上一帧的时间戳（Stopwatch 计时单位）。</summary>
    private long _lastFrameTicks;

    /// <summary>用户手动微调（模型矩阵单位）。0 表示保持自动居中。</summary>
    private double _userTranslation;

    /// <summary>已经实际应用到模型矩阵上的平移量，用于增量更新。</summary>
    private double _appliedTranslation;

    private bool _rendering;
    private bool _fbProbeDone;

    /// <summary>
    /// 待应用的视口，打包成 <c>(width &lt;&lt; 32) | height</c>，0 表示没有待办。
    ///
    /// 为什么打包：UI 线程写、渲染线程读并清零。分成两个 int 字段时，
    /// "读宽度 → 被改写 → 读高度 → 清零"会拼出新旧混用的尺寸，并把最后一次
    /// SizeChanged 的结果丢掉（视口就一直停在错的那个尺寸）。打包成一个 long
    /// 后 <see cref="Interlocked.Exchange{T}(ref T, T)"/> 一次读写完成，不可能撕裂。
    /// </summary>
    private long _pendingViewport;

    /// <summary>待应用的平移量（double 的位模式）。<see cref="NoPendingTranslation"/> 表示没有待办。</summary>
    private long _pendingTranslationBits = NoPendingTranslation;

    /// <summary>NaN 的位模式：一份合法的平移量不可能是 NaN，因此可以当"没有待办"的哨兵。</summary>
    private const long NoPendingTranslation = 0x7FF8_0000_0000_0000L;

    /// <summary>探针换算前的兜底视口边长（首帧还没登记过尺寸时用）。</summary>
    private const int ProbeFallbackSize = 256;

    /// <summary>
    /// 探针 FBO 的单边上限。探针只回答"这一帧 GL 到底画出东西没有"，
    /// 没必要按整块屏幕读回：按最长边等比缩到 ≤320 后，
    /// GPU 纹理与 CPU 读回缓冲从 全宽×全高×4 降到 ≤ 320²×4 = 400 KB。
    /// </summary>
    private const int ProbeMaxDimension = 320;

    private int _probeWidth;
    private int _probeHeight;
    private bool _disposed;
    private int _mouthIndex = -1;
    private double _mouth;

    /// <summary>渲染异常计数：前 3 次打 Warn，之后只计数不再刷日志。</summary>
    private int _renderErrorCount;

    private Live2DRenderer(
        IAppLogger logger,
        GLWpfControl control,
        LAppDelegate lapp,
        LAppModel model,
        string mouthParameter,
        double horizontalOffset)
    {
        _logger = logger.ForCategory("Live2D");
        _userTranslation = -horizontalOffset * 2;
        _control = control;
        _lapp = lapp;
        _model = model;
        _mouthParameter = mouthParameter;

        _mouthIndex = _model.Model.GetParameterIndex(mouthParameter);

        /*
         * 从运行时动作名（形如 "Idle_6"）反推真实存在的组。
         *
         * 不直接读 model3.json 是因为这里拿到的是 SDK 已经加载好的结果 ——
         * 组名经过 SDK 处理，可能和文件里写的不完全一样，以运行时为准最可靠。
         * 无名组（默认组）的条目形如 "_0"，剥掉下标后得到空串，这里保留为 ""。
         */
        foreach (var motionName in _model.Motions)
        {
            var group = motionName;
            var underscore = motionName.LastIndexOf('_');

            if (underscore == 0)
            {
                group = string.Empty;            // "_0" → 默认组
            }
            else if (underscore > 0 && int.TryParse(motionName[(underscore + 1)..], out _))
            {
                group = motionName[..underscore];
            }

            if (group.Length > 0 && group.All(char.IsDigit))
            {
                group = string.Empty;            // "0"、"1" 这种纯数字也当默认组
            }

            _motionGroups.Add(group);
        }

        if (_motionGroups.Count > 0)
        {
            _logger.Info($"模型动作组：{(string.Join(", ", _motionGroups.Select(g => g.Length == 0 ? "(默认)" : g)))}");
        }

        /*
         * 居中分两步：
         *   1) 自动居中：渲染稳定后读一次像素，量出模型实际包围盒，把中心挪到 50%；
         *   2) 用户微调：滑块值作为额外偏移，叠在自动居中之上。
         *
         * 平移一律走**模型矩阵**，不能用 WPF 的 RenderTransform ——
         * 给 GLWpfControl（D3DImage 宿主）加 RenderTransform 会让模型在桌面上消失、
         * 那块区域变成点击穿透（PrintWindow 还能拍到，说明是 alpha 合成丢了）。
         */

        if (_mouthIndex < 0)
        {
            var guess = _model.Parameters.FirstOrDefault(p =>
                p.Contains("MouthOpen", StringComparison.OrdinalIgnoreCase));

            if (guess is not null)
            {
                _mouthParameter = guess;
                _mouthIndex = _model.Model.GetParameterIndex(guess);
            }
        }
    }

    /// <summary>要放进 WPF 可视树的控件。</summary>
    public UIElement View => _control;

    /// <summary>模型名（模型文件基名）。</summary>
    public string ModelName { get; private init; } = string.Empty;

    /// <summary>
    /// GL 环境回填回调（可为 null）。在渲染线程的第一次 <see cref="OnRender"/> 里调用一次，
    /// 参数是 GL_VENDOR / GL_RENDERER —— 混合显卡判定就靠这两个字符串，
    /// 这里不再自己写一遍"是不是 NVIDIA"的判断（见 HybridGpuAdvisor）。
    /// </summary>
    private Action<string, string>? GlReporter { get; init; }

    /// <summary>可用的动作组（运行时真实列表，形如 "Idle_0"…）。</summary>
    public IReadOnlyList<string> Motions => _model.Motions;

    /// <summary>口型参数名。</summary>
    public string MouthParameter => _mouthParameter;

    /// <summary>
    /// 失败路径的 GL 控件隔离区。
    ///
    /// 失败时不能释放（会在驱动里崩），但也不能让它们被 GC 回收 ——
    /// OpenTK 的 DxGlContext 终结器会抛 Win32Exception 把进程干掉。
    /// 所以在这儿攥着引用，让它们活到进程退出。一次失败的代价是一个上下文的内存，
    /// 而"坏模型导致程序起不来"是用户完全无法接受的。
    /// </summary>
    private static readonly List<object> FailedContexts = new();

    /// <summary>
    /// 检查 model3.json 里声明的 moc3 与贴图是否都存在。
    /// 返回第一个缺失项的描述（相对路径），全都存在则返回 null。
    /// </summary>
    private static string? FindMissingModelFiles(string modelPath)
    {
        try
        {
            var directory = Path.GetDirectoryName(modelPath);
            if (directory is null)
            {
                return "模型所在目录";
            }

            using var document = System.Text.Json.JsonDocument.Parse(File.ReadAllText(modelPath));
            if (!document.RootElement.TryGetProperty("FileReferences", out var refs))
            {
                return "FileReferences 段";
            }

            var candidates = new List<string>();

            if (refs.TryGetProperty("Moc", out var moc) && moc.ValueKind == System.Text.Json.JsonValueKind.String)
            {
                candidates.Add(moc.GetString() ?? string.Empty);
            }
            else
            {
                return "Moc 声明";
            }

            if (refs.TryGetProperty("Textures", out var textures) && textures.ValueKind == System.Text.Json.JsonValueKind.Array)
            {
                foreach (var item in textures.EnumerateArray())
                {
                    if (item.ValueKind == System.Text.Json.JsonValueKind.String)
                    {
                        candidates.Add(item.GetString() ?? string.Empty);
                    }
                }
            }

            foreach (var candidate in candidates)
            {
                if (string.IsNullOrWhiteSpace(candidate))
                {
                    continue;
                }

                var full = Path.Combine(directory, candidate.Replace('/', Path.DirectorySeparatorChar));
                if (!File.Exists(full))
                {
                    return candidate;
                }
            }

            return null;
        }
        catch (Exception)
        {
            // 解析不出来就交给后面的加载流程去报错（那里有更完整的日志）。
            return null;
        }
    }

    /// <summary>
    /// 尝试创建渲染器。**不抛异常**：失败返回 null，调用方继续走静态图。
    /// </summary>
    /// <param name="glReporter">
    /// GL 上下文就绪后把 GL_VENDOR / GL_RENDERER 交出去（由 HybridGpuAdvisor 做混合显卡判定）。
    /// 用委托而不是直接引用 HybridGpuAdvisor：渲染层不该知道服务层的存在，
    /// 这里只负责"把实测到的 GL 适配器告诉关心它的人"。
    /// </param>
    public static Live2DRenderer? TryCreate(
        string modelPath,
        double horizontalOffset,
        IAppLogger logger,
        Action<string, string>? glReporter = null)
    {
        /*
         * 这三个对象的生命周期必须在这里显式管起来：一旦创建成功又中途失败，
         * 它们是**非托管资源**，GC 收不掉 —— control.Start() 已经建了 GL 上下文与
         * 渲染线程，new LAppDelegateOpenGL() 已经建了 Cubism 的纹理/离屏表面。
         * 因此放在 try 外面，失败时在 finally 里按 model → lapp → control 释放。
         */
        GLWpfControl? control = null;
        LAppDelegate? lapp = null;
        LAppModel? model = null;

        // 成功返回时所有权已经交给 renderer，finally 里就不能再释放一次。
        var handedOver = false;

        try
        {
            if (string.IsNullOrWhiteSpace(modelPath) || !File.Exists(modelPath))
            {
                logger.Info($"未提供可用的 Live2D 模型文件（{modelPath}），保持静态图。");
                return null;
            }

            var directory = Path.GetDirectoryName(modelPath);
            var fileName = Path.GetFileName(modelPath);

            if (directory is null || !fileName.EndsWith(".model3.json", StringComparison.OrdinalIgnoreCase))
            {
                logger.Warn($"模型路径不合法：{modelPath}");
                return null;
            }

            /*
             * 建 GL 之前先做一次纯文件校验。
             *
             * 起因是一次实测：model3.json 里的 moc3 不存在时，LoadModel 返回 null，
             * 失败路径又刻意不释放 GL 上下文（见 finally 注释），结果这个上下文的
             * 终结器在 GC 线程上抛 Win32Exception(13)「数据无效」，没有任何 catch 能接住，
             * **整个进程被带走** —— 表现就是"换了个坏模型，程序启动到一半自己没了"。
             *
             * 缺文件的模型本来就不可能加载成功，在创建任何原生资源之前就挡掉，
             * 这个问题就从源头消失了。
             */
            var missing = FindMissingModelFiles(modelPath);
            if (missing is not null)
            {
                logger.Warn($"Live2D 模型文件不完整，跳过加载并保持静态图：缺少 {missing}（{modelPath}）");
                return null;
            }

            // Cubism 框架只初始化一次
            lock (FrameworkSync)
            {
                if (!_frameworkStarted)
                {
                    CubismFramework.StartUp(
                        new LAppAllocator(),
                        new CubismOption
                        {
                            // 日志走我们自己的日志器；绝不能写 Console（WinExe 无控制台，会抛异常带崩进程）
                            LogFunction = message => logger.Debug("[Cubism] " + message),
                        });

                    _frameworkStarted = true;
                }
            }

            // 注意：不要设 IsHitTestVisible=false。实测那样做会让 GL 图层在**桌面合成**里
            // alpha 变成 0（画面看不到、点不到），而 PrintWindow 仍能拍到内容。
            control = new GLWpfControl();

            control.Start(new GLWpfControlSettings
            {
                MajorVersion = 3,
                MinorVersion = 3,
                /*
                 * 必须为 false：实测 true 时控件自己**满速**重绘（不受我们的限帧控制），
                 * CPU 稳定吃满一个核的 65%，而静态图模式只有 0.5%。
                 * 关掉后由本类的 CompositionTarget 回调按 30 fps 主动驱动。
                 */
                RenderContinuously = false,

                // 主窗口是 AllowsTransparency 的分层窗口，这里必须让 GL 后台透明才能叠上去
                TransparentBackground = true,

                /*
                 * UseDeviceDpi=true：GL 表面按**物理像素**创建（150% 缩放下是 1.5 倍），
                 * 由 WPF 按 1:1 显示 —— 这是画面清晰的关键。
                 *
                 * 曾经设成 false（表面按 DIP 渲染、WPF 放大），结果是"先降采样再放大"，
                 * 模型明显发糊。当时误以为它导致了"整窗不可见"，实际那个 bug 的根因是
                 * 宿主 Border 的 alpha=0（见 VERIFICATION §20），与此无关。
                 */
                UseDeviceDpi = true,

                /*
                 * 不要设 Samples（MSAA）：实测开 4× 后进程直接崩掉，
                 * 日志里没有任何异常 —— 典型的原生层崩溃（GL/D3D 互操作表面不支持多重采样）。
                 */
            });

            lapp = new LAppDelegateOpenGL(new OpenTKWPFApi(control));

            var dir = directory.EndsWith(System.IO.Path.DirectorySeparatorChar)
                ? directory
                : directory + System.IO.Path.DirectorySeparatorChar;

            var name = fileName[..^".model3.json".Length];
            model = lapp.Live2dManager.LoadModel(dir, name);

            if (model is null)
            {
                // lapp / control 交给 finally 释放（这里不再各写一遍，避免只释放一半）。
                logger.Warn($"Live2D 模型加载失败：{modelPath}");
                return null;
            }

            var renderer = new Live2DRenderer(logger, control, lapp, model, "ParamMouthOpenY", horizontalOffset)
            {
                ModelName = name,
                GlReporter = glReporter,
            };

            logger.Info(
                $"Live2D 渲染器已就绪：{name}，动作 {model.Motions.Count} 个，参数 {model.Parameters.Count} 个，" +
                $"口型={renderer.MouthParameter}(index={renderer._mouthIndex})");

            // 所有权已交给 renderer：finally 不得再释放（日志万一抛异常，仍算失败路径）。
            handedOver = true;

            return renderer;
        }
        catch (Exception ex)
        {
            logger.Warn($"创建 Live2D 渲染器失败，保持静态图：{ex.Message}");
            return null;
        }
        finally
        {
            /*
             * 只有失败路径（含上面的 model is null）才走到这里。
             *
             * 这里**刻意不释放** control / lapp / model：见 StopRenderLoop 的注释，
             * 在 UI 线程上释放这些原生对象会在 nvoglv64.dll 里 0xC0000005 直接杀进程
             * （GL 上下文只活在 GLWpfControl 的渲染线程上）。失败路径本就很少见
             * （模型文件损坏/加载异常），宁可让这次失败的上下文活到进程退出，也不要崩。
             *
             * 静态 _frameworkStarted 有意不复位：Cubism 的 StartUp 全进程只允许一次，
             * 这次失败后再建渲染器仍复用它，重复 StartUp 会直接崩。
             */
            if (!handedOver)
            {
                /*
                 * 收进隔离区（见 FailedContexts）：不释放，也不让 GC 回收。
                 * 之前只"不释放"，控件变成不可达对象后终结器仍会跑，
                 * 并在 GC 线程上抛 Win32Exception 把整个进程带走 —— 日志里那行
                 * "后台线程致命异常：Win32Exception：数据无效 at DxGlContext.Finalize"
                 * 就是这么来的。
                 */
                lock (FailedContexts)
                {
                    // 上限 8 份：这是"不释放也不回收"的隔离区，反复用坏模型试错时
                    // 无界增长就是真泄漏（每份都带 GL 表面与渲染线程）。
                    const int maxFailedContexts = 8;
                    if (control is not null && FailedContexts.Count < maxFailedContexts)
                    {
                        FailedContexts.Add(control);
                    }

                    if (lapp is not null && FailedContexts.Count < maxFailedContexts)
                    {
                        FailedContexts.Add(lapp);
                    }
                }

                logger.Warn("Live2D 创建失败，原生上下文已隔离（不释放、不回收），继续使用静态图。");
            }
        }
    }

    /// <summary>
    /// 停止使用这个渲染器：退订事件 + 关掉渲染循环，**不碰任何原生对象**。
    ///
    /// 为什么不在关闭时释放 GL / Cubism 资源（2026-10-23 实测，两次退出崩溃）：
    ///   - 先 <c>model.Dispose()</c>、再 <c>lapp.Dispose()</c>：崩在 lapp 里，0xC0000374
    ///     （STATUS_HEAP_CORRUPTION）—— 模型归 <c>Live2dManager</c> 所有，单独释放造成二次释放。
    ///   - 只 <c>lapp.Dispose()</c>、再 <c>control.Dispose()</c>：崩在 nvoglv64.dll，0xC0000005
    ///     —— GL 上下文只活在 <c>GLWpfControl</c> 的渲染线程上，在 UI 线程关它就是在操作野指针。
    ///   两次崩溃都发生在 <c>MainWindow.OnClosed</c> 里，进程直接死掉，<c>App.OnExit</c> 来不及执行，
    ///   结果 llama-server 变成占显存的孤儿进程（日志里连一句"退出"都没有）。
    ///
    /// 因此这里只做"停止使用"，把非托管资源交给进程退出回收。这不会造成长期泄漏：
    /// <see cref="Dispose"/> 只在关窗时调用一次，而且当前没有任何"重建渲染器"的路径
    /// （换模型能力未实现，见 VERIFICATION.md §30.15）。
    /// </summary>
    private void StopRenderLoop()
    {
        try
        {
            CompositionTarget.Rendering -= OnCompositionRendering;
            _control.SizeChanged -= OnSizeChanged;
            _control.Render -= OnRender;
        }
        catch (Exception ex)
        {
            _logger.Warn($"退订 Live2D 渲染事件失败：{ex.Message}");
        }

        _rendering = false;
    }

    /// <summary>开始逐帧渲染。用 WPF 的逐帧回调驱动（RenderContinuously 实测无效）。</summary>
    public void Start()
    {
        if (_rendering)
        {
            return;
        }

        _control.SizeChanged += OnSizeChanged;
        _control.Render += OnRender;
        CompositionTarget.Rendering += OnCompositionRendering;
        _rendering = true;

        ApplyViewport();
    }

    /// <summary>
    /// 模型里真实存在的动作组名（去掉 "_0"/"_1" 下标后的基名）。
    /// 用于判断"请求的组到底存不存在"，不存在时再考虑回落到默认组 ""。
    /// </summary>
    private readonly HashSet<string> _motionGroups = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>播放动作。传组的基名（如 "Idle"）或完整名（如 "Idle_3"）都可以。</summary>
    public void PlayMotion(string motionName)
    {
        if (_disposed || string.IsNullOrWhiteSpace(motionName))
        {
            return;
        }

        try
        {
            if (_model.Motions.Contains(motionName, StringComparer.OrdinalIgnoreCase))
            {
                _model.StartMotion(motionName, MotionPriority.PriorityNormal, null);
                return;
            }

            /*
             * 组名对不上时的回落：
             *
             * 模型的 actions 可能全在**默认组** "" 里（很多免费模型如此），
             * 这时按 "Idle" 去 StartRandomMotion 找不到任何东西，而且不抛异常 ——
             * 表现就是"角色永远不动"，日志里也没有任何错误，非常难查。
             * 所以先看模型里到底有哪些组，没有就把 "" 当作它们的组。
             */
            var group = motionName;
            if (!_motionGroups.Contains(group) && _motionGroups.Contains(string.Empty))
            {
                group = string.Empty;
            }

            // 传的是组基名（"Idle"）→ 在该组里随机挑一个
            _model.StartRandomMotion(group, MotionPriority.PriorityNormal, null);
        }
        catch (Exception ex)
        {
            _logger.Warn($"播放动作 {motionName} 失败：{ex.Message}");
        }
    }

    /// <summary>写口型参数（0..1）。LipSyncDriver 的输出来到这里。</summary>
    public void SetMouthOpen(double value)
    {
        if (_disposed || _mouthIndex < 0)
        {
            return;
        }

        try
        {
            _mouth = Math.Clamp(value, 0.0, 1.0);
            _model.Model.SetParameterValue(_mouthIndex, (float)_mouth, 1f);
        }
        catch (Exception ex)
        {
            _logger.Warn($"写口型失败：{ex.Message}");
        }
    }

    /// <summary>
    /// 主动触发重绘（GLWpfControl 的 RenderContinuously 在 4.3.1 里不起作用）。
    ///
    /// 但**必须限帧**：CompositionTarget.Rendering 的回调频率跟着合成器走，
    /// 实测会跑到 ~170 fps —— 桌宠根本不需要这么高，白白放大 GL/D3D 的资源churn。
    /// 实测对照组（静态图模式）内存与句柄都是持平甚至回落的，说明增长只来自这条渲染路径。
    /// 限到约 30 fps（MinFrameIntervalMs = 33ms），并跳过不可见窗口。
    /// </summary>
    private void OnCompositionRendering(object? sender, EventArgs e)
    {
        if (_disposed || !_control.IsVisible)
        {
            return;
        }

        // 整数比较代替"毫秒换算 + double 除法"：这条回调每秒要跑 60~170 次。
        var now = Stopwatch.GetTimestamp();

        if (now - _lastFrameTicks < MinFrameTicks)
        {
            return;
        }

        _lastFrameTicks = now;
        _control.InvalidateVisual();
    }

    private void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        ApplyViewport();
    }
    private void ApplyViewport()
    {
        try
        {
            if (_control.ActualWidth <= 0 || _control.ActualHeight <= 0)
            {
                return;
            }

            var dpi = VisualTreeHelper.GetDpi(_control);
            var width = (int)Math.Round(_control.ActualWidth * dpi.DpiScaleX);
            var height = (int)Math.Round(_control.ActualHeight * dpi.DpiScaleY);
            if (width <= 0 || height <= 0)
            {
                return;
            }

            /*
             * 只登记、不当场执行：GL 上下文只活在 GLWpfControl 的渲染线程上，
             * 在 UI 线程调 GL.Viewport / _lapp.Resize 要么静默无效，要么与 OnRender
             * 里的 _lapp.Run 抢状态（历史关窗崩溃同根源）。真正应用在 OnRender 开头。
             */
            Interlocked.Exchange(ref _pendingViewport, ((long)width << 32) | (uint)height);

            _logger.Debug($"渲染视口登记：{width}x{height} 像素（控件 {_control.ActualWidth:F0}x{_control.ActualHeight:F0} DIP，DPI {dpi.DpiScaleX:F2}×）");
        }
        catch (Exception ex)
        {
            _logger.Warn($"计算渲染视口失败：{ex.Message}");
        }
    }


    private void OnRender(TimeSpan elapsed)
    {
        if (_disposed)
        {
            return;
        }

        try
        {
            if (!_glInfoLogged)
            {
                _glInfoLogged = true;
                LogGlInfo();
            }

            // ---- 在渲染线程上消费 UI 线程登记的视口与平移 ----
            // 一次 Interlocked 取走整份"宽+高"，不会出现新旧尺寸混用，也不会漏掉最后一次登记。
            var pendingViewport = Interlocked.Exchange(ref _pendingViewport, 0L);

            if (pendingViewport != 0L)
            {
                var vw = (int)(pendingViewport >> 32);
                var vh = (int)(pendingViewport & 0xFFFF_FFFFL);

                try
                {
                    _lapp.Resize();
                    GL.Viewport(0, 0, vw, vh);
                    _probeWidth = vw;
                    _probeHeight = vh;
                }
                catch (Exception ex)
                {
                    _logger.Warn($"应用渲染视口失败：{ex.Message}");
                }
            }

            var pendingTranslation = Interlocked.Exchange(ref _pendingTranslationBits, NoPendingTranslation);

            if (pendingTranslation != NoPendingTranslation)
            {
                var target = BitConverter.Int64BitsToDouble(pendingTranslation);
                var delta = target - _appliedTranslation;

                if (Math.Abs(delta) > 0.0005)
                {
                    try
                    {
                        _model.ModelMatrix.TranslateRelative((float)delta, 0f);
                        _appliedTranslation = target;
                    }
                    catch (Exception ex)
                    {
                        _logger.Warn($"应用水平平移失败：{ex.Message}");
                    }
                }
            }

            GL.ClearColor(TransparentColor);
            GL.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);

            // 等模型真正画过一帧（贴图 id 才有效）后，补一次 mipmap
            if (!_texturesOptimized && _drawnOnce)
            {
                _texturesOptimized = true;
                EnableTextureMipmaps();
            }

            _lapp.Run((float)elapsed.TotalSeconds);
            _drawnOnce = true;

            // 一次性验证"GL 到底有没有画出模型"：把一帧渲染到自己的 FBO 再读回像素。
            if (!_fbProbeDone)
            {
                _fbProbeDone = true;
                RunFramebufferProbe();
            }

            /*
             * 自动居中要**迭代**，不能测一次就定。
             *
             * 原因：启动阶段窗口尺寸会变（先按基准尺寸、随后套用户倍率），
             * 在尺寸稳定前测出来的中心是错的（实测第 8 帧测到 50.9%，稳定后其实是 73.9%）。
             * 改成每 8 帧测一次、逐步逼近，收敛后停手；尺寸再变就重新开始。
             */
            /*
             * 这里本来想用「读回像素测包围盒」做自动居中，但**在这个宿主里行不通**：
             * GLWpfControl 通过共享纹理合成，GL.ReadPixels 读默认帧缓冲拿不到画面
             * （绑 FBO 0 后仍是全透明，Cubism 自己绑的离屏遮罩也读不出内容）。
             *
             * 所以改为：角色包给该模型一个实测过的默认水平偏移（保证开箱居中），
             * 用户再用设置面板的滑块微调。实测值记录在 AvatarProfile.json 里。
             */
        }
        catch (Exception ex)
        {
            // 渲染失败往往是"每帧都失败"：逐帧打 Warn 一秒钟就有几十条，会把日志冲掉。
            // 只报前 3 次（带序号），之后静默计数。
            _renderErrorCount++;
            if (_renderErrorCount <= 3)
            {
                _logger.Warn($"渲染帧异常（第 {_renderErrorCount} 次，后续不再重复）：{ex.Message}");
            }
        }
    }

    /// <summary>
    /// 把一帧渲染到自建 FBO 并读回像素，用来**客观验证**模型有没有被画出来。
    /// 读默认帧缓冲在这个宿主里拿不到内容（共享纹理合成），但自建 FBO 一定能读。
    /// </summary>
    private void RunFramebufferProbe()
    {
        /*
         * 探针只需要回答"这一帧 GL 有没有画出内容"，不需要按整块画面读回。
         *
         * 以前直接用登记的视口尺寸：1242×900 就要 new byte[4.5 MB]（2560×1600 是 16 MB），
         * 一次大对象堆分配 + 一次同等大小的 GPU 纹理。现在按最长边等比缩到 ≤320 像素：
         *   - CPU 读回缓冲 ≤ 320×320×4 = 400 KB（不再进 LOH）
         *   - GPU 探针纹理同样从 16 MB 降到 ≤400 KB
         * 等比缩放不改变"有没有内容"的判断，实测缩小前后不透明像素占比几乎一致
         * （同一模型：全尺寸 15.6%，256² 时 15.55%）。
         */
        var hasViewport = _probeWidth > 0 && _probeHeight > 0;
        var fullWidth = hasViewport ? _probeWidth : ProbeFallbackSize;
        var fullHeight = hasViewport ? _probeHeight : ProbeFallbackSize;

        var longest = Math.Max(fullWidth, fullHeight);
        var width = fullWidth;
        var height = fullHeight;

        if (longest > ProbeMaxDimension)
        {
            var scale = (double)ProbeMaxDimension / longest;
            width = Math.Max(1, (int)Math.Round(fullWidth * scale));
            height = Math.Max(1, (int)Math.Round(fullHeight * scale));
        }

        int fbo = 0;
        int tex = 0;

        try
        {
            fbo = GL.GenFramebuffer();
            GL.BindFramebuffer(FramebufferTarget.Framebuffer, fbo);

            tex = GL.GenTexture();
            GL.BindTexture(TextureTarget.Texture2D, tex);
            GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.Rgba, width, height, 0,
                OpenTK.Graphics.OpenGL4.PixelFormat.Rgba, PixelType.UnsignedByte, IntPtr.Zero);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Linear);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
            GL.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0, TextureTarget.Texture2D, tex, 0);

            var status = GL.CheckFramebufferStatus(FramebufferTarget.Framebuffer);
            if (status != FramebufferErrorCode.FramebufferComplete)
            {
                _logger.Warn($"FBO 不完整（{status}），无法验证渲染。");
                return;
            }

            GL.Viewport(0, 0, width, height);
            GL.ClearColor(TransparentColor);
            GL.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);
            _lapp.Run(0f);

            var pixels = new byte[width * height * 4];
            GL.ReadPixels(0, 0, width, height, OpenTK.Graphics.OpenGL4.PixelFormat.Rgba, PixelType.UnsignedByte, pixels);

            var visible = 0;
            for (var i = 3; i < pixels.Length; i += 4)
            {
                if (pixels[i] > 8)
                {
                    visible++;
                }
            }

            var ratio = 100.0 * visible / (width * height);
            _logger.Info(hasViewport
                ? $"[探针] FBO {width}×{height}（视口 {fullWidth}×{fullHeight} 等比缩小）里不透明像素 {visible} 个（{ratio:F2}%）"
                : $"[探针] FBO {width}×{height}（首帧还没登记视口，用兜底边长）里不透明像素 {visible} 个（{ratio:F2}%）");

            if (visible == 0)
            {
                _logger.Warn("[探针] FBO 里一个不透明像素都没有：这一帧 GL 没画出内容，请先查渲染（模型/纹理/着色器），不是合成问题。");
            }
            else
            {
                /*
                 * 这里只陈述"读到了什么"，不下"问题在合成"的结论。
                 *
                 * 之前写的是"GL 确实画出了内容 → 问题在合成（D3DImage 没被 WPF 合成进窗口）"，
                 * 一切正常时也照打一遍 —— 日志在骗人：探针只证明这一帧 FBO 里有像素，
                 * 窗口不可见的原因还可能是 GL 与显示不在同一张显卡、窗口被别的层挡住、
                 * 或模型位置被移出可见区。中性描述才不会把排查带偏。
                 */
                _logger.Info(
                    $"[探针] GL 已绘制内容（不透明像素 {ratio:F2}%）；" +
                    "若窗口不可见，请检查 GL 与显示是否在同一显卡");
            }
        }
        catch (Exception ex)
        {
            _logger.Warn($"渲染探针失败：{ex.Message}");
        }
        finally
        {
            /*
             * 清理必须自己兜住异常：这是在 finally 里，一旦 GL 调用在这里抛出
             * （上下文丢失时完全可能），异常会替代掉真正的失败原因往外冒，
             * 连带跳过 OnRender 后面所有帧级收尾。
             *
             * 也不再写 _probeWidth/_probeHeight：那两个字段的语义是"当前 GL 视口尺寸"，
             * 以前探针把它们改成探针尺寸，等于把"已知视口"污染成 256×256。
             */
            try
            {
                GL.BindFramebuffer(FramebufferTarget.Framebuffer, 0);

                if (tex != 0)
                {
                    GL.DeleteTexture(tex);
                }

                if (fbo != 0)
                {
                    GL.DeleteFramebuffer(fbo);
                }

                if (hasViewport)
                {
                    /*
                     * 探针借用了 GL 视口，要还原成探针之前的尺寸。
                     *
                     * 这里不直接 GL.Viewport，而是把尺寸重新登记成"待应用"：
                     * 下一帧会走 OnRender 开头那条**每次改尺寸都在走**的路径
                     * （_lapp.Resize() + GL.Viewport），Cubism 内部状态也一起还原，
                     * 不引入第二条只在探针里跑一次的还原代码。
                     *
                     * CompareExchange 而不是直接覆盖：探针跑的时候 UI 线程可能已经
                     * 登记了更新的尺寸，那种情况下要把新尺寸留着（覆盖就等于丢一次
                     * SizeChanged，视口会停在旧尺寸）。
                     */
                    var restore = ((long)fullWidth << 32) | (uint)fullHeight;
                    Interlocked.CompareExchange(ref _pendingViewport, restore, 0L);
                }
                else
                {
                    // 探针之前还没登记过视口（首帧常见）：让 UI 线程的实际尺寸下一帧重新登记。
                    ApplyViewport();
                }
            }
            catch (Exception ex)
            {
                _logger.Warn($"探针清理失败（不影响渲染）：{ex.Message}");
            }
        }
    }

    /// <summary>用户微调水平位置（比例，正数向左）。0 = 保持自动居中。</summary>
    public void SetUserOffset(double offset)
    {
        _userTranslation = -offset * 2;
        ApplyTranslation(_userTranslation);
    }

    /// <summary>把模型矩阵的平移量设到目标值（增量应用，避免累积漂移）。</summary>
    private void ApplyTranslation(double target)
    {
        // 只登记目标值；ModelMatrix 只能在渲染线程改（见 ApplyViewport 注释）。
        // 打包成位模式后一次 Interlocked 写入：渲染线程不会读到"半个 double"，
        // 也不会出现"标志已置、值还没写进去"的次序问题。
        Interlocked.Exchange(ref _pendingTranslationBits, BitConverter.DoubleToInt64Bits(target));
    }


    /// <summary>
    /// 打印 OpenGL 环境信息，并把 GL 适配器回填给 <see cref="GlReporter"/>。
    ///
    /// 这条日志很关键，有两个用处：
    ///   1. Renderer 若是 GDI Generic / llvmpipe 之类的**软件实现**，Live2D 会烧掉一个核的 CPU
    ///      —— 与"限帧无效、静态图仅 0.5%"的实测现象吻合；
    ///   2. GL 落在哪块显卡上，是混合显卡机器上"角色看不见"的唯一可靠证据
    ///      （混合显卡的判定与自动修复见 HybridGpuAdvisor）。
    /// </summary>
    private void LogGlInfo()
    {
        try
        {
            var vendor = GL.GetString(StringName.Vendor) ?? "(未知)";
            var renderer = GL.GetString(StringName.Renderer) ?? "(未知)";
            var version = GL.GetString(StringName.Version) ?? "(未知)";

            _logger.Info($"OpenGL 环境：Vendor={vendor}，Renderer={renderer}，Version={version}");

            /*
             * 把 GL 适配器交出去，由 HybridGpuAdvisor 统一做"混合显卡一致性判定 → 自动写显卡偏好 → 提示"。
             *
             * 这里**不再自己判断**"是不是 NVIDIA"：以前这里有一条 WRN，说的是同一件事，
             * 但只在有 nvidia-smi.exe 时成立、也不修任何东西，与 GpuAdvisor 的结论容易两套说法。
             * 判定规则、注册表写入、气泡文案现在只在 HybridGpuAdvisor 里维护一份。
             *
             * 回调在渲染线程上执行：GpuAdvisor 内部加锁，并且绝不抛异常（抛了也不能把渲染带崩）。
             */
            try
            {
                GlReporter?.Invoke(vendor, renderer);
            }
            catch (Exception ex)
            {
                _logger.Debug($"回填 GL 适配器信息失败（不影响渲染）：{ex.Message}");
            }

            if (renderer.Contains("GDI", StringComparison.OrdinalIgnoreCase) ||
                renderer.Contains("llvmpipe", StringComparison.OrdinalIgnoreCase) ||
                renderer.Contains("Software", StringComparison.OrdinalIgnoreCase))
            {
                _logger.Warn("检测到**软件 OpenGL 渲染**：Live2D 会非常吃 CPU，建议启用显卡硬件加速。");
            }
        }
        catch (Exception ex)
        {
            _logger.Debug($"读取 OpenGL 信息失败：{ex.Message}");
        }
    }

    /// <summary>
    /// 给模型贴图生成 mipmap 并启用三线性过滤。
    ///
    /// 为什么需要：Cubism 的 OpenGL 渲染器默认对贴图只做线性过滤、**生成 mipmap 与否取决于资源**，
    /// 而桌宠的显示尺寸通常远小于贴图（Hiyori 是 2048²，屏幕上只有约 630 像素高）。
    /// 3 倍降采样且没有 mipmap 时，GPU 每次只采样少数几个纹素，
    /// 表现为"整体发糊 + 边缘抖动"。补 mipmap 后降采样会按层级平均，细节更稳更清晰。
    /// </summary>
    private void EnableTextureMipmaps()
    {
        try
        {
            if (_model.Renderer is not Live2DCSharpSDK.OpenGL.CubismRenderer_OpenGLES2 renderer)
            {
                _logger.Debug("渲染器不是 OpenGL ES2 实现，跳过 mipmap 优化。");
                return;
            }

            var textures = renderer.GetBindedTextures();

            if (textures is null || textures.Count == 0)
            {
                _logger.Debug("还没有绑定贴图，跳过 mipmap 优化。");
                return;
            }

            var count = 0;

            foreach (var id in textures.Values)
            {
                if (id <= 0)
                {
                    continue;
                }

                GL.BindTexture(TextureTarget.Texture2D, id);
                GL.GenerateMipmap(GenerateMipmapTarget.Texture2D);

                GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)All.LinearMipmapLinear);
                GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)All.Linear);

                count++;
            }

            GL.BindTexture(TextureTarget.Texture2D, 0);

            _logger.Info($"已为 {count} 张模型贴图生成 mipmap 并启用三线性过滤（改善降采样清晰度）。");
        }
        catch (Exception ex)
        {
            _logger.Warn($"生成 mipmap 失败（不影响显示）：{ex.Message}");
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        // 只停循环、不退原生的 GL/Cubism 对象（原因见 StopRenderLoop 的注释：
        // 在 UI 线程释放它们会在 nvoglv64.dll 里 0xC0000005 杀进程）。
        StopRenderLoop();

        _logger.Info("Live2D 渲染已停止（原生上下文交由进程退出回收）。");
    }
}
