using System.Windows.Threading;
using AICompanion.Core.Abstractions;

namespace AICompanion.App.Animation;

/// <summary>
/// 动作调度管理器（线程安全 + 角色包映射 + 安全兜底）。
///
/// 三条硬约束：
///   1. **对外只收 <see cref="AnimationType"/> 枚举**，不收字符串 —— 动作名由
///      <see cref="AvatarProfile.MotionMap"/> 解析，换模型不用改代码；
///   2. 后台服务（ASR / LLM / TTS / 音频线程）只调 <see cref="Request(AnimationType, AnimationPriority, string, TimeSpan?)"/>，
///      它内部 <c>Dispatcher.BeginInvoke</c> 切回 UI 线程，绝不阻塞后台；
///   3. 播放前先确认动作存在；映射缺失或模型没有该动作 → 播 <see cref="AnimationType.Fallback"/> 并记 Warning。
/// </summary>
public sealed class AnimationManager
{
    private static readonly TimeSpan DebounceWindow = TimeSpan.FromSeconds(1);

    private readonly Dispatcher _dispatcher;
    private readonly IAppLogger _logger;

    /// <summary>真正播放动作的回调：(逻辑动作, 已解析并校验过的动作名)。只在 UI 线程调用。</summary>
    private readonly Action<AnimationType, string> _playMotion;

    /// <summary>
    /// 动作存在性检查（由宿主注入）。
    /// TODO: 接 Live2D / VPet 后替换为真实检查，例如解析 model3.json 的 Motions 列表，
    ///       或调用 SDK 的 HasMotion(group) —— 我无法预设你本机 SDK 的接口名。
    /// 传 null 表示"无法检查，按存在处理"（此时只做映射存在性兜底）。
    /// </summary>
    private readonly Func<string, bool>? _motionExists;

    private readonly object _sync = new();

    /// <summary>已经告警过的缺失动作，避免每帧刷日志。</summary>
    private readonly HashSet<string> _warnedMotions = new(StringComparer.OrdinalIgnoreCase);

    private AvatarProfile _profile;
    private AnimationRequest? _current;
    private DateTime _lastAppliedUtc = DateTime.MinValue;
    private DispatcherTimer? _holdTimer;

    /// <summary>当前 hold 对应的请求（复用 timer 时用字段传，避免每次 new 委托）。</summary>
    private AnimationRequest? _pendingHold;

    public AnimationManager(
        Dispatcher dispatcher,
        IAppLogger logger,
        AvatarProfile profile,
        Action<AnimationType, string> playMotion,
        Func<string, bool>? motionExists = null)
    {
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _logger = (logger ?? throw new ArgumentNullException(nameof(logger))).ForCategory("Anim");
        _profile = profile ?? AvatarProfile.CreateBuiltInDefault();
        _playMotion = playMotion ?? throw new ArgumentNullException(nameof(playMotion));
        _motionExists = motionExists;
    }

    public AvatarProfile Profile
    {
        get
        {
            lock (_sync)
            {
                return _profile;
            }
        }
    }

    /// <summary>当前模型的嘴型参数名（口型同步用）。</summary>
    public string MouthOpenParameter => Profile.MouthOpenParameter;

    public AnimationRequest? Current
    {
        get
        {
            lock (_sync)
            {
                return _current;
            }
        }
    }

    /// <summary>切换角色（换模型时调用）：替换映射表并重建告警集合。</summary>
    public void ApplyProfile(AvatarProfile profile)
    {
        if (profile is null)
        {
            return;
        }

        lock (_sync)
        {
            _profile = profile;
            _warnedMotions.Clear();
        }

        _logger.Info($"动作映射已切换到角色包：{profile.AvatarName}");
    }

    /// <summary>提交动作请求。**任意线程可调用**，立即返回。</summary>
    public void Request(AnimationType type, AnimationPriority? priority = null,
        string source = "unknown", TimeSpan? holdFor = null)
    {
        Request(new AnimationRequest(type, priority ?? AnimationPriorityDefaults.For(type), source, holdFor));
    }

    public void Request(AnimationRequest request)
    {
        if (request is null)
        {
            return;
        }

        // 后台线程到此为止：真正执行切回 UI 线程
        _dispatcher.BeginInvoke(new Action(() => ApplyOnUiThread(request)));
    }

    /// <summary>停止当前动作并回到 Idle（例如播放被打断）。</summary>
    public void ResetToIdle(string reason = "reset")
    {
        _dispatcher.BeginInvoke(new Action(() =>
        {
            _holdTimer?.Stop();

            var idle = new AnimationRequest(AnimationType.Idle, AnimationPriority.Idle, reason);
            lock (_sync)
            {
                _current = idle;
                _lastAppliedUtc = DateTime.UtcNow;
            }

            PlaySafe(idle);
        }));
    }

    // ------------------------------------------------------------------ UI 线程

