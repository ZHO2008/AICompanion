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
        var finished = await Task.WhenAny(all, Task.Delay(timeout, cancellationToken)).ConfigureAwait(false);

        if (finished != all)
        {
            TryKill(process);
            return new ProcessResult(true, -1, SafeResult(stdoutTask), SafeResult(stderrTask), true,
                $"超时（{timeout.TotalSeconds:N0} 秒）未返回。");
        }

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
