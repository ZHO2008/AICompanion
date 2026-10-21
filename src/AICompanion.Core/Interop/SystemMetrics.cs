using System.Runtime.InteropServices;

namespace AICompanion.Core.Interop;

/// <summary>
/// CPU 使用率采样器（手册 §3.6 需要"CPU 总使用率低于 50%"）。
/// 用 GetSystemTimes 计算，无需 System.Diagnostics.PerformanceCounter 包，
/// 避免在部分 Windows 上因性能计数器库损坏而抛异常。
/// 线程安全：内部用锁保护基线值，可被调度器与其它模块并发调用。
/// </summary>
public sealed class CpuUsageSampler
{
    private readonly object _sync = new();
    private ulong _prevIdle;
    private ulong _prevKernel;
    private ulong _prevUser;
    private bool _hasBaseline;

    /// <summary>
    /// 返回自上次调用以来的平均 CPU 使用率（0-100）。
    /// 首次调用没有基线，返回 0 —— 调用方应把它当作"未知/偏低"处理。
    /// </summary>
    public double Sample()
    {
        lock (_sync)
        {
            if (!NativeMethods.GetSystemTimes(out var idle, out var kernel, out var user))
            {
                return 0;
            }

            var idleNow = idle.ToUInt64();
            var kernelNow = kernel.ToUInt64();
            var userNow = user.ToUInt64();

            if (!_hasBaseline)
            {
                _prevIdle = idleNow;
                _prevKernel = kernelNow;
                _prevUser = userNow;
                _hasBaseline = true;
                return 0;
            }

            var idleDelta = idleNow - _prevIdle;
            // kernel 时间包含 idle，因此总时间 = kernel + user，忙时间 = 总时间 - idle。
            var totalDelta = (kernelNow - _prevKernel) + (userNow - _prevUser);

            _prevIdle = idleNow;
            _prevKernel = kernelNow;
            _prevUser = userNow;

            if (totalDelta == 0)
            {
                return 0;
            }

            var busy = totalDelta > idleDelta ? totalDelta - idleDelta : 0UL;
            var percent = busy * 100.0 / totalDelta;
            return Math.Clamp(percent, 0.0, 100.0);
        }
    }

    public static bool IsOnBattery()
    {
        try
        {
            if (!NativeMethods.GetSystemPowerStatus(out var status))
            {
                return false;
            }

            // ACLineStatus: 0 = 离线（电池供电），1 = 在线，255 = 未知
            return status.ACLineStatus == 0;
        }
        catch
        {
            return false;
        }
    }
}

/// <summary>
/// 待机/休眠检测（手册 §3.6 要求"系统未处于待机或休眠模式"）。
///
/// Windows 没有"我正处在待机中"的同步查询接口，因此采用**时钟漂移检测**：
/// 正常情况下 DateTime.UtcNow 与单调计时器 Environment.TickCount64 同步前进；
/// 一旦发生待机/休眠，墙上时钟会跳过一段时间，而 TickCount64 不会。
/// 两者差值超过阈值即判定"刚刚发生过待机/休眠"。
/// </summary>
public sealed class SuspendDetector
{
    private readonly object _sync = new();
    private long _lastTick;
    private DateTime _lastUtc;
    private DateTime _lastSuspendDetectedUtc = DateTime.MinValue;
    private bool _initialized;

    /// <summary>返回自上次采样以来检测到的待机时长；无待机则为 <see cref="TimeSpan.Zero"/>。</summary>
    public TimeSpan Sample(TimeSpan driftThreshold)
    {
        lock (_sync)
        {
            var nowTick = Environment.TickCount64;
            var nowUtc = DateTime.UtcNow;

            if (!_initialized)
            {
                _lastTick = nowTick;
                _lastUtc = nowUtc;
                _initialized = true;
                return TimeSpan.Zero;
            }

            var wallDelta = nowUtc - _lastUtc;
            var tickDelta = TimeSpan.FromMilliseconds(nowTick - _lastTick);

            _lastTick = nowTick;
            _lastUtc = nowUtc;

            var drift = wallDelta - tickDelta;
            if (drift > driftThreshold)
            {
                _lastSuspendDetectedUtc = nowUtc;
                return drift;
            }

            return TimeSpan.Zero;
        }
    }

    /// <summary>在指定时间窗内是否刚发生过待机/休眠（用于抑制刚唤醒就触发的空闲任务）。</summary>
    public bool WasRecentlySuspended(TimeSpan window)
    {
        lock (_sync)
        {
            if (_lastSuspendDetectedUtc == DateTime.MinValue)
            {
                return false;
            }

            return DateTime.UtcNow - _lastSuspendDetectedUtc < window;
        }
    }

    /// <summary>标记一次外部通知的休眠恢复（SystemEvents.PowerModeChanged 的 Resume）。</summary>
    public void MarkResumed()
    {
        lock (_sync)
        {
            _lastSuspendDetectedUtc = DateTime.UtcNow;
            _initialized = false;
        }
    }
}
