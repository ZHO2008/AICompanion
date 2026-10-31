using System.IO;
using System.Text;
using System.Text.Json;
using AICompanion.Core.Configuration;

namespace AICompanion.App.Services;

/// <summary>
/// 表现层用户偏好的持久化：**外观图片** 与 **角色大小倍率**。
///
/// 刻意**不写 config.yaml**：准则 9 要求"程序绝不自行更改用户配置"。
/// 用户在界面上调的这两项属于运行时偏好，因此单独存到 Assets\appearance.json；
/// 删掉该文件即恢复为 config.yaml 里的配置。
///
/// 为什么不加锁也安全：应用里只有 UI 线程会调用写入，
/// 且每次都做"读-改-写"整个文件，不会丢另一个字段。
/// </summary>
public static class AppearanceStore
{
    private const string RelativePath = "Assets/appearance.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    public static string StorePath => AppPaths.Resolve(RelativePath);

    private sealed class Model
    {
        /// <summary>用户选定的角色图片路径；null 表示用 config.yaml 里的。</summary>
        public string? ImagePath { get; set; }

        /// <summary>用户调过的角色大小倍率；null 表示用 config.yaml / 自动适配。</summary>
        public double? Scale { get; set; }

        /// <summary>气泡显示秒数（设置面板可改）；null 表示用 config.yaml 的 ui.bubble_duration_seconds。</summary>
        public double? BubbleSeconds { get; set; }

        /// <summary>透明态不透明度（设置面板可改）；null 表示用 config.yaml 的 ui.transparent_opacity。</summary>
        public double? TransparentOpacity { get; set; }

        /// <summary>
        /// Live2D 模型水平微调（单位：窗口宽度的比例，正数向左）。
        /// null 或 0 表示"按画面自动居中"，此时不做任何额外偏移。
        /// </summary>
        public double? HorizontalOffset { get; set; }

        /// <summary>
        /// 用户在设置面板选的 TTS 引擎；null 表示跟随 config.yaml 的 tts.engine。
        /// 存这里而不是回写 config.yaml，是为了守住准则 9（程序绝不改用户配置）。
        /// </summary>
        public string? TtsEngine { get; set; }

        /// <summary>
        /// 用户关掉了对话 / 语音 / 语音输入；null 表示跟随 config.yaml 的 *.enabled。
        /// 与 TtsEngine 同理：存这里而不是回写 config.yaml（准则 9）。
        /// </summary>
        public bool? LlmEnabled { get; set; }

        public bool? TtsEnabled { get; set; }

        public bool? AsrEnabled { get; set; }

        /// <summary>设置面板「对话」页选的 GGUF 路径（相对仓库根）；null 表示跟随 config.yaml。</summary>
        public string? LlmModelPath { get; set; }

        /// <summary>上下文长度；0 = 自动（按 GGUF 元数据），null 表示跟随 config.yaml。</summary>
        public int? LlmContextLength { get; set; }

        /// <summary>系统预留显存（MB）；0 = 按显存档位自动，null 表示跟随 config.yaml。</summary>
        public int? GpuReservedMb { get; set; }

        public string? TtsVoice { get; set; }

        public double? TtsSpeed { get; set; }

        /// <summary>ASR 采集源：microphone / loopback。</summary>
        public string? AsrCapture { get; set; }

        public bool? HoverSwitch { get; set; }

        public bool? ClickThrough { get; set; }

        public bool? PushToTalk { get; set; }

        public double? IdleMinutes { get; set; }

        public double? MaintenanceCooldownMinutes { get; set; }

        /// <summary>桌宠窗口启动位置（DIP，屏幕坐标）。</summary>
        public double? StartX { get; set; }

        public double? StartY { get; set; }

        public bool? Live2dEnabled { get; set; }

        /// <summary>设置面板选中的 Live2D 模型（相对仓库根）；null 表示跟随 config.yaml。</summary>
        public string? Live2dModelPath { get; set; }

        /// <summary>三类模块各自启用中的目录名；null 表示未指定（取第一个可用模块）。</summary>
        public string? LlmModule { get; set; }

        public string? TtsModule { get; set; }

        public string? AsrModule { get; set; }
    }

    private static Model LoadModel()
    {
        try
        {
            var path = StorePath;
            if (!File.Exists(path))
            {
                return new Model();
            }

            return JsonSerializer.Deserialize<Model>(File.ReadAllText(path, Encoding.UTF8), JsonOptions)
                   ?? new Model();
        }
        catch
        {
            // 文件损坏时按"没有覆盖"处理，不影响启动。
            return new Model();
        }
    }

