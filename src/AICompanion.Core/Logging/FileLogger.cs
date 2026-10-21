using System.Text;
using AICompanion.Core.Abstractions;

namespace AICompanion.Core.Logging;

/// <summary>
/// 日志落地实现（手册 §5.1 logging 段 + 准则 6）。
///
/// 设计要点：
///   - 按日轮转：app-yyyyMMdd.log，写入时跨天自动切文件；
///   - 所有写入都包在 try/catch 里，日志本身绝不成为崩溃源；
///   - 保留最近若干条在内存环形缓冲中，供设置面板直接展示，避免为了看一眼日志去读盘；
///   - 一个 <see cref="LogSink"/> 可被多个分类的 <see cref="FileLogger"/> 共享。
/// </summary>
public sealed class LogSink
{
    private const int RecentCapacity = 500;

    private readonly string _directory;
    private readonly string _filePrefix;
    private readonly LogLevel _minimum;
    private readonly int _maxLogDays;
    private readonly object _sync = new();
    private readonly LinkedList<string> _recent = new();

    private StreamWriter? _writer;
    private DateOnly _writerDate;

    /// <param name="filePrefix">
    /// 日志文件名前缀。**每个进程必须用不同前缀**：
    /// 主程序与 GameMonitor 默认都写 logs\app 目录，
    /// 若都用 "app" 前缀就会写同一个文件；两个 StreamWriter 各自缓冲、
    /// 刷盘时机不同，实测会把两行日志交错成一行（时间戳与分类标签互相插入）。
    /// </param>
    public LogSink(string directory, LogLevel minimum = LogLevel.Info, int maxLogDays = 90, string filePrefix = "app")
    {
        _directory = directory;
        _filePrefix = string.IsNullOrWhiteSpace(filePrefix) ? "app" : filePrefix;
        _minimum = minimum;
        _maxLogDays = maxLogDays;
        try
        {
            Directory.CreateDirectory(_directory);
        }
        catch
        {
            // 目录建不出来时退化为"仅内存日志"，不影响主流程。
        }
    }

    /// <summary>日志目录。注意不要命名为 Directory，否则会遮蔽 System.IO.Directory。</summary>
    public string DirectoryPath => _directory;

    public LogLevel MinimumLevel => _minimum;

    public void Write(LogLevel level, string category, string message, Exception? exception)
    {
        if (level < _minimum)
        {
            return;
        }

        var now = DateTime.Now;
        var line = new StringBuilder(message.Length + 96)
            .Append(now.ToString("yyyy-MM-dd HH:mm:ss.fff"))
            .Append(" [").Append(LevelTag(level)).Append(']')
            .Append(" [").Append(category).Append("] ")
            .Append(message)
            .ToString();

        if (exception is not null)
        {
            line = line + Environment.NewLine + "    " + exception;
        }

        lock (_sync)
        {
            _recent.AddLast(line);
            while (_recent.Count > RecentCapacity)
            {
                _recent.RemoveFirst();
            }

            try
            {
                EnsureWriter(now);
                _writer?.WriteLine(line);
                _writer?.Flush();
            }
            catch
            {
                // 故意吞掉：磁盘满/权限问题不得让业务继续抛出。
            }
        }
    }