    private void ApplyOnUiThread(AnimationRequest request)
    {
        var now = DateTime.UtcNow;

        AnimationRequest? current;
        DateTime lastApplied;

        lock (_sync)
        {
            current = _current;
            lastApplied = _lastAppliedUtc;
        }

        var withinDebounce = now - lastApplied < DebounceWindow;

        // Critical 无视防抖；其余只有在优先级更高（可打断）时才放行
        if (withinDebounce &&
            request.Priority != AnimationPriority.Critical &&
            (current is null || request.Priority <= current.Priority))
        {
            _logger.Debug(
                $"丢弃动作 {request.Type}({request.Priority}, 来源={request.Source})：{DebounceWindow.TotalSeconds:F0}s 防抖窗口内，" +
                $"当前为 {current?.Type.ToString() ?? "无"}");
            return;
        }

        var interrupted = current is not null && request.Priority > current.Priority;

        lock (_sync)
        {
            _current = request;
            _lastAppliedUtc = now;
        }

        if (interrupted)
        {
            _logger.Debug($"动作 {request.Type}({request.Priority}) 打断 {current!.Type}({current.Priority})");
        }

        PlaySafe(request);
        ScheduleHold(request);
    }

    /// <summary>
    /// 安全播放：解析映射 → 校验动作存在 → 失败则降级到 Fallback 并告警。
    /// </summary>
    private void PlaySafe(AnimationRequest request)
    {
        var profile = Profile;
        var motion = profile.GetMotionOrNull(request.Type);

        if (motion is null)
        {
            WarnOnce($"动作映射缺失：{request.Type}", $"角色包 {profile.AvatarName} 的 AvatarProfile.json 未配置 motion_map.{request.Type}");
            motion = FallbackOrNull(profile, $"映射缺失（{request.Type}）");
            if (motion is null)
            {
                return;
            }
        }
        else if (_motionExists is not null && !SafeExists(motion))
        {
            WarnOnce($"模型没有动作：{motion}", $"模型里找不到动作 \"{motion}\"（来自 {request.Type} 的映射）");
            motion = FallbackOrNull(profile, $"模型缺少动作（{motion}）");
            if (motion is null)
            {
                return;
            }
        }

        try
        {
            _playMotion(request.Type, motion);
        }
        catch (Exception ex)
        {
            // 动作实现失败不能影响对话主流程
            _logger.Warn($"播放动作 {request.Type}→{motion} 失败：{ex.Message}");
        }
    }

    private string? FallbackOrNull(AvatarProfile profile, string reason)
    {
        var fallback = profile.GetFallbackMotion();
        if (fallback is null)
        {
            _logger.Warn($"无兜底动作可用（{reason}），本次不做播放。请在 AvatarProfile.json 里配置 motion_map.Fallback。");
            return null;
        }

        // 兜底动作本身也要过一遍存在性检查
        if (_motionExists is not null && !SafeExists(fallback))
        {
            _logger.Warn($"兜底动作 \"{fallback}\" 在模型中也不存在（{reason}），本次不做播放。");
            return null;
        }

        _logger.Warn($"已降级播放兜底动作 \"{fallback}\"（{reason}）。");
        return fallback;
    }

    /// <summary>TODO: 这里的检查逻辑要换成你本机 Live2D / VPet 的真实 API。</summary>
    private bool SafeExists(string motion)
    {
        try
        {
            return _motionExists?.Invoke(motion) ?? true;
        }
        catch (Exception ex)
        {
            _logger.Warn($"检查动作 \"{motion}\" 是否存在时异常：{ex.Message}");
            return true;   // 检查失败按存在处理，避免整条链路被一个检查异常挡死
        }
    }

    private void WarnOnce(string key, string message)
    {
        lock (_sync)
        {
            if (!_warnedMotions.Add(key))
            {
                return;
            }
        }

        _logger.Warn(message);
    }

    /// <summary>HoldFor 到期自动回落 Idle，避免角色停在某个中间动作上。</summary>
    private void ScheduleHold(AnimationRequest request)
    {
        if (request.HoldFor is not { } hold || hold <= TimeSpan.Zero)
        {
            _holdTimer?.Stop();
            return;
        }

        // 复用一个 timer：每次动作都 new 一个的话，旧 timer 只 Stop 不退订，
        // Tick 委托连同它捕获的 request 会一直挂在 Dispatcher 上（动作做越多次堆得越多）。
        if (_holdTimer is null)
        {
            _holdTimer = new DispatcherTimer(DispatcherPriority.Background, _dispatcher);
            _holdTimer.Tick += (_, _) => OnHoldExpired();
        }

        _pendingHold = request;
        _holdTimer.Interval = hold;
        _holdTimer.Stop();
        _holdTimer.Start();
    }

    /// <summary>停掉 hold 定时器。窗口关闭时必须调用，否则它会继续 tick 并回调已释放在的渲染器。</summary>
    public void Dispose()
    {
        _pendingHold = null;

        var timer = _holdTimer;
        _holdTimer = null;

        if (timer is null)
        {
            return;
        }

        timer.Stop();
        timer.Tick -= (_, _) => OnHoldExpired();
    }

    private void OnHoldExpired()
    {
        _holdTimer?.Stop();

        var request = _pendingHold;
        if (request is null)
        {
            return;
        }

        lock (_sync)
        {
            if (!ReferenceEquals(_current, request))
            {
                return;
            }
        }

        ResetToIdle("hold-expired");
    }
}
