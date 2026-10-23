using System.Text;
using System.Text.Json;
using AICompanion.Core.Abstractions;
using AICompanion.Core.Configuration;
using AICompanion.Core.Knowledge;
using AICompanion.Core.Llm;
using AICompanion.Core.Models;

namespace AICompanion.Core.Context;

/// <summary>
/// 上下文管理器（手册 §3.2）。
///
/// 三条必须守住的性质：
///   1. **原始对话永不删除**：每条消息都即时落到按日轮转的 chat-yyyyMMdd.jsonl；
///      压缩时再把被移出窗口的原文追加进 chat_log_archive.jsonl。
///      内存里的 RecentMessages 只是"窗口"，不是"账本"。
///   2. **摘要分三级**：SummaryStack[0] 近期 / [1] 中期 / [2] 远期；
///      某层 token 估算超过上限就向上合并，保证注入 prompt 的摘要总量可控。
///   3. **锚点优先**：用户显式"记住"的内容永远排在最前面，并且锚点摘要优先于普通摘要。
/// </summary>
public sealed class ContextManager
{
    private readonly AppConfig _config;
    private readonly IAppLogger _logger;
    private readonly IBubbleNotifier _bubble;
    private readonly UnifiedKnowledgeManager _knowledge;
    private readonly LlamaServerController _llama;

    private readonly object _sync = new();
    private readonly List<ChatMessage> _recent = new();
    private readonly List<SummaryEntry> _summaryStack = new();
    private readonly List<ChatMessage> _anchors = new();

    /// <summary>
    /// 摘要集合的总条数上限。token 上限只约束"单层"，锚点摘要与各层条目叠起来仍会长期增长，
    /// 所以再加一道条数兜底：超出时丢弃最旧的（摘要只是窗口，原文另有归档，不丢数据）。
    /// </summary>
    private const int MaxSummaryEntries = 200;

    private readonly string _chatLogDirectory;
    private readonly string _archivePath;

    public ContextManager(
        AppConfig config,
        IAppLogger logger,
        IBubbleNotifier bubble,
        UnifiedKnowledgeManager knowledge,
        LlamaServerController llama)
    {
        _config = config;
        _logger = logger.ForCategory("Context");
        _bubble = bubble;
        _knowledge = knowledge;
        _llama = llama;

        _chatLogDirectory = AppPaths.Resolve(config.Logging.ChatLogDir);
        _archivePath = Path.Combine(_chatLogDirectory, config.Context.ArchiveFileName);

        try
        {
            Directory.CreateDirectory(_chatLogDirectory);
        }
        catch (Exception ex)
        {
            _logger.Warn($"创建聊天日志目录失败：{ex.Message}");
        }
    }

    /// <summary>手册里的常量 MaxRecentRounds，默认 30，可通过配置修改。</summary>
    public int MaxRecentRounds => Math.Max(1, _config.Context.MaxRecentRounds);

    /// <summary>
    /// 窗口的**消息条数**上限：一轮对话 = 用户消息 + 助手回复两条，因此是轮数 × 2。
    /// 配置项 <c>context.max_recent_rounds</c> 的语义是"最近 N 轮"，
    /// 拿它直接和 _recent.Count（条数）比较会让实际窗口只有 N/2 轮。
    /// </summary>
    private int MaxRecentMessages => Math.Max(2, MaxRecentRounds * 2);

    public IReadOnlyList<ChatMessage> RecentMessages
    {
        get
        {
            lock (_sync)
            {
                return _recent.ToList();
            }
        }
    }

    /// <summary>索引 0 = 近期，1 = 中期，2 = 远期。</summary>
    public IReadOnlyList<SummaryEntry> SummaryStack
    {
        get
        {
            lock (_sync)
            {
                return _summaryStack.ToList();
            }
        }
    }

    public IReadOnlyList<ChatMessage> AnchorMemory
    {
        get
        {
            lock (_sync)
            {
                return _anchors.ToList();
            }
        }
    }

