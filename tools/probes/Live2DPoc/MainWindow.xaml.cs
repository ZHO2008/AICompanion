using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using Live2DCSharpSDK.App;
using Live2DCSharpSDK.Framework;
using Live2DCSharpSDK.Framework.Motion;
using Live2DCSharpSDK.OpenGL;
using Live2DCSharpSDK.WPF;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;
using OpenTK.Wpf;

namespace Live2DPoc;

/// <summary>
/// Live2D 最小可行性验证窗口（**通用**：对任何 Cubism 3/4 模型都能用，不写死路径）。
///
/// 用法：
///   Live2DPoc.exe "&lt;模型目录 或 *.model3.json&gt;" [--transparent]
/// 也可以直接把模型文件夹 / model3.json 拖进窗口。
///
/// 验证四件事：
///   1. 模型能渲染出来；
///   2. 有连续帧（模型在动，不是静止图）；
///   3. 口型参数写入后能回读出同一个值（证明参数接口真通）；
///   4. 鼠标点击能命中（hit test）。
/// 刻意不做：状态机、缩放、点击穿透 —— 这些验证通过后再接。
/// </summary>
public partial class MainWindow : Window
{
    // ---- 日志 ----
    // 不用 Console：WinExe 没有控制台，stdout 被重定向后句柄可能无效，
    // Cubism 的日志回调一写就抛 IOException，会把程序直接带崩（已踩过）。
    private static readonly string LogPath = Path.Combine(Path.GetTempPath(), "live2dpoc.log");
    private static readonly object LogSync = new();

    private static void Log(string message)
    {
        try
        {
            lock (LogSync)
            {
                File.AppendAllText(LogPath, $"{DateTime.Now:HH:mm:ss.fff} {message}{Environment.NewLine}");
            }
        }
        catch
        {
            // 日志失败绝不影响主流程
        }
    }

    private GLWpfControl _gl = null!;
    private LAppDelegate _lapp = null!;
    private LAppModel? _model;

    private DispatcherTimer _mouthTimer = null!;
    private DateTime _startedUtc;
    private int _frames;
    private int _renderCalls;
    private int _renderErrors;
    private double _mouth;
    private double _mouthReadBack = double.NaN;

    /// <summary>透明背景实验模式（--transparent）。</summary>
    private bool _transparent;

    /// <summary>当前模型目录与模型名 —— 由传入路径推导，不写死。</summary>
    private string _modelDirectory = string.Empty;
    private string _modelName = string.Empty;

    /// <summary>口型参数名：优先模型实际拥有的那个，找不到就跳过口型验证。</summary>
    private string _mouthParameter = "ParamMouthOpenY";

    private string? _resolveError;

    public MainWindow()
    {
        InitializeComponent();

        var args = Environment.GetCommandLineArgs().Skip(1).ToArray();
        _transparent = args.Any(a => a.Equals("--transparent", StringComparison.OrdinalIgnoreCase));

        var pathArg = args.FirstOrDefault(a => !a.StartsWith("--", StringComparison.Ordinal));

        if (_transparent)
        {
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            Log("透明模式已开启");
        }

        AllowDrop = true;
        Drop += OnDrop;

        if (pathArg is not null)
        {
            if (TryResolveModel(pathArg, out var dir, out var name, out var error))
            {
                _modelDirectory = dir;
                _modelName = name;
            }
            else
            {
                _resolveError = error;
                Log("模型路径无效：" + error);
            }
        }

        Loaded += OnLoaded;
        Closed += OnClosed;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_modelDirectory.Length == 0 && _resolveError is null)
            {
                _resolveError = "没有指定模型。\n\n" +
                                "把模型文件夹（或 .model3.json）拖进这个窗口，\n" +
                                "或用命令行传入：Live2DPoc.exe \"D:\\Models\\Mao\"";
            }

            // ---- 1) Cubism 框架 ----
            var allocator = new LAppAllocator();
            var option = new CubismOption
            {
                LogFunction = message => Log("[Cubism] " + message),
            };
            CubismFramework.StartUp(allocator, option);

            // ---- 2) GL 控件 ----
            _gl = new GLWpfControl();
            _gl.SizeChanged += OnGlSizeChanged;
            _gl.Render += OnGlRender;
            _gl.MouseMove += OnGlMouseMove;
            _gl.MouseLeftButtonDown += OnGlMouseDown;
            _gl.MouseLeftButtonUp += OnGlMouseUp;
            GlHost.Child = _gl;

            _gl.Start(new GLWpfControlSettings
            {
                MajorVersion = 3,
                MinorVersion = 3,
                RenderContinuously = true,
                TransparentBackground = _transparent,
            });

