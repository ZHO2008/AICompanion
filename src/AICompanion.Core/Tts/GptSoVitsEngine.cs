using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
using AICompanion.Core.Abstractions;
using AICompanion.Core.Configuration;
using AICompanion.Core.Models;

namespace AICompanion.Core.Tts;

/// <summary>
/// GPT-SoVITS 引擎：对接 <c>api_v2.py</c>（默认 127.0.0.1:9880）。
///
/// 端点（取自整合包里的 api_v2.py，不是凭印象写的）：
///   - <c>POST /tts</c>            JSON 请求体，<c>media_type=wav</c> 时整段返回 WAV；
///   - <c>GET /set_gpt_weights</c>  切到你训练好的 <c>*.ckpt</c>；
///   - <c>GET /set_sovits_weights</c> 切到你训练好的 <c>*.pth</c>；
///   - <c>GET /set_refer_audio</c>  只换参考音频（零样本）。
///
/// 三种用法都支持，按配置组合：
///   1. **训练好的模型**：填 <c>gpt_weights_path</c> + <c>sovits_weights_path</c>；
///   2. **零样本**：只填 <c>ref_audio_path</c> + <c>prompt_text</c>（3~10 秒干净人声）；
///   3. 两者叠加：用训练模型 + 指定参考音频，音色可控性最好。
/// </summary>
public sealed class GptSoVitsEngine : HttpTtsEngineBase
{
    private bool _weightsApplied;
    private bool _weightsFailed;

    public GptSoVitsEngine(AppConfig config, IAppLogger logger)
        : base(
            config,
            logger,
            "TTS.GptSoVits",
            config.Tts.GptSoVits.Host,
            config.Tts.GptSoVits.Port,
            config.Tts.GptSoVits.TimeoutSeconds)
    {
    }

    public override string Name => "gpt_sovits";

    private GptSoVitsConfig Cfg => Config.Tts.GptSoVits;

    public override async Task<bool> RefreshAvailabilityAsync(CancellationToken cancellationToken = default)
    {
        var ok = await base.RefreshAvailabilityAsync(cancellationToken).ConfigureAwait(false);
        if (ok)
        {
            await TryApplyWeightsAsync(cancellationToken).ConfigureAwait(false);
        }

        return ok;
    }

    /// <summary>
    /// 切换权重（只做一次）。失败不致命：服务端可能本来就已经加载了某个模型，
    /// 这时继续用默认模型合成比"整个引擎不可用"更合理，所以只记 WRN。
    /// </summary>
    private async Task TryApplyWeightsAsync(CancellationToken cancellationToken)
    {
        if (_weightsApplied || _weightsFailed)
        {
            return;
        }

        var gpt = Cfg.GptWeightsPath;
        var sovits = Cfg.SovitsWeightsPath;

        if (string.IsNullOrWhiteSpace(gpt) && string.IsNullOrWhiteSpace(sovits))
        {
            _weightsApplied = true;
            Logger.Info("未配置 gpt_weights_path / sovits_weights_path，沿用服务端已加载的模型。");
            return;
        }

        try
        {
            if (!string.IsNullOrWhiteSpace(gpt))
            {
                await CallGetAsync($"/set_gpt_weights?weights_path={Uri.EscapeDataString(ResolvePath(gpt, "gpt_weights_path"))}", cancellationToken)
                    .ConfigureAwait(false);
                Logger.Info($"已切换到 GPT 权重：{gpt}");
            }

            if (!string.IsNullOrWhiteSpace(sovits))
            {
                await CallGetAsync($"/set_sovits_weights?weights_path={Uri.EscapeDataString(ResolvePath(sovits, "sovits_weights_path"))}", cancellationToken)
                    .ConfigureAwait(false);
                Logger.Info($"已切换到 SoVITS 权重：{sovits}");
            }

            _weightsApplied = true;
        }
        catch (Exception ex)
        {
            _weightsFailed = true;
            Logger.Warn($"切换 GPT-SoVITS 权重失败（继续用服务端当前模型）：{ex.Message}");
        }
    }

