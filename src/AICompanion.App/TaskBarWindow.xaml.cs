using System.Windows;
using System.Windows.Media;

namespace AICompanion.App;

/// <summary>
/// 贴在角色窗口正下方的小功能栏（左键点击角色时弹出）。
///
/// 为什么用独立窗口而不是在主窗口里加一行：
/// 主窗口尺寸固定为 340×460 且透明穿透/覆盖可切换；把功能栏做成独立窗口，
/// 就能"浮在角色下面"而不改变角色的显示区域，也不会影响透明区域的命中测试。
///
/// 关闭策略：**不在 Deactivated 时自动隐藏**。
/// 原因：点角色会先让功能栏失去激活（触发隐藏），随后角色窗口的点击事件又把功能栏打开，
/// 结果表现为"点角色关不掉功能栏"。因此只在这三种情况下隐藏：
/// 再次点击角色、点了功能栏里的某个按钮、角色窗口被隐藏。
/// </summary>
public partial class TaskBarWindow : Window
{
    /// <summary>基准尺寸（倍率 1.0 时的窗口大小）。</summary>
    private const double BaseWidth = 360;
    private const double BaseHeight = 52;

    public event EventHandler? SettingsRequested;
    public event EventHandler? AppearanceRequested;
    public event EventHandler? ResetAppearanceRequested;
    public event EventHandler? ChatRequested;
    public event EventHandler? SmallerRequested;
    public event EventHandler? LargerRequested;

    private readonly ScaleTransform _barScale = new(1, 1);

    public TaskBarWindow()
    {
        InitializeComponent();

        // 在代码里挂变换：XAML 内联的 ScaleTransform 会被冻结，运行时改不了。
        RootBorder.LayoutTransform = _barScale;
    }

    /// <summary>
    /// 与角色窗口同倍率缩放。
    ///
    /// 不这么做的话：角色放大到 150% 时工具栏还是原尺寸，看起来像另一个程序的东西；
    /// 而且工具栏宽度是按基准尺寸算的，不缩放会导致它比角色还窄或还宽。
    /// </summary>
    public void ApplyScale(double scale)
    {
        _barScale.ScaleX = scale;
        _barScale.ScaleY = scale;

        Width = BaseWidth * scale;
        Height = BaseHeight * scale;

        if (IsVisible)
        {
            UpdateLayout();
        }
    }

    private void OnSmallerClick(object sender, RoutedEventArgs e)
    {
        SmallerRequested?.Invoke(this, EventArgs.Empty);
    }

    private void OnLargerClick(object sender, RoutedEventArgs e)
    {
        LargerRequested?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>定位到 owner 的正下方并居中；下方放不下时改放到上方。</summary>
    public void ShowBelow(Window owner)
    {
        try
        {
            // 先确保尺寸已确定，否则 Width/Height 可能还是 0
            UpdateLayout();

            MoveBelow(owner);

            if (!IsVisible)
            {
                Show();
            }

            Activate();
        }
        catch
        {
            // 定位失败不应影响主窗口
        }
    }

    /// <summary>
    /// 只更新位置，**不 Show、不 Activate、不做布局**。
    ///
    /// 拖动角色时每一帧都会调用它。如果这里走 ShowBelow，就会每帧 Activate 一次，
    /// 高频抢焦点会让角色窗口与工具栏一起闪（表现就是拖动时 UI 抽搐）。
    /// </summary>
    public void MoveBelow(Window owner)
    {
        try
        {
            var width = ActualWidth > 0 ? ActualWidth : Width;
            var height = ActualHeight > 0 ? ActualHeight : Height;

            /*
             * 优先用 owner.Width/Height（显式设置，立刻生效），而不是 ActualHeight。
             *
             * 原因：改倍率时 MainWindow 先设 Height、紧接着就调 ShowBelow，
             * 此时 WPF 还没走完布局，ActualHeight 仍是**旧值**，
             * 于是功能栏会按旧高度定位 —— 表现就是缩放后功能栏错位到很远的地方。
             */
            /*
             * 全部按**物理像素**算，并且用"角色当前所在的那块屏"的工作区。
             * 以前用 SystemParameters.WorkArea（只有主屏），双屏时功能栏会被拽回主屏，
             * 与角色分处两块屏，按钮自然点不到。
             */
            var ownerRect = MonitorGeometry.GetPhysicalRect(owner);
            var selfRect = MonitorGeometry.GetPhysicalRect(this);

            var selfWidth = selfRect.Width > 0 ? selfRect.Width : width;
            var selfHeight = selfRect.Height > 0 ? selfRect.Height : height;

            var scale = System.Windows.Media.VisualTreeHelper.GetDpi(owner).DpiScaleX;
            var gap = 6 * scale;

            var left = ownerRect.Left + (ownerRect.Width - selfWidth) / 2;
            var below = ownerRect.Bottom + gap;
            var above = ownerRect.Top - selfHeight - gap;

            var work = MonitorGeometry.WorkAreaFor(ownerRect);
            var top = below;

            // 下方放不下就翻到上方
            if (below + selfHeight > work.Bottom)
            {
                top = above >= work.Top ? above : Math.Max(work.Top, work.Bottom - selfHeight);
            }

            var desired = new Rect(
                Math.Clamp(left, work.Left, Math.Max(work.Left, work.Right - selfWidth)),
                Math.Clamp(top, work.Top, Math.Max(work.Top, work.Bottom - selfHeight)),
                selfWidth,
                selfHeight);

            MonitorGeometry.MoveToPhysical(this, desired);
        }
        catch
        {
            // 定位失败不应影响主窗口
        }
    }

    private void OnSettingsClick(object sender, RoutedEventArgs e)
    {
        SettingsRequested?.Invoke(this, EventArgs.Empty);
    }

    private void OnAppearanceClick(object sender, RoutedEventArgs e)
    {
        AppearanceRequested?.Invoke(this, EventArgs.Empty);
    }

    private void OnResetAppearanceClick(object sender, RoutedEventArgs e)
    {
        ResetAppearanceRequested?.Invoke(this, EventArgs.Empty);
    }

    private void OnChatClick(object sender, RoutedEventArgs e)
    {
        ChatRequested?.Invoke(this, EventArgs.Empty);
    }
}