            /*
             * RenderContinuously 在 4.3.1 里并没有真正驱动循环（实测只进 3 帧就停）。
             * 改用 WPF 的逐帧回调主动 InvalidateVisual：每次重绘触发 Render 事件，
             * lapp.Run() 才会被稳定按刷新率调用。
             */
            CompositionTarget.Rendering += OnCompositionRendering;

            // ---- 3) 渲染代理 ----
            _lapp = new LAppDelegateOpenGL(new OpenTKWPFApi(_gl));

            if (_resolveError is not null)
            {
                StatusText.Text = "无法加载模型：\n\n" + _resolveError;
                return;
            }

            // ---- 4) 加载模型 ----
            var dir = _modelDirectory.EndsWith(Path.DirectorySeparatorChar)
                ? _modelDirectory
                : _modelDirectory + Path.DirectorySeparatorChar;

            _model = _lapp.Live2dManager.LoadModel(dir, _modelName);
            _startedUtc = DateTime.UtcNow;

            if (_model is null)
            {
                StatusText.Text = $"模型加载失败：{_modelName}\n看日志 {LogPath}";
                Log($"LoadModel 返回 null：dir={dir} name={_modelName}");
                return;
            }

            // 口型参数：先按标准名找，找不到再从参数表里挑一个含 MouthOpen 的
            var mouthIndex = _model.Model.GetParameterIndex(_mouthParameter);
            if (mouthIndex < 0)
            {
                var guess = _model.Parameters.FirstOrDefault(p =>
                    p.Contains("MouthOpen", StringComparison.OrdinalIgnoreCase));

                if (guess is not null)
                {
                    _mouthParameter = guess;
                    mouthIndex = _model.Model.GetParameterIndex(_mouthParameter);
                }
            }

            if (mouthIndex < 0)
            {
                Log($"注意：该模型没有口型参数（试过 {_mouthParameter}），口型验证跳过");
            }

            // ---- 5) 口型驱动（模拟 LipSyncDriver 的 50ms 输出）----
            _mouthTimer = new DispatcherTimer(DispatcherPriority.Render)
            {
                Interval = TimeSpan.FromMilliseconds(50),
            };
            _mouthTimer.Tick += OnMouthTick;
            _mouthTimer.Start();

            var motions = string.Join(", ", _model.Motions.Take(6));
            var more = _model.Motions.Count > 6 ? $" …共 {_model.Motions.Count} 个" : string.Empty;

            StatusText.Text =
                $"模型：{_modelName}\n" +
                $"目录：{_modelDirectory}\n" +
                $"动作：{motions}{more}\n" +
                $"参数数：{_model.Parameters.Count}    口型：{_mouthParameter}\n" +
                (mouthIndex < 0 ? "（该模型没有口型参数）" : "点/拖模型可触发动作");

