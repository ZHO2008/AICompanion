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
                _tray ??= new TrayIcon(_host!, _mainWindow!, _bubbles!);
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
            _host?.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(10));
        }
        catch
        {
            // 忽略。
        }

        base.OnExit(e);
    }
}
