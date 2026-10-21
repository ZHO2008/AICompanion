using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using AICompanion.Core.Models;

namespace AICompanion.Core.Knowledge;

/// <summary>
/// 知识条目的文件级读写（手册 §3.1）。
///
/// 文件格式约定（对本项目是约定，对用户是可读的纯文本）：
///   - 一个 .json 文件里可以放**单个条目对象**，也可以放**条目数组**，两种都支持；
///   - 写出时采用"一条目一文件"，文件名形如 <c>entry_preprocess_&lt;guid&gt;.json</c>，
///     好处是待审核候选只需移动文件即可入库，符合手册 §3.7"确认（移入正式知识库）"的描述。
/// </summary>
public static class KnowledgeFileStore
{
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        // 中文不转义，保证用户手动编辑 JSON 时看到的是可读文本。
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>
    /// 用于 **JSONL（一行一个 JSON 对象）** 的序列化选项：与 <see cref="JsonOptions"/>
    /// 完全一致，只把 WriteIndented 关掉。
    ///
    /// 为什么必须单独一份：聊天日志 chat-yyyyMMdd.jsonl 与归档 chat_log_archive.jsonl
    /// 都是按行读取的（ConversationRefiner 会逐行反序列化成 ChatMessage）。
    /// 如果误用缩进版选项，一个对象会展开成 6 行左右的漂亮 JSON，
    /// 于是"每行一个对象"的前提失效 —— 逐行解析几乎全部失败，
    /// 而失败会被逐行 catch 掉，表现为"整理器永远认为聊天记录太少"，
    /// 属于很难从现象定位的静默故障。
    /// </summary>
    public static readonly JsonSerializerOptions JsonLineOptions = new(JsonOptions)
    {
        WriteIndented = false,
    };

    /// <summary>目录下的说明文件内容（手册 §6 要求知识库为空时确保存在一个说明文件）。</summary>
    public const string ReadmeFileName = "README.md";

    public static string ReadmeContent =>
        """
        # 知识库目录说明

        本目录由 AI 伴侣的 UnifiedKnowledgeManager 扫描。启动时加载全部 `*.json`，
        之后通过 FileSystemWatcher 监听变化并热重载，**无需重启程序**。

        ## 文件格式

        一个 `.json` 文件可以是单个条目对象，也可以是条目数组：

        ```json
        {
          "id": "8f0f1c1e-0000-0000-0000-000000000000",
          "type": "Preprocess",
          "priority": 100,
          "triggers": ["你好", "hi"],
          "replyTemplate": "你好呀，今天过得怎么样？",
          "animation": "wave",
          "voiceConfig": {
            "fallbackPiperVoice": "zh_CN-default",
            "language": "zh",
            "speed": 1.0,
            "emotionIntensity": 1.0,
            "emotionStyle": "neutral"
          },
          "isActive": true
        }
        ```

        ## 字段说明

        | 字段 | 说明 |
        | --- | --- |
        | `type` | `Preprocess` / `Summary` / `CorePersonality` / `LongTermMemory` / `AnchorMemory` |
        | `priority` | 排序权重，越大越优先 |
        | `triggers` | 触发关键词，输入包含任一关键词即命中（**仅 `Preprocess` 参与直接回复**） |
        | `replyTemplate` | 命中后直接返回的回复文本 |
        | `voiceConfig.fallbackPiperVoice` | **必填**。缺失时该条目会被拒绝加载（防止降级到 Piper 时无音色可用） |
        | `isActive` | 置为 `false` 可临时禁用而不删除 |
        | `expiresIn` | 可选，形如 `"07:00:00"`；超期条目自动失效 |

        ## 重要约定

        - **知识库只接受人工编写或审核确认的内容**。程序自身永远不会写入本目录。
        - 模型在空闲时生成的候选条目只会出现在 `.pending/` 子目录中，必须由你在
          "待审核知识"界面确认后才会移入本目录。
        - `.pending/` 是子目录，热重载**不会**扫描它，因此候选内容永远不会被当成正式知识。
        """;

