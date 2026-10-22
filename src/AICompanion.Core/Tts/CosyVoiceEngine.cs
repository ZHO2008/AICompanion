using System.Net.Http;
using System.Text;
using AICompanion.Core.Abstractions;
using AICompanion.Core.Configuration;
using AICompanion.Core.Models;

namespace AICompanion.Core.Tts;

/// <summary>
/// CosyVoice（2 / 3）引擎：对接官方 <c>runtime/python/fastapi/server.py</c>（默认 127.0.0.1:50000）。
///
/// 端点（按官方 server.py 的实际签名实现）：
///   - <c>/inference_sft</c>           tts_text + spk_id
///   - <c>/inference_zero_shot</c>     tts_text + prompt_text + prompt_wav(文件)
///   - <c>/inference_cross_lingual</c>  tts_text + prompt_wav(文件)
///   - <c>/inference_instruct</c>      tts_text + spk_id + instruct_text
///   - <c>/inference_instruct2</c>     tts_text + instruct_text + prompt_wav(文件)
///
/// **返回的是裸 PCM**（16 bit 小端、单声道），没有 RIFF 头 —— 官方 <c>generate_data</c> 直接
/// <c>yield tts_speech.numpy().astype(int16).tobytes()</c>。所以必须自己封 WAV，且要知道采样率：
/// CosyVoice2/3 是 24 kHz，CosyVoice 1 是 22.05 kHz，因此采样率做成配置项而不是写死。
/// </summary>
public sealed class CosyVoiceEngine : HttpTtsEngineBase
{
    private static readonly string[] SupportedModes =
    [
        "sft",
        "zero_shot",
        "cross_lingual",
        "instruct",
        "instruct2",
    ];

    public CosyVoiceEngine(AppConfig config, IAppLogger logger)
        : base(
            config,
            logger,
            "TTS.CosyVoice",
            config.Tts.CosyVoice.Host,
            config.Tts.CosyVoice.Port,
            config.Tts.CosyVoice.TimeoutSeconds)
    {
    }

    public override string Name => "cosyvoice";

    private CosyVoiceConfig Cfg => Config.Tts.CosyVoice;

    /// <summary>把配置里的 mode 归一化，非法值回退 zero_shot 并给出原因。</summary>
    public string ResolveMode(out string? warning)
    {
        warning = null;
        var mode = (Cfg.Mode ?? string.Empty).Trim().ToLowerInvariant();

        if (mode.Length == 0)
        {
            return "zero_shot";
        }

        if (Array.IndexOf(SupportedModes, mode) >= 0)
        {
            return mode;
        }

        warning = $"tts.cosyvoice.mode 取值 \"{Cfg.Mode}\" 不支持，已回退为 zero_shot（可选：{string.Join(" / ", SupportedModes)}）。";
        return "zero_shot";
    }

    public override async Task<byte[]> SynthesizeAsync(
        string text,
        VoiceConfig config,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            throw new ArgumentException("合成文本为空。", nameof(text));
        }

        var mode = ResolveMode(out var modeWarning);
        if (modeWarning is not null)
        {
            Logger.Warn(modeWarning);
        }

        var speaker = FirstNonEmpty(config.VoiceId, Cfg.SpkId);
        var promptText = FirstNonEmpty(config.PromptText, Cfg.PromptText);
        var promptWav = AppPaths.Resolve(FirstNonEmpty(config.ReferenceAudioPath, Cfg.PromptWav) ?? string.Empty);
        var instruct = FirstNonEmpty(config.InstructText, Cfg.InstructText);

        // CosyVoice3 的 cross_lingual 要求正文里带 <|endofprompt|> 标记，其余模式不需要动正文。
        var ttsText = mode == "cross_lingual" ? ApplyPrefix(text) : text;

        using var form = new MultipartFormDataContent();
        form.Add(new StringContent(ttsText, Encoding.UTF8), "tts_text");

