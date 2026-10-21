using AICompanion.Core.Models;

namespace AICompanion.Core.Abstractions;

/// <summary>
/// 语音合成引擎抽象（手册 §3.4）。
///
/// 与手册签名的一致性说明：
///   - 手册原签名 <c>Task&lt;byte[]&gt; SynthesizeAsync(string text, VoiceConfig config)</c> 保持不变，
///     仅按准则 7（可中断）追加了一个带默认值的 CancellationToken —— 手册写法的调用点仍然原样编译。
///   - 追加只读属性 <see cref="Name"/>，用于设置面板显示"当前实际引擎：Piper（降级）"。
/// </summary>
public interface ITTSEngine
{
    /// <summary>引擎标识，取值为 "chatterbox" 或 "piper"，用于状态栏展示与日志。</summary>
    string Name { get; }

    /// <summary>
    /// 合成语音，返回 WAV 字节流。仅依赖入参（准则 2：幂等可重放）。
    /// 内部必须完成三级音色兜底（角色配置 → 全局默认 → 硬编码内置）。
    /// </summary>
    Task<byte[]> SynthesizeAsync(string text, VoiceConfig config, CancellationToken cancellationToken = default);

    /// <summary>停止当前播放/合成。</summary>
    void Stop();

    /// <summary>释放引擎持有的非托管资源。</summary>
    void Unload();

    /// <summary>引擎当前是否可用。Chatterbox 看 /health，Piper 看模型文件是否存在。</summary>
    bool IsAvailable { get; }
}
