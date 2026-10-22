using AICompanion.Core.Abstractions;
using NAudio.Wave;

namespace AICompanion.Core.Asr;

/// <summary>
/// 麦克风采集抽象。用接口隔离 NAudio（准则 8），核心层不直接依赖第三方音频栈。
/// </summary>
public interface IAudioCapture : IDisposable
{
    bool IsCapturing { get; }

    int SampleRate { get; }

    /// <summary>每块采集数据（16kHz 单声道 float，取值范围 -1..1）。块长不固定，由上层重新分帧。</summary>
    event EventHandler<float[]>? SamplesAvailable;

    event EventHandler<Exception>? CaptureError;

    void Start(int sampleRate);

    void Stop();
}

/// <summary>基于 NAudio WaveInEvent 的麦克风采集。</summary>
public sealed class NAudioCapture : IAudioCapture
{
    private readonly IAppLogger _logger;
    private readonly object _sync = new();

    private WaveInEvent? _waveIn;

    public NAudioCapture(IAppLogger logger)
    {
        _logger = logger.ForCategory("AudioCapture");
    }

    public bool IsCapturing { get; private set; }

    public int SampleRate { get; private set; }

    public event EventHandler<float[]>? SamplesAvailable;

    public event EventHandler<Exception>? CaptureError;

    public void Start(int sampleRate)
    {
        lock (_sync)
        {
            if (IsCapturing)
            {
                return;
            }

            SampleRate = sampleRate;

            try
            {
                _waveIn = new WaveInEvent
                {
                    WaveFormat = new WaveFormat(sampleRate, 16, 1),
                    // 20ms 一块，与 VAD 帧长一致，减少上层分帧的复杂度。
                    BufferMilliseconds = 20,
                    NumberOfBuffers = 4,
                };

                _waveIn.DataAvailable += OnDataAvailable;
                _waveIn.RecordingStopped += OnRecordingStopped;
                _waveIn.StartRecording();

                IsCapturing = true;
                _logger.Info($"麦克风采集已启动：{sampleRate} Hz，16 bit，单声道。");
            }
            catch (Exception ex)
            {
                _logger.Error($"启动麦克风失败：{ex.Message}", ex);

                // 启动失败必须自己收尾：此时 IsCapturing 还是 false，
                // Stop() 会在 "!IsCapturing" 处直接返回，waveIn 的原生句柄就再也释放不掉了。
                try
                {
                    _waveIn?.Dispose();
                }
                catch
                {
                    // 忽略。
                }

                _waveIn = null;
                RaiseError(ex);
            }
        }
    }

    public void Stop()
    {
        lock (_sync)
        {
            if (!IsCapturing || _waveIn is null)
            {
                return;
            }

            try
            {
                _waveIn.DataAvailable -= OnDataAvailable;
                _waveIn.RecordingStopped -= OnRecordingStopped;
                _waveIn.StopRecording();
            }
            catch (Exception ex)
            {
                _logger.Warn($"停止麦克风时异常：{ex.Message}");
            }
            finally
            {
                try
                {
                    _waveIn.Dispose();
                }
                catch
                {
                    // 忽略。
                }

                _waveIn = null;
                IsCapturing = false;
                _logger.Info("麦克风采集已停止。");
            }
        }
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs e)
    {
        if (e.BytesRecorded <= 0)
        {
            return;
        }

        try
        {
            var samples = new float[e.BytesRecorded / 2];
            for (var i = 0; i < samples.Length; i++)
            {
                var value = (short)(e.Buffer[i * 2] | (e.Buffer[i * 2 + 1] << 8));
                samples[i] = value / 32768f;
            }

            SamplesAvailable?.Invoke(this, samples);
        }
        catch (Exception ex)
        {
            _logger.Error($"处理采集数据失败：{ex.Message}", ex);
            RaiseError(ex);
        }
    }

    private void OnRecordingStopped(object? sender, StoppedEventArgs e)
    {
        // 设备侧停了就必须把状态复位，否则 IsCapturing 永远为 true：
        // 上层自愈时 Start() 会在 "if (IsCapturing) return;" 处直接退出，
        // 表现为"状态显示正在监听、但再也收不到音频"。
        lock (_sync)
        {
            IsCapturing = false;
        }

        if (e.Exception is not null)
        {
            _logger.Error($"录音设备异常终止：{e.Exception.Message}", e.Exception);
            RaiseError(e.Exception);
        }
        else
        {
            _logger.Warn("录音设备已停止（没有异常信息）。");
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
            // 订阅者抛异常不应影响采集线程。
        }
    }

    public void Dispose() => Stop();
}
