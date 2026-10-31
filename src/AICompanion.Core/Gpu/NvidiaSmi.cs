using AICompanion.Core.Abstractions;
using AICompanion.Core.Interop;

namespace AICompanion.Core.Gpu;

public sealed record NvidiaSmiValue(bool Success, int Value, string Raw, string? FailureReason);

/// <summary>
/// nvidia-smi 查询封装（手册 §3.3 / §3.7 / §3.9 都要用它）。
///
/// 设计取舍：全部走外部进程调用而不是 NVML P/Invoke ——
///   - 手册明确要求用 nvidia-smi 命令行；
///   - 无 NVIDIA 驱动时表现为"进程启动失败"，正好对应手册的"命令执行失败"分支；
///   - 不需要随附 nvml.dll 的位数匹配问题。
/// </summary>
public sealed class NvidiaSmi
{
    /// <summary>nvidia-smi 不在 PATH 时的常见安装位置。</summary>
    private static readonly string[] FallbackPaths =
    {
        @"C:\Windows\System32\nvidia-smi.exe",
        @"C:\Program Files\NVIDIA Corporation\NVSMI\nvidia-smi.exe",
    };

    private readonly IAppLogger _logger;
    private readonly TimeSpan _timeout;
    private string? _resolvedExecutable;

    public NvidiaSmi(IAppLogger logger, TimeSpan? timeout = null)
    {
        _logger = logger.ForCategory("NvidiaSmi");
        _timeout = timeout ?? TimeSpan.FromSeconds(10);
    }

    /// <summary>该机器上 nvidia-smi 是否可用（结果会被缓存）。</summary>
    public async Task<bool> IsAvailableAsync(CancellationToken cancellationToken = default)
        => await ResolveExecutableAsync(cancellationToken).ConfigureAwait(false) is not null;

    /// <summary>空闲显存（MB）。失败时 Success=false。</summary>
    public Task<NvidiaSmiValue> QueryFreeMemoryMbAsync(CancellationToken cancellationToken = default)
        => QueryIntegerAsync("--query-gpu=memory.free --format=csv,noheader,nounits", cancellationToken);

    /// <summary>总显存（MB）。用于"按显存档位"挑选保守预算（见 VramTiers）。</summary>
    public Task<NvidiaSmiValue> QueryTotalMemoryMbAsync(CancellationToken cancellationToken = default)
        => QueryIntegerAsync("--query-gpu=memory.total --format=csv,noheader,nounits", cancellationToken);

    /// <summary>GPU 使用率（百分比）。失败时 Success=false。</summary>
    public Task<NvidiaSmiValue> QueryUtilizationPercentAsync(CancellationToken cancellationToken = default)
        => QueryIntegerAsync("--query-gpu=utilization.gpu --format=csv,noheader,nounits", cancellationToken);

    /// <summary>已用显存（MB），用于气泡里的只读展示（手册 §3.3"运行时气泡"）。</summary>
    public Task<NvidiaSmiValue> QueryUsedMemoryMbAsync(CancellationToken cancellationToken = default)
        => QueryIntegerAsync("--query-gpu=memory.used --format=csv,noheader,nounits", cancellationToken);

    /// <summary>显存使用率百分比（已用/总量×100），供 ConversationRefiner 的 60% 门槛判断（手册 §3.7）。</summary>
    public async Task<NvidiaSmiValue> QueryMemoryUtilizationPercentAsync(CancellationToken cancellationToken = default)
    {
        var result = await QueryAsync(
            "--query-gpu=memory.used,memory.total --format=csv,noheader,nounits",
            cancellationToken).ConfigureAwait(false);

        if (!result.Success)
        {
            return new NvidiaSmiValue(false, 0, result.Raw, result.FailureReason);
        }

        var parts = result.Raw.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2
            || !ProcessRunner.TryParseFirstInteger(parts[0], out var used)
            || !ProcessRunner.TryParseFirstInteger(parts[1], out var total)
            || total <= 0)
        {
            return new NvidiaSmiValue(false, 0, result.Raw, $"无法从 \"{result.Raw}\" 解析已用/总显存。");
        }

        var percent = (int)Math.Round(used * 100.0 / total);
        return new NvidiaSmiValue(true, percent, result.Raw, null);
    }

    private async Task<NvidiaSmiValue> QueryIntegerAsync(string arguments, CancellationToken cancellationToken)
    {
        var result = await QueryAsync(arguments, cancellationToken).ConfigureAwait(false);
        if (!result.Success)
        {
            return result;
        }

        if (!ProcessRunner.TryParseFirstInteger(result.Raw, out var value))
        {
            return new NvidiaSmiValue(false, 0, result.Raw, $"无法从 \"{result.Raw}\" 解析整数。");
        }

        return new NvidiaSmiValue(true, value, result.Raw, null);
    }

    private async Task<NvidiaSmiValue> QueryAsync(string arguments, CancellationToken cancellationToken)
    {
        var exe = await ResolveExecutableAsync(cancellationToken).ConfigureAwait(false);
        if (exe is null)
        {
            return new NvidiaSmiValue(false, 0, string.Empty, "未找到可执行的 nvidia-smi。");
        }

        var run = await ProcessRunner.RunAsync(exe, arguments, _timeout, cancellationToken).ConfigureAwait(false);
        if (!run.Started)
        {
            return new NvidiaSmiValue(false, 0, string.Empty, run.FailureReason ?? "nvidia-smi 启动失败。");
        }

        if (run.TimedOut)
        {
            return new NvidiaSmiValue(false, 0, string.Empty, $"nvidia-smi 超时（{_timeout.TotalSeconds:N0} 秒）。");
        }

        if (run.ExitCode != 0)
        {
            var reason = string.IsNullOrWhiteSpace(run.StandardError)
                ? $"nvidia-smi 退出码 {run.ExitCode}。"
                : $"nvidia-smi 退出码 {run.ExitCode}：{run.StandardError.Trim()}";
            return new NvidiaSmiValue(false, 0, run.StandardOutput, reason);
        }

        return new NvidiaSmiValue(true, 0, run.StandardOutput.Trim(), null);
    }

    private async Task<string?> ResolveExecutableAsync(CancellationToken cancellationToken)
    {
        if (_resolvedExecutable is not null)
        {
            return _resolvedExecutable.Length == 0 ? null : _resolvedExecutable;
        }

        var probe = await ProcessRunner.RunAsync("nvidia-smi", "--version", TimeSpan.FromSeconds(10), cancellationToken)
            .ConfigureAwait(false);

        if (probe.Started)
        {
            _resolvedExecutable = "nvidia-smi";
            return _resolvedExecutable;
        }

        foreach (var path in FallbackPaths)
        {
            if (File.Exists(path))
            {
                _logger.Info($"nvidia-smi 不在 PATH 中，改用固定路径：{path}");
                _resolvedExecutable = path;
                return _resolvedExecutable;
            }
        }

        _logger.Warn($"未找到 nvidia-smi（PATH 探测失败，候选路径也不存在）：{probe.FailureReason}");
        _resolvedExecutable = string.Empty;   // 缓存失败结果，避免每次都重复探测。
        return null;
    }
}
