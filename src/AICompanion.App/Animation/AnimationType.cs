namespace AICompanion.App.Animation;

/// <summary>
/// 逻辑动作。**代码只认这个枚举，不认模型的动作名。**
///
/// 各 Live2D / VPet 模型的 motion 组名五花八门（idle / tap_body / question…），
/// 因此由角色包里的 AvatarProfile.json 负责把逻辑动作映射到该模型的实际名字，
/// 换模型只改配置、不改代码。
/// </summary>
public enum AnimationType
{
    /// <summary>待机。</summary>
    Idle = 0,

    /// <summary>倾听中（ASR Running）。</summary>
    Listen = 1,

    /// <summary>思考中（LLM 等首 Token）。</summary>
    Think = 2,

    /// <summary>说话中（TTS 开始播放）。</summary>
    Speak = 3,

    /// <summary>疑惑（TTS 引擎崩溃降级等异常）。</summary>
    Confused = 4,

    /// <summary>开心。</summary>
    Happy = 5,

    /// <summary>难过。</summary>
    Sad = 6,

    /// <summary>
    /// 兜底动作：映射缺失或模型里没有该动作时播它。
    /// 注意它本身也可能缺失，此时只记日志、不做任何播放。
    /// </summary>
    Fallback = 7,
}

/// <summary>
/// 动作优先级：从低到高。高优先级可打断低优先级；<see cref="Critical"/> 额外无视 1 秒防抖。
/// </summary>
public enum AnimationPriority
{
    Idle = 0,
    Normal = 1,
    Speak = 2,
    StateChange = 3,
    Critical = 4,
}

/// <summary>
/// 一次动作请求。不可变，可安全跨线程传递。
/// </summary>
/// <param name="Type">逻辑动作（映射到模型动作名的唯一入口）。</param>
/// <param name="Priority">优先级，决定能否打断与是否受防抖限制。</param>
/// <param name="Source">来源标记（"ASR" / "LLM" / "TTS" …），只用于日志与排障。</param>
/// <param name="HoldFor">保持时长；null 表示保持到下一次请求。</param>
public sealed record AnimationRequest(
    AnimationType Type,
    AnimationPriority Priority,
    string Source = "unknown",
    TimeSpan? HoldFor = null)
{
    public DateTime CreatedUtc { get; init; } = DateTime.UtcNow;
}

/// <summary>逻辑动作到优先级的默认约定（调用方也可以显式指定）。</summary>
public static class AnimationPriorityDefaults
{
    public static AnimationPriority For(AnimationType type) => type switch
    {
        AnimationType.Idle => AnimationPriority.Idle,
        AnimationType.Happy or AnimationType.Sad => AnimationPriority.Normal,
        AnimationType.Speak => AnimationPriority.Speak,
        AnimationType.Listen or AnimationType.Think => AnimationPriority.StateChange,
        AnimationType.Confused => AnimationPriority.Critical,
        _ => AnimationPriority.Normal,
    };
}
