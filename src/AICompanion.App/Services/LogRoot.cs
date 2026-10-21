using System.IO;
using AICompanion.Core.Configuration;

namespace AICompanion.App.Services;

/// <summary>
/// 解析日志根目录。
///
/// 为什么不能直接用 <see cref="Environment.CurrentDirectory"/>：
/// 开发时 CWD 是仓库根目录，日志落在 <c>logs\app</c> 下很方便；
/// 但装成安装包以后 CWD 可能是任意位置（快捷方式、开始菜单、开机自启），
/// 甚至可能指向只读目录（Program Files），写日志会直接失败。
///
/// 策略：按 <c>当前目录 → 程序目录 → %LOCALAPPDATA%\AICompanion</c> 的顺序找第一个**可写**的，
/// 找不到就退回当前目录（让上层的日志失败暴露出来，而不是悄悄丢日志）。
/// </summary>
public static class LogRoot
{
    private static string? _cached;

    /// <summary>可写的日志根目录（不含 logs\app 后缀）。</summary>
    public static string Resolve()
    {
        if (_cached is not null)
        {
            return _cached;
        }

        foreach (var candidate in Candidates())
        {
            if (string.IsNullOrWhiteSpace(candidate))
            {
                continue;
            }

            if (TryPrepare(candidate))
            {
                _cached = candidate;
                return _cached;
            }
        }

        _cached = Environment.CurrentDirectory;
        return _cached;
    }

    /// <summary>
    /// 完整的日志目录（<c>&lt;root&gt;\logs\app</c>）。
    /// </summary>
    public static string AppLogDirectory() => Path.Combine(Resolve(), "logs", "app");

    private static IEnumerable<string> Candidates()
    {
        yield return Environment.CurrentDirectory;
        yield return AppPaths.Root;

        string localAppData;

        try
        {
            localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        }
        catch
        {
            localAppData = string.Empty;
        }

        if (!string.IsNullOrWhiteSpace(localAppData))
        {
            yield return Path.Combine(localAppData, "AICompanion");
        }
    }

    /// <summary>真正建一次目录并写个探针文件，确认可写。</summary>
    private static bool TryPrepare(string root)
    {
        try
        {
            var dir = Path.Combine(root, "logs", "app");
            Directory.CreateDirectory(dir);

            var probe = Path.Combine(dir, ".write-probe");

            File.WriteAllText(probe, string.Empty);
            File.Delete(probe);

            return true;
        }
        catch
        {
            return false;
        }
    }
}
