using AICompanion.Core.Abstractions;
using AICompanion.Core.Audio;

namespace AICompanion.App.Animation;

/// <summary>
/// 播放器装饰器：把"开始/结束播放"暴露成事件，供动作调度与口型同步使用。
///
/// 为什么用装饰器而不是改 <see cref="IAudioPlayer"/>：
/// 核心层只关心"把这段 WAV 播完"，不应该知道 VPet/Live2D 的动作或口型细节。
/// 表现层的需求通过包装实现，核心接口保持不变。
/// </summary>
public sealed class ObservableAudioPlayer : IAudioPlayer, IDisposable
{
    private readonly IAudioPlayer _inner;
    private readonly IAppLogger _logger;

    public ObservableAudioPlayer(IAudioPlayer inner, IAppLogger logger)
    {
        _inner = inner;
        _logger = logger.ForCategory("Audio");
    }

    /// <summary>开始播放时触发，参数是整段 WAV（口型同步需要它算包络）。</summary>
    public event EventHandler<byte[]>? PlaybackStarted;

    /// <summary>播放结束/被打断/停止时触发。UI 必须借此把 MouthOpen 归零。</summary>
    public event EventHandler? PlaybackStopped;

    public bool IsPlaying => _inner.IsPlaying;

    public async Task PlayAsync(byte[] wav, CancellationToken cancellationToken = default)
    {
        // 事件在**后台线程**触发即可：订阅方必须自己切回 UI 线程。
        try
        {
            PlaybackStarted?.Invoke(this, wav);
        }
        catch (Exception ex)
        {
            _logger.Warn($"PlaybackStarted 处理异常：{ex.Message}");
        }

        try
        {
            await _inner.PlayAsync(wav, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            try
            {
                PlaybackStopped?.Invoke(this, EventArgs.Empty);
            }
            catch (Exception ex)
            {
                _logger.Warn($"PlaybackStopped 处理异常：{ex.Message}");
            }
        }
    }

    public void Stop()
    {
        _inner.Stop();

        try
        {
            PlaybackStopped?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            _logger.Warn($"PlaybackStopped 处理异常：{ex.Message}");
        }
    }

    /// <summary>
    /// 把 Dispose 透给内层播放器。
    /// 之前 AppHost 里写的是 <c>(Audio as IDisposable)?.Dispose()</c>，而本类没实现 IDisposable，
    /// 那个调用一直是空操作 —— 内层 <c>WaveOutPlayer</c> pinned 的 WAV 缓冲区因此永远不被释放。
    /// </summary>
    public void Dispose()
    {
        Stop();

        if (_inner is IDisposable disposable)
        {
            disposable.Dispose();
        }
    }
}