    private void EnsureWriter(DateTime now)
    {
        var today = DateOnly.FromDateTime(now);
        if (_writer is not null && _writerDate == today)
        {
            return;
        }

        _writer?.Dispose();
        _writer = null;

        var path = Path.Combine(_directory, $"{_filePrefix}-{today:yyyyMMdd}.log");

        // FileShare.ReadWrite 而不是 Read：引导日志器与正式日志器、甚至本程序与 GameMonitor
        // 都可能同时写同一个日志文件。用 FileShare.Read 时第二个写入者会直接 IOException，
        // 而日志失败是被吞掉的（日志绝不能成为崩溃源），结果就是"日志莫名少了一大段"。
        _writer = new StreamWriter(
            new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite),
            Encoding.UTF8)
        {
            AutoFlush = false,
        };
        _writerDate = today;
    }

    /// <summary>关闭当前日志文件句柄。切换日志目录（引导 → 正式）时必须调用，否则会互相占住文件。</summary>
    public void Close()
    {
        lock (_sync)
        {
            try
            {
                _writer?.Flush();
                _writer?.Dispose();
            }
            catch
            {
                // 忽略。
            }

            _writer = null;
        }
    }

    /// <summary>读取内存中的最近日志（倒序，最新在前）。</summary>
    public IReadOnlyList<string> RecentSnapshot(int count = 200)
    {
        lock (_sync)
        {
            return _recent.Reverse().Take(count).ToList();
        }
    }

    /// <summary>启动时清理超过 max_log_days 的日志文件（手册 §5.1）。</summary>
    public void CleanupOldFiles()
    {
        if (_maxLogDays <= 0)
        {
            return;
        }

        try
        {
            if (!Directory.Exists(_directory))
            {
                return;
            }

            var cutoff = DateTime.Now.Date.AddDays(-_maxLogDays);
            foreach (var file in Directory.EnumerateFiles(_directory, "*.log"))
            {
                try
                {
                    if (File.GetLastWriteTime(file) < cutoff)
                    {
                        File.Delete(file);
                    }
                }
                catch
                {
                    // 单个文件删不掉不影响其它文件。
                }
            }
        }
        catch
        {
            // 忽略。
        }
    }

    /// <summary>日志目录当前占用字节数，供设置面板展示。</summary>
    public long GetDirectorySizeBytes()
    {
        try
        {
            if (!Directory.Exists(_directory))
            {
                return 0;
            }

            return Directory.EnumerateFiles(_directory, "*.log").Sum(f =>
            {
                try
                {
                    return new FileInfo(f).Length;
                }
                catch
                {
                    return 0L;
                }
            });
        }
        catch
        {
            return 0L;
        }
    }

    /// <summary>设置面板的"清理日志"按钮。当前正在写入的文件会被跳过。</summary>
    public int PurgeAll()
    {
        var deleted = 0;
        lock (_sync)
        {
            try
            {
                if (!Directory.Exists(_directory))
                {
                    return 0;
                }

                foreach (var file in Directory.EnumerateFiles(_directory, "*.log"))
                {
                    try
                    {
                        File.Delete(file);
                        deleted++;
                    }
                    catch
                    {
                        // 正在被本进程占用的当天文件会失败，属预期。
                    }
                }
            }
            catch
            {
                // 忽略。
            }
        }

        return deleted;
    }

    private static string LevelTag(LogLevel level) => level switch
    {
        LogLevel.Debug => "DBG",
        LogLevel.Info => "INF",
        LogLevel.Warn => "WRN",
        _ => "ERR",
    };
}

/// <summary>共享同一个 <see cref="LogSink"/> 的分类日志器。</summary>
public sealed class FileLogger : IAppLogger
{
    private readonly LogSink _sink;
    private readonly string _category;

    public FileLogger(LogSink sink, string category = "App")
    {
        _sink = sink;
        _category = category;
    }

    public void Debug(string message, Exception? exception = null) => _sink.Write(LogLevel.Debug, _category, message, exception);

    public void Info(string message, Exception? exception = null) => _sink.Write(LogLevel.Info, _category, message, exception);

    public void Warn(string message, Exception? exception = null) => _sink.Write(LogLevel.Warn, _category, message, exception);

    public void Error(string message, Exception? exception = null) => _sink.Write(LogLevel.Error, _category, message, exception);

    public IAppLogger ForCategory(string category) => new FileLogger(_sink, category);

    public static FileLogger CreateRoot(string directory, LogLevel minimum = LogLevel.Info, int maxLogDays = 90, string filePrefix = "app")
        => new(new LogSink(directory, minimum, maxLogDays, filePrefix));
}
