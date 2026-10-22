using AICompanion.Core.Abstractions;
using NAudio.Wave;

namespace AICompanion.Core.Asr;

/// <summary>
/// 基于 NAudio WASAPI 回环的**系统音频**采集。
///
/// 用途：识别电脑正在播放的声音（视频、直播、通话），也便于做端到端验证
/// （例如放一段 B 站视频，看识别结果对不对，不必对着麦克风念）。
///
/// 与麦克风采集的差别：
///   - WASAPI 回环给出的是 32 位浮点、设备原采样率（常见 44.1k/48k）、多声道；
///   - whisper 要 16 kHz 单声道 float，所以这里做「下混 + 盒式重采样」。
/// </summary>
public sealed class WasapiLoopbackCapture : IAudioCapture
{
    /// <summary>输出块长（毫秒）。与 VAD 的帧长对齐，减少上层再分帧。</summary>
    private const int EmitMilliseconds = 20;

    private readonly IAppLogger _logger;
    private readonly object _sync = new();

    /// <summary>待重采样的单声道数据（源采样率）。</summary>
    private readonly List<float> _pending = new();

    private NAudio.Wave.WasapiLoopbackCapture? _capture;
    private double _position;

    public WasapiLoopbackCapture(IAppLogger logger)
    {
        _logger = logger.ForCategory("AudioCapture");
    }

    public bool IsCapturing { get; private set; }

    public int SampleRate { get; private set; }

    public event EventHandler<float[]>? SamplesAvailable;

    public event EventHandler<Exception>? CaptureError;

    public void Start(int sampleRate)
    {
        // 声明在 lock 外：失败后要在锁外 StopRecording/Dispose（锁内会与采集线程互等）。
        NAudio.Wave.WasapiLoopbackCapture? failedCapture = null;

        lock (_sync)
        {
            if (IsCapturing)
            {
                return;
            }

            SampleRate = sampleRate;

            try
            {
                _capture = new NAudio.Wave.WasapiLoopbackCapture();
                var format = _capture.WaveFormat;

                if (format.Encoding != WaveFormatEncoding.IeeeFloat || format.BitsPerSample != 32)
                {
                    throw new NotSupportedException(
                        $"系统音频格式不是 32 位浮点（实际 {format.Encoding}/{format.BitsPerSample}bit），无法采集。");
                }

                _position = 0;
                _pending.Clear();

                _capture.DataAvailable += OnDataAvailable;
                _capture.RecordingStopped += OnRecordingStopped;
                _capture.StartRecording();

                IsCapturing = true;
                _logger.Info(
                    $"系统音频采集已启动：源 {format.SampleRate} Hz / {format.Channels} 声道 / float32 → 输出 {sampleRate} Hz 单声道。");
            }
            catch (Exception ex)
            {
                _logger.Error($"启动系统音频采集失败：{ex.Message}", ex);

                // 锁内只摘引用，真正的 StopRecording/Dispose 放到锁外做：
                // 采集线程可能正卡在 OnDataAvailable 的 lock(_sync) 上，
                // 持锁等采集线程退出就是典型互等死锁。
                failedCapture = _capture;
                _capture = null;
                RaiseError(ex);
            }
        }

        if (failedCapture is not null)
        {
            CleanupCapture(failedCapture);
        }
    }

    public void Stop()
    {
        NAudio.Wave.WasapiLoopbackCapture? capture;

        lock (_sync)
        {
            if (!IsCapturing)
            {
                return;
            }

            IsCapturing = false;
            capture = _capture;
            _capture = null;

            // 尾巴丢掉，避免下次启动拼到旧数据
            _pending.Clear();
        }

        CleanupCapture(capture);
        _logger.Info("系统音频采集已停止。");
    }