        switch (mode)
        {
            case "sft":
                Require(speaker, "sft 模式需要 spk_id（tts.cosyvoice.spk_id 或角色音色 VoiceId）。");
                form.Add(new StringContent(speaker!, Encoding.UTF8), "spk_id");
                break;

            case "instruct":
                Require(speaker, "instruct 模式需要 spk_id（tts.cosyvoice.spk_id 或角色音色 VoiceId）。");
                Require(instruct, "instruct 模式需要 instruct_text（tts.cosyvoice.instruct_text）。");
                form.Add(new StringContent(speaker!, Encoding.UTF8), "spk_id");
                form.Add(new StringContent(EnsureEndMarker(instruct!), Encoding.UTF8), "instruct_text");
                break;

            case "zero_shot":
                Require(promptText, "zero_shot 模式需要 prompt_text（参考音频的原文）。");
                AddWavPart(form, promptWav, "zero_shot 模式需要 prompt_wav（tts.cosyvoice.prompt_wav）。", cancellationToken);
                form.Add(new StringContent(ApplyPrefix(promptText!), Encoding.UTF8), "prompt_text");
                break;

            case "instruct2":
                Require(instruct, "instruct2 模式需要 instruct_text。");
                AddWavPart(form, promptWav, "instruct2 模式需要 prompt_wav。", cancellationToken);
                form.Add(new StringContent(EnsureEndMarker(instruct!), Encoding.UTF8), "instruct_text");
                break;

            case "cross_lingual":
                AddWavPart(form, promptWav, "cross_lingual 模式需要 prompt_wav。", cancellationToken);
                break;
        }

        var url = Url($"/inference_{mode}");

        using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = form };
        using var response = await Http
            .SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken)
            .ConfigureAwait(false);

        var body = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            // 出错时服务端回的是 JSON，不是音频。
            throw new InvalidOperationException(
                $"CosyVoice 合成失败（{mode}）：HTTP {(int)response.StatusCode} {Truncate(Encoding.UTF8.GetString(body))}");
        }

        if (body.Length == 0)
        {
            throw new InvalidOperationException($"CosyVoice 返回了空音频（{mode}）。");
        }

        // 个别分支会直接回 WAV，那就原样使用。
        var audio = LooksLikeWav(body) ? body : WavFromPcm16(body, Cfg.SampleRate);

        MarkAvailable();
        Logger.Info(
            $"CosyVoice 合成完成（{mode}，{Cfg.SampleRate} Hz，PCM {body.Length} 字节 → WAV {audio.Length} 字节）：{text.Length} 字。");
        return audio;
    }

    /// <summary>
    /// 给 prompt_text / 正文补 CosyVoice3 的指令前缀。已经带 <c>&lt;|endofprompt|&gt;</c> 就不重复加，
    /// 前缀为空则原样返回（CosyVoice1/2 的行为）。
    /// </summary>
    private string ApplyPrefix(string value)
    {
        var prefix = Cfg.PromptPrefix;
        if (string.IsNullOrWhiteSpace(prefix) ||
            value.Contains("<|endofprompt|>", StringComparison.Ordinal))
        {
            return value;
        }

        return prefix + value;
    }

    /// <summary>
    /// instruct / instruct2 的指令文本：CosyVoice3 要求 <c>&lt;|endofprompt|&gt;</c> 落在指令**末尾**
    /// （模型用它切分"指令"与"要读的正文"）。配了 prompt_prefix 就认为用户在用 CosyVoice3，
    /// 缺标记时补一个；没配则原样发送（CosyVoice1/2 不需要）。
    /// </summary>
    private string EnsureEndMarker(string instruct)
    {
        if (string.IsNullOrWhiteSpace(Cfg.PromptPrefix) ||
            instruct.Contains("<|endofprompt|>", StringComparison.Ordinal))
        {
            return instruct;
        }

        Logger.Info("instruct_text 缺少 <|endofprompt|> 标记，已按 prompt_prefix 的配置补在末尾。");
        return instruct + "<|endofprompt|>";
    }

    private static void AddWavPart(MultipartFormDataContent form, string? path, string missingMessage, CancellationToken cancellationToken)
    {
        Require(path, missingMessage);

        if (!System.IO.File.Exists(path!))
        {
            throw new FileNotFoundException($"参考音频不存在：{path}", path);
        }

        var bytes = System.IO.File.ReadAllBytes(path!);
        var content = new ByteArrayContent(bytes);
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("audio/wav");
        form.Add(content, "prompt_wav", System.IO.Path.GetFileName(path!));
    }

    private static void Require(string? value, string message)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException(message);
        }
    }

    private static string? FirstNonEmpty(params string?[] values)
    {
        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }

        return null;
    }
}