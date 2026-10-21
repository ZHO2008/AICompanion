namespace AICompanion.Core.Abstractions;

public enum LogLevel
{
    Debug = 0,
    Info = 1,
    Warn = 2,
    Error = 3,
}

/// <summary>
/// 全局日志抽象（手册准则 6）。
/// 实现必须保证三条铁律：绝不抛异常、绝不弹窗、线程安全。
/// </summary>
public interface IAppLogger
{
    void Debug(string message, Exception? exception = null);

    void Info(string message, Exception? exception = null);

    void Warn(string message, Exception? exception = null);

    void Error(string message, Exception? exception = null);

    /// <summary>派生一个带分类标签的日志器（例如 "TTS"、"Health"、"Gpu"）。</summary>
    IAppLogger ForCategory(string category);
}
