using System.Buffers.Binary;
using System.Net.Http;
using System.Net.Sockets;
using AICompanion.Core.Abstractions;
using AICompanion.Core.Configuration;

namespace AICompanion.Core.Tts;

/// <summary>
/// 「外部 HTTP 语音服务」引擎的公共骨架（Chatterbox / GPT-SoVITS / CosyVoice 共用）。
///
/// 抽出来的原因是一次实际事故：三个引擎的探活、超时、连接池重建、WAV 封装几乎逐字相同，
/// 复制三遍后任何一处修复都会漏掉另外两处。这里只保留"三家确实不一样"的部分给子类。
///
/// 两条硬约定：
///   1. **不走系统代理**：这些都是 127.0.0.1 的本机服务，走代理会连不上（本机曾因此排查过一轮）。
///   2. **超时由配置给**：GPT-SoVITS 与 CosyVoice 首次合成都可能几十秒，默认 HTTP 100 秒不够稳。
/// </summary>
public abstract class HttpTtsEngineBase : IHttpTtsEngine
{
    protected readonly AppConfig Config;
    protected readonly IAppLogger Logger;

    private readonly string _host;
    private readonly int _port;
    private readonly int _timeoutSeconds;

    private HttpClient _http;
    private DateTime _startedUtc = DateTime.UtcNow;

    protected HttpTtsEngineBase(
        AppConfig config,
        IAppLogger logger,
        string category,
        string host,
        int port,
        int timeoutSeconds)
    {
        Config = config;
        Logger = logger.ForCategory(category);
        _host = string.IsNullOrWhiteSpace(host) ? "127.0.0.1" : host.Trim();
        _port = port is > 0 and < 65536 ? port : 9880;
        _timeoutSeconds = Math.Clamp(timeoutSeconds <= 0 ? 120 : timeoutSeconds, 5, 1800);
        _http = CreateClient();
    }

    public abstract string Name { get; }

    public string BaseUrl => $"http://{_host}:{_port}";

    public int Port => _port;

    public TimeSpan Uptime => DateTime.UtcNow - _startedUtc;

    public bool IsAvailable { get; private set; }

    protected HttpClient Http => _http;

    protected string Url(string path) => path.StartsWith('/') ? BaseUrl + path : $"{BaseUrl}/{path}";

    /// <summary>合成由子类实现（三家的请求体差别太大，不适合再抽象）。</summary>
    public abstract Task<byte[]> SynthesizeAsync(
        string text,
        Models.VoiceConfig config,
        CancellationToken cancellationToken = default);

