using System.Runtime.InteropServices;
using AICompanion.Core.Abstractions;
using AICompanion.Core.Configuration;

namespace AICompanion.Core.Asr;

/// <summary>
/// 语音活动检测抽象（手册 §3.5"集成 WebRTC VAD"）。
/// 做成接口是为了满足三级兜底原则：WebRTC VAD 不可用时自动退到能量 VAD，而不是直接不可用。
/// </summary>
public interface IVadDetector : IDisposable
{
    string Name { get; }

    /// <summary>每帧需要的采样数（WebRTC VAD 要求固定帧长）。</summary>
    int FrameSamples { get; }

    /// <summary>判断一帧是否为语音。</summary>
    bool IsSpeech(float[] frame, int count);

    void Reset();
}

/// <summary>
/// 能量 VAD：永远可用的兜底实现，零依赖。
/// 用短时 RMS 与阈值比较，带一点迟滞（hangover）以避免把词尾吞掉。
/// </summary>
public sealed class EnergyVadDetector : IVadDetector
{
    private readonly double _threshold;
    private readonly double _releaseThreshold;

    public EnergyVadDetector(int sampleRate, double threshold, IAppLogger logger)
    {
        _threshold = Math.Max(0.001, threshold);
        // 释放阈值取判定阈值的 60%，形成迟滞，避免在阈值附近抖动导致语音段被切碎。
        _releaseThreshold = _threshold * 0.6;

        // 20 毫秒一帧是 VAD 的常规取值。
        FrameSamples = Math.Max(1, sampleRate / 50);
        logger.ForCategory("VAD").Info($"使用能量 VAD（阈值 {_threshold:0.####}，帧长 {FrameSamples} 采样）。");
    }

    public string Name => "energy";

    public int FrameSamples { get; }

    private bool _active;

    public bool IsSpeech(float[] frame, int count)
    {
        if (count <= 0)
        {
            return _active;
        }

        double sum = 0;
        for (var i = 0; i < count; i++)
        {
            sum += frame[i] * frame[i];
        }

        var rms = Math.Sqrt(sum / count);

        if (rms >= _threshold)
        {
            _active = true;
        }
        else if (rms < _releaseThreshold)
        {
            _active = false;
        }

        return _active;
    }

    public void Reset() => _active = false;

    public void Dispose()
    {
        // 无资源需要释放。
    }
}

/// <summary>
/// WebRTC VAD（通过 libfvad 独立构建）。
///
/// 为什么用 libfvad：WebRTC 的 VAD 原本深埋在 Chromium 的构建体系里，
/// libfvad 把它抽出来做成一个只有 6 个导出函数的独立 C 库，是目前最实用的 WebRTC VAD 用法。
///
/// 加载方式刻意用 <see cref="NativeLibrary"/> + 函数指针，而不是 <c>[DllImport]</c>：
///   - 库的路径来自 config.yaml（准则 8），DllImport 的库名是编译期常量，做不到这一点；
///   - 用函数指针可以直接拿到"某个导出不存在"的明确错误，便于回退到能量 VAD。
///
/// 库不存在时 <see cref="TryCreate"/> 返回 false，由工厂回退，不会抛异常。
/// </summary>
public sealed class FvadDetector : IVadDetector
{
    private const int FrameMilliseconds = 20;

    private readonly IntPtr _instance;
    private readonly FvadProcessDelegate _process;
    private readonly FvadResetDelegate _reset;
    private readonly FvadFreeDelegate _free;
    private readonly FvadSetModeDelegate _setMode;
    private readonly FvadSetSampleRateDelegate _setSampleRate;
    private readonly int _sampleRate;
    private readonly int _mode;
    private readonly short[] _int16Frame;
    private readonly IntPtr _pinnedFrame;

    private FvadDetector(
        IntPtr instance,
        FvadProcessDelegate process,
        FvadResetDelegate reset,
        FvadFreeDelegate free,
        FvadSetModeDelegate setMode,
        FvadSetSampleRateDelegate setSampleRate,
        int sampleRate,
        int mode,
        int frameSamples)
    {
        _instance = instance;
        _process = process;
        _reset = reset;
        _free = free;
        _setMode = setMode;
        _setSampleRate = setSampleRate;
        _sampleRate = sampleRate;
        _mode = mode;
        FrameSamples = frameSamples;

        _int16Frame = new short[frameSamples];
        _pinnedFrame = Marshal.AllocHGlobal(frameSamples * sizeof(short));

        ApplyConfiguration();
    }