    /// <summary>
    /// 停止并释放一个采集器。**必须在锁外调用**：
    /// StopRecording 会等采集线程收尾，而采集线程在 OnDataAvailable 里要拿 _sync。
    /// </summary>
    private void CleanupCapture(NAudio.Wave.WasapiLoopbackCapture? capture)
    {
        if (capture is null)
        {
            return;
        }

        try
        {
            capture.DataAvailable -= OnDataAvailable;
            capture.RecordingStopped -= OnRecordingStopped;

            try
            {
                capture.StopRecording();
            }
            catch
            {
                // 忽略
            }

            capture.Dispose();
        }
        catch (Exception ex)
        {
            _logger.Warn($"释放系统音频采集时异常：{ex.Message}");
        }
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        if (e.BytesRecorded <= 0 || _capture is null)
        {
            return;
        }

        try
        {
            var channels = _capture.WaveFormat.Channels;
            var rate = _capture.WaveFormat.SampleRate;
            var frames = e.BytesRecorded / (4 * channels);

            float[]? output = null;

            lock (_sync)
            {
                // 1) float32 交织 → 单声道
                for (var frame = 0; frame < frames; frame++)
                {
                    var offset = frame * channels * 4;
                    var sum = 0f;

                    for (var ch = 0; ch < channels; ch++)
                    {
                        sum += BitConverter.ToSingle(e.Buffer, offset + ch * 4);
                    }

                    _pending.Add(sum / channels);
                }

                // 2) 盒式平均重采样到 16k
                var step = (double)rate / SampleRate;
                var produced = new List<float>();

                while (_position + step <= _pending.Count)
                {
                    var from = (int)_position;
                    var to = (int)Math.Min(_pending.Count, _position + step);

                    var acc = 0f;
                    var count = 0;

                    for (var i = from; i < to; i++)
                    {
                        acc += _pending[i];
                        count++;
                    }

                    produced.Add(count > 0 ? acc / count : 0f);
                    _position += step;
                }

                // 3) 丢掉已消费的样本
                var consumed = (int)_position;

                if (consumed > 0)
                {
                    _pending.RemoveRange(0, Math.Min(consumed, _pending.Count));
                    _position -= consumed;
                }

                if (produced.Count > 0)
                {
                    output = produced.ToArray();
                }
            }

            if (output is null)
            {
                return;
            }

            // 按 ~20ms 一块发出去
            var chunkSize = Math.Max(1, SampleRate * EmitMilliseconds / 1000);
            var index = 0;

            while (index < output.Length)
            {
                var size = Math.Min(chunkSize, output.Length - index);
                var chunk = new float[size];
                Array.Copy(output, index, chunk, 0, size);
                index += size;

                SamplesAvailable?.Invoke(this, chunk);
            }
        }
        catch (Exception ex)
        {
            _logger.Error($"处理系统音频数据失败：{ex.Message}", ex);
            RaiseError(ex);
        }
    }

    private void OnRecordingStopped(object? sender, StoppedEventArgs e)
    {
        if (e.Exception is not null)
        {
            _logger.Error($"系统音频采集中断：{e.Exception.Message}", e.Exception);
            RaiseError(e.Exception);
        }
    }

    private void RaiseError(Exception ex)
    {
        try
        {
            CaptureError?.Invoke(this, ex);
        }
        catch
        {
            // 订阅者异常不影响采集线程
        }
    }

    public void Dispose() => Stop();
}

/// <summary>
/// 采集源工厂：按配置决定用麦克风还是系统音频回环。
/// </summary>
public static class AudioCaptureFactory
{
    /// <summary>配置值：麦克风（默认）。</summary>
    public const string Microphone = "microphone";

    /// <summary>配置值：系统音频回环（识别电脑正在播放的声音）。</summary>
    public const string Loopback = "loopback";

    public static IAudioCapture Create(string? source, IAppLogger logger)
    {
        if (string.Equals(source, Loopback, StringComparison.OrdinalIgnoreCase))
        {
            logger.Info("采集源：系统音频回环（会识别电脑正在播放的声音）。");
            return new WasapiLoopbackCapture(logger);
        }

        if (!string.IsNullOrWhiteSpace(source) &&
            !string.Equals(source, Microphone, StringComparison.OrdinalIgnoreCase))
        {
            logger.Warn($"未知的采集源 \"{source}\"，回退为麦克风。可选值：microphone / loopback。");
        }

        return new NAudioCapture(logger);
    }
}
