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

            // 用显式 Width/Height：刚改过尺寸时 ActualWidth 还是旧值
            var selfWidth = Width > 0 ? Width : ActualWidth;
            var selfHeight = Height > 0 ? Height : ActualHeight;

            // 角色窗口同样优先用显式 Width：缩放后 ActualWidth 会滞后一帧
            var petWidth = pet.Width > 0 ? pet.Width : pet.ActualWidth;

            var left = pet.Left + petWidth - selfWidth + OffsetOut;
            var top = pet.Top + OffsetOut;

            // 屏幕边界保护：角色贴边时按钮也要留在屏幕内
            var work = SystemParameters.WorkArea;
            Left = Math.Clamp(left, work.Left, Math.Max(work.Left, work.Right - selfWidth));
            Top = Math.Clamp(top, work.Top, Math.Max(work.Top, work.Bottom - selfHeight));
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
            var selfWidth = Width > 0 ? Width : ActualWidth;
            var selfHeight = Height > 0 ? Height : ActualHeight;
            var petWidth = pet.Width > 0 ? pet.Width : pet.ActualWidth;

            var left = pet.Left + petWidth - selfWidth + OffsetOut;
            var top = pet.Top + OffsetOut;

            var work = SystemParameters.WorkArea;
            Left = Math.Clamp(left, work.Left, Math.Max(work.Left, work.Right - selfWidth));
            Top = Math.Clamp(top, work.Top, Math.Max(work.Top, work.Bottom - selfHeight));
        }
        catch
        {
        }
    }

    /// <summary>更新按钮上的模式标记：覆盖态显示「覆」，透明态显示「透」。</summary>
    public void SetMode(bool clickThrough)
    {

        ModeText.Text = clickThrough ? "透" : "覆";
        ModeText.Foreground = clickThrough
            ? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xFF, 0xC9, 0x7A))
            : new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xF0, 0xF0, 0xF0));

        ToggleButton.ToolTip = clickThrough
            ? "当前：透明（点击穿透）。点此切回覆盖（可交互）"
            : "当前：覆盖（可交互）。点此切为透明（点击穿透）";
    }

    private void OnToggleClick(object sender, RoutedEventArgs e)
    {
        ToggleRequested?.Invoke(this, EventArgs.Empty);
    }
}
