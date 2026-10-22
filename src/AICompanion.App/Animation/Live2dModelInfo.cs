using System.IO;
using System.Text.Json;
using AICompanion.Core.Abstractions;

namespace AICompanion.App.Animation;

/// <summary>
/// 从 <c>*.model3.json</c> 读出的模型能力清单：有哪些动作组、有哪些参数组。
///
/// 存在的意义：<see cref="AnimationManager"/> 里"动作是否真的存在于模型"这一层兜底
/// 之前只能是 TODO（没法凭空知道模型有什么）。现在有了模型文件，
/// 就能真实解析 <c>FileReferences.Motions</c> 的组名，映射到不存在的动作时自动降级到 Fallback。
/// </summary>
public sealed class Live2dModelInfo
{
    private readonly HashSet<string> _motionGroups;

    private Live2dModelInfo(
        string modelPath,
        IEnumerable<string> motionGroups,
        IReadOnlyDictionary<string, string[]> parameterGroups)
    {
        ModelPath = modelPath;
        _motionGroups = new HashSet<string>(motionGroups, StringComparer.OrdinalIgnoreCase);

        // Live2D 规范里由 model3.json 的 Groups.Name="LipSync" 指定口型参数
        LipSyncParameter = parameterGroups.TryGetValue("LipSync", out var ids) && ids.Length > 0
            ? ids[0]
            : null;

        EyeBlinkParameters = parameterGroups.TryGetValue("EyeBlink", out var eyes) && eyes.Length > 0
            ? eyes
            : Array.Empty<string>();
    }

    /// <summary>模型文件绝对路径。</summary>
    public string ModelPath { get; }

    /// <summary>视频里可用的动作组名（如 Idle / TapBody）。</summary>
    public IReadOnlyCollection<string> MotionGroups => _motionGroups;

    /// <summary>参数组：LipSync / EyeBlink 等 → 参数 id 列表（仅本类内部使用）。</summary>

    /// <summary>口型参数名（来自 model3.json 的 LipSync 组），没有则为 null。</summary>
    public string? LipSyncParameter { get; }

    /// <summary>眨眼参数名（通常左右眼两个）。</summary>
    public string[] EyeBlinkParameters { get; }

    /// <summary>模型里是否存在该动作组。</summary>
    public bool HasMotion(string group)
        => !string.IsNullOrWhiteSpace(group) && _motionGroups.Contains(group);

    /// <summary>
    /// 解析模型文件。失败（文件不存在 / JSON 损坏 / 没有 Motions）时返回 null，
    /// 由调用方决定降级策略 —— 这里不抛异常，也不记 Warning（那是调用方的语义）。
    /// </summary>
    public static Live2dModelInfo? TryLoad(string modelPath, IAppLogger logger)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(modelPath) || !File.Exists(modelPath))
            {
                return null;
            }

            using var document = JsonDocument.Parse(File.ReadAllText(modelPath));
            var root = document.RootElement;

            if (!root.TryGetProperty("FileReferences", out var refs))
            {
                logger.Warn($"模型文件 {Path.GetFileName(modelPath)} 缺少 FileReferences，无法读取动作列表。");
                return null;
            }

            // 动作组名 = FileReferences.Motions 的键
            var groups = new List<string>();
            if (refs.TryGetProperty("Motions", out var motions) && motions.ValueKind == JsonValueKind.Object)
            {
                foreach (var group in motions.EnumerateObject())
                {
                    groups.Add(group.Name);
                }
            }

            // 参数组（LipSync / EyeBlink）：Groups[] 里 Target=Parameter 的项
            var parameterGroups = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
            if (root.TryGetProperty("Groups", out var groupArray) && groupArray.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in groupArray.EnumerateArray())
                {
                    if (!item.TryGetProperty("Name", out var name) ||
                        !item.TryGetProperty("Ids", out var ids) ||
                        ids.ValueKind != JsonValueKind.Array)
                    {
                        continue;
                    }

                    var list = ids.EnumerateArray()
                        .Where(e => e.ValueKind == JsonValueKind.String)
                        .Select(e => e.GetString()!)
                        .ToArray();

                    if (list.Length > 0)
                    {
                        parameterGroups[name.GetString() ?? string.Empty] = list;
                    }
                }
            }

            return new Live2dModelInfo(modelPath, groups, parameterGroups);
        }
        catch (Exception ex)
        {
            logger.Warn($"解析模型文件 {modelPath} 失败：{ex.Message}");
            return null;
        }
    }
}