    private static bool SaveModel(Model model)
    {
        try
        {
            var path = StorePath;

            if (string.IsNullOrWhiteSpace(model.ImagePath) &&
                model.Scale is null &&
                model.BubbleSeconds is null &&
                model.TransparentOpacity is null &&
                model.HorizontalOffset is null &&
                string.IsNullOrWhiteSpace(model.TtsEngine) &&
                model.LlmEnabled is null &&
                model.TtsEnabled is null &&
                model.AsrEnabled is null &&
                string.IsNullOrWhiteSpace(model.LlmModelPath) &&
                model.LlmContextLength is null &&
                model.GpuReservedMb is null &&
                string.IsNullOrWhiteSpace(model.TtsVoice) &&
                model.TtsSpeed is null &&
                string.IsNullOrWhiteSpace(model.AsrCapture) &&
                model.HoverSwitch is null &&
                model.ClickThrough is null &&
                model.PushToTalk is null &&
                model.IdleMinutes is null &&
                model.MaintenanceCooldownMinutes is null &&
                model.StartX is null &&
                model.StartY is null &&
                model.Live2dEnabled is null &&
                string.IsNullOrWhiteSpace(model.Live2dModelPath) &&
                string.IsNullOrWhiteSpace(model.LlmModule) &&
                string.IsNullOrWhiteSpace(model.TtsModule) &&
                string.IsNullOrWhiteSpace(model.AsrModule))
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }

                return true;
            }

            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.WriteAllText(path, JsonSerializer.Serialize(model, JsonOptions), new UTF8Encoding(false));
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>读取用户选定的形象路径；没有覆盖时返回 null。</summary>
    public static string? LoadImagePath()
    {
        var model = LoadModel();
        return string.IsNullOrWhiteSpace(model.ImagePath) ? null : model.ImagePath;
    }

    /// <summary>保存形象覆盖；传 null 表示清除覆盖（恢复 config.yaml 里的外观）。</summary>
    public static bool SaveImagePath(string? imagePath)
    {
        var model = LoadModel();
        model.ImagePath = string.IsNullOrWhiteSpace(imagePath) ? null : imagePath;
        return SaveModel(model);
    }

    /// <summary>读取用户调过的角色大小；没有覆盖时返回 null（走 config.yaml / 自动适配）。</summary>
    public static double? LoadScale()
    {
        var scale = LoadModel().Scale;
        return scale is > 0 and <= 5 ? scale : null;
    }

    /// <summary>保存角色大小覆盖；传 null 表示恢复自动适配。</summary>
    public static bool SaveScale(double? scale)
    {
        var model = LoadModel();
        model.Scale = scale;
        return SaveModel(model);
    }

    /// <summary>读取气泡显示秒数；没有用户设置时返回 null（走 config.yaml）。</summary>
    public static double? LoadBubbleSeconds()
    {
        var seconds = LoadModel().BubbleSeconds;
        return seconds is >= 1 and <= 120 ? seconds : null;
    }

    /// <summary>保存气泡显示秒数。</summary>
    public static bool SaveBubbleSeconds(double seconds)
    {
        var model = LoadModel();
        model.BubbleSeconds = Math.Clamp(seconds, 1, 120);
        return SaveModel(model);
    }

    /// <summary>读取透明态不透明度；没有用户设置时返回 null（走 config.yaml）。</summary>
    public static double? LoadTransparentOpacity()
    {
        var opacity = LoadModel().TransparentOpacity;
        return opacity is >= 0.1 and <= 1.0 ? opacity : null;
    }

    /// <summary>保存透明态不透明度。</summary>
    public static bool SaveTransparentOpacity(double opacity)
    {
        var model = LoadModel();
        model.TransparentOpacity = Math.Clamp(opacity, 0.1, 1.0);
        return SaveModel(model);
    }
    /// <summary>读取用户设置的模型水平微调；null 表示未设置（按自动居中）。</summary>
    public static double? LoadHorizontalOffset()
    {
        return LoadModel().HorizontalOffset;
    }

    /// <summary>保存模型水平微调。传 null / 0 表示恢复自动居中。</summary>
    public static bool SaveHorizontalOffset(double? offset)
    {
        var model = LoadModel();

        // 0 视为"没设"，这样删掉文件与滑块归零是同一个效果
        model.HorizontalOffset = offset is null || Math.Abs(offset.Value) < 0.0005 ? null : offset;
        return SaveModel(model);
    }

