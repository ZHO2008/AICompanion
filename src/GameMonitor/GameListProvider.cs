using System.Diagnostics;
using System.Text;
using AICompanion.Core.Abstractions;

namespace GameMonitor;

/// <summary>
/// 读取 game_list.txt（手册 §3.9 配置）：与 GameMonitor.exe 同目录，每行一个游戏主进程名。
/// 文件不存在时自动生成带注释的模板，避免用户面对一个"没反应"的程序。
/// </summary>
public sealed class GameListProvider
{
    private const string Template = """
        # AI 伴侣 · 游戏检测列表
        # 每行一个游戏主进程名（含或不含 .exe 都可以，大小写不敏感）。
        # 以 # 开头的行与空行会被忽略。
        #
        # 示例：
        # GenshinImpact.exe
        # YuanShen.exe
        # starrail.exe

        """;

    private readonly IAppLogger _logger;
    private readonly string _path;
    private readonly object _sync = new();
    private List<string> _names = new();
    private DateTime _lastWriteUtc = DateTime.MinValue;

    public GameListProvider(string path, IAppLogger logger)
    {
        _path = path;
        _logger = logger.ForCategory("GameList");
        EnsureFile();
        ReloadIfChanged(force: true);
    }

    public string FilePath => _path;

    public IReadOnlyList<string> Names
    {
        get
        {
            lock (_sync)
            {
                return _names.ToList();
            }
        }
    }

    private void EnsureFile()
    {
        try
        {
            if (File.Exists(_path))
            {
                return;
            }

            var directory = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.WriteAllText(_path, Template, new UTF8Encoding(false));
            _logger.Info($"已生成游戏列表模板：{_path}（请按需填入游戏进程名）");
        }
        catch (Exception ex)
        {
            _logger.Warn($"生成游戏列表模板失败：{ex.Message}");
        }
    }

    /// <summary>文件被编辑后自动重载，无需重启 GameMonitor。</summary>
    public void ReloadIfChanged(bool force = false)
    {
        try
        {
            if (!File.Exists(_path))
            {
                return;
            }

            var writeTime = File.GetLastWriteTimeUtc(_path);
            if (!force && writeTime == _lastWriteUtc)
            {
                return;
            }

            var lines = File.ReadAllLines(_path, Encoding.UTF8);
            var parsed = new List<string>();

            foreach (var raw in lines)
            {
                var line = raw.Trim();
                if (line.Length == 0 || line.StartsWith('#'))
                {
                    continue;
                }

                // 统一去掉 .exe 后缀，便于与 Process.ProcessName 比较（后者不含后缀）。
                var name = line.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                    ? line[..^4]
                    : line;

                if (!parsed.Contains(name, StringComparer.OrdinalIgnoreCase))
                {
                    parsed.Add(name);
                }
            }

            lock (_sync)
            {
                _names = parsed;
            }

            _lastWriteUtc = writeTime;
            _logger.Info($"游戏列表已加载：{parsed.Count} 项（{string.Join(", ", parsed)}）");
        }
        catch (Exception ex)
        {
            _logger.Warn($"读取游戏列表失败：{ex.Message}");
        }
    }
}

/// <summary>进程扫描（手册 §3.9 进程检测）。</summary>
public sealed class GameProcessScanner
{
    private readonly GameListProvider _list;
    private readonly IAppLogger _logger;

    public GameProcessScanner(GameListProvider list, IAppLogger logger)
    {
        _list = list;
        _logger = logger.ForCategory("Scanner");
    }

    /// <summary>返回当前正在运行的、位于列表中的进程名（原始大小写来自列表）。</summary>
    public IReadOnlyList<string> GetRunningGames()
    {
        var names = _list.Names;
        if (names.Count == 0)
        {
            return Array.Empty<string>();
        }

        var running = new List<string>();

        try
        {
            foreach (var process in Process.GetProcesses())
            {
                try
                {
                    var processName = process.ProcessName;

                    foreach (var candidate in names)
                    {
                        // ProcessName 不含 .exe，列表已统一去后缀，这里做不区分大小写的相等比较。
                        if (string.Equals(processName, candidate, StringComparison.OrdinalIgnoreCase))
                        {
                            if (!running.Contains(candidate, StringComparer.OrdinalIgnoreCase))
                            {
                                running.Add(candidate);
                            }

                            break;
                        }
                    }
                }
                catch
                {
                    // 部分系统进程无法读取属性，跳过即可。
                }
                finally
                {
                    process.Dispose();
                }
            }
        }
        catch (Exception ex)
        {
            _logger.Warn($"扫描进程列表失败：{ex.Message}");
        }

        return running;
    }
}
