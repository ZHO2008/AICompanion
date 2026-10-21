using AICompanion.Core.Abstractions;

namespace AICompanion.Core.Asr;

/// <summary>
/// <see cref="ISpeechRecognizer"/> 的"未接线"实现。
///
/// 用途：当 whisper.cpp 尚未编译、动态库不存在时，让整个程序仍然可以正常启动并走
/// "语音不可用 → 文字输入"的降级路径（手册 §3.5 与 §9 的验收要求），
/// 而不是因为缺少一个可选组件就崩溃或无法启动。
///
/// 真实的 whisper.cpp 实现见 <c>WhisperCppRecognizer</c>：它按本项目实际编译出的
/// <c>whisper.h</c> 逐字段对齐 <c>whisper_full_params</c> 后再落地 P/Invoke。
/// </summary>
public sealed class UnavailableSpeechRecognizer : ISpeechRecognizer
{
    public UnavailableSpeechRecognizer(string reason)
    {
        StatusMessage = reason;
    }

    public bool IsReady => false;

    public string? StatusMessage { get; }

    public Task<string> TranscribeAsync(float[] samples, int sampleRate, CancellationToken cancellationToken = default)
        => throw new InvalidOperationException(StatusMessage ?? "语音识别引擎不可用。");

    public void Reset()
    {
        // 无状态可重置。
    }

    public void Dispose()
    {
        // 无资源。
    }
}
