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

    /// <summary>单文件大小上限：超过就滚动一次，只保留一代 xxx-yyyyMMdd.log.1。</summary>
    private const long MaxFileBytes = 32L * 1024 * 1024;

    private readonly string _directory;
    private readonly string _filePrefix;
    private readonly LogLevel _minimum;
    private readonly int _maxLogDays;
    private readonly object _sync = new();
    private readonly LinkedList<string> _recent = new();

    private StreamWriter? _writer;
    private DateOnly _writerDate;

    /// <summary>
    /// 上次"滚动失败"时的文件长度（-1 = 没失败过），失败后要等文件再长一段才重试。
    ///
    /// 为什么需要它：文件被别的进程独占时 <c>File.Move</c> 会失败，若每次写盘都重试，
    /// 就会每写一行都做一遍"关闭 → 改名 → 重开"，把日志变成 I/O 风暴。
    /// </summary>
    private long _rollRetryAfterLength = -1;

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
        var line = FormatLine(now, level, category, message, exception);

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

                // 大小上限检查放在写盘之后：先保证这一行落地，再决定要不要滚动。
                RollIfTooLarge(now);
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

        // 换日或换目录后，上一份文件的"滚动失败"记录作废（滚动路径直接调 OpenWriter，不会经过这里）。
        _rollRetryAfterLength = -1;

        OpenWriter(today);
    }

    /// <summary>按指定日期打开追加写句柄（调用方负责先关掉旧 writer）。</summary>
    private void OpenWriter(DateOnly date)
    {
        var path = Path.Combine(_directory, $"{_filePrefix}-{date:yyyyMMdd}.log");

        // FileShare.ReadWrite 而不是 Read：引导日志器与正式日志器、甚至本程序与 GameMonitor
        // 都可能同时写同一个日志文件。用 FileShare.Read 时第二个写入者会直接 IOException，
        // 而日志失败是被吞掉的（日志绝不能成为崩溃源），结果就是"日志莫名少了一大段"。
        _writer = new StreamWriter(
            new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite),
            Encoding.UTF8)
        {
            AutoFlush = false,
        };
        _writerDate = date;
    }

    /// <summary>
    /// 单文件大小上限：超过 <see cref="MaxFileBytes"/> 就把当前文件滚动一次
    /// （关闭 writer → 把 xxx-yyyyMMdd.log 改名为 .log.1 → 重新打开当日日志文件）。
    ///
    /// 调用点必须已持有 _sync 且刚写完一行：改名与写盘必须串行，
    /// 否则会把另一个线程刚写进去的内容挪到 .1 里。
    /// 这里吞掉一切 IO 异常 —— 日志设施不允许反过来打断业务（与本类其它写路径一致）。
    /// </summary>
    private void RollIfTooLarge(DateTime now)
    {
        if (_writer is null)
        {
            return;
        }

        long length;
        try
        {
            // StreamWriter 会缓冲，但调用点刚 Flush 过，这里就是磁盘上的真实长度。
            length = _writer.BaseStream.Length;
        }
        catch
        {
            return;
        }

        if (length <= MaxFileBytes || length < _rollRetryAfterLength)
        {
            return;
        }

        var date = _writerDate;
        var path = Path.Combine(_directory, $"{_filePrefix}-{date:yyyyMMdd}.log");
        var rolledPath = path + ".1";

        try
        {
            _writer.Flush();
            _writer.Dispose();
        }
        catch
        {
            // 关不掉也继续往下走：改名与重新打开仍可能成功。
        }

        _writer = null;

        var moved = false;
        try
        {
            // 只保留一代滚动文件：旧的 .1 直接覆盖。
            // 不用"先 Delete 再 Move"——中途失败会两个文件都没有。
            File.Move(path, rolledPath, overwrite: true);
            moved = true;
            _rollRetryAfterLength = -1;
        }
        catch
        {
            // 改名失败（文件被其它进程独占等）不影响继续写日志；
            // 等文件再长 1/4 个上限才重试，避免每写一行都重试一遍。
            _rollRetryAfterLength = length + (MaxFileBytes / 4);
        }

        try
        {
            OpenWriter(date);
        }
        catch
        {
            // 打不开就退化为"仅内存日志"，下次写盘时 EnsureWriter 还会再试。
            _writer = null;
        }

        // 滚动这件事本身要留痕，而且成功时必须写进**新**文件（旧文件已经改名）。
        var notice = FormatLine(
            now,
            LogLevel.Info,
            "Log",
            moved
                ? $"日志文件超过 {MaxFileBytes / (1024 * 1024)} MB，已滚动为 {Path.GetFileName(rolledPath)}，继续写入新文件。"
                : $"日志文件超过 {MaxFileBytes / (1024 * 1024)} MB，但滚动失败（可能被其它进程占用），继续写入当前文件。",
            null);

        _recent.AddLast(notice);
        while (_recent.Count > RecentCapacity)
        {
            _recent.RemoveFirst();
        }

        try
        {
            _writer?.WriteLine(notice);
            _writer?.Flush();
        }
        catch
        {
            // 忽略。
        }
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

    /// <summary>拼一行日志文本（普通写入与滚动通知共用，保证格式一致）。</summary>
    private static string FormatLine(DateTime now, LogLevel level, string category, string message, Exception? exception)
    {
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

        return line;
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