    public string ArchivePath => _archivePath;

    /// <summary>追加一条消息：进内存窗口 + 立即写入当日聊天日志（原文永不丢失）。</summary>
    public void AddMessage(ChatMessage message)
    {
        if (message is null)
        {
            return;
        }

        lock (_sync)
        {
            _recent.Add(message);

            // 硬上限：压缩只发生在空闲维护里，若用户一直不空闲（连续对话），
            // _recent 就会无止境地涨。这里按"消息窗口上限的 2 倍 + 余量"兜底裁剪，
            // 保证即使长时间不触发维护，内存窗口也不会失控（被裁掉的消息仍在聊天日志里）。
            var hardCap = Math.Max(64, MaxRecentMessages * 2);
            while (_recent.Count > hardCap)
            {
                _recent.RemoveAt(0);
            }
        }

        AppendChatLog(message);
    }

    /// <summary>本轮对话（用户 + 回复）批量加入。</summary>
    public void AddExchange(ChatMessage user, ChatMessage assistant)
    {
        AddMessage(user);
        AddMessage(assistant);
    }

    /// <summary>
    /// 锚点记忆（手册 §3.2 锚点记忆管理）。
    /// 达到上限时移除最旧一条，并把它的内容合并成一条"锚点摘要"存入摘要栈。
    /// </summary>
    public void AddAnchorMemory(ChatMessage message)
    {
        if (message is null)
        {
            return;
        }

        var limit = Math.Max(1, _config.Context.AnchorLimit);
        SummaryEntry? anchorSummary = null;

        lock (_sync)
        {
            if (_anchors.Count >= limit)
            {
                var oldest = _anchors[0];
                _anchors.RemoveAt(0);

                // 溢出的锚点不丢弃，压缩成锚点摘要保留语义。
                anchorSummary = new SummaryEntry
                {
                    Text = $"（早期锚点）{oldest}",
                    Level = 1,
                    Source = SummarySource.Anchor,
                    CreatedAt = DateTime.Now,
                };
                anchorSummary.EstimatedTokens = EstimateTokens(anchorSummary.Text);
                _summaryStack.Add(anchorSummary);
                TrimSummaryStackLocked();

                _logger.Info($"锚点记忆达到上限 {limit}，最旧一条已压缩为锚点摘要。");
            }

            _anchors.Add(message);
        }

        _logger.Info($"已新增锚点记忆（当前 {AnchorMemory.Count} 条）。");
    }

    /// <summary>
    /// 构建发送给 LLM 的上下文（手册 §3.2）。
    ///
    /// 顺序：角色设定 → 锚点记忆（按时间） → 锚点摘要 → 摘要栈（远期到近期） → 最近 10 轮对话。
    ///
    /// 相对手册的一处**有意增补**：开头补了角色核心设定。手册 §3.2 的顺序里没有它，
    /// 但 §0 把"角色一致性"列为项目目标、§3.1 又明确说"其它类型条目的检索仅用于上下文构建"，
    /// 不注入角色设定就无法达成角色一致性。增补放在最前面，不影响手册规定的四段相对顺序。
    /// </summary>
    public string BuildContext(string? userInput = null)
    {
        var builder = new StringBuilder(4096);

        AppendPersonality(builder, userInput);

        List<ChatMessage> anchors;
        List<SummaryEntry> summaries;
        List<ChatMessage> recent;

        lock (_sync)
        {
            anchors = _anchors.OrderBy(a => a.Timestamp).ToList();
            summaries = _summaryStack.ToList();
            recent = _recent.ToList();
        }

        if (anchors.Count > 0)
        {
            builder.AppendLine("【必须牢记的锚点记忆】");
            foreach (var anchor in anchors)
            {
                builder.AppendLine(anchor.ToString());
            }

            builder.AppendLine();
        }

        var anchorSummaries = summaries
            .Where(s => s.Source == SummarySource.Anchor)
            .OrderByDescending(s => s.Level)
            .ToList();

        if (anchorSummaries.Count > 0)
        {
            builder.AppendLine("【锚点摘要】");
            foreach (var entry in anchorSummaries)
            {
                builder.AppendLine(entry.Text);
            }

            builder.AppendLine();
        }

        var conversationSummaries = summaries
            .Where(s => s.Source == SummarySource.Conversation)
            .OrderByDescending(s => s.Level)
            .ToList();

        if (conversationSummaries.Count > 0)
        {
            builder.AppendLine("【历史摘要（远期→近期）】");
            foreach (var entry in conversationSummaries)
            {
                builder.AppendLine($"L{entry.Level}: {entry.Text}");
            }

            builder.AppendLine();
        }

        // 最近的 N 轮对话 —— 一轮记作用户 + 回复两条消息。
        var take = Math.Max(1, _config.Context.SendRecentRounds) * 2;
        var tail = recent.Count > take ? recent.GetRange(recent.Count - take, take) : recent;

        if (tail.Count > 0)
        {
            builder.AppendLine("【最近对话】");
            foreach (var message in tail)
            {
                builder.AppendLine(message.ToString());
            }
        }

        return builder.ToString().TrimEnd();
    }