    /// <summary>读取用户选的 TTS 引擎；没有覆盖时返回 null（跟随 config.yaml）。</summary>
    public static string? LoadTtsEngine()
    {
        var engine = LoadModel().TtsEngine;
        return string.IsNullOrWhiteSpace(engine) ? null : engine.Trim().ToLowerInvariant();
    }

    /// <summary>保存用户选的 TTS 引擎；传 null 或空表示清除覆盖。</summary>
    public static bool SaveTtsEngine(string? engine)
    {
        var model = LoadModel();
        model.TtsEngine = string.IsNullOrWhiteSpace(engine) ? null : engine.Trim().ToLowerInvariant();
        return SaveModel(model);
    }

    /// <summary>
    /// 三项 AI 功能开关的覆盖值。null 表示跟随 config.yaml 的 <c>*.enabled</c>。
    ///
    /// 为什么设置面板要单独存一份：config.yaml 是用户的文件，程序绝不回写（准则 9），
    /// 而"我现在不想用语音"这种临时开关又必须能记住，所以沿用外观偏好的做法存这里。
    /// </summary>
    public sealed record AiToggleOverride(bool? Llm, bool? Tts, bool? Asr);

    /// <summary>读取 AI 功能开关覆盖；三项都为 null 表示没设过。</summary>
    public static AiToggleOverride LoadAiToggles()
    {
        var model = LoadModel();
        return new AiToggleOverride(model.LlmEnabled, model.TtsEnabled, model.AsrEnabled);
    }

    /// <summary>保存 AI 功能开关覆盖；传 null 表示清除该项（跟随 config.yaml）。</summary>
    public static bool SaveAiToggles(bool? llm, bool? tts, bool? asr)
    {
        var model = LoadModel();
        model.LlmEnabled = llm;
        model.TtsEnabled = tts;
        model.AsrEnabled = asr;
        return SaveModel(model);
    }

    // ------------------------------------------------------------------ 设置面板新增字段
    //
    // 说明：这些字段都是"设置面板改了就想记住"的运行时偏好。
    // 仍然只写 Assets\appearance.json，config.yaml 一个字都不动（准则 9）。

    /// <summary>对话页：用户选的 GGUF（相对仓库根）。</summary>
    public static string? LoadLlmModelPath() => LoadModel().LlmModelPath;

    public static bool SaveLlmModelPath(string? relativePath)
    {
        var model = LoadModel();
        model.LlmModelPath = string.IsNullOrWhiteSpace(relativePath) ? null : relativePath.Trim();
        return SaveModel(model);
    }

    /// <summary>对话页：上下文长度；0 = 自动。</summary>
    public static int? LoadLlmContextLength()
    {
        var value = LoadModel().LlmContextLength;
        return value is >= 0 and <= 262144 ? value : null;
    }

    public static bool SaveLlmContextLength(int? value)
    {
        var model = LoadModel();
        model.LlmContextLength = value is >= 0 and <= 262144 ? value : null;
        return SaveModel(model);
    }

    /// <summary>对话页：系统预留显存（MB）；0 = 按显存档位自动。</summary>
    public static int? LoadGpuReservedMb()
    {
        var value = LoadModel().GpuReservedMb;
        return value is >= 0 and <= 262144 ? value : null;
    }

    public static bool SaveGpuReservedMb(int? value)
    {
        var model = LoadModel();
        model.GpuReservedMb = value is >= 0 and <= 262144 ? value : null;
        return SaveModel(model);
    }

    /// <summary>语音页：全局默认音色。</summary>
    public static string? LoadTtsVoice()
    {
        var voice = LoadModel().TtsVoice;
        return string.IsNullOrWhiteSpace(voice) ? null : voice.Trim();
    }

    public static bool SaveTtsVoice(string? voice)
    {
        var model = LoadModel();
        model.TtsVoice = string.IsNullOrWhiteSpace(voice) ? null : voice.Trim();
        return SaveModel(model);
    }

    /// <summary>语音页：语速系数。</summary>
    public static double? LoadTtsSpeed()
    {
        var speed = LoadModel().TtsSpeed;
        return speed is >= 0.5 and <= 2.0 ? speed : null;
    }

    public static bool SaveTtsSpeed(double? speed)
    {
        var model = LoadModel();
        model.TtsSpeed = speed is >= 0.5 and <= 2.0 ? speed : null;
        return SaveModel(model);
    }

    /// <summary>语音页：采集源（microphone / loopback）。</summary>
    public static string? LoadAsrCapture()
    {
        var capture = LoadModel().AsrCapture;
        return string.IsNullOrWhiteSpace(capture) ? null : capture.Trim().ToLowerInvariant();
    }

