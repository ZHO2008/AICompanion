using AICompanion.Core.Abstractions;
using AICompanion.Core.Configuration;
using AICompanion.Core.Context;
using AICompanion.Core.Interop;
using AICompanion.Core.Refine;
using AICompanion.Core.Tts;

namespace AICompanion.Core.Scheduling;

public enum MaintenanceOutcome
{
    NotStarted = 0,
    Completed = 1,
    Cancelled = 2,
    Failed = 3,
}

public sealed class MaintenanceEventArgs : EventArgs
{
    public required MaintenanceOutcome Outcome { get; init; }

    public string? Detail { get; init; }
}

/// <summary>
/// 空闲任务调度器（手册 §3.6）。
///
/// 触发条件（三者同时满足）：
///   1. 距最后一次用户输入超过 5 分钟（可配置）；
///   2. CPU 总使用率低于 50%（可配置）；
///   3. 系统未处于待机/休眠（用时钟漂移检测，见 <see cref="SuspendDetector"/>）。
///
/// 中断条件（任务执行期间持续检查）：
///   用户产生新输入，或 CPU 超过 70% → 立即 Cancel()。
///   因此每个任务内部都在耗时操作前检查 IsCancellationRequested。
///
/// 定时器间隔 500 毫秒（手册指定），但定时器回调本身不做任何重活，只做条件判断与启动任务。
/// </summary>
public sealed class IdleTaskScheduler : IDisposable
{
    private readonly AppConfig _config;
    private readonly IAppLogger _logger;
    private readonly IBubbleNotifier _bubble;
    private readonly ContextManager _context;
    private readonly ConversationRefiner _refiner;
    private readonly TTSManager _tts;

    private readonly CpuUsageSampler _cpu = new();
    private readonly SuspendDetector _suspend = new();
    private readonly object _sync = new();

    private Timer? _timer;
    private CancellationTokenSource? _taskCts;
    private DateTime _lastUserActivityUtc = DateTime.UtcNow;
    private bool _userActiveDuringTask;
    private volatile bool _taskRunning;
    private bool _disposed;

    public IdleTaskScheduler(
        AppConfig config,
        IAppLogger logger,
        IBubbleNotifier bubble,
        ContextManager context,
        ConversationRefiner refiner,
        TTSManager tts)
    {
        _config = config;
        _logger = logger.ForCategory("Idle");
        _bubble = bubble;
        _context = context;
        _refiner = refiner;
        _tts = tts;
    }


    /// <summary>最后一次用户活动时间，供其它模块（如 TTS 软重启）判断空闲。</summary>
    public DateTime LastUserActivityUtc
    {
        get
        {
            lock (_sync)
            {
                return _lastUserActivityUtc;
            }
        }
    }

    public TimeSpan UserIdle => DateTime.UtcNow - LastUserActivityUtc;

    public double LastCpuPercent { get; private set; }

    public event EventHandler<MaintenanceEventArgs>? MaintenanceFinished;

    public void Start()
    {
        var interval = TimeSpan.FromMilliseconds(Math.Max(100, _config.Idle.PollIntervalMs));
        _timer = new Timer(_ => Tick(), null, interval, interval);

        _logger.Info(
            $"空闲调度器已启动（轮询 {interval.TotalMilliseconds:N0} ms，" +
            $"空闲阈值 {_config.Idle.IdleMinutes:N0} 分钟，CPU 上限 {_config.Idle.CpuLowPercent:N0}%）。");
    }

    public void Stop()
    {
        try
        {
            _timer?.Dispose();
            _timer = null;
        }
        catch
        {
            // 忽略。
        }

        try
        {
            _taskCts?.Cancel();
        }
        catch
        {
            // 忽略。
        }
    }

    /// <summary>任何用户操作（文字输入、点击、语音）都要调用它，用于重置空闲计时并中断维护任务。</summary>
    public void NotifyUserActivity()
    {
        lock (_sync)
        {
            _lastUserActivityUtc = DateTime.UtcNow;
            if (_taskRunning)
            {
                _userActiveDuringTask = true;
            }
        }
    }