    /// <summary>
    /// 把采样率与模式写进原生实例。
    ///
    /// 必须封装成方法并在 <see cref="Reset"/> 之后重新调用，原因是 libfvad 的
    /// <c>fvad_reset()</c> 内部会执行 <c>inst->rate_idx = 0</c> ——
    /// 也就是把采样率**重置回默认的 8kHz**。
    /// 而 8kHz 下 320 样本等于 40ms，属于非法帧长，<c>fvad_process</c> 会一律返回 -1，
    /// 表现成"永远判定为非语音"。
    /// 由于 ASRManager 在每个语音段结束都会调用 Reset()，不重新应用的话
    /// 语音输入会在第一段之后就静默完全失效。
    /// </summary>
    private void ApplyConfiguration()
    {
        if (_setSampleRate(_instance, _sampleRate) != 0)
        {
            throw new InvalidOperationException($"fvad_set_sample_rate({_sampleRate}) 失败。");
        }

        if (_setMode(_instance, _mode) != 0)
        {
            throw new InvalidOperationException($"fvad_set_mode({_mode}) 失败。");
        }
    }

    /// <summary>
    /// 启动自检：按配置的帧长用全零帧空跑一次，返回 fvad_process 的原始结果。
    /// 返回 -1 说明帧长与该实例的采样率不匹配（即配置没生效）。
    /// </summary>
    internal int SelfCheck() => ProbeLength(_process, _instance, FrameSamples);

    public string Name => "webrtc";

    public int FrameSamples { get; }

    public static bool TryCreate(
        string libraryPath,
        int sampleRate,
        int aggressiveness,
        out FvadDetector? detector,
        out string error)
    {
        detector = null;

        if (string.IsNullOrWhiteSpace(libraryPath) || !File.Exists(libraryPath))
        {
            error = $"未找到 WebRTC VAD 动态库：{libraryPath}";
            return false;
        }

        if (sampleRate is not (8000 or 16000 or 32000 or 48000))
        {
            error = $"WebRTC VAD 不支持采样率 {sampleRate}（仅支持 8000/16000/32000/48000）。";
            return false;
        }

        try
        {
            var handle = NativeLibrary.Load(libraryPath);

            var newFn = GetDelegate<FvadNewDelegate>(handle, "fvad_new");
            var freeFn = GetDelegate<FvadFreeDelegate>(handle, "fvad_free");
            var resetFn = GetDelegate<FvadResetDelegate>(handle, "fvad_reset");
            var setModeFn = GetDelegate<FvadSetModeDelegate>(handle, "fvad_set_mode");
            var setRateFn = GetDelegate<FvadSetSampleRateDelegate>(handle, "fvad_set_sample_rate");
            var processFn = GetDelegate<FvadProcessDelegate>(handle, "fvad_process");

            var instance = newFn();
            if (instance == IntPtr.Zero)
            {
                error = "fvad_new() 返回空指针。";
                return false;
            }

            if (setModeFn(instance, Math.Clamp(aggressiveness, 0, 3)) != 0)
            {
                error = "fvad_set_mode 设置失败。";
                freeFn(instance);
                return false;
            }

            if (setRateFn(instance, sampleRate) != 0)
            {
                error = $"fvad_set_sample_rate({sampleRate}) 设置失败。";
                freeFn(instance);
                return false;
            }

            var frameSamples = sampleRate * FrameMilliseconds / 1000;
            var mode = Math.Clamp(aggressiveness, 0, 3);

            detector = new FvadDetector(
                instance,
                processFn,
                resetFn,
                freeFn,
                setModeFn,
                setRateFn,
                sampleRate,
                mode,
                frameSamples);

            /*
             * 启动自检 —— 必须放在一次 Reset() **之后**。
             *
             * 实测踩到的坑：libfvad 的 fvad_reset() 内部执行 inst->rate_idx = 0，
             * 把采样率打回默认 8kHz。若自检在 Reset() 之前做会通过，但运行期
             * ASRManager 每段语音结束都会 Reset()，之后采样率就丢了：
             * 320 样本在 8kHz 下是 40ms，属非法帧长，fvad_process 一律返回 -1，
             * 表现成"永远判定为非语音"，语音输入静默完全失效。
             * 因此自检必须复现运行期的真实状态（reset 之后）。
             */
            detector.Reset();

            var selfCheck = detector.SelfCheck();
            if (selfCheck == -1)
            {
                var accepted = new List<int>();
                foreach (var candidate in new[] { 80, 160, 240, 320, 480, 960, 1440 })
                {
                    if (ProbeLength(processFn, instance, candidate) != -1)
                    {
                        accepted.Add(candidate);
                    }
                }

                error =
                    $"WebRTC VAD 自检失败：按 {frameSamples} 采样调用 fvad_process 返回 -1（帧长非法）。" +
                    $"请求的采样率是 {sampleRate}，而该实例实际接受的长度为 " +
                    $"[{(accepted.Count > 0 ? string.Join(", ", accepted) : "无")}]。";
                detector.Dispose();
                detector = null;
                return false;
            }

            error = string.Empty;
            return true;
        }
        catch (Exception ex)
        {
            error = $"加载 WebRTC VAD 失败：{ex.Message}";
            return false;
        }
    }

