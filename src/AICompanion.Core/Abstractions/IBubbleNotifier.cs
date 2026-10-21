namespace AICompanion.Core.Abstractions;

public enum BubbleLevel
{
    Info = 0,
    Warning = 1,
    Error = 2,
}

/// <summary>
/// 气泡通知通道（手册准则 4：崩溃/异常不得弹 Windows 消息框，只能走气泡、表情或托盘闪烁）。
///
/// 采用 key 寻址而非"追加文本"：
///   - 同一个 key 重复 Show 会覆盖旧内容，避免告警堆积成墙；
///   - 服务恢复时用 Clear(key) 精确清除对应告警，而不影响其它提示。
/// 实现必须自行封送到 UI 线程，调用方可以从任意线程调用。
/// </summary>
public interface IBubbleNotifier
{
    /// <param name="key">稳定标识，例如 "llm-offline"、"chatterbox-degraded"。</param>
    /// <param name="message">面向用户的中文提示，应包含"原因 + 建议操作"。</param>
    /// <param name="duration">为空表示常驻，直到被 Clear；非空表示到期自动消失（用于一次性提示）。</param>
    void Show(string key, string message, BubbleLevel level = BubbleLevel.Info, TimeSpan? duration = null);

    void Clear(string key);

    void ClearAll();

    /// <summary>设置面板状态栏用的只读状态行（例如"当前实际引擎：Piper（降级）"）。</summary>
    void SetStatus(string key, string value);

    void ClearStatus(string key);
}