    private static string ResolvePath(string path, string key)
    {
        var resolved = AppPaths.Resolve(path);
        if (!System.IO.Path.IsPathRooted(resolved))
        {
            // 服务端是独立进程，相对路径会按它自己的工作目录解析 —— 必须给绝对路径。
            throw new InvalidOperationException($"config 里的 {key} 必须是绝对路径或可解析到绝对路径：{path}");
        }

        return resolved;
    }

    private async Task CallGetAsync(string relativeUrl, CancellationToken cancellationToken)
    {
        using var response = await Http.GetAsync(Url(relativeUrl), cancellationToken).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"HTTP {(int)response.StatusCode} {Truncate(body)}");
        }
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

        if (!_weightsApplied && !_weightsFailed)
        {
            await TryApplyWeightsAsync(cancellationToken).ConfigureAwait(false);
        }

        // 音色：角色配置优先，其次 config 里的默认参考音频。
        var refAudio = AppPaths.Resolve(
            string.IsNullOrWhiteSpace(config.ReferenceAudioPath) ? Cfg.RefAudioPath : config.ReferenceAudioPath);

        var promptText = string.IsNullOrWhiteSpace(config.PromptText) ? Cfg.PromptText : config.PromptText;

        if (string.IsNullOrWhiteSpace(refAudio))
        {
            throw new InvalidOperationException(
                "GPT-SoVITS 需要参考音频：请配置 tts.gpt_sovits.ref_audio_path，或在角色音色里填 ReferenceAudioPath。");
        }

        if (!System.IO.File.Exists(refAudio))
        {
            throw new FileNotFoundException($"GPT-SoVITS 参考音频不存在：{refAudio}", refAudio);
        }

        var language = string.IsNullOrWhiteSpace(config.Language) ? Cfg.TextLang : config.Language;
        var speed = Math.Clamp(config.Speed <= 0 ? 1.0 : config.Speed * Math.Max(0.1, Cfg.SpeedFactor), 0.5, 2.0);

        var payload = new JsonObject
        {
            ["text"] = text,
            ["text_lang"] = language,
            ["ref_audio_path"] = refAudio,
            ["prompt_text"] = promptText ?? string.Empty,
            ["prompt_lang"] = string.IsNullOrWhiteSpace(Cfg.PromptLang) ? language : Cfg.PromptLang,
            ["top_k"] = Cfg.TopK,
            ["top_p"] = Cfg.TopP,
            ["temperature"] = Cfg.Temperature,
            ["text_split_method"] = Cfg.TextSplitMethod,
            ["speed_factor"] = speed,
            ["media_type"] = "wav",
            ["streaming_mode"] = false,
            ["parallel_infer"] = true,
            ["seed"] = -1,
        };

        var url = Url(Cfg.TtsPath);

        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json"),
        };

        using var response = await Http
            .SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken)
            .ConfigureAwait(false);

        var audio = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"GPT-SoVITS 合成失败：HTTP {(int)response.StatusCode} {Truncate(Encoding.UTF8.GetString(audio))}");
        }

        if (audio.Length == 0)
        {
            throw new InvalidOperationException("GPT-SoVITS 返回了空音频。");
        }

        if (!LooksLikeWav(audio))
        {
            // 明确报错而不是把非音频数据丢给播放器 —— 后者只会表现为"没声音"，极难排查。
            throw new InvalidOperationException(
                $"GPT-SoVITS 返回的不是 WAV（前 16 字节：{Convert.ToHexString(audio.AsSpan(0, Math.Min(16, audio.Length)))}）。" +
                "请确认 config 里 media_type 未被改成 raw/ogg，或检查服务端日志。");
        }

        MarkAvailable();
        Logger.Info($"GPT-SoVITS 合成完成：{text.Length} 字 → {audio.Length} 字节 WAV。");
        return audio;
    }
}