    private void Tick()
    {
        if (_disposed)
        {
            return;
        }

        try
        {
            LastCpuPercent = _cpu.Sample();

            // 待机/休眠检测：漂移超过 30 秒即认为刚发生过待机。
            var suspended = _suspend.Sample(TimeSpan.FromSeconds(30));
            if (suspended > TimeSpan.Zero)
            {
                _logger.Info($"检测到系统待机/休眠约 {suspended.TotalMinutes:N1} 分钟，跳过本轮维护。");
                lock (_sync)
                {
                    // 把空闲计时也重置，避免唤醒后立刻触发维护。
                    _lastUserActivityUtc = DateTime.UtcNow;
                }

                return;
            }

            if (_taskRunning)
            {
                CheckInterrupt();
                return;
            }

            if (!ShouldStartMaintenance())
            {
                return;
            }

            _ = RunMaintenanceAsync();
        }
        catch (Exception ex)
        {
            // 定时器回调里的异常会静默吞掉整个回调，必须自己记日志。
            _logger.Error($"空闲调度 tick 异常：{ex.Message}", ex);
        }
    }

    private bool ShouldStartMaintenance()
    {
        if (UserIdle < TimeSpan.FromMinutes(_config.Idle.IdleMinutes))
        {
            return false;
        }

        // 条件 2：CPU 低于阈值。首次采样返回 0，会被当作"空闲"，这是可接受的保守行为。
        if (LastCpuPercent >= _config.Idle.CpuLowPercent)
        {
            return false;
        }

        // 条件 3：刚发生过待机/休眠时不启动（唤醒后系统通常很忙）。
        if (_suspend.WasRecentlySuspended(TimeSpan.FromMinutes(2)))
        {
            return false;
        }

        return true;
    }

    private void CheckInterrupt()
    {
        var cpuHigh = LastCpuPercent > _config.Idle.CpuHighPercent;
        bool userActive;

        lock (_sync)
        {
            userActive = _userActiveDuringTask;
        }

        if (!cpuHigh && !userActive)
        {
            return;
        }

        var reason = userActive
            ? "检测到用户操作"
            : $"CPU 使用率 {LastCpuPercent:N1}% 超过 {_config.Idle.CpuHighPercent:N0}%";

        _logger.Info($"{reason}，请求取消当前维护任务。");

        try
        {
            _taskCts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // 任务刚好结束，忽略。
        }
    }

    /// <summary>按手册 §3.6 的任务序列依次执行。</summary>
    private async Task RunMaintenanceAsync()
    {
        CancellationTokenSource cts;

        lock (_sync)
        {
            if (_taskRunning)
            {
                return;
            }

            _taskRunning = true;
            _userActiveDuringTask = false;
            cts = new CancellationTokenSource();
            _taskCts = cts;
        }

        var outcome = MaintenanceOutcome.Completed;
        string? detail = null;
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        try
        {
            var token = cts.Token;
            _logger.Info("开始执行空闲维护任务序列。");

            // 任务 1：上下文压缩。
            token.ThrowIfCancellationRequested();
            await _context.CompressIfNeededAsync(token).ConfigureAwait(false);

            // 任务 2：对话整理（产出待审核候选）。
            token.ThrowIfCancellationRequested();
            await _refiner.RefineFromRecentChatAsync(token).ConfigureAwait(false);

            // 任务 3：Chatterbox 软重启检查。
            token.ThrowIfCancellationRequested();
            await _tts.CheckSoftRestartAsync(UserIdle, token).ConfigureAwait(false);

            detail = $"耗时 {stopwatch.Elapsed.TotalSeconds:N1} 秒";
        }
        catch (OperationCanceledException)
        {
            outcome = MaintenanceOutcome.Cancelled;
            detail = "被用户操作或高 CPU 中断";
            _logger.Info($"维护任务已取消（{detail}）。");
        }
        catch (Exception ex)
        {
            outcome = MaintenanceOutcome.Failed;
            detail = ex.Message;
            _logger.Error($"维护任务失败：{ex.Message}", ex);
        }
        finally
        {
            stopwatch.Stop();

            lock (_sync)
            {
                _taskRunning = false;
                _taskCts = null;
                _userActiveDuringTask = false;
            }

            cts.Dispose();

            if (outcome == MaintenanceOutcome.Completed)
            {
                _logger.Info($"维护任务完成（{detail}）。");
            }

            MaintenanceFinished?.Invoke(this, new MaintenanceEventArgs { Outcome = outcome, Detail = detail });
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Stop();

        try
        {
            _taskCts?.Dispose();
        }
        catch
        {
            // 忽略。
        }
    }
}
