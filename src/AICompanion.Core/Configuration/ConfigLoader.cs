using System.Reflection;
using System.Text;
using AICompanion.Core.Abstractions;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace AICompanion.Core.Configuration;

public sealed class ConfigLoadResult
{
    public required AppConfig Config { get; init; }

    /// <summary>本次启动是否因为文件缺失而自动生成了默认配置（手册 §5.1）。</summary>
    public bool ConfigFileGenerated { get; init; }

    /// <summary>YAML 本身解析失败时的原因；此时 Config 为全默认值，程序继续运行而不崩溃。</summary>
    public string? ParseError { get; init; }

    public List<string> Warnings { get; init; } = new();
}

/// <summary>
/// 配置加载与逐字段兜底（手册 §5.1 + §6）。
///
/// 三条硬性保证：
///   1. 文件缺失 → 从嵌入资源生成带注释的默认模板，并标记以便气泡提示；
///   2. YAML 语法错误 → 记录 ParseError，退回全默认配置，**绝不崩溃**；
///   3. 任何字段为 null / 空 → 用模板里的默认值填充，并把被填充的字段名记入 Warnings。
/// </summary>
public static class ConfigLoader
{
    public const string DefaultFileName = "config.yaml";

    private const string EmbeddedTemplateSuffix = "config.default.yaml";

    public static ConfigLoadResult Load(string? explicitPath, IAppLogger logger)
    {
        var warnings = new List<string>();
        var path = ResolveConfigPath(explicitPath);
        var generated = false;

        if (path is null)
        {
            // 三处都没找到 → 在工作目录生成模板。
            path = Path.Combine(Environment.CurrentDirectory, DefaultFileName);
            generated = TryWriteDefaultTemplate(path, logger);
            if (generated)
            {
                warnings.Add($"未找到 {DefaultFileName}，已生成默认配置文件，请按需修改后重启。");
            }
        }

        AppPaths.Configure(path);

        var config = new AppConfig { ConfigFilePath = path };
        string? parseError = null;

        try
        {
            var text = ReadConfigText(path, logger, warnings);
            var deserializer = new DeserializerBuilder()
                .WithNamingConvention(UnderscoredNamingConvention.Instance)
                .IgnoreUnmatchedProperties()
                .Build();

            var parsed = deserializer.Deserialize<AppConfig>(text);
            if (parsed is not null)
            {
                Merge(parsed, config, warnings);
            }
            else
            {
                warnings.Add($"配置文件内容为空，已全部使用默认值：{path}");
            }
        }
        catch (Exception ex)
        {
            parseError = ex.Message;
            logger.Error($"解析 {DefaultFileName} 失败，已回退到全默认配置：{ex.Message}", ex);
        }

        Validate(config, warnings);

        config.ConfigFilePath = path;

        logger.Info($"配置已加载：{path}（兜底修正 {warnings.Count} 项，自动生成={generated}，解析错误={parseError ?? "无"}）");
        foreach (var w in warnings)
        {
            logger.Warn("配置兜底：" + w);
        }

        return new ConfigLoadResult
        {
            Config = config,
            ConfigFileGenerated = generated,
            ParseError = parseError,
            Warnings = warnings,
        };
    }

    /// <summary>按 explicitPath → 当前工作目录 → 程序目录 的顺序查找配置文件。</summary>
    private static string? ResolveConfigPath(string? explicitPath)
    {
        if (!string.IsNullOrWhiteSpace(explicitPath))
        {
            var full = Path.GetFullPath(explicitPath);
            return File.Exists(full) ? full : null;
        }

        foreach (var dir in new[] { Environment.CurrentDirectory, AppContext.BaseDirectory })
        {
            try
            {
                var candidate = Path.Combine(dir, DefaultFileName);
                if (File.Exists(candidate))
                {
                    return Path.GetFullPath(candidate);
                }
            }
            catch
            {
                // 目录不可访问时继续尝试下一个候选。
            }
        }

        return null;
    }

