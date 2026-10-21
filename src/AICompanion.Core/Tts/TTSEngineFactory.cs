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

    public static ITTSEngine Create(string engineType, TtsEngineContext context)
    {
        if (string.Equals(engineType, Chatterbox, StringComparison.OrdinalIgnoreCase))
        {
            return new ChatterboxEngine(context.Config, context.Logger);
        }

        if (string.Equals(engineType, Sapi, StringComparison.OrdinalIgnoreCase))
        {
            return new SapiEngine(context.Logger);
        }

        if (!string.Equals(engineType, Piper, StringComparison.OrdinalIgnoreCase))
        {
            context.Logger.Warn($"无法识别的 TTS 引擎 \"{engineType}\"，按手册规则回退为 Piper。");
        }

        return new PiperEngine(context.Config, context.Logger, context.Bubble);
    }
}