    private void AppendPersonality(StringBuilder builder, string? userInput)
    {
        try
        {
            var entries = _knowledge.QueryContextEntries(
                userInput ?? string.Empty,
                Math.Max(1, _config.Context.PersonalityEntryLimit));

            var personalities = entries.Where(e => e.Type == KnowledgeType.CorePersonality).ToList();
            if (personalities.Count == 0)
            {
                return;
            }

            builder.AppendLine("【角色设定】");
            foreach (var entry in personalities)
            {
                builder.AppendLine(entry.ReplyTemplate);
            }

            builder.AppendLine();
        }
        catch (Exception ex)
        {
            _logger.Warn($"注入角色设定失败（不影响对话）：{ex.Message}");
        }
    }

    /// <summary>
    /// 压缩（手册 §3.2 压缩逻辑），**仅在空闲任务中调用**。
    /// </summary>
    public async Task<bool> CompressIfNeededAsync(CancellationToken cancellationToken = default)
    {
        List<ChatMessage> overflow;
        var maxRounds = MaxRecentRounds;
        var maxMessages = MaxRecentMessages;

        lock (_sync)
        {
            // 手册步骤 1：未超过阈值直接返回。阈值按**轮**配置，这里换算成消息条数再比较。
            if (_recent.Count <= maxMessages)
            {
                return false;
            }

            // 手册步骤 2：取出最早的一批（超出的部分）。
            var count = _recent.Count - maxMessages;
            overflow = _recent.GetRange(0, count);
        }

        if (overflow.Count == 0)
        {
            return false;
        }

        _logger.Info($"上下文超出窗口（{overflow.Count + maxMessages} 条 > {maxMessages} 条 = {maxRounds} 轮），开始压缩 {overflow.Count} 条最早消息。");

        // 手册步骤 3：调用 LLM（复用已有的聊天模型）生成摘要。
        var summaryText = await SummarizeAsync(overflow, cancellationToken).ConfigureAwait(false);

        cancellationToken.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(summaryText))
        {
            _logger.Warn("摘要生成为空，本轮压缩放弃（原文已归档，不丢数据）。");
            await ArchiveAsync(overflow, cancellationToken).ConfigureAwait(false);
            RemoveFromWindow(overflow);
            return false;
        }

        // 手册步骤 4：压入第 0 层，并按 token 上限逐级向上合并。
        MergeIntoSummaryStack(new SummaryEntry
        {
            Text = summaryText,
            Level = 0,
            Source = SummarySource.Conversation,
            CreatedAt = DateTime.Now,
            EstimatedTokens = EstimateTokens(summaryText),
        });

        // 手册步骤 5：原始消息完整追加到归档文件。
        await ArchiveAsync(overflow, cancellationToken).ConfigureAwait(false);

