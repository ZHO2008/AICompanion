namespace AICompanion.Core.Abstractions;

/// <summary>
/// 通过本机 HTTP 服务合成的引擎。
///
/// 这一类引擎（Chatterbox / GPT-SoVITS / CosyVoice）有共同的形态：服务是**外部进程**，
/// 程序只负责发请求、收音频。因此可用性检查与软重启必须走同一条路径，
/// 否则每加一个引擎就要在 <c>TTSManager</c> 里再复制一遍降级逻辑。
///
/// 约定：
///   - <see cref="IsAvailable"/> 是缓存值，绝不在 getter 里做网络调用（准则 1）；
///   - 探测由 <see cref="RefreshAvailabilityAsync"/> 完成，初始化与健康检查时调用。
/// </summary>
public interface IHttpTtsEngine : ITTSEngine
{
    /// <summary>服务根地址，例如 http://127.0.0.1:9880。</summary>
    string BaseUrl { get; }

    /// <summary>服务端口。游戏模式要按端口反查进程并释放显存，所以必须暴露出来。</summary>
    int Port { get; }

    /// <summary>本次实例的连续运行时长，用于"空闲软重启"的时间阈值判断。</summary>
    TimeSpan Uptime { get; }

    /// <summary>探活并刷新 <see cref="ITTSEngine.IsAvailable"/> 缓存。</summary>
    Task<bool> RefreshAvailabilityAsync(CancellationToken cancellationToken = default);

    /// <summary>软重启：重建本地连接池并重新探活，不动外部进程。</summary>
    Task<bool> TrySoftRestartAsync(CancellationToken cancellationToken = default);
}