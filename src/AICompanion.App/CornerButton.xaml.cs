using System.Windows;

namespace AICompanion.App;

/// <summary>
/// 角色右上角的「透明 / 覆盖」切换按钮。
///
/// 为什么必须是独立窗口：透明态下主窗口带 <c>WS_EX_TRANSPARENT</c>，
/// 所有鼠标事件都会穿透到下层，放在主窗口里的按钮在那个模式下完全点不到。
/// 独立窗口不带该样式，因此**两种模式下都能点** —— 这是从透明态切回覆盖态的可靠入口
/// （悬停切换在光标恰好在角色身上时并不可靠）。
/// </summary>
public partial class CornerButton : Window
{
    /// <summary>按钮相对角色右上角的外扩像素，避免压住角色本体。</summary>
    private const double OffsetOut = -6;

    /// <summary>基准尺寸与基准字号（倍率 1.0 时的大小）。</summary>
    private const double BaseSize = 34;
    private const double BaseFontSize = 13;

    /// <summary>缩放后的下限：再小就点不准了，也看不清「覆/透」。</summary>
    private const double MinSize = 18;
    private const double MinFontSize = 9.5;

    /// <summary>当前是否处于透明（穿透）态，决定按钮显示的文字。</summary>

    public event EventHandler? ToggleRequested;

    public CornerButton()
    {
        InitializeComponent();

        Width = BaseSize;
        Height = BaseSize;
    }

    /// <summary>
    /// 与角色同倍率缩放。
    ///
    /// 它是独立窗口，不会自动跟随角色的 LayoutTransform，
    /// 不显式缩放的话：角色缩到 50% 时按钮还是 34×34，比角色脑袋还大，
    /// 看着像另一个程序贴上去的东西。
    /// </summary>
    public void ApplyScale(double scale)
    {
        var s = Math.Clamp(scale, 0.2, 3.0);

        var size = Math.Max(MinSize, BaseSize * s);

        Width = size;
        Height = size;

        ModeText.FontSize = Math.Max(MinFontSize, BaseFontSize * s);
    }

    /// <summary>
    /// 贴到角色的右上角。
    ///
    /// 全程用**屏幕物理像素**：以前用 <c>SystemParameters.WorkArea</c>（只有主屏）夹取，
    /// 双屏时按钮会被拽回主屏边缘，跟角色彻底脱节 —— 角色在副屏、按钮在主屏，点都点不到。
    /// 现在按角色实际所在的显示器定位，贴边时也在那一块屏内夹取。
    /// </summary>
    private void PositionAt(Window pet)
    {
        var petRect = MonitorGeometry.GetPhysicalRect(pet);
        var selfRect = MonitorGeometry.GetPhysicalRect(this);

        var selfWidth = selfRect.Width > 0 ? selfRect.Width : Width;
        var selfHeight = selfRect.Height > 0 ? selfRect.Height : Height;

        // OffsetOut 是 DIP，按角色所在屏的缩放换算成物理像素
        var scale = System.Windows.Media.VisualTreeHelper.GetDpi(pet).DpiScaleX;
        var offset = OffsetOut * scale;

        var desired = new Rect(
            petRect.Right - selfWidth + offset,
            petRect.Top + offset,
            selfWidth,
            selfHeight);

        MonitorGeometry.MoveToPhysical(this, MonitorGeometry.ClampInside(desired));
    }
    /// <summary>把按钮贴到角色窗口的右上角。</summary>
    public void ShowAt(Window pet)
    {
        try
        {
            if (!IsVisible)
            {
                // 先显示再定位，避免首帧跳到左上角
                Show();
            }

            PositionAt(pet);
        }
        catch
        {
            // 定位失败不应影响主窗口
        }
    }

    /// <summary>只挪位置，不激活（拖动角色时每帧调用）。</summary>
    public void MoveTo(Window pet)
    {
        try
        {
            PositionAt(pet);
        }
        catch
        {
        }
    }

    /// <summary>更新按钮上的模式标记：覆盖态显示「覆」，透明态显示「透」。</summary>
    public void SetMode(bool clickThrough)
    {
        ModeText.Text = clickThrough ? "透" : "覆";

        // 冻结：每次切换模式都 new 两个画刷，未冻结的画刷带 Freezable 事件开销，
        // 而且这两个颜色是常量，冻结后 WPF 可以走更快的渲染路径。
        var brush = new System.Windows.Media.SolidColorBrush(
            clickThrough
                ? System.Windows.Media.Color.FromRgb(0xFF, 0xC9, 0x7A)
                : System.Windows.Media.Color.FromRgb(0xF0, 0xF0, 0xF0));

        brush.Freeze();
        ModeText.Foreground = brush;

        ToggleButton.ToolTip = clickThrough
            ? "当前：透明（点击穿透）。点此切回覆盖（可交互）"
            : "当前：覆盖（可交互）。点此切为透明（点击穿透）";
    }

    private void OnToggleClick(object sender, RoutedEventArgs e)
    {
        ToggleRequested?.Invoke(this, EventArgs.Empty);
    }
}