        // 手册步骤 6：从 RecentMessages 中移除已压缩的消息。
        RemoveFromWindow(overflow);

        _logger.Info($"压缩完成：摘要 {EstimateTokens(summaryText)} token，窗口剩余 {RecentMessages.Count} 条。");
        return true;
    }

    private async Task<string> SummarizeAsync(List<ChatMessage> messages, CancellationToken cancellationToken)
    {
        var transcript = string.Join(Environment.NewLine, messages.Select(m => m.ToString()));

        var request = new List<ChatMessage>
        {
            ChatMessage.System(
                "你是一个对话摘要器。请把下面这段对话压缩成不超过 200 字的中文摘要，" +
                "保留人物关系、用户偏好、约定事项与情绪基调，省略寒暄细节。只输出摘要正文。"),
            ChatMessage.User(transcript),
        };

        try
        {
            var result = await _llama.Client
                .ChatStreamAsync(request, onDelta: null, cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            if (!result.Success)
            {
                _logger.Warn($"摘要请求失败：{result.FailureReason}");
                return string.Empty;
            }

            return result.Text.Trim();
        }
        catch (Exception ex)
        {
            _logger.Error($"生成摘要时异常：{ex.Message}", ex);
            return string.Empty;
        }
    }

    /// <summary>
    /// 把一条摘要压入第 0 层，并在某层 token 超过上限时向更高层合并。
    /// 层级上限为 2（远期）；远期仍超限时合并为一条，不再增长。
    /// </summary>
    private void MergeIntoSummaryStack(SummaryEntry entry)
    {
        var limit = Math.Max(50, _config.Context.SummaryTokenLimit);

        lock (_sync)
        {
            _summaryStack.Add(entry);

            for (var level = 0; level < 2; level++)
            {
                var atLevel = _summaryStack.Where(s => s.Level == level && s.Source == SummarySource.Conversation).ToList();
                var tokens = atLevel.Sum(s => s.EstimatedTokens);

                if (tokens <= limit || atLevel.Count <= 1)
                {
                    break;
                }

                // 把该层所有条目合并成一条，升到上一层。
                var merged = string.Join("；", atLevel.Select(s => s.Text));
                foreach (var item in atLevel)
                {
                    _summaryStack.Remove(item);
                }

                var upper = new SummaryEntry
                {
                    Text = merged,
                    Level = level + 1,
                    Source = SummarySource.Conversation,
                    CreatedAt = DateTime.Now,
                    EstimatedTokens = EstimateTokens(merged),
                };

                _summaryStack.Add(upper);
                _logger.Info($"摘要层 L{level} 超过 {limit} token，已合并升入 L{level + 1}。");
            }

            // L2 仍超限时压成单条，避免 prompt 无限膨胀。
            var far = _summaryStack.Where(s => s.Level >= 2 && s.Source == SummarySource.Conversation).ToList();
            if (far.Count > 1)
            {
                var merged = string.Join("；", far.Select(s => s.Text));
                foreach (var item in far)
                {
                    _summaryStack.Remove(item);
                }

                _summaryStack.Add(new SummaryEntry
                {
                    Text = merged,
                    Level = 2,
                    Source = SummarySource.Conversation,
                    CreatedAt = DateTime.Now,
                    EstimatedTokens = EstimateTokens(merged),
                });
            }

            TrimSummaryStackLocked();
        }
    }

    /// <summary>
    /// 摘要条数兜底：超过 <see cref="MaxSummaryEntries"/> 时从最旧的开始丢弃。
    /// 调用方必须已持有 <see cref="_sync"/>（方法名带 Locked 表示这一点）。
    /// </summary>
    private void TrimSummaryStackLocked()
    {
        var overflow = _summaryStack.Count - MaxSummaryEntries;
        if (overflow <= 0)
        {
            return;
        }

        _summaryStack.RemoveRange(0, overflow);
        _logger.Debug($"摘要条数超过上限 {MaxSummaryEntries}，已丢弃最旧的 {overflow} 条。");
    }

    private void RemoveFromWindow(List<ChatMessage> messages)
    {
        lock (_sync)
        {
            foreach (var message in messages)
            {
                _recent.Remove(message);
            }
        }
    }

    /// <summary>把被压缩的原文追加进 chat_log_archive.jsonl（手册步骤 5）。</summary>
    private async Task ArchiveAsync(List<ChatMessage> messages, CancellationToken cancellationToken)
    {
        try
        {
            var builder = new StringBuilder();
            foreach (var message in messages)
            {
                // 必须用 JsonLineOptions（非缩进）：归档是 JSONL，一行必须是一个完整对象，
                // 否则 ConversationRefiner 逐行反序列化会全部失败（静默变成"聊天记录太少"）。
                builder.AppendLine(JsonSerializer.Serialize(message, KnowledgeFileStore.JsonLineOptions));
            }

            await File.AppendAllTextAsync(_archivePath, builder.ToString(), Encoding.UTF8, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.Error($"写入归档文件失败：{ex.Message}", ex);
        }
    }

    /// <summary>每条消息即时写入按日轮转的聊天日志（原文永久保留）。</summary>
    private void AppendChatLog(ChatMessage message)
    {
        try
        {
            var path = Path.Combine(_chatLogDirectory, $"chat-{DateTime.Now:yyyyMMdd}.jsonl");
            // 同理：chat-yyyyMMdd.jsonl 也是按行读取的，必须是紧凑单行 JSON。
            var line = JsonSerializer.Serialize(message, KnowledgeFileStore.JsonLineOptions) + Environment.NewLine;

            lock (_sync)
            {
                File.AppendAllText(path, line, Encoding.UTF8);
            }
        }
        catch (Exception ex)
        {
            _logger.Warn($"写入聊天日志失败：{ex.Message}");
        }
    }

    /// <summary>
    /// 粗略 token 估算：中文按 1 字 ≈ 1 token，其余字符按 4 字符 ≈ 1 token。
    /// 只用于决定"要不要合并摘要层"，不需要精确。
    /// </summary>
    public static int EstimateTokens(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return 0;
        }

        var cjk = 0;
        var other = 0;

        foreach (var ch in text)
        {
            if (ch >= 0x2E80 && ch <= 0x9FFF)
            {
                cjk++;
            }
            else
            {
                other++;
            }
        }

        return cjk + (other / 4) + 1;
    }

    /// <summary>清理超过 max_log_days 的聊天日志（手册 §5.1 与阶段 3 第 6 项）。</summary>
    public int CleanupOldChatLogs()
    {
        var days = Math.Max(1, _config.Logging.MaxLogDays);
        var cutoff = DateTime.Now.Date.AddDays(-days);
        var removed = 0;

        try
        {
            if (!Directory.Exists(_chatLogDirectory))
            {
                return 0;
            }

            foreach (var file in Directory.GetFiles(_chatLogDirectory, "chat-*.jsonl"))
            {
                try
                {
                    if (File.GetLastWriteTime(file) < cutoff)
                    {
                        File.Delete(file);
                        removed++;
                    }
                }
                catch
                {
                    // 单个文件删不掉不影响其它文件。
                }
            }

            if (removed > 0)
            {
                _logger.Info($"已清理 {removed} 个超过 {days} 天的聊天日志。");
            }
        }
        catch (Exception ex)
        {
            _logger.Warn($"清理聊天日志失败：{ex.Message}");
        }

        return removed;
    }

    /// <summary>聊天日志目录占用空间，设置面板展示用。</summary>
    public long GetChatLogSizeBytes()
    {
        try
        {
            if (!Directory.Exists(_chatLogDirectory))
            {
                return 0;
            }

            return Directory.EnumerateFiles(_chatLogDirectory, "*.jsonl")
                .Sum(f =>
                {
                    try
                    {
                        return new FileInfo(f).Length;
                    }
                    catch
                    {
                        return 0L;
                    }
                });
        }
        catch
        {
            return 0L;
        }
    }
}
