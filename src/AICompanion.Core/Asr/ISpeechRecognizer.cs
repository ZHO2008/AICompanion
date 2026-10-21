namespace AICompanion.Core.Asr;

/// <summary>
/// 语音识别器抽象（手册 §1.3 的"进程内 C++ 共享库"）。
///
/// 为什么先抽接口、再补实现：
/// whisper.cpp 的 <c>whisper_full_params</c> 是一个字段很多的巨型结构体，
/// P/Invoke 时**必须**与具体版本的 <c>whisper.h</c> 逐字段对齐；
/// 结构体布局写错不会编译报错，而是运行时栈损坏。
/// 因此具体实现 <c>WhisperCppRecognizer</c> 在拿到本项目实际编译的 whisper.cpp 源码后，
/// 按真实头文件生成，而不是凭空猜测。
/// </summary>
public interface ISpeechRecognizer : IDisposable
{
    /// <summary>识别器是否已就绪（模型与动态库都已成功加载）。</summary>
    bool IsReady { get; }

    /// <summary>未就绪时的原因说明，用于设置面板与日志。</summary>
    string? StatusMessage { get; }

    /// <summary>识别一段语音（16kHz 单声道 float）。返回识别文本。</summary>
    Task<string> TranscribeAsync(float[] samples, int sampleRate, CancellationToken cancellationToken = default);

    /// <summary>重置内部状态（例如重新加载模型）。</summary>
    void Reset();
}
