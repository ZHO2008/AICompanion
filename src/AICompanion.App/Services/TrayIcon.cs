using System.Drawing;
using System.Windows;
using AICompanion.Core.Abstractions;
using WF = System.Windows.Forms;

namespace AICompanion.App.Services;

/// <summary>
/// 系统托盘（手册 §1.1 与 §3.4 的"重启语音服务"、§3.9 的"重连游戏检测"）。
///
/// 用 WinForms 的 NotifyIcon：WPF 自身没有托盘 API，而引入第三方托盘库
/// 不如直接用框架内置实现可靠（详见 csproj 里 UseWindowsForms 的注释）。
/// </summary>
public sealed class TrayIcon : IDisposable
{
    private readonly AppHost _host;
    private readonly MainWindow _window;
    private readonly BubbleNotifier _bubbles;
    private readonly WF.NotifyIcon _notifyIcon;
    private readonly WF.ToolStripMenuItem _interactiveItem;

    /// <summary>托盘右键菜单。它不归 NotifyIcon 所有（框架不会替我们释放），必须自己 Dispose。</summary>
    private readonly WF.ContextMenuStrip _menu;

    /// <summary>气泡变化的处理器（存字段才能在 Dispose 里退订）。</summary>
    private readonly EventHandler _bubblesChangedHandler;

    private readonly Icon _icon;

    private bool _disposed;

    public TrayIcon(AppHost host, MainWindow window, BubbleNotifier bubbles)
    {
        _host = host;
        _window = window;
        _bubbles = bubbles;

        _icon = CreateIcon();

        _menu = new WF.ContextMenuStrip();
        var menu = _menu;

        var showItem = new WF.ToolStripMenuItem("显示/隐藏角色", null, (_, _) => _window.ToggleVisibility());
        menu.Items.Add(showItem);

        _interactiveItem = new WF.ToolStripMenuItem("与角色交互（关闭鼠标穿透）", null, (_, _) => ToggleInteractive());
        menu.Items.Add(_interactiveItem);

        menu.Items.Add(new WF.ToolStripMenuItem("输入文字…", null, (_, _) => FocusTextInput()));
        menu.Items.Add(new WF.ToolStripSeparator());

        // 手册 §3.4：托盘"重启语音服务"依次重置 TTS 与 ASR。
        menu.Items.Add(new WF.ToolStripMenuItem("重启语音服务", null, async (_, _) => await RestartVoiceServicesAsync()));

        // 手册 §3.9：GameMonitor 断开时提供"重连游戏检测"。
        menu.Items.Add(new WF.ToolStripMenuItem("重连游戏检测", null, async (_, _) => await ReconnectGameMonitorAsync()));
        menu.Items.Add(new WF.ToolStripSeparator());

        menu.Items.Add(new WF.ToolStripMenuItem("设置…", null, (_, _) => OpenSettings()));
        menu.Items.Add(new WF.ToolStripMenuItem("退出", null, (_, _) => ExitApplication()));

        _notifyIcon = new WF.NotifyIcon
        {
            Icon = _icon,
            Text = "AI 伴侣（正在启动…）",
            Visible = true,
            ContextMenuStrip = menu,
        };

        _notifyIcon.DoubleClick += (_, _) => _window.ToggleVisibility();

        // 气泡变化时同步托盘提示文本（准则 4：不弹窗，用气泡提示；图标只有一枚，无法用它表达状态）。
        _bubblesChangedHandler = (_, _) => UpdateTooltip();
        _bubbles.Changed += _bubblesChangedHandler;
    }

    public void OnHostReady()
    {
        UpdateTooltip();
    }

    private void UpdateTooltip()
    {
        if (_disposed)
        {
            return;
        }

        try
        {
            var worst = _bubbles.Current.FirstOrDefault();
            var text = worst is null
                ? $"AI 伴侣｜{_host.RuntimeMode.Mode}｜TTS {_host.Tts?.CurrentEngineName ?? "-"}"
                : $"AI 伴侣：{worst.Message}";

            // NotifyIcon.Text 上限 63 个字符，超长会抛异常。
            _notifyIcon.Text = text.Length <= 63 ? text : text[..60] + "...";
        }
        catch
        {
            // 提示文本失败不影响功能。
        }
    }

