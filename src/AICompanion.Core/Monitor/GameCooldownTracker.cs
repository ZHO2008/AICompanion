namespace AICompanion.Core.Monitor;

/// <summary>
/// 冷却期记录（手册 §3.9 冷却期 + 决策 Q11）。
///
/// 手册原文要求"冷却期绑定到触发恢复的那个具体进程名"，但 restore 的前提是
/// **所有**游戏进程都已退出，此时并不存在"触发恢复的那个进程"。
/// 因此按决策 Q11 修正为：绑定"**最后退出的那个游戏进程名**" ——
/// 语义上等价（就是刚刚让程序恢复显存的那个游戏），而且一定存在。
///
/// 两侧各持有一份：主程序用自己的那份做最终裁决，GameMonitor 用收到的
/// cooldown 消息更新自己那份以免盲发。主程序侧始终是权威。
/// </summary>
public sealed class GameCooldownTracker
{
    private readonly object _sync = new();
    private readonly Dictionary<string, DateTime> _untilByProcess = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>设置冷却期。</summary>
    public void SetCooldown(string? processName, TimeSpan duration)
    {
        if (string.IsNullOrWhiteSpace(processName))
        {
            return;
        }

        var until = DateTime.UtcNow + duration;
        lock (_sync)
        {
            PruneExpiredLocked();
            _untilByProcess[processName] = until;
        }
    }

    /// <summary>直接按绝对时间设置（用于接收主程序下发的 cooldown 消息）。</summary>
    public void SetCooldownUntil(string? processName, DateTime untilUtc)
    {
        if (string.IsNullOrWhiteSpace(processName))
        {
            return;
        }

        lock (_sync)
        {
            PruneExpiredLocked();
            _untilByProcess[processName] = untilUtc.Kind == DateTimeKind.Utc ? untilUtc : untilUtc.ToUniversalTime();
        }
    }

    /// <summary>该进程是否仍在冷却期内。不同进程名互不影响（手册明确要求）。</summary>
    public bool IsInCooldown(string? processName)
    {
        if (string.IsNullOrWhiteSpace(processName))
        {
            return false;
        }

        lock (_sync)
        {
            if (!_untilByProcess.TryGetValue(processName, out var until))
            {
                return false;
            }

            if (DateTime.UtcNow >= until)
            {
                _untilByProcess.Remove(processName);
                return false;
            }

            return true;
        }
    }

    /// <summary>返回剩余冷却时间，供日志与气泡说明。</summary>
    /// <summary>
    /// 清掉已过期的冷却项。原先只在 <see cref="IsInCooldown"/> 命中时惰性删除，
    /// 那些"设过一次就再没被查询过"的进程名会一直留在字典里（长驻进程的慢泄漏）。
    /// </summary>
    private void PruneExpiredLocked()
    {
        if (_untilByProcess.Count == 0)
        {
            return;
        }

        var now = DateTime.UtcNow;
        var expired = _untilByProcess.Where(kv => now >= kv.Value).Select(kv => kv.Key).ToList();
        foreach (var key in expired)
        {
            _untilByProcess.Remove(key);
        }
    }

    public TimeSpan RemainingCooldown(string? processName)
    {
        if (string.IsNullOrWhiteSpace(processName))
        {
            return TimeSpan.Zero;
        }

        lock (_sync)
        {
            if (!_untilByProcess.TryGetValue(processName, out var until))
            {
                return TimeSpan.Zero;
            }

            var remaining = until - DateTime.UtcNow;
            return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
        }
    }

    /// <summary>清理已过期的记录，避免长期运行时字典无限增长。</summary>
    public int PruneExpired()
    {
        lock (_sync)
        {
            var now = DateTime.UtcNow;
            var expired = _untilByProcess.Where(kv => kv.Value <= now).Select(kv => kv.Key).ToList();
            foreach (var key in expired)
            {
                _untilByProcess.Remove(key);
            }

            return expired.Count;
        }
    }

    public IReadOnlyDictionary<string, DateTime> Snapshot()
    {
        lock (_sync)
        {
            return new Dictionary<string, DateTime>(_untilByProcess, StringComparer.OrdinalIgnoreCase);
        }
    }
}
