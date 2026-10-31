using AICompanion.Core.Abstractions;
using AICompanion.Core.Configuration;

namespace AICompanion.Core.Tts;

/// <summary>
/// 创建引擎所需的上下文。
///
/// 手册给的签名是 <c>TTSEngineFactory.Create(string engineType)</c>，这里必须多加参数：
/// 按准则 8，引擎的模型路径与端口必须来自 config.yaml，而日志与气泡也是引擎必需的能力。
/// 若坚持无参工厂，就只能把配置写成静态全局或硬编码，反而违反手册的其它条款。
/// 调用点在手册写法上仍然只是一个 <c>Create(...)</c>，因此保留了工厂语义。
/// </summary>
public sealed record TtsEngineContext(AppConfig Config, IAppLogger Logger, IBubbleNotifier Bubble);

/// <summary>
/// TTS 引擎工厂（手册 §3.4 工厂）。
///
/// 手册规定：参数为 "chatterbox" 时返回 ChatterboxEngine；
/// "piper" 或**任何其它值**都返回 PiperEngine —— 这正是"CPU 模式下强制 Piper"的保障。
/// </summary>
public static class TTSEngineFactory
{
    public const string Chatterbox = "chatterbox";
    public const string Piper = "piper";

    /// <summary>
    /// Windows 内置语音（SAPI）。这不是手册里列出的引擎，
    /// 而是为了让程序在**没有任何外部语音资源**的机器上也能出声而增加的兜底引擎。
    /// </summary>
    public const string Sapi = "sapi";

    /// <summary>GPT-SoVITS（api_v2.py，默认 9880）。</summary>
    public const string GptSoVits = "gpt_sovits";

    /// <summary>CosyVoice 2 / 3（官方 FastAPI server.py，默认 50000）。</summary>
    public const string CosyVoice = "cosyvoice";

    /// <summary>
    /// 设置面板下拉框里可选的引擎。<c>sapi</c> 不在此列：它是内部兜底，
    /// 让用户主动选"系统语音"没有意义，反而容易误以为程序不该降级。
    /// </summary>
    public static readonly string[] UserSelectable = [Piper, Chatterbox, GptSoVits, CosyVoice];

    /// <summary>引擎在界面上的显示名（带一句定位说明，方便用户判断要不要起服务）。</summary>
    public static string DisplayName(string engineType) => engineType switch
    {
        Piper => "Piper（本地 CPU）",
        Chatterbox => "Chatterbox（HTTP）",
        GptSoVits => "GPT-SoVITS（HTTP 9880）",
        CosyVoice => "CosyVoice 2/3（HTTP 50000）",
        Sapi => "Windows 内置语音（兜底）",
        _ => engineType,
    };

    /// <summary>
    /// 「拓展」页启用的 tts 模块的引擎标识前缀：<c>module:&lt;目录名&gt;</c>。
    ///
    /// 为什么用新标识而不是悄悄改成 piper：状态栏、日志与设置面板都要如实说出
    /// "现在用的是哪个模块"，否则用户看到 piper 却不知道路径已被模块覆盖。
    /// 实现上仍然复用 <see cref="PiperEngine"/>（模块提供 piper 兼容的命令行入口），
    /// 因此 TTS 的对外接口与状态机一行都不用改。
    /// </summary>
    public const string ModulePrefix = "module:";

    /// <summary>
    /// 是否是模块引擎标识。
    ///
    /// 允许 <paramref name="engineType"/> 为 null：<c>tts.engine</c> 在 config.yaml 里被写成空值时
    /// 绑出来就是 null，而这里原来直接调 <c>StartsWith</c> 会抛 NullReferenceException ——
    /// 一个手滑的 YAML 空值就足以让启动路径崩在工厂里。
    /// </summary>
    public static bool IsModuleEngine(string? engineType) =>
        !string.IsNullOrEmpty(engineType) &&
        engineType.StartsWith(ModulePrefix, StringComparison.OrdinalIgnoreCase);

    public static ITTSEngine Create(string engineType, TtsEngineContext context)
    {
        if (IsModuleEngine(engineType))
        {
            // 入口与模型路径已由 AppHost 按模块目录写进 config.Tts.Piper（只改内存）。
            // 这里只负责把它们落到 Piper 实现上，并说明"为什么是 piper"。
            var moduleName = engineType[ModulePrefix.Length..];

            context.Logger.Info(
                $"TTS 引擎标识 {engineType} → 复用 Piper 实现；" +
                $"入口 {context.Config.Tts.Piper.ExecutablePath}，模型 {context.Config.Tts.Piper.ModelPath}（来自模块 {moduleName}）。");

            return new PiperEngine(context.Config, context.Logger);
        }

        if (string.Equals(engineType, Chatterbox, StringComparison.OrdinalIgnoreCase))
        {
            return new ChatterboxEngine(context.Config, context.Logger);
        }

        if (string.Equals(engineType, GptSoVits, StringComparison.OrdinalIgnoreCase))
        {
            return new GptSoVitsEngine(context.Config, context.Logger);
        }

        if (string.Equals(engineType, CosyVoice, StringComparison.OrdinalIgnoreCase))
        {
            return new CosyVoiceEngine(context.Config, context.Logger);
        }

        if (string.Equals(engineType, Sapi, StringComparison.OrdinalIgnoreCase))
        {
            return new SapiEngine(context.Logger);
        }

        if (!string.Equals(engineType, Piper, StringComparison.OrdinalIgnoreCase))
        {
            context.Logger.Warn($"无法识别的 TTS 引擎 \"{engineType}\"，按手册规则回退为 Piper。");
        }

        return new PiperEngine(context.Config, context.Logger);
    }
}
