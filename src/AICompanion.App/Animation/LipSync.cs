using System.Buffers.Binary;
using System.Windows.Threading;
using AICompanion.Core.Abstractions;

namespace AICompanion.App.Animation;

/// <summary>
/// 口型同步（Lip-sync）。
///
/// 数据流严格按要求分离：
///   **后台线程**每 50ms 算出一次当前音频的振幅（RMS，映射到 0.0–1.0）
///   → <c>Dispatcher.BeginInvoke</c> 把这个 float 丢给 UI 线程
///   → UI 线程只做一件事：把值写进模型的 MouthOpen 参数。
/// 音频数据本身**不**传给 UI。
///
/// 说明：本项目的播放器是基于 winmm PlaySound 的内存播放（没有逐块回调），
/// 所以这里先把 WAV 解码成 PCM、按 50ms 分桶预计算 RMS 包络，再用 50ms 定时器
/// 按播放进度取值上报 —— 与"每 50ms 从音频流里算一次 RMS"等价，
/// 且不引入 NAudio 依赖。若将来换成 NAudio 流式播放，
/// 直接把每块数据的 <see cref="ComputeRms"/> 结果交给 <see cref="PushAmplitude"/> 即可。
/// </summary>
public sealed class LipSyncDriver : IDisposable
{
    private const int IntervalMs = 50;

    /// <summary>静音低于该值直接归零，避免底噪让嘴一直微张。</summary>
    private const double SilenceFloor = 0.02;

    /// <summary>嘴部开合上限，避免满幅音频把嘴拉到失真。</summary>
    private const double MaxOpen = 1.0;

    private readonly Dispatcher _dispatcher;
    private readonly Action<double> _applyMouthOpen;
    private readonly IAppLogger _logger;

    private readonly object _sync = new();

    private double[]? _envelope;
    private Timer? _timer;
    private DateTime _startedUtc;
    private double _lastReported;
    private bool _disposed;

    public LipSyncDriver(Dispatcher dispatcher, Action<double> applyMouthOpen, IAppLogger logger)
    {
        _dispatcher = dispatcher;
        _applyMouthOpen = applyMouthOpen;
        _logger = logger.ForCategory("LipSync");
    }

    /// <summary>
    /// 开始跟随一段 WAV 的口型。调用方通常是播放器在"开始播放"时。
    /// 该方法会立即返回，包络计算在后台线程完成。
    /// </summary>
    public void Start(byte[] wav)
    {
        Stop(resetMouth: true, reason: "restart");

        if (wav is null || wav.Length == 0)
        {
            return;
        }

        Task.Run(() =>
        {
            try
            {
                // 计算包络期间可能已经被 Stop/Dispose 掉（播放被打断、窗口关闭）：
                // 这时再落地就是给一个没人持有的定时器，它会每 50ms 空转到进程结束。
                if (_disposed)
                {
                    return;
                }

                var envelope = BuildEnvelope(wav, IntervalMs);
                if (envelope.Length == 0)
                {
                    return;
                }

                lock (_sync)
                {
                    if (_disposed)
                    {
                        return;
                    }

                    _envelope = envelope;
                    _startedUtc = DateTime.UtcNow;
                    _timer = new Timer(OnTick, null, IntervalMs, IntervalMs);
                }

                _logger.Debug($"口型包络已就绪：{envelope.Length} 个 50ms 片段。");
            }
            catch (Exception ex)
            {
                _logger.Warn($"口型包络计算失败：{ex.Message}");
            }
        });
    }

    /// <summary>
    /// 停止跟随并把嘴立刻合上。
    /// 播放被打断/取消时**必须**调用，否则嘴会停在最后一个开合值上。
    /// </summary>
    public void Stop(bool resetMouth = true, string reason = "stopped")
    {
        Timer? timer;

        lock (_sync)
        {
            timer = _timer;
            _timer = null;
            _envelope = null;
            _lastReported = 0;
        }

        try
        {
            timer?.Dispose();
        }
        catch
        {
            // 已释放时忽略
        }

        if (resetMouth)
        {
            ReportToUi(0.0);
            _logger.Debug($"口型已归零（{reason}）。");
        }
    }

    /// <summary>后台定时器：按播放进度取包络值并上报。**不碰 UI**。</summary>
    private void OnTick(object? state)
    {
        double[]? envelope;
        int index;

        lock (_sync)
        {
            envelope = _envelope;
            if (envelope is null)
            {
                return;
            }

            index = (int)((DateTime.UtcNow - _startedUtc).TotalMilliseconds / IntervalMs);
        }

        if (index >= envelope.Length)
        {
            // 音频播完：合上嘴并停表
            Stop(resetMouth: true, reason: "finished");
            return;
        }

        // 简单的起音/释放平滑：上扬快、回落慢，避免嘴抖。
        //
        // 平滑基准 _lastReported 必须在锁内读写：Stop() 会在锁内把它清零，
        // 在锁外读/写就可能拿一个已被清零的旧值当基准，表现为嘴部开合突然回退。
        var target = envelope[index];

        double smoothed;
        lock (_sync)
        {
            var previous = _lastReported;
            smoothed = target > previous
                ? previous + (target - previous) * 0.7
                : previous + (target - previous) * 0.3;

            _lastReported = smoothed;
        }

        ReportToUi(smoothed);
    }

