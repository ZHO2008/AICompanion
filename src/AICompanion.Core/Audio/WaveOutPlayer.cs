using System.Runtime.InteropServices;
using AICompanion.Core.Abstractions;

namespace AICompanion.Core.Audio;

/// <summary>音频播放抽象。表现层可替换实现（例如换成 WASAPI），核心层只依赖这个接口。</summary>
public interface IAudioPlayer
{
    /// <summary>播放一段 WAV，返回时表示这段话播完了（或被取消）。</summary>
    Task PlayAsync(byte[] wav, CancellationToken cancellationToken = default);

    void Stop();

    bool IsPlaying { get; }
}

/// <summary>
/// 基于 winmm.dll PlaySound 的最简播放器。
///
/// 选它的原因：零 NuGet 依赖、能直接播内存里的 WAV（Piper 的输出就是 WAV）、
/// 不需要引入 NAudio 这类完整音频栈。
///
/// 已知限制（写在这里以免后人误用）：
///   - PlaySound 只支持 WAV；
///   - SND_MEMORY 模式下没有播放完成回调，因此"播放完成"是用 WAV 头算出时长后
///     用 Task.Delay 近似等待的，误差在几十毫秒级，对本场景（对话播报）完全够用。
/// </summary>
public sealed class WaveOutPlayer : IAudioPlayer, IDisposable
{
    private const uint SndAsync = 0x0001;
    private const uint SndNoDefault = 0x0002;
    private const uint SndMemory = 0x0004;
    private const uint SndPurge = 0x0040;

    private readonly IAppLogger _logger;
    private readonly object _sync = new();

    private GCHandle _pinnedAudio;
    private DateTime _estimatedEndUtc = DateTime.MinValue;

    public WaveOutPlayer(IAppLogger logger)
    {
        _logger = logger.ForCategory("Audio");
    }

    public bool IsPlaying
    {
        get
        {
            lock (_sync)
            {
                return _pinnedAudio.IsAllocated && DateTime.UtcNow < _estimatedEndUtc;
            }
        }
    }

    public async Task PlayAsync(byte[] wav, CancellationToken cancellationToken = default)
    {
        if (wav is null || wav.Length == 0)
        {
            return;
        }

        TimeSpan duration;
        if (WavInfo.TryParse(wav, out var info))
        {
            duration = info.Duration;
        }
        else
        {
            // 解析不出头部时给一个保守估计，避免立刻被判为"播完"。
            duration = TimeSpan.FromSeconds(Math.Clamp(wav.Length / 32000.0, 0.5, 30));
            _logger.Warn("无法解析 WAV 头部，按字节数估算播放时长。");
        }

        lock (_sync)
        {
            ReleasePin();

            // 先掐掉上一段，避免叠音。
            PlaySound(IntPtr.Zero, IntPtr.Zero, SndPurge);

            _pinnedAudio = GCHandle.Alloc(wav, GCHandleType.Pinned);
            var pointer = _pinnedAudio.AddrOfPinnedObject();

            if (!PlaySound(pointer, IntPtr.Zero, SndAsync | SndMemory | SndNoDefault))
            {
                var error = Marshal.GetLastWin32Error();
                _logger.Error($"PlaySound 播放失败（Win32 错误 {error}），将释放缓冲区。");
                ReleasePin();
                return;
            }

            _estimatedEndUtc = DateTime.UtcNow + duration;
        }

        try
        {
            await Task.Delay(duration, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            Stop();
            throw;
        }
    }

    public void Stop()
    {
        lock (_sync)
        {
            try
            {
                PlaySound(IntPtr.Zero, IntPtr.Zero, SndPurge);
            }
            catch (Exception ex)
            {
                _logger.Warn($"停止播放失败：{ex.Message}");
            }

            _estimatedEndUtc = DateTime.MinValue;
            ReleasePin();
        }
    }

    private void ReleasePin()
    {
        if (_pinnedAudio.IsAllocated)
        {
            try
            {
                _pinnedAudio.Free();
            }
            catch
            {
                // 忽略。
            }
        }
    }

    public void Dispose() => Stop();

    [DllImport("winmm.dll", EntryPoint = "PlaySoundW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PlaySound(IntPtr pszSound, IntPtr hmod, uint fdwSound);
}

/// <summary>无声播放器：在没有音频设备或需要静默运行时使用，行为是"立刻播完"。</summary>
public sealed class NullAudioPlayer : IAudioPlayer
{
    private readonly IAppLogger _logger;

    public NullAudioPlayer(IAppLogger logger) => _logger = logger.ForCategory("Audio");

    public bool IsPlaying => false;

    public Task PlayAsync(byte[] wav, CancellationToken cancellationToken = default)
    {
        _logger.Debug("无声模式：跳过音频播放。");
        return Task.CompletedTask;
    }

    public void Stop()
    {
        // 无操作。
    }
}