    private void ToggleInteractive()
    {
        var interactive = !_window.IsInteractive;
        _window.SetInteractive(interactive);
        _interactiveItem.Text = interactive ? "与角色交互（已开启，点击关闭）" : "与角色交互（关闭鼠标穿透）";
    }

    private void FocusTextInput()
    {
        _window.SetInteractive(true);
        _interactiveItem.Text = "与角色交互（已开启，点击关闭）";

        if (!_window.IsVisible)
        {
            _window.Show();
        }

        _window.Activate();
    }

    private async Task RestartVoiceServicesAsync()
    {
        try
        {
            _host.Logger.Info("托盘：重启语音服务。");

            // 手册要求依次重置 TTS 引擎与 ASR 状态。
            await _host.Tts.ResetAsync();
            await _host.Asr.ResetAsync();

            _bubbles.Show("voice-restarted", "语音服务已重启。", BubbleLevel.Info,
                TimeSpan.FromSeconds(_host.Config.Ui.BubbleDurationSeconds));
        }
        catch (Exception ex)
        {
            _host.Logger.Error($"重启语音服务失败：{ex.Message}", ex);
            _bubbles.Show("voice-restart-failed", "重启语音服务失败，详见日志。", BubbleLevel.Error);
        }
    }

    private async Task ReconnectGameMonitorAsync()
    {
        try
        {
            await _host.Monitor.ReconnectAsync();
        }
        catch (Exception ex)
        {
            _host.Logger.Error($"重连游戏检测失败：{ex.Message}", ex);
            _bubbles.Show("gamemonitor-offline", "重连游戏检测失败，详见日志。", BubbleLevel.Error);
        }
    }

    private void OpenSettings()
    {
        try
        {
            // 必须传 onAppearanceChanged（与主窗口打开设置面板时一致）：
            // 否则从托盘改完"水平微调"/气泡时长并保存后，角色上不会有任何变化（要重启才生效）。
            var settings = new SettingsWindow(_host, _bubbles, _window.ApplyUserAppearanceSettings)
            {
                Owner = _window.IsVisible ? _window : null,
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

    private void ExitApplication()
    {
        try
        {
            _notifyIcon.Visible = false;
        }
        catch
        {
            // 忽略。
        }

        System.Windows.Application.Current.Shutdown();
    }

    /// <summary>程序化生成一个简单图标，避免依赖外部 .ico 资源。</summary>
    private static Icon CreateIcon()
    {
        using var bitmap = new Bitmap(32, 32);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            graphics.Clear(Color.Transparent);

            using var body = new SolidBrush(Color.FromArgb(255, 108, 160, 240));
            graphics.FillEllipse(body, 2, 6, 28, 22);

            using var eye = new SolidBrush(Color.FromArgb(255, 30, 30, 40));
            graphics.FillEllipse(eye, 11, 14, 4, 5);
            graphics.FillEllipse(eye, 18, 14, 4, 5);
        }

        var handle = bitmap.GetHicon();
        try
        {
            // 复制一份，避免依赖随后被销毁的 HICON。
            using var temp = Icon.FromHandle(handle);
            return (Icon)temp.Clone();
        }
        finally
        {
            DestroyIcon(handle);
        }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr hIcon);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        try
        {
            _bubbles.Changed -= _bubblesChangedHandler;
        }
        catch
        {
            // 忽略。
        }

        try
        {
            _notifyIcon.Visible = false;

            // NotifyIcon 会持有菜单引用：先断开，菜单才能被干净地释放。
            _notifyIcon.ContextMenuStrip = null;
            _notifyIcon.Dispose();
        }
        catch
        {
            // 忽略。
        }

        try
        {
            // ToolStrip.Dispose 会连带释放 Items 里的 8 个菜单项，所以不需要逐个 Dispose。
            _menu.Dispose();
        }
        catch
        {
            // 忽略。
        }

        _icon.Dispose();
    }
}
