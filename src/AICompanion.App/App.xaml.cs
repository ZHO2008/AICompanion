using System.IO;
using System.Windows;
using AICompanion.App.Services;
using AICompanion.Core.Abstractions;
using Microsoft.Win32;

namespace AICompanion.App;

/// <summary>
/// 应用入口。
///
/// 准则 4 的落点：**整个程序没有任何 MessageBox 调用**。
/// 启动失败、配置错误、服务离线一律走气泡与托盘提示。
/// </summary>
public partial class App : Application
{
    private AppHost? _host;
    private BubbleNotifier? _bubbles;
    private MainWindow? _mainWindow;
    private TrayIcon? _tray;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        /*
         * 单实例与子进程兜底都要在"任何重活"之前做：
         * 第二个实例如果先拉起 llama-server 再退出，就等于白占一次显存。
         */
        SingleInstance.Acquire();

        if (!SingleInstance.IsFirst)
        {
            // 把已在运行的那个实例唤出来，然后安静退出。
            SingleInstance.SignalExisting();
            Shutdown(0);
            return;
        }

        ChildProcessJob.TryAttach(message => System.Diagnostics.Debug.WriteLine($"[AICompanion] {message}"));

        try
        {
            /*
             * 气泡通道必须先于一切核心组件建立：启动过程中的降级提示全靠它。
             *
             * 这里创建的 LogSink 要**同时**交给 BubbleNotifier 和 AppHost。
             * 若各自建一个指向同一文件的 sink，同一进程内就会有两个 StreamWriter
             * 写同一个日志文件，实测会出现两行日志交错、甚至把中文从中间切开。
             */
            var sharedSink = new Core.Logging.LogSink(
                AICompanion.App.Services.LogRoot.AppLogDirectory());

            var bootstrapLogger = new Core.Logging.FileLogger(sharedSink, "Bootstrap");

            /*
             * 未处理异常兜底。
             *
             * 之前没有任何处理器：UI 线程上的异常会直接把进程带走，日志里一行都没有，
             * 事后只能看到"启动完成"之后突然没了，完全无法定位（用户报过"怎么自己退了"）。
             * 这里统一记录 + 用气泡提示，并且不让 UI 线程的异常杀死进程 ——
             * 桌宠这类常驻程序，宁可带伤继续跑，也不要无声消失。
             */
            DispatcherUnhandledException += (_, args) =>
            {
                try
                {
                    bootstrapLogger.Error(
                        $"界面线程未处理异常：{args.Exception.GetType().Name}：{args.Exception.Message}",
                        args.Exception);
                    _bubbles?.Show(
                        "ui-crash",
                        $"内部错误：{args.Exception.Message}（已记录，程序继续运行）",
                        BubbleLevel.Error);
                }
                catch
                {
                    // 记录失败也不能再抛。
                }

                args.Handled = true;
            };

            AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            {
                try
                {
                    if (args.ExceptionObject is Exception ex)
                    {
                        bootstrapLogger.Error($"后台线程致命异常：{ex.GetType().Name}：{ex.Message}", ex);
                    }
                    else
                    {
                        bootstrapLogger.Error($"后台线程致命异常：{args.ExceptionObject}");
                    }
                }
                catch
                {
                    // 忽略。
                }
            };

            TaskScheduler.UnobservedTaskException += (_, args) =>
            {
                try
                {
                    bootstrapLogger.Warn($"未观察的任务异常：{args.Exception.InnerException?.Message ?? args.Exception.Message}");
                }
                catch
                {
                    // 忽略。
                }

                // 标记已观察，避免 .NET 在 GC 时把整个进程带走。
                args.SetObserved();
            };

            _bubbles = new BubbleNotifier(Dispatcher, bootstrapLogger);
            _host = new AppHost(Dispatcher, _bubbles, sharedSink);
            _mainWindow = new MainWindow(_host, _bubbles);

            // 主窗口先显示，用户能立刻看到"正在启动"而不是一片空白。
            _mainWindow.Show();
            _bubbles.Show("starting", "正在启动…", BubbleLevel.Info, TimeSpan.FromSeconds(5));

            _tray = new TrayIcon(_host, _mainWindow, _bubbles);

            await _host.StartAsync();

            _mainWindow.OnHostReady();
            _tray.OnHostReady();

            // 用户再次双击图标时，把窗口显示出来（并置前），而不是让第二份程序跑起来。
            var window = _mainWindow;
            SingleInstance.StartListener(() => Dispatcher.BeginInvoke(new Action(() =>
            {
                try
                {
                    if (!window.IsVisible)
                    {
                        window.ShowPet();
                    }

                    window.Activate();
                    window.Topmost = true;
                    window.Topmost = _host.Config.Ui.AlwaysOnTop;
                }
                catch
                {
                    // 唤醒失败不影响已有窗口。
                }
            })));

            // 手册 §3.8 休眠唤醒处理：表现层订阅系统事件后转发给 HealthMonitor。
            SystemEvents.PowerModeChanged += OnPowerModeChanged;
        }
        catch (Exception ex)
        {
            // 启动期异常绝不弹窗（准则 4）：写日志 + 气泡，并保证托盘可用以便用户退出。
            // 注意要连异常类型与堆栈一起记：启动早期失败时，这是唯一的排查线索。
            try
            {
                _host?.Logger?.Error($"启动失败（{ex.GetType().Name}）：{ex.Message}{Environment.NewLine}{ex}", ex);
            }
            catch
            {
                // 日志也不可用时只能放弃记录。
            }

            _bubbles?.Show(
                "startup-failed",
                $"启动失败：{ex.Message}。详情见 logs/app 目录。",
                BubbleLevel.Error);

            try
            {
                if (_tray is null)
                {
                    /*
                     * 兜底托盘只在三个依赖都就绪时才建。
                     *
                     * 异常可能发生在 MainWindow / TrayIcon 构造之前：那时 _mainWindow 是 null，
                     * 而 TrayIcon 的构造不校验参数，用户点托盘"显示/隐藏角色"就会在
                     * _window.ToggleVisibility() 上 NRE；_host 为 null 时"设置…""重启语音服务"同样会炸。
                     * 没有主窗口也就没有可托管的界面，与其建一个点了就崩的托盘，
                     * 不如直接退出，避免留下一个看不见的僵尸进程。
                     */
                    if (_host is not null && _mainWindow is not null && _bubbles is not null)
                    {
                        _tray = new TrayIcon(_host, _mainWindow, _bubbles);
                    }
                    else
                    {
                        System.Diagnostics.Debug.WriteLine(
                            "[AICompanion] 启动失败且宿主/主窗口未就绪，无法建立可用托盘，直接退出。");
                        Shutdown(1);
                    }
                }
            }
            catch
            {
                // 托盘都建不起来就只能退出，避免留下一个看不见的僵尸进程。
                Shutdown(1);
            }
        }
    }

    private async void OnPowerModeChanged(object sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode != PowerModes.Resume || _host?.Health is null)
        {
            return;
        }

        try
        {
            _host.Logger.Info("收到系统 Resume 事件。");
            await _host.Health.HandlePowerResumeAsync();
        }
        catch (Exception ex)
        {
            _host.Logger.Error($"处理休眠唤醒失败：{ex.Message}", ex);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try
        {
            SingleInstance.Release();
        }
        catch
        {
            // 忽略。
        }

        try
        {
            SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        }
        catch
        {
            // 忽略。
        }

        try
        {
            _tray?.Dispose();
        }
        catch
        {
            // 忽略。
        }

        try
        {
            // 退出时必须等待：llama-server 由本程序拉起，不清理会留下占显存的孤儿进程。
            if (_host is not null)
            {
                var dispose = _host.DisposeAsync().AsTask();
                if (!dispose.Wait(TimeSpan.FromSeconds(10)))
                {
                    /*
                     * 超时了不能静默：llama-server（以及 GameMonitor 的清理）可能没走完，
                     * 结果是留下占显存的孤儿进程，而日志里一个字都没有，事后无从排查。
                     *
                     * 这里刻意**不写日志文件**：此刻 AppHost 可能已经走到 DisposeAsync 末尾
                     * 关掉了 LogSink，往已 Close 的 sink 写日志会把旧目录的文件重新打开
                     * （正是要避免的日志分流）。Debug.WriteLine 在 DebugView / 调试器下可见，
                     * 且完全没有副作用。
                     */
                    System.Diagnostics.Debug.WriteLine(
                        "[AICompanion] 退出时等待 AppHost.DisposeAsync 超过 10 秒，llama-server 可能残留。");
                }
            }
        }
        catch (Exception ex)
        {
            // 退出清理失败也不能抛（会盖住正常退出），留一条调试线索即可。
            System.Diagnostics.Debug.WriteLine($"[AICompanion] 退出清理异常：{ex}");
        }

        base.OnExit(e);
    }
}
