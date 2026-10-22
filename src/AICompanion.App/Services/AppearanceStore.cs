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

        public DateTime SavedAt { get; set; }
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
                string.IsNullOrWhiteSpace(model.TtsEngine))
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

            model.SavedAt = DateTime.Now;
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
}
