using AICompanion.Core.Abstractions;

namespace AICompanion.Core.Interop;

/// <summary>
/// 按本地监听端口找出占用它的进程并结束它。
///
/// 用途：手册 §3.9 要求"如果 Chatterbox 正在使用，同样停止其进程"。
/// 但 Chatterbox 是**外部启动**的（不在本程序托管的进程树里），
/// 所以只能反查端口占用者。用 <c>netstat -ano</c> 而不是 IP Helper API，
/// 是为了避免再引入一套 P/Invoke，同时 netstat 在所有 Windows 上都存在。
///
/// 安全性：严格限定在 127.0.0.1 上的指定端口，只结束"正在监听该端口"的进程；
/// 找不到就返回 false，绝不猜测、绝不结束其它进程。
/// </summary>
public static class PortProcessLocator
{
    public static async Task<int?> FindListenerPidAsync(int port, CancellationToken cancellationToken = default)
    {
        var result = await ProcessRunner
            .RunAsync("netstat", "-ano -p TCP", TimeSpan.FromSeconds(10), cancellationToken)
            .ConfigureAwait(false);

        if (!result.Started || result.ExitCode != 0)
        {
            return null;
        }

        foreach (var rawLine in result.StandardOutput.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0)
            {
                continue;
            }

            var fields = line.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (fields.Length < 4)
            {
                continue;
            }

            // 期望形如：TCP  127.0.0.1:5000  0.0.0.0:0  LISTENING  12345
            var local = fields[1];
            var state = fields[3];

            if (!state.Equals("LISTENING", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var colon = local.LastIndexOf(':');
            if (colon <= 0)
            {
                continue;
            }

            if (!int.TryParse(local.AsSpan(colon + 1), out var localPort) || localPort != port)
            {
                continue;
            }

            // PID 是最后一列。
            if (int.TryParse(fields[^1], out var pid) && pid > 0)
            {
                return pid;
            }
        }

        return null;
    }

    /// <summary>结束占用指定端口的进程。成功返回 true。</summary>
    public static async Task<bool> TryKillListenerOnPortAsync(int port, IAppLogger logger, CancellationToken cancellationToken = default)
    {
        var pid = await FindListenerPidAsync(port, cancellationToken).ConfigureAwait(false);
        if (pid is null)
        {
            logger.Info($"端口 {port} 上没有找到监听进程，无需结束。");
            return false;
        }

        try
        {
            using var process = System.Diagnostics.Process.GetProcessById(pid.Value);
            logger.Info($"正在结束占用端口 {port} 的进程：{process.ProcessName}（PID {pid}）。");
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex)
        {
            logger.Warn($"结束端口 {port} 的进程（PID {pid}）失败：{ex.Message}");
            return false;
        }
    }
}