            Log($"已加载模型：{_modelName}，目录={_modelDirectory}，" +
                $"动作 {_model.Motions.Count} 个，参数 {_model.Parameters.Count} 个，" +
                $"口型={_mouthParameter}(index={mouthIndex})，透明={_transparent}");
        }
        catch (Exception ex)
        {
            StatusText.Text = "初始化异常：\n" + ex.Message;
            Log("初始化异常：" + ex);
        }
    }

    /// <summary>
    /// 解析模型路径：既接受模型目录，也接受 .model3.json 文件。
    /// 不满足条件时给出明确原因（而不是静默失败）。
    /// </summary>
    private static bool TryResolveModel(string input, out string directory, out string name, out string? error)
    {
        directory = string.Empty;
        name = string.Empty;
        error = null;

        string full;
        try
        {
            full = Path.GetFullPath(input.Trim('"', ' '));
        }
        catch (Exception ex)
        {
            error = $"路径无法解析：{ex.Message}";
            return false;
        }

        if (File.Exists(full))
        {
            if (!full.EndsWith(".model3.json", StringComparison.OrdinalIgnoreCase))
            {
                error = $"不是 Live2D 模型文件（需要 *.model3.json）：\n{full}";
                return false;
            }

            directory = Path.GetDirectoryName(full) ?? string.Empty;
            name = Path.GetFileName(full)[..^".model3.json".Length];
            return true;
        }

        if (!Directory.Exists(full))
        {
            error = $"路径不存在：\n{full}";
            return false;
        }

        var candidates = Directory.GetFiles(full, "*.model3.json", SearchOption.TopDirectoryOnly);
        if (candidates.Length == 0)
        {
            error = $"目录里没有 *.model3.json：\n{full}";
            return false;
        }

        // 多个模型时优先选与目录同名的那一个，否则取第一个并记日志
        var folderName = Path.GetFileName(full.TrimEnd(Path.DirectorySeparatorChar));
        var chosen = candidates.FirstOrDefault(c =>
                         Path.GetFileName(c).Equals(folderName + ".model3.json", StringComparison.OrdinalIgnoreCase))
                     ?? candidates[0];

        directory = full;
        name = Path.GetFileName(chosen)[..^".model3.json".Length];

        if (candidates.Length > 1)
        {
            Log($"目录里有 {candidates.Length} 个模型，选用 {Path.GetFileName(chosen)}");
        }

        return true;
    }

    private void OnDrop(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            return;
        }

        if (e.Data.GetData(DataFormats.FileDrop) is not string[] paths || paths.Length == 0)
        {
            return;
        }

        if (!TryResolveModel(paths[0], out var dir, out var name, out var error))
        {
            StatusText.Text = "拖入的不是可用模型：\n\n" + error;
            Log("拖入路径无效：" + error);
            return;
        }

        // 换模型要重建 GL 上下文，直接换容易残留状态；提示重启最可靠
        StatusText.Text =
            $"已选择模型：{name}\n" +
            $"目录：{dir}\n\n" +
            "请重新启动本程序加载该模型：\n" +
            $"Live2DPoc.exe \"{dir}\"";

        Log($"拖入模型：{name} @ {dir}（提示重启加载）");
    }

    private void OnCompositionRendering(object? sender, EventArgs e)
    {
        _gl?.InvalidateVisual();
    }

    private void OnGlSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_lapp is null)
        {
            return;
        }

        _lapp.Resize();
        GL.Viewport(0, 0, (int)_gl.ActualWidth, (int)_gl.ActualHeight);
    }

    private void OnGlRender(TimeSpan elapsed)
    {
        _renderCalls++;
        if (_renderCalls <= 5)
        {
            Log($"渲染回调 #{_renderCalls} 进入");
        }

        try
        {
            GL.ClearColor(_transparent
                ? new Color4(0f, 0f, 0f, 0f)
                : new Color4(0.06f, 0.07f, 0.10f, 1f));
            GL.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);

            if (_lapp is null || _model is null)
            {
                return;
            }

            _lapp.Run((float)elapsed.TotalSeconds);
            _frames++;

            if (_frames % 120 == 0)
            {
                var fps = _frames / Math.Max(0.001, (DateTime.UtcNow - _startedUtc).TotalSeconds);

                StatusText.Text =
                    $"模型：{_modelName}    FPS≈{fps:F0}\n" +
                    $"动作：{_model.Motions.Count} 个    参数：{_model.Parameters.Count} 个\n" +
                    $"口型 {_mouthParameter}  写入 {_mouth:F2} / 回读 {_mouthReadBack:F2}";

                Log($"状态刷新：帧={_frames}，FPS≈{fps:F0}，口型写入={_mouth:F2}，回读={_mouthReadBack:F2}");
            }
        }
        catch (Exception ex)
        {
            if (_renderErrors++ < 3)
            {
                Log("渲染异常：" + ex);
            }
        }
    }

    /// <summary>写口型参数 —— 未来 AnimationManager / LipSyncDriver 就调这一个方法。</summary>
    private void OnMouthTick(object? sender, EventArgs e)
    {
        if (_model is null)
        {
            return;
        }

        try
        {
            var index = _model.Model.GetParameterIndex(_mouthParameter);
            if (index < 0)
            {
                return;
            }

            var t = (DateTime.UtcNow - _startedUtc).TotalSeconds;
            _mouth = 0.5 + 0.35 * Math.Sin(t * 6.0);

            _model.Model.SetParameterValue(index, (float)_mouth, 1f);

            // 立刻回读：确认值真的进了模型（而不是被丢弃）
            _mouthReadBack = _model.Model.GetParameterValue(index);
        }
        catch (Exception ex)
        {
            Log("写口型参数失败：" + ex.Message);
        }
    }

    private void OnGlMouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (_lapp is null)
        {
            return;
        }

        var p = e.GetPosition(_gl);
        _lapp.OnMouseCallBack((float)p.X, (float)p.Y);
    }

    private void OnGlMouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (_lapp is null)
        {
            return;
        }

        _lapp.OnMouseCallBack(true);

        // 点一下就给一个动作，证明"动作 API 可用"（组名取模型自己的第一个组）
        var group = _model?.Motions.FirstOrDefault();
        if (group is not null)
        {
            _model!.StartRandomMotion(group, MotionPriority.PriorityNormal, null);
        }
    }

    private void OnGlMouseUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        _lapp?.OnMouseCallBack(false);
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        try
        {
            CompositionTarget.Rendering -= OnCompositionRendering;
            _mouthTimer?.Stop();
            _model?.Dispose();
            _lapp?.Dispose();
            _gl?.Dispose();
            CubismFramework.CleanUp();
        }
        catch (Exception ex)
        {
            Log("清理异常：" + ex.Message);
        }
    }
}
