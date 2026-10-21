using System.Runtime.InteropServices;
using System.Windows.Threading;
using AICompanion.Core.Abstractions;
using AICompanion.Core.Input;

namespace AICompanion.App.Services;

/// <summary>
/// 按住说话（push-to-talk）的全局按键监听。
///
/// 为什么用轮询 GetAsyncKeyState 而不是键盘钩子（WH_KEYBOARD_LL）：
///   - 钩子会插进系统的键盘输入链，任何一个实现的疏漏都可能拖慢或吞掉用户按键，
///     对一个桌面伴侣来说风险不对称（我们要的是"顺带听一下"，不是输入法）；
///   - 轮询 60 ms 一次的开销可以忽略，而且天然能拿到"当前是否按住"，
///     不需要自己拼 按下/抬起 事件；
///   - 不需要管理员权限，也不会被杀毒软件当成键盘记录器。
///
/// 代价是最长 60 ms 的响应延迟，人手感知不到。
/// 按键描述的解析在 <see cref="KeyCombo"/>（Core 层，可被探针直接测）。
/// </summary>
public sealed class PushToTalkMonitor : IDisposable
{
    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    private readonly DispatcherTimer _timer;
    private readonly IAppLogger _logger;
    private readonly Action _onPress;
    private readonly Action _onRelease;
    private readonly int[] _modifiers;
    private readonly int _mainKey;
    private readonly string _spec;

    private bool _isDown;
    private bool _disposed;

    // 松开去抖：连续这么多个 tick 都读到"未按住"才真正结束录音。
    // 键盘/注入的按键状态偶尔会抖一下（实测模拟按键约 0.2-0.4 秒就被系统放开），
    // 单次读到"松开"就收尾会把用户的话截断。代价是尾部多 120 ms，听感上反而更自然。
    private const int ReleaseDebounceTicks = 2;

    private int _upTicks;

    /// <param name="dispatcher">
    /// 定时器所属的 Dispatcher。必须显式传入 UI 线程的 Dispatcher：
    /// 这个对象是在 AppHost 的 async 续体里创建的，那里已经跑在线程池上，
    /// 用 Dispatcher.CurrentDispatcher 会新建一个永远不会被 pump 的 Dispatcher，
    /// 定时器虽然有 Start() 但一次都不会触发。
    /// </param>
    public PushToTalkMonitor(
        string keySpec,
        IAppLogger logger,
        Action onPress,
        Action onRelease,
        System.Windows.Threading.Dispatcher? dispatcher = null)
    {
        _spec = keySpec;
        _logger = logger.ForCategory("PTT");
        _onPress = onPress;
        _onRelease = onRelease;

        if (!KeyCombo.TryParse(keySpec, out _modifiers, out _mainKey))
        {
            throw new ArgumentException(
                $"无法解析按住说话的按键配置：{keySpec}（示例：Home / F2 / Alt+Q / Ctrl+Alt+Q）",
                nameof(keySpec));
        }

        // 60 ms：人耳/人手感知不到，CPU 开销可以忽略。
        _timer = new DispatcherTimer(DispatcherPriority.Background, dispatcher ?? System.Windows.Threading.Dispatcher.CurrentDispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(60),
        };
        _timer.Tick += OnTick;
    }

    public void Start()
    {
        if (_disposed)
        {
            return;
        }

        _logger.Info($"按住说话监听已启动：按住 {_spec} 录音，松开识别。");
        _timer.Start();
    }

    public void Stop()
    {
        _timer.Stop();

        // 停止时如果还按着，补一次松开，避免状态卡在录音中。
        if (_isDown)
        {
            _isDown = false;
            SafeInvoke(_onRelease, "松开");
        }
    }

    private void OnTick(object? sender, EventArgs e)
    {
        if (_disposed)
        {
            return;
        }

        var down = IsComboDown();

        if (down)
        {
            _upTicks = 0;

            if (!_isDown)
            {
                _isDown = true;
                SafeInvoke(_onPress, "按下");
            }

            return;
        }

        // 未按住：按下去抖确认，避免状态毛刺把录音提前截断。
        if (!_isDown)
        {
            _upTicks = 0;
            return;
        }

        _upTicks++;

        if (_upTicks < ReleaseDebounceTicks)
        {
            return;
        }

        _upTicks = 0;
        _isDown = false;
        SafeInvoke(_onRelease, "松开");
    }

    private bool IsComboDown()
    {
        foreach (var modifier in _modifiers)
        {
            if (!IsKeyDown(modifier))
            {
                return false;
            }
        }

        return IsKeyDown(_mainKey);
    }

    private static bool IsKeyDown(int vKey) => (GetAsyncKeyState(vKey) & 0x8000) != 0;

    private void SafeInvoke(Action action, string phase)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            _logger.Error($"按住说话{phase}处理异常：{ex.Message}", ex);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _timer.Tick -= OnTick;
        _timer.Stop();
    }
}