    public static bool SaveAsrCapture(string? capture)
    {
        var model = LoadModel();
        model.AsrCapture = string.IsNullOrWhiteSpace(capture) ? null : capture.Trim().ToLowerInvariant();
        return SaveModel(model);
    }

    public static bool? LoadHoverSwitch() => LoadModel().HoverSwitch;

    public static bool SaveHoverSwitch(bool? enabled)
    {
        var model = LoadModel();
        model.HoverSwitch = enabled;
        return SaveModel(model);
    }

    public static bool? LoadClickThrough() => LoadModel().ClickThrough;

    public static bool SaveClickThrough(bool? enabled)
    {
        var model = LoadModel();
        model.ClickThrough = enabled;
        return SaveModel(model);
    }

    public static bool? LoadPushToTalk() => LoadModel().PushToTalk;

    public static bool SavePushToTalk(bool? enabled)
    {
        var model = LoadModel();
        model.PushToTalk = enabled;
        return SaveModel(model);
    }

    public static double? LoadIdleMinutes()
    {
        var value = LoadModel().IdleMinutes;
        return value is >= 1 and <= 120 ? value : null;
    }

    public static bool SaveIdleMinutes(double? minutes)
    {
        var model = LoadModel();
        model.IdleMinutes = minutes is >= 1 and <= 120 ? minutes : null;
        return SaveModel(model);
    }

    public static double? LoadMaintenanceCooldownMinutes()
    {
        var value = LoadModel().MaintenanceCooldownMinutes;
        return value is >= 1 and <= 240 ? value : null;
    }

    public static bool SaveMaintenanceCooldownMinutes(double? minutes)
    {
        var model = LoadModel();
        model.MaintenanceCooldownMinutes = minutes is >= 1 and <= 240 ? minutes : null;
        return SaveModel(model);
    }

    /// <summary>桌宠窗口的启动位置（DIP）。</summary>
    public sealed record StartPosition(double X, double Y);

    public static StartPosition? LoadStartPosition()
    {
        var model = LoadModel();
        return model.StartX is { } x && model.StartY is { } y
            ? new StartPosition(x, y)
            : null;
    }

    public static bool SaveStartPosition(double? x, double? y)
    {
        var model = LoadModel();
        model.StartX = x;
        model.StartY = y;
        return SaveModel(model);
    }

    public static bool? LoadLive2dEnabled() => LoadModel().Live2dEnabled;

    public static bool SaveLive2dEnabled(bool? enabled)
    {
        var model = LoadModel();
        model.Live2dEnabled = enabled;
        return SaveModel(model);
    }

    public static string? LoadLive2dModelPath() => LoadModel().Live2dModelPath;

    public static bool SaveLive2dModelPath(string? relativePath)
    {
        var model = LoadModel();
        model.Live2dModelPath = string.IsNullOrWhiteSpace(relativePath) ? null : relativePath.Trim();
        return SaveModel(model);
    }

    /// <summary>读取某一类（llm / tts / asr）启用中的模块目录名。</summary>
    public static string? LoadEnabledModule(string kind)
    {
        var model = LoadModel();

        return kind.ToLowerInvariant() switch
        {
            "llm" => string.IsNullOrWhiteSpace(model.LlmModule) ? null : model.LlmModule,
            "tts" => string.IsNullOrWhiteSpace(model.TtsModule) ? null : model.TtsModule,
            "asr" => string.IsNullOrWhiteSpace(model.AsrModule) ? null : model.AsrModule,
            _ => null,
        };
    }

    /// <summary>保存某一类启用中的模块目录名；传 null 表示回到"第一个可用模块"。</summary>
    public static bool SaveEnabledModule(string kind, string? directoryName)
    {
        var model = LoadModel();
        var value = string.IsNullOrWhiteSpace(directoryName) ? null : directoryName.Trim();

        switch (kind.ToLowerInvariant())
        {
            case "llm":
                model.LlmModule = value;
                break;
            case "tts":
                model.TtsModule = value;
                break;
            case "asr":
                model.AsrModule = value;
                break;
            default:
                return false;
        }

        return SaveModel(model);
    }

    /// <summary>
    /// 清除全部界面覆盖（「恢复默认（清除覆盖）」按钮）：删掉 appearance.json。
    /// config.yaml 始终只读，所以删文件就等于回到出厂默认。
    /// </summary>
    public static bool ClearAll()
    {
        try
        {
            var path = StorePath;
            if (File.Exists(path))
            {
                File.Delete(path);
            }

            return true;
        }
        catch
        {
            return false;
        }
    }
}