    /// <summary>
    /// 按"UTF-8 优先、GBK 兜底"读取配置文本。
    ///
    /// 为什么需要：中文 Windows 上很常见的操作是用记事本"另存为 ANSI"，
    /// 得到的是 GBK 编码的 config.yaml。按 UTF-8 硬读不会抛异常，只会把中文
    /// 全部变成 U+FFFD —— 用户看到"我明明填了角色名，界面上是一串问号"，
    /// 而日志里没有任何线索。这里先严格按 UTF-8 解，失败再按 GBK 解并告警。
    /// </summary>
    private static string ReadConfigText(string path, IAppLogger logger, List<string> warnings)
    {
        var bytes = File.ReadAllBytes(path);

        // 带 BOM 的一定是 UTF-8，剥掉 BOM 后按 UTF-8 解。
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
        {
            return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
        }

        try
        {
            var strict = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
            return strict.GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            // 不是合法 UTF-8：继续按 GBK 试。
        }

        try
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            var text = Encoding.GetEncoding(936).GetString(bytes);

            var message = $"{Path.GetFileName(path)} 不是 UTF-8 编码，已按 GBK 读取；建议另存为 UTF-8，免得以后中文变成乱码。";
            warnings.Add(message);
            logger.Warn(message);

            return text;
        }
        catch (Exception ex)
        {
            var message = $"{Path.GetFileName(path)} 既不是 UTF-8 也读不出 GBK（{ex.Message}），中文可能显示为乱码。";
            warnings.Add(message);
            logger.Warn(message);

            return Encoding.UTF8.GetString(bytes);
        }
    }
    /// <summary>把嵌入资源里的模板写到目标路径（手册要求"从嵌入资源中生成"）。</summary>
    public static bool TryWriteDefaultTemplate(string path, IAppLogger logger)
    {
        try
        {
            var template = ReadEmbeddedTemplate();
            var dir = Path.GetDirectoryName(Path.GetFullPath(path));
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            File.WriteAllText(path, template, new UTF8Encoding(false));
            logger.Info($"已生成默认配置文件：{path}");
            return true;
        }
        catch (Exception ex)
        {
            logger.Error($"生成默认配置文件失败：{path} —— {ex.Message}", ex);
            return false;
        }
    }

    public static string ReadEmbeddedTemplate()
    {
        var assembly = typeof(ConfigLoader).Assembly;
        var name = assembly
            .GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith(EmbeddedTemplateSuffix, StringComparison.OrdinalIgnoreCase));

        if (name is null)
        {
            throw new InvalidOperationException(
                $"嵌入资源中未找到 {EmbeddedTemplateSuffix}，请检查 AICompanion.Core.csproj 的 EmbeddedResource 配置。");
        }

        using var stream = assembly.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException($"无法打开嵌入资源 {name}。");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    /// <summary>
    /// 把解析结果合并进"已带默认值"的实例：只有当解析出来的值非 null 时才覆盖。
    /// 解析对象的属性初始化器已经提供了字段级默认值，这里主要处理"整节被写成 null"的情况。
    /// </summary>
    private static void Merge(AppConfig parsed, AppConfig target, List<string> warnings)
    {
        target.Llm = parsed.Llm ?? Missing("llm", warnings, target.Llm);
        target.Tts = parsed.Tts ?? Missing("tts", warnings, target.Tts);
        target.Asr = parsed.Asr ?? Missing("asr", warnings, target.Asr);
        target.Live2d = parsed.Live2d ?? Missing("live2d", warnings, target.Live2d);
        target.Knowledge = parsed.Knowledge ?? Missing("knowledge", warnings, target.Knowledge);
        target.Logging = parsed.Logging ?? Missing("logging", warnings, target.Logging);
        target.Monitor = parsed.Monitor ?? Missing("monitor", warnings, target.Monitor);
        target.Gpu = parsed.Gpu ?? Missing("gpu", warnings, target.Gpu);
        target.Context = parsed.Context ?? Missing("context", warnings, target.Context);
        target.Idle = parsed.Idle ?? Missing("idle", warnings, target.Idle);
        target.Ui = parsed.Ui ?? Missing("ui", warnings, target.Ui);

        // 嵌套小节同样可能被显式写成 null。
        target.Tts.Chatterbox = parsed.Tts?.Chatterbox ?? Missing("tts.chatterbox", warnings, target.Tts.Chatterbox);
        target.Tts.Piper = parsed.Tts?.Piper ?? Missing("tts.piper", warnings, target.Tts.Piper);
        target.Tts.GptSoVits = parsed.Tts?.GptSoVits ?? Missing("tts.gpt_sovits", warnings, target.Tts.GptSoVits);
        target.Tts.CosyVoice = parsed.Tts?.CosyVoice ?? Missing("tts.cosyvoice", warnings, target.Tts.CosyVoice);
    }

    private static T Missing<T>(string section, List<string> warnings, T fallback)
    {
        warnings.Add($"配置节 {section} 缺失或为空，已使用默认值。");
        return fallback;
    }

    /// <summary>
    /// 逐字段兜底（手册 §6："反序列化后逐个检查，任何字段为 null 或空则使用硬编码的默认值"）。
    /// 默认值直接取自 <see cref="AppConfig"/> 各属性的初始化器，也就是模板里的值。
    /// </summary>
    private static void Validate(AppConfig c, List<string> warnings)
    {
        // ---------------- llm ----------------
        var llmDefaults = new LlmConfig();
        c.Llm.ModelPath = FixString(c.Llm.ModelPath, llmDefaults.ModelPath, "llm.model_path", warnings);
        c.Llm.Host = FixString(c.Llm.Host, llmDefaults.Host, "llm.host", warnings);
        c.Llm.ServerExecutablePath = FixString(c.Llm.ServerExecutablePath, llmDefaults.ServerExecutablePath, "llm.server_executable_path", warnings);
        c.Llm.Port = FixPort(c.Llm.Port, llmDefaults.Port, "llm.port", warnings);
        c.Llm.ContextLength = FixPositive(c.Llm.ContextLength, llmDefaults.ContextLength, "llm.context_length", warnings);
        c.Llm.StartupTimeoutSeconds = FixPositive(c.Llm.StartupTimeoutSeconds, llmDefaults.StartupTimeoutSeconds, "llm.startup_timeout_seconds", warnings);
        if (c.Llm.LayerCount < 0)
        {
            warnings.Add($"llm.layer_count 为负（{c.Llm.LayerCount}），已改为 0（自动探测）。");
            c.Llm.LayerCount = 0;
        }

        if (c.Llm.Threads < 0)
        {
            c.Llm.Threads = 0;
        }

        // 只允许回环地址（手册 §1.5：所有本地网络通信绑定 127.0.0.1）。
        c.Llm.Host = EnforceLoopback(c.Llm.Host, "llm.host", warnings);
        c.Tts.Chatterbox.Host = EnforceLoopback(c.Tts.Chatterbox.Host, "tts.chatterbox.host", warnings);
        c.Monitor.Host = EnforceLoopback(c.Monitor.Host, "monitor.host", warnings);

        // ---------------- tts ----------------
        var ttsDefaults = new TtsConfig();
        c.Tts.Engine = FixString(c.Tts.Engine, ttsDefaults.Engine, "tts.engine", warnings).ToLowerInvariant();
        if (c.Tts.Engine is not ("chatterbox" or "piper" or "gpt_sovits" or "cosyvoice"))
        {
            warnings.Add($"tts.engine 取值 \"{c.Tts.Engine}\" 无法识别，已回退为 \"piper\"。");
            c.Tts.Engine = "piper";
        }

        // default_voice 允许为空：那时由三级兜底走到硬编码 "zh_CN-default"，不应报错。
        c.Tts.Piper.ModelPath = FixString(c.Tts.Piper.ModelPath, new PiperConfig().ModelPath, "tts.piper.model_path", warnings);
        c.Tts.Piper.LibraryPath = FixString(c.Tts.Piper.LibraryPath, new PiperConfig().LibraryPath, "tts.piper.library_path", warnings);
        c.Tts.Piper.ExecutablePath = FixString(c.Tts.Piper.ExecutablePath, new PiperConfig().ExecutablePath, "tts.piper.executable_path", warnings);
        c.Tts.Piper.EspeakDataPath = FixString(c.Tts.Piper.EspeakDataPath, new PiperConfig().EspeakDataPath, "tts.piper.espeak_data_path", warnings);
        c.Tts.Chatterbox.Port = FixPort(c.Tts.Chatterbox.Port, ttsDefaults.Chatterbox.Port, "tts.chatterbox.port", warnings);
        c.Tts.Chatterbox.SynthesizePath = FixString(c.Tts.Chatterbox.SynthesizePath, ttsDefaults.Chatterbox.SynthesizePath, "tts.chatterbox.synthesize_path", warnings);
        c.Tts.Chatterbox.RestartPath = FixString(c.Tts.Chatterbox.RestartPath, ttsDefaults.Chatterbox.RestartPath, "tts.chatterbox.restart_path", warnings);

        // GPT-SoVITS / CosyVoice：只校验"会影响连接与产物格式"的字段，其余原样透传。
        c.Tts.GptSoVits.Port = FixPort(c.Tts.GptSoVits.Port, ttsDefaults.GptSoVits.Port, "tts.gpt_sovits.port", warnings);
        c.Tts.GptSoVits.TtsPath = FixString(c.Tts.GptSoVits.TtsPath, ttsDefaults.GptSoVits.TtsPath, "tts.gpt_sovits.tts_path", warnings);
        c.Tts.GptSoVits.PromptLang = FixString(c.Tts.GptSoVits.PromptLang, ttsDefaults.GptSoVits.PromptLang, "tts.gpt_sovits.prompt_lang", warnings);
        c.Tts.GptSoVits.TextLang = FixString(c.Tts.GptSoVits.TextLang, ttsDefaults.GptSoVits.TextLang, "tts.gpt_sovits.text_lang", warnings);
        c.Tts.GptSoVits.TextSplitMethod = FixString(c.Tts.GptSoVits.TextSplitMethod, ttsDefaults.GptSoVits.TextSplitMethod, "tts.gpt_sovits.text_split_method", warnings);
        c.Tts.GptSoVits.TimeoutSeconds = FixPositive(c.Tts.GptSoVits.TimeoutSeconds, ttsDefaults.GptSoVits.TimeoutSeconds, "tts.gpt_sovits.timeout_seconds", warnings);
        c.Tts.CosyVoice.Port = FixPort(c.Tts.CosyVoice.Port, ttsDefaults.CosyVoice.Port, "tts.cosyvoice.port", warnings);
        c.Tts.CosyVoice.Mode = FixString(c.Tts.CosyVoice.Mode, ttsDefaults.CosyVoice.Mode, "tts.cosyvoice.mode", warnings).ToLowerInvariant();
        c.Tts.CosyVoice.PromptPrefix = c.Tts.CosyVoice.PromptPrefix ?? string.Empty;
        c.Tts.CosyVoice.SampleRate = FixPositive(c.Tts.CosyVoice.SampleRate, ttsDefaults.CosyVoice.SampleRate, "tts.cosyvoice.sample_rate", warnings);
        c.Tts.CosyVoice.TimeoutSeconds = FixPositive(c.Tts.CosyVoice.TimeoutSeconds, ttsDefaults.CosyVoice.TimeoutSeconds, "tts.cosyvoice.timeout_seconds", warnings);
        c.Tts.SoftRestartHours = FixPositive(c.Tts.SoftRestartHours, ttsDefaults.SoftRestartHours, "tts.soft_restart_hours", warnings);
        c.Tts.SoftRestartIdleMinutes = FixPositive(c.Tts.SoftRestartIdleMinutes, ttsDefaults.SoftRestartIdleMinutes, "tts.soft_restart_idle_minutes", warnings);

        // ---------------- asr ----------------
        var asrDefaults = new AsrConfig();
        c.Asr.ModelPath = FixString(c.Asr.ModelPath, asrDefaults.ModelPath, "asr.model_path", warnings);
        c.Asr.LibraryPath = FixString(c.Asr.LibraryPath, asrDefaults.LibraryPath, "asr.library_path", warnings);
        if (c.Asr.Threads < 0)
        {
            c.Asr.Threads = 0;
        }
        c.Asr.Language = FixString(c.Asr.Language, asrDefaults.Language, "asr.language", warnings);
        c.Asr.SampleRate = FixPositive(c.Asr.SampleRate, asrDefaults.SampleRate, "asr.sample_rate", warnings);
        c.Asr.VadMode = FixString(c.Asr.VadMode, asrDefaults.VadMode, "asr.vad_mode", warnings).ToLowerInvariant();
        c.Asr.VadLibraryPath = FixString(c.Asr.VadLibraryPath, asrDefaults.VadLibraryPath, "asr.vad_library_path", warnings);        if (c.Asr.VadMode is not ("auto" or "webrtc" or "energy"))
        {
            warnings.Add($"asr.vad_mode 取值 \"{c.Asr.VadMode}\" 无法识别，已回退为 \"auto\"。");
            c.Asr.VadMode = "auto";
        }

        if (c.Asr.VadAggressiveness is < 0 or > 3)
        {
            warnings.Add($"asr.vad_aggressiveness 超出 0-3 范围，已回退为 {asrDefaults.VadAggressiveness}。");
            c.Asr.VadAggressiveness = asrDefaults.VadAggressiveness;
        }

        c.Asr.EnergyThreshold = FixPositive(c.Asr.EnergyThreshold, asrDefaults.EnergyThreshold, "asr.energy_threshold", warnings);
        c.Asr.SilenceMilliseconds = FixPositive(c.Asr.SilenceMilliseconds, asrDefaults.SilenceMilliseconds, "asr.silence_milliseconds", warnings);
        c.Asr.MinSpeechMilliseconds = FixPositive(c.Asr.MinSpeechMilliseconds, asrDefaults.MinSpeechMilliseconds, "asr.min_speech_milliseconds", warnings);
        c.Asr.MaxSegmentSeconds = FixPositive(c.Asr.MaxSegmentSeconds, asrDefaults.MaxSegmentSeconds, "asr.max_segment_seconds", warnings);

        // ---------------- live2d ----------------
        var live2dDefaults = new Live2dConfig();
        c.Live2d.DefaultModelPath = FixString(c.Live2d.DefaultModelPath, live2dDefaults.DefaultModelPath, "live2d.default_model_path", warnings);
        c.Live2d.DefaultTexturePath = FixString(c.Live2d.DefaultTexturePath, live2dDefaults.DefaultTexturePath, "live2d.default_texture_path", warnings);
        c.Live2d.ExpressionDirectory = FixString(c.Live2d.ExpressionDirectory, live2dDefaults.ExpressionDirectory, "live2d.expression_directory", warnings);

        // ---------------- knowledge ----------------
        var knowledgeDefaults = new KnowledgeConfig();
        c.Knowledge.Directory = FixString(c.Knowledge.Directory, knowledgeDefaults.Directory, "knowledge.directory", warnings);
        c.Knowledge.PendingDirectoryName = FixString(c.Knowledge.PendingDirectoryName, knowledgeDefaults.PendingDirectoryName, "knowledge.pending_directory_name", warnings);
        c.Knowledge.NotifyThreshold = FixPositive(c.Knowledge.NotifyThreshold, knowledgeDefaults.NotifyThreshold, "knowledge.notify_threshold", warnings);
        c.Knowledge.PendingRetentionDays = FixPositive(c.Knowledge.PendingRetentionDays, knowledgeDefaults.PendingRetentionDays, "knowledge.pending_retention_days", warnings);
        c.Knowledge.NotifyStateFileName = FixString(c.Knowledge.NotifyStateFileName, knowledgeDefaults.NotifyStateFileName, "knowledge.notify_state_file_name", warnings);

        // ---------------- logging ----------------
        var logDefaults = new LoggingConfig();
        c.Logging.ChatLogDir = FixString(c.Logging.ChatLogDir, logDefaults.ChatLogDir, "logging.chat_log_dir", warnings);
        c.Logging.AppLogDir = FixString(c.Logging.AppLogDir, logDefaults.AppLogDir, "logging.app_log_dir", warnings);
        c.Logging.MaxLogDays = FixPositive(c.Logging.MaxLogDays, logDefaults.MaxLogDays, "logging.max_log_days", warnings);
        c.Logging.MinimumLevel = FixString(c.Logging.MinimumLevel, logDefaults.MinimumLevel, "logging.minimum_level", warnings);

        // ---------------- monitor ----------------
        var monDefaults = new MonitorConfig();
        c.Monitor.Port = FixPort(c.Monitor.Port, monDefaults.Port, "monitor.port", warnings);
        c.Monitor.HeartbeatIntervalSeconds = FixPositive(c.Monitor.HeartbeatIntervalSeconds, monDefaults.HeartbeatIntervalSeconds, "monitor.heartbeat_interval_seconds", warnings);
        c.Monitor.DisconnectTimeoutSeconds = FixPositive(c.Monitor.DisconnectTimeoutSeconds, monDefaults.DisconnectTimeoutSeconds, "monitor.disconnect_timeout_seconds", warnings);
        c.Monitor.CooldownMinutes = FixPositive(c.Monitor.CooldownMinutes, monDefaults.CooldownMinutes, "monitor.cooldown_minutes", warnings);
        c.Monitor.GameListFile = FixString(c.Monitor.GameListFile, monDefaults.GameListFile, "monitor.game_list_file", warnings);
        c.Monitor.ScanIntervalIdleSeconds = FixPositive(c.Monitor.ScanIntervalIdleSeconds, monDefaults.ScanIntervalIdleSeconds, "monitor.scan_interval_idle_seconds", warnings);
        c.Monitor.ScanIntervalActiveSeconds = FixPositive(c.Monitor.ScanIntervalActiveSeconds, monDefaults.ScanIntervalActiveSeconds, "monitor.scan_interval_active_seconds", warnings);
        c.Monitor.UnloadGpuThresholdPercent = FixPercent(c.Monitor.UnloadGpuThresholdPercent, monDefaults.UnloadGpuThresholdPercent, "monitor.unload_gpu_threshold_percent", warnings);
        c.Monitor.RestoreGpuThresholdPercent = FixPercent(c.Monitor.RestoreGpuThresholdPercent, monDefaults.RestoreGpuThresholdPercent, "monitor.restore_gpu_threshold_percent", warnings);
        c.Monitor.RestoreLowGpuMinutes = FixPositive(c.Monitor.RestoreLowGpuMinutes, monDefaults.RestoreLowGpuMinutes, "monitor.restore_low_gpu_minutes", warnings);
        if (c.Monitor.DisconnectTimeoutSeconds <= c.Monitor.HeartbeatIntervalSeconds)
        {
            warnings.Add("monitor.disconnect_timeout_seconds 不大于心跳间隔，已按心跳间隔的 3 倍修正，避免误判断连。");
            c.Monitor.DisconnectTimeoutSeconds = c.Monitor.HeartbeatIntervalSeconds * 3;
        }

        // ---------------- gpu ----------------
        var gpuDefaults = new GpuConfig();
        c.Gpu.ReservedMb = FixNonNegative(c.Gpu.ReservedMb, "gpu.reserved_mb", warnings);   // 0 = 按显存档位自动
        c.Gpu.KvCacheReservedMb = FixNonNegative(c.Gpu.KvCacheReservedMb, "gpu.kv_cache_reserved_mb", warnings);   // 0 = 自动
        c.Gpu.PerLayerMb = FixNonNegative(c.Gpu.PerLayerMb, "gpu.per_layer_mb", warnings);   // 0 = 按 GGUF 自动
        c.Gpu.SafetyFactor = FixSafetyFactor(c.Gpu.SafetyFactor, "gpu.safety_factor", warnings);   // 0 = 按显存档位自动
        if (c.Gpu.SafetyFactor > 1.0)
        {
            warnings.Add($"gpu.safety_factor 大于 1（{c.Gpu.SafetyFactor}），已回退为 {gpuDefaults.SafetyFactor}。");
            c.Gpu.SafetyFactor = gpuDefaults.SafetyFactor;
        }

        c.Gpu.MaxOomRetries = FixPositive(c.Gpu.MaxOomRetries, gpuDefaults.MaxOomRetries, "gpu.max_oom_retries", warnings);
        c.Gpu.OomNglStep = FixPositive(c.Gpu.OomNglStep, gpuDefaults.OomNglStep, "gpu.oom_ngl_step", warnings);
        c.Gpu.OomRetryDelaySeconds = FixPositive(c.Gpu.OomRetryDelaySeconds, gpuDefaults.OomRetryDelaySeconds, "gpu.oom_retry_delay_seconds", warnings);

        // ---------------- context ----------------
        var ctxDefaults = new ContextConfig();
        c.Context.MaxRecentRounds = FixPositive(c.Context.MaxRecentRounds, ctxDefaults.MaxRecentRounds, "context.max_recent_rounds", warnings);
        c.Context.SendRecentRounds = FixPositive(c.Context.SendRecentRounds, ctxDefaults.SendRecentRounds, "context.send_recent_rounds", warnings);
        c.Context.SummaryTokenLimit = FixPositive(c.Context.SummaryTokenLimit, ctxDefaults.SummaryTokenLimit, "context.summary_token_limit", warnings);
        c.Context.AnchorLimit = FixPositive(c.Context.AnchorLimit, ctxDefaults.AnchorLimit, "context.anchor_limit", warnings);
        c.Context.RefineWindowSize = FixPositive(c.Context.RefineWindowSize, ctxDefaults.RefineWindowSize, "context.refine_window_size", warnings);
        c.Context.ArchiveFileName = FixString(c.Context.ArchiveFileName, ctxDefaults.ArchiveFileName, "context.archive_file_name", warnings);
        c.Context.PersonalityEntryLimit = FixPositive(c.Context.PersonalityEntryLimit, ctxDefaults.PersonalityEntryLimit, "context.personality_entry_limit", warnings);
        if (c.Context.SendRecentRounds > c.Context.MaxRecentRounds)
        {
            warnings.Add($"context.send_recent_rounds（{c.Context.SendRecentRounds}）大于 max_recent_rounds，已收敛为 {c.Context.MaxRecentRounds}。");
            c.Context.SendRecentRounds = c.Context.MaxRecentRounds;
        }

        // ---------------- idle ----------------
        var idleDefaults = new IdleConfig();
        c.Idle.PollIntervalMs = FixPositive(c.Idle.PollIntervalMs, idleDefaults.PollIntervalMs, "idle.poll_interval_ms", warnings);
        c.Idle.IdleMinutes = FixPositive(c.Idle.IdleMinutes, idleDefaults.IdleMinutes, "idle.idle_minutes", warnings);
        c.Idle.CpuLowPercent = FixPercent(c.Idle.CpuLowPercent, idleDefaults.CpuLowPercent, "idle.cpu_low_percent", warnings);
        c.Idle.CpuHighPercent = FixPercent(c.Idle.CpuHighPercent, idleDefaults.CpuHighPercent, "idle.cpu_high_percent", warnings);
        c.Idle.RefineGpuMemoryPercent = FixPercent(c.Idle.RefineGpuMemoryPercent, idleDefaults.RefineGpuMemoryPercent, "idle.refine_gpu_memory_percent", warnings);
        if (c.Idle.CpuHighPercent <= c.Idle.CpuLowPercent)
        {
            warnings.Add($"idle.cpu_high_percent（{c.Idle.CpuHighPercent}）不大于 cpu_low_percent（{c.Idle.CpuLowPercent}），已修正为 70。");
            c.Idle.CpuHighPercent = idleDefaults.CpuHighPercent;
        }

        // ---------------- ui ----------------
        var uiDefaults = new UiConfig();
        c.Ui.FallbackDisplayName = FixString(c.Ui.FallbackDisplayName, uiDefaults.FallbackDisplayName, "ui.fallback_display_name", warnings);
        c.Ui.FallbackBackgroundColor = FixString(c.Ui.FallbackBackgroundColor, uiDefaults.FallbackBackgroundColor, "ui.fallback_background_color", warnings);
        c.Ui.PetImagePath = FixString(c.Ui.PetImagePath, uiDefaults.PetImagePath, "ui.pet_image_path", warnings);
        c.Ui.BubbleDurationSeconds = FixPositive(c.Ui.BubbleDurationSeconds, uiDefaults.BubbleDurationSeconds, "ui.bubble_duration_seconds", warnings);
        if (c.Ui.IdleExpressionSeconds < 0)
        {
            c.Ui.IdleExpressionSeconds = 0;
        }

        // 悬停切换的两个时长允许为 0（= 立即切换 / 不自动回退），只拦负数。
        if (c.Ui.TransparentHoverDelayMs < 0)
        {
            warnings.Add($"ui.transparent_hover_delay_ms 为负（{c.Ui.TransparentHoverDelayMs}），已改为 0。");
            c.Ui.TransparentHoverDelayMs = 0;
        }

        if (c.Ui.TransparentHoverDelayMs > 5000)
        {
            warnings.Add($"ui.transparent_hover_delay_ms 过大（{c.Ui.TransparentHoverDelayMs}），已收敛到 5000。");
            c.Ui.TransparentHoverDelayMs = 5000;
        }

        if (c.Ui.TransparentAutoRevertSeconds < 0)
        {
            warnings.Add($"ui.transparent_auto_revert_seconds 为负（{c.Ui.TransparentAutoRevertSeconds}），已改为 0（不自动回退）。");
            c.Ui.TransparentAutoRevertSeconds = 0;
        }

        if (c.Ui.ExpressionHoldSeconds < 0)
        {
            warnings.Add($"ui.expression_hold_seconds 为负（{c.Ui.ExpressionHoldSeconds}），已改为 0（不回退表情）。");
            c.Ui.ExpressionHoldSeconds = 0;
        }

        // 不透明度必须落在 0.1 - 1.0：0 会让角色彻底看不见，>1 无意义。
        if (c.Ui.TransparentOpacity < 0.1 || c.Ui.TransparentOpacity > 1.0)
        {
            warnings.Add($"ui.transparent_opacity 超出 0.1-1.0（{c.Ui.TransparentOpacity}），已改为 0.5。");
            c.Ui.TransparentOpacity = 0.5;
        }

        // 0 = 自动适配屏幕；其余必须落在 0.5 - 2.5，超出范围按自动处理。
        if (c.Ui.Scale != 0)
        {
            if (c.Ui.Scale < 0.5 || c.Ui.Scale > 2.5)
            {
                warnings.Add($"ui.scale 超出 0.5-2.5（{c.Ui.Scale}），已改为 0（按屏幕自动适配）。");
                c.Ui.Scale = 0;
            }
        }
    }

    // ------------------------------------------------------------------ 字段级修正工具

    private static string FixString(string? value, string fallback, string field, List<string> warnings)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            return value;
        }

        warnings.Add($"{field} 缺失或为空，已回退为 \"{fallback}\"。");
        return fallback;
    }

    /// <summary>允许 0（表示"自动"）的整数校验。</summary>
    private static int FixNonNegative(int value, string field, List<string> warnings)
    {
        if (value >= 0)
        {
            return value;
        }

        warnings.Add($"{field} 为负数（{value}），已按 0（自动）处理。");
        return 0;
    }

    /// <summary>允许 0（表示"自动"）的安全系数校验；非 0 时限制在 0–1。</summary>
    private static double FixSafetyFactor(double value, string field, List<string> warnings)
    {
        if (double.IsNaN(value) || double.IsInfinity(value) || value < 0)
        {
            warnings.Add($"{field} 非法（{value}），已按 0（自动）处理。");
            return 0;
        }

        if (value > 1)
        {
            warnings.Add($"{field} 大于 1（{value}），已按 0（自动）处理。");
            return 0;
        }

        return value;
    }

    private static int FixPositive(int value, int fallback, string field, List<string> warnings)
    {
        if (value > 0)
        {
            return value;
        }

        warnings.Add($"{field} 非正数（{value}），已回退为 {fallback}。");
        return fallback;
    }

    private static double FixPositive(double value, double fallback, string field, List<string> warnings)
    {
        if (value > 0 && !double.IsNaN(value) && !double.IsInfinity(value))
        {
            return value;
        }

        warnings.Add($"{field} 非正数或非法（{value}），已回退为 {fallback}。");
        return fallback;
    }

    private static int FixPort(int value, int fallback, string field, List<string> warnings)
    {
        if (value is > 0 and <= 65535)
        {
            return value;
        }

        warnings.Add($"{field} 端口号非法（{value}），已回退为 {fallback}。");
        return fallback;
    }

    private static int FixPercent(int value, int fallback, string field, List<string> warnings)
    {
        if (value is > 0 and <= 100)
        {
            return value;
        }

        warnings.Add($"{field} 超出 1-100 范围（{value}），已回退为 {fallback}。");
        return fallback;
    }

    private static double FixPercent(double value, double fallback, string field, List<string> warnings)
    {
        if (value is > 0 and <= 100 && !double.IsNaN(value))
        {
            return value;
        }

        warnings.Add($"{field} 超出 1-100 范围（{value}），已回退为 {fallback}。");
        return fallback;
    }

    /// <summary>强制回环地址（手册 §1.5）。写成 0.0.0.0 或外部 IP 都会被改写并告警。</summary>
    private static string EnforceLoopback(string value, string field, List<string> warnings)
    {
        if (string.Equals(value, "127.0.0.1", StringComparison.Ordinal) ||
            string.Equals(value, "localhost", StringComparison.OrdinalIgnoreCase))
        {
            return "127.0.0.1";
        }

        warnings.Add($"{field} 取值 \"{value}\" 不是回环地址，已强制改为 127.0.0.1（手册 §1.5）。");
        return "127.0.0.1";
    }

    public static LogLevel ParseLogLevel(string value) => Enum.TryParse<LogLevel>(value, ignoreCase: true, out var level)
        ? level
        : LogLevel.Info;
}
