using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using AICompanion.Core.Abstractions;

namespace AICompanion.App.Animation;

/// <summary>
/// 角色包专属配置（每个 Live2D 角色包根目录下一个 <c>AvatarProfile.json</c>）。
///
/// 设计目标：**代码与模型资源解耦**。
/// 逻辑动作由 <see cref="AnimationType"/> 固定，具体动作名/参数名交给这个文件。
/// 换模型时只需连同模型与 AvatarProfile.json 一起替换，不需要改 C#。
///
/// 动作名从哪里来：模型包里的 <c>*.model3.json</c> 的 <c>Motions</c> 字段（组名）。
/// </summary>
public sealed class AvatarProfile
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        WriteIndented = true,
    };

    /// <summary>角色名，仅用于日志。</summary>
    [JsonPropertyName("avatar_name")]
    public string AvatarName { get; set; } = "default";


    /// <summary>
    /// 逻辑动作 → 模型动作组名。
    /// 键名用 <see cref="AnimationType"/> 的成员名（Idle/Listen/Think/Speak/Confused/Happy/Sad/Fallback）。
    /// </summary>
    [JsonPropertyName("motion_map")]
    public Dictionary<string, string> MotionMap { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 逻辑参数 → Live2D 参数名（如 MouthOpen → ParamMouthOpenY）。
    /// 口型同步、眨眼等表现都要经过它查名字。
    /// </summary>
    [JsonPropertyName("parameter_map")]
    public Dictionary<string, string> ParameterMap { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 模型水平偏移，单位是"窗口宽度的比例"（正数向左移）。
    ///
    /// 为什么需要：Cubism 的 LAppView 居中显示的是**画布**，不是角色本体。
    /// 有些模型的角色在画布里偏向右侧（Hiyori 就是），看起来就像整体偏右。
    /// 这个值是模型相关的，所以放角色包里，而不是写死在代码里。
    /// </summary>
    [JsonPropertyName("horizontal_offset")]
    public double HorizontalOffset { get; set; }

    /// <summary>配置文件名（放在角色包根目录）。</summary>
    public const string FileName = "AvatarProfile.json";

    /// <summary>
    /// 内置默认配置：AvatarProfile.json 缺失或损坏时用它兜底，保证程序不崩。
    ///
    /// 这些默认动作名只是**占位**，接真模型后必须按该模型的 Motions 字段改写，
    /// 否则会走 Fallback 分支（会记录 Warning）。
    /// </summary>
    public static AvatarProfile CreateBuiltInDefault() => new()
    {
        AvatarName = "builtin-default",
        MotionMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [nameof(AnimationType.Idle)] = "idle",
            [nameof(AnimationType.Listen)] = "listen",
            [nameof(AnimationType.Think)] = "think",
            [nameof(AnimationType.Speak)] = "talk",
            [nameof(AnimationType.Confused)] = "question",
            [nameof(AnimationType.Happy)] = "happy",
            [nameof(AnimationType.Sad)] = "sad",
            [nameof(AnimationType.Fallback)] = "idle",
        },
        ParameterMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["MouthOpen"] = "ParamMouthOpenY",
            ["EyeBlink"] = "ParamEyeLOpen",
            ["BodyAngle"] = "ParamAngleZ",
        },
    };

    /// <summary>
    /// 从角色包目录加载配置。
    /// 文件不存在/解析失败/内容为空时**一律回退到内置默认配置**并记录 Warning（准则 10：不静默失败）。
    /// </summary>
    /// <param name="avatarDirectory">角色包目录（与 .model3.json 同级）。</param>
    /// <param name="logger">日志。</param>
    /// <param name="source">实际来源描述，写日志用。</param>
    public static AvatarProfile Load(string avatarDirectory, IAppLogger logger, out string source)
    {
        var path = Path.Combine(avatarDirectory, FileName);

        try
        {
            if (!File.Exists(path))
            {
                source = $"内置默认（未找到 {path}）";
                logger.Warn($"未找到角色包配置 {path}，使用内置默认动作映射。请按模型的 Motions 字段创建该文件。");
                return CreateBuiltInDefault();
            }

            var json = File.ReadAllText(path);
            var profile = JsonSerializer.Deserialize<AvatarProfile>(json, JsonOptions);

            if (profile is null)
            {
                source = $"内置默认（{path} 解析结果为空）";
                logger.Warn($"角色包配置 {path} 解析为空，使用内置默认动作映射。");
                return CreateBuiltInDefault();
            }

            // 关键：System.Text.Json 反序列化字典时**不会**沿用属性上的
            // StringComparer.OrdinalIgnoreCase，而是新建一个大小写敏感的字典。
            // 不重建的话，"think" 与默认补齐的 "Think" 会同时存在，
            // 查询用 type.ToString()="Think" 取到的就不是用户配的那一项。
            profile.MotionMap = Normalize(profile.MotionMap);
            profile.ParameterMap = Normalize(profile.ParameterMap);

            if (profile.MotionMap.Count == 0)
            {
                source = $"内置默认（{path} 没有 motion_map）";
                logger.Warn($"角色包配置 {path} 里没有 motion_map，使用内置默认动作映射。");
                return CreateBuiltInDefault();
            }

            // 缺失的映射用默认值补齐，避免个别动作漏配就整条走 Fallback。
            var builtIn = CreateBuiltInDefault();
            foreach (var pair in builtIn.MotionMap)
            {
                profile.MotionMap.TryAdd(pair.Key, pair.Value);
            }

            foreach (var pair in builtIn.ParameterMap)
            {
                profile.ParameterMap.TryAdd(pair.Key, pair.Value);
            }

            source = path;
            logger.Info(
                $"已加载角色包配置：{path}（角色={profile.AvatarName}，动作 {profile.MotionMap.Count} 项，参数 {profile.ParameterMap.Count} 项）");

            return profile;
        }
        catch (Exception ex)
        {
            source = $"内置默认（{path} 解析失败）";
            logger.Warn($"角色包配置 {path} 解析失败（{ex.Message}），使用内置默认动作映射。");
            return CreateBuiltInDefault();
        }
    }

    /// <summary>
    /// 把字典重建为**大小写不敏感**。用户手写 JSON 时大小写常常不一致，
    /// 而 <c>AnimationType.ToString()</c> 是固定的 PascalCase，所以必须忽略大小写匹配。
    /// 手工 TryAdd 而不是 new Dictionary(src, comparer)：后者遇到仅大小写不同的重复键会抛异常。
    /// </summary>
    private static Dictionary<string, string> Normalize(Dictionary<string, string>? source)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        if (source is null)
        {
            return result;
        }

        foreach (var pair in source)
        {
            if (string.IsNullOrWhiteSpace(pair.Key))
            {
                continue;
            }

            // 大小写不同的重复键：保留第一个出现的，不抛异常。
            result.TryAdd(pair.Key.Trim(), pair.Value);
        }

        return result;
    }

    /// <summary>取某个逻辑动作对应的模型动作名；未配置时返回 null。</summary>
    public string? GetMotionOrNull(AnimationType type)
    {
        return MotionMap.TryGetValue(type.ToString(), out var motion) && !string.IsNullOrWhiteSpace(motion)
            ? motion
            : null;
    }

    /// <summary>取兜底动作名；连 Fallback 都没配时返回 null。</summary>
    public string? GetFallbackMotion() => GetMotionOrNull(AnimationType.Fallback);

    /// <summary>取逻辑参数对应的模型参数名；未配置时用调用方给的默认值。</summary>
    public string GetParameter(string logicalName, string fallback)
    {
        return ParameterMap.TryGetValue(logicalName, out var name) && !string.IsNullOrWhiteSpace(name)
            ? name
            : fallback;
    }

    /// <summary>口型同步使用的参数名。</summary>
    public string MouthOpenParameter => GetParameter("MouthOpen", "ParamMouthOpenY");
}
