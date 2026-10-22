using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
using AICompanion.Core.Abstractions;
using AICompanion.Core.Configuration;
using AICompanion.Core.Models;

namespace AICompanion.Core.Tts;

/// <summary>
/// Chatterbox 引擎（手册 §3.4）：通过 HTTP 调用 127.0.0.1:5000 的 chatterbox-api。
///
/// 关于 <see cref="IsAvailable"/>：接口要求它是同步属性，而可用性必须靠 /health 异步探测。
/// 因此这里采用"缓存 + 显式刷新"策略：
///   - <see cref="RefreshAvailabilityAsync"/> 在 TTSManager 初始化时调用一次（决定是否降级）；
///   - 之后由 HealthMonitor 的探活结果与软重启流程负责更新缓存。
/// 绝不在属性 getter 里做阻塞式网络调用 —— 那正是准则 1 明令禁止的行为。
/// </summary>
public sealed class ChatterboxEngine : IHttpTtsEngine
{
    private readonly AppConfig _config;
    private readonly IAppLogger _logger;
    private readonly string _baseUrl;

    private HttpClient _http;
    private volatile bool _available;
    private DateTime _startedUtc = DateTime.UtcNow;

    public ChatterboxEngine(AppConfig config, IAppLogger logger)
    {
        _config = config;
        _logger = logger.ForCategory("TTS.Chatterbox");
        _baseUrl = $"http://{config.Tts.Chatterbox.Host}:{config.Tts.Chatterbox.Port}";
        _http = CreateClient();
    }

    public string Name => "chatterbox";

    public bool IsAvailable => _available;

    /// <summary>本引擎实例已连续运行的时长，用于软重启判断（手册 §3.4：超过 4 小时）。</summary>
    public TimeSpan Uptime => DateTime.UtcNow - _startedUtc;

    public string BaseUrl => _baseUrl;

    public int Port => _config.Tts.Chatterbox.Port;

    private static HttpClient CreateClient()
    {
        var handler = new HttpClientHandler { UseProxy = false, Proxy = null };
        return new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
    }

    /// <summary>按手册要求通过 /health 判断可用性，并把结果写入缓存。</summary>
    public async Task<bool> RefreshAvailabilityAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromSeconds(5));

            using var response = await _http.GetAsync($"{_baseUrl}/health", cts.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                _available = false;
                _logger.Warn($"Chatterbox /health 返回 HTTP {(int)response.StatusCode}。");
                return false;
            }

            var body = await response.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
            var compact = body.Replace(" ", string.Empty, StringComparison.Ordinal);
            _available = compact.Contains("\"status\":\"ok\"", StringComparison.OrdinalIgnoreCase);

            if (!_available)
            {
                _logger.Warn($"Chatterbox /health 响应不含 status:ok：{Truncate(body)}");
            }

            return _available;
        }
        catch (Exception ex)
        {
            _available = false;
            _logger.Warn($"Chatterbox /health 探活失败：{ex.Message}");
            return false;
        }
    }

    public async Task<byte[]> SynthesizeAsync(string text, VoiceConfig config, CancellationToken cancellationToken = default)
    {
        var payload = new JsonObject
        {
            ["text"] = text,
            ["voice_id"] = config.VoiceId ?? string.Empty,
            ["language"] = config.Language,
            ["speed"] = config.Speed,
            ["emotion_style"] = config.EmotionStyle,
            ["emotion_intensity"] = config.EmotionIntensity,
        };

        if (!string.IsNullOrWhiteSpace(config.ReferenceAudioPath))
        {
            payload["reference_audio_path"] = config.ReferenceAudioPath;
        }

        var path = _config.Tts.Chatterbox.SynthesizePath;
        var url = path.StartsWith('/') ? _baseUrl + path : $"{_baseUrl}/{path}";

        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json"),
        };

        using var response = await _http
            .SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken)
            .ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            throw new InvalidOperationException($"Chatterbox 合成失败：HTTP {(int)response.StatusCode} {Truncate(body)}");
        }

        var audio = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
        if (audio.Length == 0)
        {
            throw new InvalidOperationException("Chatterbox 返回了空的音频数据。");
        }

        _available = true;
        _logger.Info($"Chatterbox 合成完成：{text.Length} 字 → {audio.Length} 字节。");
        return audio;
    }

    /// <summary>
    /// 软重启（手册 §3.4）：先尝试官方重启接口，失败则重建本地 HttpClient 实例。
    /// 重建实例可以清掉可能已经损坏的 HTTP 连接池，是最轻量的一次"重启"。
    /// </summary>
    public async Task<bool> TrySoftRestartAsync(CancellationToken cancellationToken = default)
    {
        var path = _config.Tts.Chatterbox.RestartPath;
        var url = path.StartsWith('/') ? _baseUrl + path : $"{_baseUrl}/{path}";

        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromSeconds(15));

            using var response = await _http.PostAsync(url, content: null, cts.Token).ConfigureAwait(false);
            if (response.IsSuccessStatusCode)
            {
                _logger.Info("已通过重启接口完成 Chatterbox 软重启。");
            }
            else
            {
                _logger.Info($"Chatterbox 重启接口返回 HTTP {(int)response.StatusCode}，改为重建本地客户端。");
            }
        }
        catch (Exception ex)
        {
            _logger.Info($"调用 Chatterbox 重启接口失败（{ex.Message}），改为重建本地客户端。");
        }

        // 无论接口是否可用，都重建一次客户端与运行计时。
        try
        {
            _http.Dispose();
        }
        catch
        {
            // 忽略。
        }

        _http = CreateClient();
        _startedUtc = DateTime.UtcNow;

        var healthy = await RefreshAvailabilityAsync(cancellationToken).ConfigureAwait(false);
        _logger.Info($"Chatterbox 软重启完成，/health 可用={healthy}。");
        return healthy;
    }

    public void Stop()
    {
        // Chatterbox 是外部进程，停止播放由播放器负责。
    }

    public void Unload()
    {
        try
        {
            _http.Dispose();
        }
        catch
        {
            // 忽略。
        }
    }

    private static string Truncate(string value) => value.Length <= 200 ? value : value[..200] + "...";
}
