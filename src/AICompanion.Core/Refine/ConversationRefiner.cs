using System.Text;
using System.Text.Json;
using AICompanion.Core.Abstractions;
using AICompanion.Core.Configuration;
using AICompanion.Core.Gpu;
using AICompanion.Core.Knowledge;
using AICompanion.Core.Llm;
using AICompanion.Core.Models;

namespace AICompanion.Core.Refine;

/// <summary>模型返回的候选条目（对应 GBNF 约束下的 JSON 结构）。</summary>
public sealed class RefinedCandidate
{
    public string Type { get; set; } = "LongTermMemory";

    public List<string> Triggers { get; set; } = new();

    public string Reply { get; set; } = string.Empty;

    public List<string> Keywords { get; set; } = new();
}

/// <summary>
/// 对话整理器（手册 §3.7）。
///
/// 替代离线蒸馏：复用**正在使用的** 7B 模型（这里就是本地 llama-server），
/// 在空闲时把最近聊天整理成待审核的候选知识条目。
///
/// 铁律（准则 9）：本类**只写 .pending/ 目录**，永远不会写正式知识库。
/// 代码里没有任何一处调用 <c>KnowledgeFileStore.SaveEntry(正式目录, ...)</c>。
/// </summary>
public sealed class ConversationRefiner
{
    /// <summary>约束模型输出为"候选条目 JSON 数组"的 GBNF 语法。</summary>
    public const string CandidateGrammar = """
        root      ::= "[" ws (candidate (ws "," ws candidate)*)? ws "]"
        candidate ::= "{" ws "\"type\"" ws ":" ws type ws "," ws "\"triggers\"" ws ":" ws strlist ws "," ws "\"reply\"" ws ":" ws string ws "}"
        type      ::= "\"Preprocess\"" | "\"LongTermMemory\"" | "\"CorePersonality\""
        strlist   ::= "[" ws (string (ws "," ws string)*)? ws "]"
        string    ::= "\"" char* "\""
        char      ::= [^"\\] | "\\" ["\\/bfnrt]
        ws        ::= [ \t\n\r]*
        """;

    private readonly AppConfig _config;
    private readonly IAppLogger _logger;
    private readonly IBubbleNotifier _bubble;
    private readonly LlamaServerController _llama;
    private readonly NvidiaSmi _smi;
    private readonly PendingReviewService _pending;

    public ConversationRefiner(
        AppConfig config,
        IAppLogger logger,
        IBubbleNotifier bubble,
        LlamaServerController llama,
        NvidiaSmi smi,
        PendingReviewService pending)
    {
        _config = config;
        _logger = logger.ForCategory("Refiner");
        _bubble = bubble;
        _llama = llama;
        _smi = smi;
        _pending = pending;
    }

    /// <summary>最近一次整理的候选条数，设置面板展示用。</summary>
    public int LastCandidateCount { get; private set; }

    /// <summary>
    /// 整理最近聊天（手册 §3.7 实现流程）。
    /// 返回本次生成的候选条数；被取消或跳过时返回 0。
    /// </summary>
    public async Task<int> RefineFromRecentChatAsync(CancellationToken cancellationToken = default)
    {
        // 触发前置条件：显存使用率低于 60%，查询失败则跳过本次整理（手册明确要求）。
        var memory = await _smi.QueryMemoryUtilizationPercentAsync(cancellationToken).ConfigureAwait(false);
        if (!memory.Success)
        {
            _logger.Info($"跳过整理：无法查询显存使用率（{memory.FailureReason}）。");
            return 0;
        }

        var threshold = _config.Idle.RefineGpuMemoryPercent;
        if (memory.Value >= threshold)
        {
            _logger.Info($"跳过整理：显存使用率 {memory.Value}% 不低于阈值 {threshold}%。");
            return 0;
        }

        if (!_llama.IsRunning)
        {
            _logger.Info("跳过整理：llama-server 未运行。");
            return 0;
        }

        // 步骤 1：读取最近聊天记录（窗口可配置，默认 200 条）。
        var recent = await ReadRecentMessagesAsync(_config.Context.RefineWindowSize, cancellationToken)
            .ConfigureAwait(false);

        if (recent.Count < 4)
        {
            _logger.Info($"跳过整理：最近聊天记录只有 {recent.Count} 条，样本太少。");
            return 0;
        }

        _logger.Info($"开始整理最近 {recent.Count} 条聊天记录（显存使用率 {memory.Value}%）。");

        // 步骤 2：构建 prompt，要求输出 JSON 数组。
        var transcript = string.Join(Environment.NewLine, recent.Select(m => m.ToString()));
        var request = new List<ChatMessage>
        {
            ChatMessage.System(
                "你是一个记忆整理器。从下面的对话中提取值得长期记住的信息，输出 JSON 数组。" +
                "每个元素的字段：type（只能是 Preprocess、LongTermMemory、CorePersonality 之一）、" +
                "triggers（字符串数组，用于命中的关键词）、reply（要记住的内容或应答模板）。" +
                "Preprocess 用于用户会反复问的固定问题；LongTermMemory 用于用户的偏好与事实；" +
                "CorePersonality 用于稳定的性格设定。没有值得记住的内容就输出 []。只输出 JSON，不要解释。"),
            ChatMessage.User(transcript),
        };

        // 步骤 3-4：调用 LLM，并在生成循环中检查取消。
        var response = await _llama.Client
            .ChatStreamAsync(
                request,
                onDelta: null,
                grammar: CandidateGrammar,
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        if (response.Cancelled)
        {
            // 手册步骤 4：请求取消则立即停止并丢弃本次结果。
            _logger.Info("整理被取消，本次结果已丢弃。");
            return 0;
        }

        if (!response.Success)
        {
            _logger.Warn($"整理失败：{response.FailureReason}");
            return 0;
        }

        cancellationToken.ThrowIfCancellationRequested();

        // 步骤 5：解析 JSON 并写入 .pending/（绝不写正式知识库）。
        var candidates = ParseCandidates(response.Text);
        if (candidates.Count == 0)
        {
            _logger.Info("本次整理没有产出候选条目。");
            LastCandidateCount = 0;
            return 0;
        }

        var written = 0;
        foreach (var candidate in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var entry = ToKnowledgeEntry(candidate);
            if (entry is null)
            {
                continue;
            }

            if (await WriteCandidateAsync(entry, cancellationToken).ConfigureAwait(false))
            {
                written++;
            }
        }

        LastCandidateCount = written;
        _logger.Info($"整理完成，写入 {written} 条待审核候选（未写入正式知识库）。");

        if (written > 0)
        {
            _bubble.Show(
                "refine-done",
                $"空闲整理产出 {written} 条待确认记忆，可在设置面板的\"待审核知识\"中查看。",
                BubbleLevel.Info,
                TimeSpan.FromSeconds(_config.Ui.BubbleDurationSeconds));
        }

        return written;
    }

    private static List<RefinedCandidate> ParseCandidates(string text)
    {
        var result = new List<RefinedCandidate>();
        if (string.IsNullOrWhiteSpace(text))
        {
            return result;
        }

        // GBNF 应该保证输出是纯 JSON，但模型仍可能包上代码块，这里做一次宽容提取。
        var start = text.IndexOf('[');
        var end = text.LastIndexOf(']');
        if (start < 0 || end <= start)
        {
            return result;
        }

        var json = text[start..(end + 1)];

        try
        {
            var parsed = JsonSerializer.Deserialize<List<RefinedCandidate>>(
                json,
                new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true,
                    AllowTrailingCommas = true,
                });

            return parsed ?? result;
        }
        catch (JsonException)
        {
            return result;
        }
    }

