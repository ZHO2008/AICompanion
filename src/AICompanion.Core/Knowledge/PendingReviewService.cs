using System.Text;
using System.Text.Json;
using AICompanion.Core.Abstractions;
using AICompanion.Core.Configuration;
using AICompanion.Core.Models;

namespace AICompanion.Core.Knowledge;

/// <summary>待审核候选条目（附带来源文件，供面板操作）。</summary>
public sealed record PendingCandidate(string FilePath, string FileName, DateTime CreatedUtc, KnowledgeEntry? Entry, string? Error)
{
    public string DisplayText => Entry is null
        ? $"[无法解析] {FileName}"
        : $"{Entry.Type} · {(Entry.Triggers.Count > 0 ? string.Join('/', Entry.Triggers) : "无触发器")} · {Preview}";

    private string Preview => Entry?.ReplyTemplate is { Length: > 0 } t
        ? (t.Length <= 40 ? t : t[..40] + "...")
        : "(无回复模板)";
}

/// <summary>
/// 待审核知识管理（手册 §3.7）。
///
/// 这里是"程序绝不自行写入正式知识库"（准则 9）的执行点：
/// ConversationRefiner 生成的候选只会落到 <c>.pending/</c>，
/// 只有本类中被用户显式调用的 <see cref="ConfirmAsync"/> 才会把文件移入正式目录。
/// </summary>
public sealed class PendingReviewService
{
    private readonly AppConfig _config;
    private readonly IAppLogger _logger;
    private readonly IBubbleNotifier _bubble;
    private readonly UnifiedKnowledgeManager _manager;

    private readonly string _pendingDirectory;
    private readonly string _notifyStatePath;

    public PendingReviewService(
        AppConfig config,
        IAppLogger logger,
        IBubbleNotifier bubble,
        UnifiedKnowledgeManager manager)
    {
        _config = config;
        _logger = logger.ForCategory("PendingReview");
        _bubble = bubble;
        _manager = manager;

        _pendingDirectory = Path.Combine(AppPaths.Resolve(config.Knowledge.Directory), config.Knowledge.PendingDirectoryName);
        _notifyStatePath = Path.Combine(_pendingDirectory, config.Knowledge.NotifyStateFileName);
    }

    public string DirectoryPath => _pendingDirectory;

