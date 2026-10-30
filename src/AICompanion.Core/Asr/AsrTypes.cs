namespace AICompanion.Core.Asr;

/// <summary>ASR 状态机（手册 §3.5）。</summary>
public enum AsrState
{
    /// <summary>尚未初始化。</summary>
    NotInitialized = 0,

    /// <summary>已初始化但未监听。</summary>
    Stopped = 1,

    /// <summary>正在监听麦克风。</summary>
    Running = 2,

    /// <summary>故障：模型缺失、设备异常或连续识别失败。</summary>
    Faulted = 3,

    /// <summary>用户关掉了语音输入（asr.enabled=false）。这是配置，不是故障。</summary>
    Disabled = 4,
}

/// <summary>故障原因，用于设置面板显示与用户指引。</summary>
public enum AsrFaultReason
{
    None = 0,

    /// <summary>ASR 模型文件缺失 —— 可用设置面板重新指定路径。</summary>
    ModelMissing = 1,

    /// <summary>whisper 动态库缺失或加载失败。</summary>
    LibraryMissing = 2,

    /// <summary>麦克风设备异常。</summary>
    DeviceError = 3,

    /// <summary>连续识别失败。</summary>
    RecognitionFailure = 4,

    /// <summary>引擎内部错误。</summary>
    InternalError = 5,
}

/// <summary>一次识别结果。</summary>
public sealed record AsrRecognition(string Text, TimeSpan AudioDuration, TimeSpan Elapsed)
{
    public double RealTimeFactor => AudioDuration.TotalSeconds <= 0
        ? 0
        : Elapsed.TotalSeconds / AudioDuration.TotalSeconds;
}