    /// <summary>加载目录下的全部条目（不递归，因此不会误读 .pending 子目录）。</summary>
    public static (List<KnowledgeEntry> Entries, List<string> Warnings) LoadDirectory(string directory)
    {
        var entries = new List<KnowledgeEntry>();
        var warnings = new List<string>();

        if (!Directory.Exists(directory))
        {
            return (entries, warnings);
        }

        string[] files;
        try
        {
            files = Directory.GetFiles(directory, "*.json", SearchOption.TopDirectoryOnly);
        }
        catch (Exception ex)
        {
            warnings.Add($"枚举知识库目录失败：{ex.Message}");
            return (entries, warnings);
        }

        foreach (var file in files)
        {
            try
            {
                var text = File.ReadAllText(file, Encoding.UTF8);

                // 文件可能正在被写入（热重载场景），解析失败只记警告，下次事件会再试。
                if (string.IsNullOrWhiteSpace(text))
                {
                    warnings.Add($"{Path.GetFileName(file)}：文件为空，已跳过。");
                    continue;
                }

                var parsed = ParseEntries(text, Path.GetFileName(file), warnings);
                foreach (var entry in parsed)
                {
                    Normalize(entry, Path.GetFileName(file), warnings);
                    entries.Add(entry);
                }
            }
            catch (JsonException ex)
            {
                warnings.Add($"{Path.GetFileName(file)}：JSON 解析失败（{ex.Message}），已跳过。");
            }
            catch (IOException ex)
            {
                warnings.Add($"{Path.GetFileName(file)}：读取失败（{ex.Message}），可能在写入中，已跳过。");
            }
            catch (Exception ex)
            {
                warnings.Add($"{Path.GetFileName(file)}：加载异常（{ex.Message}），已跳过。");
            }
        }

        return (entries, warnings);
    }

    private static List<KnowledgeEntry> ParseEntries(string text, string fileName, List<string> warnings)
    {
        var trimmed = text.TrimStart();

        if (trimmed.StartsWith('['))
        {
            var list = JsonSerializer.Deserialize<List<KnowledgeEntry>>(trimmed, JsonOptions);
            return list ?? new List<KnowledgeEntry>();
        }

        var single = JsonSerializer.Deserialize<KnowledgeEntry>(trimmed, JsonOptions);
        if (single is null)
        {
            warnings.Add($"{fileName}：反序列化结果为 null，已跳过。");
            return new List<KnowledgeEntry>();
        }

        return new List<KnowledgeEntry> { single };
    }

    /// <summary>补齐必填字段；VoiceConfig 缺少 FallbackPiperVoice 时按准则 3 视为无效。</summary>
    private static void Normalize(KnowledgeEntry entry, string fileName, List<string> warnings)
    {
        if (entry.Id == Guid.Empty)
        {
            entry.Id = Guid.NewGuid();
        }

        entry.Triggers ??= new List<string>();
        entry.ContextKeywords ??= new List<string>();
        entry.Metadata ??= new Dictionary<string, object?>();

        // 去掉空白触发器，避免 "" 命中一切输入。
        entry.Triggers = entry.Triggers
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .Select(t => t.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        entry.ContextKeywords = entry.ContextKeywords
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .Select(t => t.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (entry.Timestamp == default)
        {
            entry.Timestamp = DateTime.Now;
        }

        entry.Confidence = Math.Clamp(entry.Confidence, 0.0, 1.0);

        if (entry.VoiceConfig is not null && !entry.VoiceConfig.TryValidate(out var reason))
        {
            // 不丢弃整条知识，只丢掉非法音色配置，让它退到三级兜底的下一级。
            warnings.Add($"{fileName}：条目的 voiceConfig 无效（{reason}），已忽略该音色配置，改用全局默认。");
            entry.VoiceConfig = null;
        }
    }

    /// <summary>把条目写成"一条目一文件"。</summary>
    public static string SaveEntry(string directory, KnowledgeEntry entry)
    {
        Directory.CreateDirectory(directory);
        entry.Timestamp = entry.Timestamp == default ? DateTime.Now : entry.Timestamp;

        var fileName = $"entry_{entry.Type.ToString().ToLowerInvariant()}_{entry.Id:N}.json";
        var path = Path.Combine(directory, fileName);

        var json = JsonSerializer.Serialize(entry, JsonOptions);
        // 先写临时文件再替换，避免热重载读到半截内容。
        var temp = path + ".tmp";
        File.WriteAllText(temp, json, new UTF8Encoding(false));
        File.Move(temp, path, overwrite: true);

        return path;
    }

    public static void DeleteEntryFile(string directory, Guid id)
    {
        if (!Directory.Exists(directory))
        {
            return;
        }

        foreach (var file in Directory.GetFiles(directory, $"*{id:N}*.json", SearchOption.TopDirectoryOnly))
        {
            try
            {
                File.Delete(file);
            }
            catch
            {
                // 单个文件删不掉不阻塞其它操作。
            }
        }
    }

    /// <summary>确保知识库目录与说明文件存在（手册 §6）。</summary>
    public static void EnsureReadme(string directory)
    {
        try
        {
            Directory.CreateDirectory(directory);
            var readme = Path.Combine(directory, ReadmeFileName);
            if (!File.Exists(readme))
            {
                File.WriteAllText(readme, ReadmeContent, new UTF8Encoding(false));
            }
        }
        catch
        {
            // 说明文件写不出来不影响主流程。
        }
    }
}