    /// <summary>把振幅切回 UI 线程写入模型参数。</summary>
    private void ReportToUi(double value)
    {
        if (_disposed)
        {
            return;
        }

        var v = Math.Clamp(value, 0.0, MaxOpen);

        _dispatcher.BeginInvoke(new Action(() =>
        {
            try
            {
                // 这里就是 UI 线程：只写模型参数，不做音频计算。
                _applyMouthOpen(v);
            }
            catch (Exception ex)
            {
                _logger.Warn($"写入 MouthOpen 失败：{ex.Message}");
            }
        }));
    }

    // ------------------------------------------------------------------ 信号处理

    /// <summary>
    /// 把 16bit PCM 的 WAV 解码成每 <paramref name="intervalMs"/> 毫秒一个 RMS 值的包络。
    /// </summary>
    public static double[] BuildEnvelope(byte[] wav, int intervalMs)
    {
        if (!TryParsePcm(wav, out var pcmOffset, out var pcmLength, out var channels, out var sampleRate))
        {
            return Array.Empty<double>();
        }

        var bytesPerFrame = 2 * channels;                       // 16bit
        var framesPerBucket = Math.Max(1, sampleRate * intervalMs / 1000);
        var totalFrames = pcmLength / bytesPerFrame;
        if (totalFrames <= 0)
        {
            return Array.Empty<double>();
        }

        var buckets = (totalFrames + framesPerBucket - 1) / framesPerBucket;
        var envelope = new double[buckets];

        for (var b = 0; b < buckets; b++)
        {
            var startFrame = b * framesPerBucket;
            var endFrame = Math.Min(totalFrames, startFrame + framesPerBucket);

            double sumSquares = 0;
            var count = 0;

            for (var f = startFrame; f < endFrame; f++)
            {
                for (var c = 0; c < channels; c++)
                {
                    var index = pcmOffset + f * bytesPerFrame + c * 2;
                    if (index + 1 >= wav.Length)
                    {
                        break;
                    }

                    var sample = BinaryPrimitives.ReadInt16LittleEndian(wav.AsSpan(index, 2)) / 32768.0;
                    sumSquares += sample * sample;
                    count++;
                }
            }

            var rms = count > 0 ? Math.Sqrt(sumSquares / count) : 0;

            // RMS 通常落在 0–0.3，做一次放大后映射到 0–1，低于底噪归零。
            envelope[b] = rms < SilenceFloor ? 0.0 : Math.Clamp(rms * 3.2, 0.0, MaxOpen);
        }

        return envelope;
    }

    /// <summary>计算一段 PCM 的 RMS（0..1）。供流式播放器直接使用。</summary>
    public static double ComputeRms(byte[] pcm, int offset, int count, int channels = 1)
    {
        if (pcm is null || count <= 0 || offset < 0 || offset >= pcm.Length)
        {
            return 0.0;
        }

        var end = Math.Min(pcm.Length - 1, offset + count);
        var bytesPerFrame = 2 * channels;
        double sumSquares = 0;
        var samples = 0;

        for (var i = offset; i + 1 < end; i += bytesPerFrame)
        {
            for (var c = 0; c < channels; c++)
            {
                var index = i + c * 2;
                if (index + 1 >= pcm.Length)
                {
                    break;
                }

                var sample = BinaryPrimitives.ReadInt16LittleEndian(pcm.AsSpan(index, 2)) / 32768.0;
                sumSquares += sample * sample;
                samples++;
            }
        }

        if (samples == 0)
        {
            return 0.0;
        }

        var rms = Math.Sqrt(sumSquares / samples);
        return rms < SilenceFloor ? 0.0 : Math.Clamp(rms * 3.2, 0.0, MaxOpen);
    }

    /// <summary>最小 WAV 解析：只取 fmt 与 data 块的位置，不做完整校验。</summary>
    private static bool TryParsePcm(byte[] wav, out int pcmOffset, out int pcmLength, out int channels, out int sampleRate)
    {
        pcmOffset = 0;
        pcmLength = 0;
        channels = 1;
        sampleRate = 16000;

        if (wav is null || wav.Length < 44 ||
            wav[0] != (byte)'R' || wav[1] != (byte)'I' || wav[2] != (byte)'F' || wav[3] != (byte)'F' ||
            wav[8] != (byte)'W' || wav[9] != (byte)'A' || wav[10] != (byte)'V' || wav[11] != (byte)'E')
        {
            return false;
        }

        var pos = 12;
        var foundFmt = false;

        while (pos + 8 <= wav.Length)
        {
            var id = System.Text.Encoding.ASCII.GetString(wav, pos, 4);
            var size = BinaryPrimitives.ReadInt32LittleEndian(wav.AsSpan(pos + 4, 4));
            var body = pos + 8;

            if (id == "fmt " && body + 16 <= wav.Length)
            {
                channels = BinaryPrimitives.ReadInt16LittleEndian(wav.AsSpan(body + 2, 2));
                sampleRate = BinaryPrimitives.ReadInt32LittleEndian(wav.AsSpan(body + 4, 4));
                var bits = BinaryPrimitives.ReadInt16LittleEndian(wav.AsSpan(body + 14, 2));

                if (bits != 16)
                {
                    return false;   // 只支持 16bit PCM
                }

                foundFmt = true;
            }
            else if (id == "data" && foundFmt)
            {
                pcmOffset = body;
                pcmLength = Math.Min(size, wav.Length - body);
                return channels > 0 && sampleRate > 0;
            }

            pos = body + size + (size % 2);
        }

        return false;
    }

    public void Dispose()
    {
        _disposed = true;
        Stop(resetMouth: false, reason: "dispose");
    }
}