    /// <summary>
    /// 用全零缓冲按指定长度空跑一次 fvad_process，返回其原始返回值。
    /// 1/0 = 帧长被接受（分别判为语音/非语音），-1 = 帧长非法。
    /// </summary>
    private static int ProbeLength(FvadProcessDelegate process, IntPtr instance, int length)
    {
        if (length <= 0)
        {
            return -1;
        }

        var buffer = new short[length];
        var pinned = GCHandle.Alloc(buffer, GCHandleType.Pinned);
        try
        {
            return process(instance, pinned.AddrOfPinnedObject(), (IntPtr)length);
        }
        finally
        {
            pinned.Free();
        }
    }

    private static T GetDelegate<T>(IntPtr handle, string exportName) where T : Delegate
    {
        if (!NativeLibrary.TryGetExport(handle, exportName, out var address) || address == IntPtr.Zero)
        {
            throw new EntryPointNotFoundException($"动态库中找不到导出函数 {exportName}。");
        }

        return Marshal.GetDelegateForFunctionPointer<T>(address);
    }

    /// <summary>
    /// 最近一次 <c>fvad_process</c> 的原始返回值，仅用于诊断。
    /// libfvad 的语义是：1=语音、0=非语音、-1=帧长非法。
    /// 把原始值暴露出来，是因为"判定为 false"有两种截然不同的原因（真的非语音 /
    /// 帧长非法），现场排障时必须能区分，否则只能靠猜。
    /// </summary>
    public int LastRawResult { get; private set; } = int.MinValue;

    public bool IsSpeech(float[] frame, int count)
    {
        if (count <= 0)
        {
            return false;
        }

        var usable = Math.Min(count, _int16Frame.Length);
        for (var i = 0; i < usable; i++)
        {
            var clamped = Math.Clamp(frame[i], -1.0f, 1.0f);
            _int16Frame[i] = (short)(clamped * short.MaxValue);
        }

        // 帧长必须是 VAD 要求的固定长度，不足则补零。
        for (var i = usable; i < _int16Frame.Length; i++)
        {
            _int16Frame[i] = 0;
        }

        Marshal.Copy(_int16Frame, 0, _pinnedFrame, _int16Frame.Length);

        var result = _process(_instance, _pinnedFrame, (IntPtr)_int16Frame.Length);
        LastRawResult = result;

        return result == 1;
    }

    public void Reset()
    {
        try
        {
            _reset(_instance);
        }
        catch
        {
            // 忽略。
        }

        // fvad_reset 会把采样率打回 8kHz，必须立刻重新应用（否则帧长非法、判定失效）。
        try
        {
            ApplyConfiguration();
        }
        catch
        {
            // 配置失败时保持原状；SelfCheck 会暴露问题。
        }
    }

    public void Dispose()
    {
        try
        {
            if (_instance != IntPtr.Zero)
            {
                _free(_instance);
            }
        }
        catch
        {
            // 忽略。
        }

        if (_pinnedFrame != IntPtr.Zero)
        {
            Marshal.FreeHGlobal(_pinnedFrame);
        }
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate IntPtr FvadNewDelegate();

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void FvadFreeDelegate(IntPtr vad);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void FvadResetDelegate(IntPtr vad);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int FvadSetModeDelegate(IntPtr vad, int mode);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int FvadSetSampleRateDelegate(IntPtr vad, int sampleRate);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int FvadProcessDelegate(IntPtr vad, IntPtr frame, IntPtr length);
}

/// <summary>按配置选择 VAD，并按三级兜底原则自动降级。</summary>
public static class VadDetectorFactory
{
    public static IVadDetector Create(AppConfig config, IAppLogger logger)
    {
        var mode = (config.Asr.VadMode ?? "auto").ToLowerInvariant();
        var log = logger.ForCategory("VAD");

        if (mode is "webrtc" or "auto")
        {
            var path = AppPaths.Resolve(config.Asr.VadLibraryPath);
            if (FvadDetector.TryCreate(path, config.Asr.SampleRate, config.Asr.VadAggressiveness, out var vad, out var error)
                && vad is not null)
            {
                log.Info($"使用 WebRTC VAD（libfvad）：{path}");
                return vad;
            }

            if (mode == "webrtc")
            {
                log.Warn($"配置要求 WebRTC VAD，但不可用，已回退能量 VAD：{error}");
            }
            else
            {
                log.Info($"WebRTC VAD 不可用，使用能量 VAD：{error}");
            }
        }

        return new EnergyVadDetector(config.Asr.SampleRate, config.Asr.EnergyThreshold, logger);
    }
}