    private HttpClient CreateClient()
    {
        var handler = new HttpClientHandler { UseProxy = false, Proxy = null };
        return new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(_timeoutSeconds) };
    }

    /// <summary>
    /// 探活：先试 FastAPI 的 <c>/docs</c>（三家都是 FastAPI，返回 200 说明服务真的起来了），
    /// 再退化为一次 TCP 连接（服务没挂 /docs 时也能判断端口是否在听）。
    /// </summary>
    public virtual async Task<bool> RefreshAvailabilityAsync(CancellationToken cancellationToken = default)
    {
        var ok = await ProbeDocsAsync(cancellationToken).ConfigureAwait(false)
                 || await ProbeTcpAsync(cancellationToken).ConfigureAwait(false);

        IsAvailable = ok;

        if (ok)
        {
            Logger.Info($"{Name} 服务就绪：{BaseUrl}");
        }
        else
        {
            Logger.Warn($"{Name} 服务未就绪：{BaseUrl} 连不上（外部进程没启动？）");
        }

        return ok;
    }

    private async Task<bool> ProbeDocsAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromSeconds(5));

            using var response = await _http.GetAsync($"{BaseUrl}/docs", cts.Token).ConfigureAwait(false);
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    private async Task<bool> ProbeTcpAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromSeconds(3));

            using var client = new TcpClient();
            await client.ConnectAsync(_host, _port, cts.Token).ConfigureAwait(false);
            return client.Connected;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 软重启：重建客户端（丢掉可能坏掉的连接池）再探活。外部进程不动 —— 那是用户的进程，
    /// 程序只允许在"游戏模式让出显存"时按端口结束它，且会明确记日志。
    /// </summary>
    public virtual async Task<bool> TrySoftRestartAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            _http.Dispose();
        }
        catch
        {
            // 忽略：旧客户端可能已经处于坏状态。
        }

        _http = CreateClient();
        _startedUtc = DateTime.UtcNow;

        var healthy = await RefreshAvailabilityAsync(cancellationToken).ConfigureAwait(false);
        Logger.Info($"{Name} 软重启完成，可用={healthy}。");
        return healthy;
    }

    /// <summary>合成成功后由子类调用，让缓存立刻反映"刚才确实通"。</summary>
    protected void MarkAvailable() => IsAvailable = true;

    public void Stop()
    {
        // 外部进程，停止播放由播放器负责。
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

    protected static string Truncate(string value, int max = 200) =>
        value.Length <= max ? value : value[..max] + "...";

    /// <summary>WAV 头特征（"RIFF"...."WAVE"）。有些部署会直接回 WAV，那就不要重复封装。</summary>
    protected static bool LooksLikeWav(byte[] data) =>
        data.Length >= 12
        && data[0] == (byte)'R' && data[1] == (byte)'I' && data[2] == (byte)'F' && data[3] == (byte)'F'
        && data[8] == (byte)'W' && data[9] == (byte)'A' && data[10] == (byte)'V' && data[11] == (byte)'E';

    /// <summary>
    /// 把裸 PCM（16 bit 单声道小端）封装成 WAV。
    ///
    /// CosyVoice 的 FastAPI 服务返回的就是**没有文件头的原始 PCM**（见官方 server.py 的
    /// <c>generate_data</c>），若直接当 WAV 交给播放器会得到"格式不支持"或满耳噪声。
    /// </summary>
    public static byte[] WavFromPcm16(byte[] pcm, int sampleRate, short channels = 1)
    {
        if (pcm.Length == 0)
        {
            throw new ArgumentException("PCM 数据为空。", nameof(pcm));
        }

        if (sampleRate <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sampleRate), "采样率必须为正数。");
        }

        var header = new byte[44];
        var span = header.AsSpan();
        var dataLen = pcm.Length;

        BinaryPrimitives.WriteInt32LittleEndian(span[0..], 0x46464952);            // "RIFF"
        BinaryPrimitives.WriteInt32LittleEndian(span[4..], 36 + dataLen);          // 文件长 - 8
        BinaryPrimitives.WriteInt32LittleEndian(span[8..], 0x45564157);            // "WAVE"
        BinaryPrimitives.WriteInt32LittleEndian(span[12..], 0x20746d66);           // "fmt "
        BinaryPrimitives.WriteInt32LittleEndian(span[16..], 16);                   // fmt 块长
        BinaryPrimitives.WriteInt16LittleEndian(span[20..], 1);                    // PCM
        BinaryPrimitives.WriteInt16LittleEndian(span[22..], channels);
        BinaryPrimitives.WriteInt32LittleEndian(span[24..], sampleRate);
        BinaryPrimitives.WriteInt32LittleEndian(span[28..], sampleRate * channels * 2);
        BinaryPrimitives.WriteInt16LittleEndian(span[32..], (short)(channels * 2));
        BinaryPrimitives.WriteInt16LittleEndian(span[34..], 16);
        BinaryPrimitives.WriteInt32LittleEndian(span[36..], 0x61746164);           // "data"
        BinaryPrimitives.WriteInt32LittleEndian(span[40..], dataLen);

        var wav = new byte[44 + dataLen];
        header.CopyTo(wav, 0);
        pcm.CopyTo(wav, 44);
        return wav;
    }
}