    private static KnowledgeEntry? ToKnowledgeEntry(RefinedCandidate candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate.Reply))
        {
            return null;
        }

        var type = candidate.Type switch
        {
            "Preprocess" => KnowledgeType.Preprocess,
            "CorePersonality" => KnowledgeType.CorePersonality,
            _ => KnowledgeType.LongTermMemory,
        };

        var triggers = candidate.Triggers
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .Select(t => t.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        // Preprocess 没有触发器就永远不会命中，没有意义。
        if (type == KnowledgeType.Preprocess && triggers.Count == 0)
        {
            type = KnowledgeType.LongTermMemory;
        }

        return new KnowledgeEntry
        {
            Type = type,
            Priority = type == KnowledgeType.Preprocess ? 100 : 10,
            Triggers = triggers,
            ReplyTemplate = candidate.Reply.Trim(),
            ContextKeywords = candidate.Keywords
                .Where(k => !string.IsNullOrWhiteSpace(k))
                .Select(k => k.Trim())
                .ToList(),
            Source = KnowledgeSource.Candidate,
            Confidence = 0.5,
            IsActive = true,
        };
    }

    /// <summary>写入 .pending/ 目录，文件名遵循手册的 candidate_{timestamp}_{guid}.json。</summary>
    private async Task<bool> WriteCandidateAsync(KnowledgeEntry entry, CancellationToken cancellationToken)
    {
        try
        {
            Directory.CreateDirectory(_pending.DirectoryPath);

            var fileName = $"candidate_{DateTime.Now:yyyyMMddHHmmss}_{Guid.NewGuid():N}.json";
            var path = Path.Combine(_pending.DirectoryPath, fileName);

            var json = JsonSerializer.Serialize(entry, KnowledgeFileStore.JsonOptions);
            await File.WriteAllTextAsync(path, json, new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);

            return true;
        }
        catch (Exception ex)
        {
            _logger.Error($"写入候选条目失败：{ex.Message}", ex);
            return false;
        }
    }

    /// <summary>
    /// 读取最近的聊天记录。数据源是当日与前一日的 chat-*.jsonl，
    /// 因为内存窗口只有 30 条，而整理窗口默认是 200 条。
    /// </summary>
    private async Task<List<ChatMessage>> ReadRecentMessagesAsync(int windowSize, CancellationToken cancellationToken)
    {
        var directory = AppPaths.Resolve(_config.Logging.ChatLogDir);
        var result = new List<ChatMessage>();

        if (!Directory.Exists(directory))
        {
            return result;
        }

        try
        {
            var files = Directory.GetFiles(directory, "chat-*.jsonl")
                .OrderByDescending(f => f)
                .Take(3)
                .Reverse()
                .ToList();

            var lines = new List<string>();

            foreach (var file in files)
            {
                cancellationToken.ThrowIfCancellationRequested();

                try
                {
                    lines.AddRange(await File.ReadAllLinesAsync(file, cancellationToken).ConfigureAwait(false));
                }
                catch (IOException ex)
                {
                    _logger.Debug($"读取聊天日志失败（可能在写入中）：{ex.Message}");
                }
            }

            // 只取最后 windowSize 条。
            var tail = lines.Count > windowSize ? lines.GetRange(lines.Count - windowSize, windowSize) : lines;

            foreach (var line in tail)
            {
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                try
                {
                    var message = JsonSerializer.Deserialize<ChatMessage>(line, KnowledgeFileStore.JsonOptions);
                    if (message is not null)
                    {
                        result.Add(message);
                    }
                }
                catch (JsonException)
                {
                    // 单行损坏不影响其它记录。
                }
            }
        }
        catch (Exception ex)
        {
            _logger.Warn($"读取最近聊天记录失败：{ex.Message}");
        }

        return result;
    }
}
