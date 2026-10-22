using System.Diagnostics;
using System.Text;

namespace AICompanion.Core.Interop;

public sealed record ProcessResult(
    bool Started,
    int ExitCode,
    string StandardOutput,
    string StandardError,
    bool TimedOut,
    string? FailureReason);

/// <summary>
/// 外部命令行调用的统一封装（nvidia-smi、piper.exe 等）。
/// 关键点：全部异步、带超时、永不抛异常 —— 调用方只看到 Started=false 或 TimedOut=true。
/// </summary>
public static class ProcessRunner
{
    public static async Task<ProcessResult> RunAsync(
        string fileName,
        string arguments,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        var psi = new ProcessStartInfo(fileName, arguments)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        using var process = new Process { StartInfo = psi };

        try
        {
            if (!process.Start())
            {
                return new ProcessResult(false, -1, string.Empty, string.Empty, false, "进程启动返回 false。");
            }
        }
        catch (Exception ex)
        {
            return new ProcessResult(false, -1, string.Empty, string.Empty, false, ex.Message);
        }

        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
        var exitTask = process.WaitForExitAsync(cancellationToken);

        var all = Task.WhenAll(stdoutTask, stderrTask, exitTask);

        // 计时任务必须可取消：进程正常结束时如果不 Cancel，这个 Task.Delay 的计时器
        // 会一直活到 timeout（nvidia-smi 是 10 秒），每次调用都留一个。
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var delayTask = Task.Delay(timeout, timeoutCts.Token);

        var finished = await Task.WhenAny(all, delayTask).ConfigureAwait(false);

        if (finished != all)
        {
            TryKill(process);

            // 进程被杀后管道立刻 EOF；给半秒把两个读流任务收掉，
            // 否则它们会在 using 释放流之后以 ObjectDisposedException 结束且无人观察。
            try
            {
                await Task.WhenAny(Task.WhenAll(stdoutTask, stderrTask), Task.Delay(500)).ConfigureAwait(false);
            }
            catch
            {
                // 忽略。
            }

            return new ProcessResult(true, -1, SafeResult(stdoutTask), SafeResult(stderrTask), true,
                $"超时（{timeout.TotalSeconds:N0} 秒）未返回。");
        }

        timeoutCts.Cancel();

        try
        {
            await all.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            return new ProcessResult(true, -1, SafeResult(stdoutTask), SafeResult(stderrTask), false, "已取消。");
        }
        catch (Exception ex)
        {
            return new ProcessResult(true, -1, SafeResult(stdoutTask), SafeResult(stderrTask), false, ex.Message);
        }

        return new ProcessResult(
            true,
            process.ExitCode,
            SafeResult(stdoutTask),
            SafeResult(stderrTask),
            false,
            null);
    }

    /// <summary>解析形如 "7338" 或 "7338 MiB" 的首个整数。</summary>
    public static bool TryParseFirstInteger(string text, out int value)
    {
        value = 0;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var digits = new StringBuilder();
        foreach (var ch in text)
        {
            if (char.IsDigit(ch))
            {
                digits.Append(ch);
            }
            else if (digits.Length > 0)
            {
                break;
            }
            else if (ch == '-' || ch == '+')
            {
                // 忽略符号。
            }
        }

        return digits.Length > 0 && int.TryParse(digits.ToString(), out value);
    }

    private static string SafeResult(Task<string> task)
    {
        try
        {
            return task.IsCompletedSuccessfully ? task.Result : string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            // 进程可能已自行退出。
        }
    }
}