    /// <summary>列出全部候选条目（含解析失败的，便于用户在面板里直接丢弃）。</summary>
    public async Task<IReadOnlyList<PendingCandidate>> ListCandidatesAsync(CancellationToken cancellationToken = default)
    {
        return await Task.Run<IReadOnlyList<PendingCandidate>>(() =>
        {
            var result = new List<PendingCandidate>();

            if (!Directory.Exists(_pendingDirectory))
            {
                return result;
            }

            foreach (var file in Directory.GetFiles(_pendingDirectory, "*.json", SearchOption.TopDirectoryOnly))
            {
                cancellationToken.ThrowIfCancellationRequested();

                var name = Path.GetFileName(file);

                // 提醒状态文件不是候选条目。
                if (string.Equals(name, _config.Knowledge.NotifyStateFileName, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var created = SafeGetCreationUtc(file);

                try
                {
                    var text = File.ReadAllText(file, Encoding.UTF8);
                    var trimmed = text.TrimStart();

                    KnowledgeEntry? entry;
                    if (trimmed.StartsWith('['))
                    {
                        var list = JsonSerializer.Deserialize<List<KnowledgeEntry>>(trimmed, KnowledgeFileStore.JsonOptions);
                        entry = list?.FirstOrDefault();
                    }
                    else
                    {
                        entry = JsonSerializer.Deserialize<KnowledgeEntry>(trimmed, KnowledgeFileStore.JsonOptions);
                    }

                    result.Add(new PendingCandidate(file, name, created, entry, entry is null ? "解析结果为空。" : null));
                }
                catch (Exception ex)
                {
                    result.Add(new PendingCandidate(file, name, created, null, ex.Message));
                }
            }

            return result.OrderByDescending(c => c.CreatedUtc).ToList();
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>确认入库：把候选文件解析成条目后写入正式目录并删除候选文件（准则 9 的人工闸门）。</summary>
    public async Task<bool> ConfirmAsync(string filePath, CancellationToken cancellationToken = default)
    {
        try
        {
            var text = await File.ReadAllTextAsync(filePath, Encoding.UTF8, cancellationToken).ConfigureAwait(false);
            var trimmed = text.TrimStart();

            KnowledgeEntry? entry;
            if (trimmed.StartsWith('['))
            {
                var list = JsonSerializer.Deserialize<List<KnowledgeEntry>>(trimmed, KnowledgeFileStore.JsonOptions);
                entry = list?.FirstOrDefault();
            }
            else
            {
                entry = JsonSerializer.Deserialize<KnowledgeEntry>(trimmed, KnowledgeFileStore.JsonOptions);
            }

            if (entry is null)
            {
                _bubble.Show("pending-confirm-failed", "该候选条目无法解析，已保留在待审核目录中。", BubbleLevel.Warning);
                return false;
            }

            // 准则 3：入库前再校验一次音色，避免不可降级的条目进入正式库。
            if (entry.VoiceConfig is not null && !entry.VoiceConfig.TryValidate(out var reason))
            {
                _logger.Warn($"候选条目 {entry.Id} 的音色配置无效（{reason}），将丢弃音色配置后入库。");
                entry.VoiceConfig = null;
            }

            entry.Source = KnowledgeSource.Manual;
            entry.Id = entry.Id == Guid.Empty ? Guid.NewGuid() : entry.Id;

            await _manager.AddOrUpdateEntryAsync(entry, cancellationToken).ConfigureAwait(false);

            File.Delete(filePath);
            _logger.Info($"用户确认候选条目入库：{entry.Id}");
            return true;
        }
        catch (Exception ex)
        {
            _logger.Error($"确认候选条目失败：{ex.Message}", ex);
            return false;
        }
    }

    /// <summary>丢弃候选条目。</summary>
    public Task<bool> DiscardAsync(string filePath, CancellationToken cancellationToken = default)
    {
        return Task.Run(() =>
        {
            try
            {
                if (File.Exists(filePath))
                {
                    File.Delete(filePath);
                    _logger.Info($"用户丢弃候选条目：{Path.GetFileName(filePath)}");
                }

                return true;
            }
            catch (Exception ex)
            {
                _logger.Error($"丢弃候选条目失败：{ex.Message}", ex);
                return false;
            }
        }, cancellationToken);
    }

    /// <summary>启动时删除超过保留期的候选文件（手册 §3.7）。</summary>
    public void CleanupExpiredCandidates()
    {
        try
        {
            if (!Directory.Exists(_pendingDirectory))
            {
                return;
            }

            var days = Math.Max(1, _config.Knowledge.PendingRetentionDays);
            var cutoff = DateTime.UtcNow.AddDays(-days);
            var removed = 0;

            foreach (var file in Directory.GetFiles(_pendingDirectory, "*.json", SearchOption.TopDirectoryOnly))
            {
                if (string.Equals(Path.GetFileName(file), _config.Knowledge.NotifyStateFileName, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (SafeGetCreationUtc(file) < cutoff)
                {
                    try
                    {
                        File.Delete(file);
                        removed++;
                    }
                    catch
                    {
                        // 单个文件删不掉不影响其它文件。
                    }
                }
            }

            if (removed > 0)
            {
                _logger.Info($"已清理 {removed} 个超过 {days} 天的待审核候选文件。");
            }
        }
        catch (Exception ex)
        {
            _logger.Warn($"清理待审核候选失败：{ex.Message}");
        }
    }

    /// <summary>
    /// 候选数量达到阈值时提醒，且**每天最多一次**（手册 §3.7）。
    /// "每天一次"的状态需要跨重启保持，因此持久化到 <c>.notify_state.json</c>。
    /// </summary>
    public async Task NotifyIfNeededAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var threshold = Math.Max(1, _config.Knowledge.NotifyThreshold);
            var candidates = await ListCandidatesAsync(cancellationToken).ConfigureAwait(false);
            if (candidates.Count < threshold)
            {
                return;
            }

            if (WasNotifiedToday())
            {
                return;
            }

            _bubble.Show(
                "pending-review",
                $"有 {candidates.Count} 条新记忆待确认，可在设置面板的\"待审核知识\"中查看。",
                BubbleLevel.Info,
                TimeSpan.FromSeconds(_config.Ui.BubbleDurationSeconds * 3));

            MarkNotifiedToday();
            _logger.Info($"待审核候选 {candidates.Count} 条，已提醒用户。");
        }
        catch (Exception ex)
        {
            _logger.Warn($"待审核提醒失败：{ex.Message}");
        }
    }

    /// <summary>候选数量，供状态栏显示。</summary>
    public int CountCandidates()
    {
        try
        {
            if (!Directory.Exists(_pendingDirectory))
            {
                return 0;
            }

            return Directory.GetFiles(_pendingDirectory, "*.json", SearchOption.TopDirectoryOnly)
                .Count(f => !string.Equals(
                    Path.GetFileName(f),
                    _config.Knowledge.NotifyStateFileName,
                    StringComparison.OrdinalIgnoreCase));
        }
        catch
        {
            return 0;
        }
    }

    // ------------------------------------------------------------------ 提醒状态持久化

    private sealed class NotifyState
    {
        public DateTime LastNotifiedUtc { get; set; }
    }

    private bool WasNotifiedToday()
    {
        try
        {
            if (!File.Exists(_notifyStatePath))
            {
                return false;
            }

            var state = JsonSerializer.Deserialize<NotifyState>(File.ReadAllText(_notifyStatePath, Encoding.UTF8));
            return state is not null && state.LastNotifiedUtc.Date == DateTime.UtcNow.Date;
        }
        catch
        {
            // 状态文件损坏时按"今天没提醒过"处理，宁可多提醒一次。
            return false;
        }
    }

    private void MarkNotifiedToday()
    {
        try
        {
            Directory.CreateDirectory(_pendingDirectory);
            var state = new NotifyState { LastNotifiedUtc = DateTime.UtcNow };
            File.WriteAllText(
                _notifyStatePath,
                JsonSerializer.Serialize(state, KnowledgeFileStore.JsonOptions),
                new UTF8Encoding(false));
        }
        catch (Exception ex)
        {
            _logger.Warn($"写入提醒状态失败（可能导致重复提醒）：{ex.Message}");
        }
    }

    private static DateTime SafeGetCreationUtc(string path)
    {
        try
        {
            return File.GetCreationTimeUtc(path);
        }
        catch
        {
            return DateTime.UtcNow;
        }
    }
}
