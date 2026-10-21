using AICompanion.Core.Abstractions;
using AICompanion.Core.Configuration;
using AICompanion.Core.Models;

namespace AICompanion.Core.Knowledge;

/// <summary>
/// 统一知识管理器（手册 §3.1）。
///
/// 三条核心设计：
///   1. **静态加载 + 人工审核入库**：程序自身永远不写正式知识库（准则 9）。
///      唯一的写入口是用户显式操作（审核面板确认 / 手动新增 / 角色包导入）。
///   2. **无锁查询**：条目集合放在一个不可变数组引用里，读路径 <see cref="Snapshot"/> 只做一次
///      Volatile 读；重建在后台线程完成后用 <see cref="Interlocked.Exchange{T}"/> 原子替换。
///      因此 Query 永远不会因为热重载而阻塞。
///   3. **热重载不递归**：FileSystemWatcher 的 IncludeSubdirectories=false，
///      保证 <c>.pending/</c> 里的待审核候选永远不会被当成正式知识加载。
/// </summary>
public sealed class UnifiedKnowledgeManager : IDisposable
{
    /// <summary>文件事件去抖窗口：编辑器保存一个文件常触发多个事件。</summary>
    private static readonly TimeSpan DebounceWindow = TimeSpan.FromMilliseconds(400);

    private readonly AppConfig _config;
    private readonly IAppLogger _logger;
    private readonly IBubbleNotifier _bubble;
    private readonly object _reloadGate = new();
    private readonly CancellationTokenSource _lifetime = new();

    private KnowledgeEntry[] _entries = Array.Empty<KnowledgeEntry>();
    private FileSystemWatcher? _watcher;
    private CancellationTokenSource? _debounceCts;
    private bool _emptyNoticeShown;
    private bool _disposed;

    public UnifiedKnowledgeManager(AppConfig config, IAppLogger logger, IBubbleNotifier bubble)
    {
        _config = config;
        _logger = logger.ForCategory("Knowledge");
        _bubble = bubble;

        DirectoryPath = AppPaths.Resolve(config.Knowledge.Directory);
        PendingDirectoryPath = Path.Combine(DirectoryPath, config.Knowledge.PendingDirectoryName);

        Importer = new KnowledgeImporter(config, logger, bubble, this);
        Pending = new PendingReviewService(config, logger, bubble, this);
    }

    public string DirectoryPath { get; }

    public string PendingDirectoryPath { get; }

    public KnowledgeImporter Importer { get; }

    public PendingReviewService Pending { get; }

    /// <summary>当前条目快照。这是无锁热路径，不要在里面分配或加锁。</summary>
    public IReadOnlyList<KnowledgeEntry> Snapshot => Volatile.Read(ref _entries);

    public int Count => Volatile.Read(ref _entries).Length;

    public event EventHandler? Reloaded;

    /// <summary>启动时加载知识库并建立热重载监听（手册 §3.1 热重载）。</summary>
    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        KnowledgeFileStore.EnsureReadme(DirectoryPath);

        try
        {
            Directory.CreateDirectory(PendingDirectoryPath);
        }
        catch (Exception ex)
        {
            _logger.Warn($"创建待审核目录失败：{ex.Message}");
        }

        await ReloadAsync(cancellationToken).ConfigureAwait(false);
        SetupWatcher();

        // 手册 §3.7：启动时清理超过保留期的候选文件。
        Pending.CleanupExpiredCandidates();

        _logger.Info($"知识库已就绪：{DirectoryPath}，条目数 {Count}。");
    }

    /// <summary>
    /// 手册 §3.1 查询逻辑：只匹配 Preprocess + IsActive + 触发器命中的条目；未命中返回 null。
    /// 同优先级时取时间较新的一条。
    /// </summary>
    public KnowledgeEntry? Query(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return null;
        }

        var snapshot = Volatile.Read(ref _entries);
        KnowledgeEntry? best = null;

        foreach (var entry in snapshot)
        {
            if (!entry.IsPreprocessUsable)
            {
                continue;
            }

            if (!MatchesAnyTrigger(entry, input))
            {
                continue;
            }

            if (best is null ||
                entry.Priority > best.Priority ||
                (entry.Priority == best.Priority && entry.Timestamp > best.Timestamp))
            {
                best = entry;
            }
        }

        return best;
    }

    private static bool MatchesAnyTrigger(KnowledgeEntry entry, string input)
    {
        foreach (var trigger in entry.Triggers)
        {
            if (input.Contains(trigger, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// 供上下文构建使用的检索（手册 §3.1 注："模糊匹配和其他类型条目的检索仅用于上下文构建"）。
    /// 按 ContextKeywords 命中数排序，角色核心设定永远带上前若干条。
    /// </summary>
    public IReadOnlyList<KnowledgeEntry> QueryContextEntries(string input, int limit)
    {
        var snapshot = Volatile.Read(ref _entries);
        var scored = new List<(KnowledgeEntry Entry, int Score)>();

        foreach (var entry in snapshot)
        {
            if (!entry.IsActive || entry.IsExpired || entry.Type == KnowledgeType.Preprocess)
            {
                continue;
            }

            var score = 0;

            if (entry.Type == KnowledgeType.CorePersonality)
            {
                score += 1000;   // 角色一致性优先（手册 §0：长对话记忆与角色一致性）。
            }

            if (entry.Type == KnowledgeType.AnchorMemory)
            {
                score += 500;
            }

            if (!string.IsNullOrWhiteSpace(input))
            {
                foreach (var keyword in entry.ContextKeywords)
                {
                    if (input.Contains(keyword, StringComparison.OrdinalIgnoreCase))
                    {
                        score += 10;
                    }
                }

                foreach (var trigger in entry.Triggers)
                {
                    if (input.Contains(trigger, StringComparison.OrdinalIgnoreCase))
                    {
                        score += 5;
                    }
                }
            }

            if (score > 0)
            {
                scored.Add((entry, score));
            }
        }

        return scored
            .OrderByDescending(x => x.Score)
            .ThenByDescending(x => x.Entry.Priority)
            .Take(Math.Max(0, limit))
            .Select(x => x.Entry)
            .ToList();
    }

    /// <summary>
    /// 后台完整重建条目列表，再用 Interlocked.Exchange 原子替换（手册 §3.1 热重载）。
    /// </summary>
    public async Task ReloadAsync(CancellationToken cancellationToken = default)
    {
        List<KnowledgeEntry> entries;
        List<string> warnings;

        try
        {
            (entries, warnings) = await Task.Run(
                () => KnowledgeFileStore.LoadDirectory(DirectoryPath),
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            _logger.Error($"重建知识库失败：{ex.Message}", ex);
            return;
        }

        // 按 Priority 降序预排，Query 时仍然显式比较优先级，这里只是让常见情况更快命中。
        var next = entries
            .OrderByDescending(e => e.Priority)
            .ThenByDescending(e => e.Timestamp)
            .ToArray();

        var previous = Interlocked.Exchange(ref _entries, next);

        foreach (var warning in warnings)
        {
            _logger.Warn("知识库加载：" + warning);
        }

        _logger.Info($"知识库重载完成：{previous.Length} → {next.Length} 条（警告 {warnings.Count} 条）。");

        HandleEmptyKnowledgeBase(next.Length);
        Reloaded?.Invoke(this, EventArgs.Empty);
    }

    private void HandleEmptyKnowledgeBase(int count)
    {
        if (count > 0)
        {
            _emptyNoticeShown = false;
            return;
        }

        // 手册 §6：条目数为零时提示，并确保目录下有说明文件。
        KnowledgeFileStore.EnsureReadme(DirectoryPath);

        if (_emptyNoticeShown)
        {
            return;
        }

        _emptyNoticeShown = true;
        _bubble.Show(
            "knowledge-empty",
            "知识库为空，角色将以通用设定运行。你可以在 Knowledge 目录中放入 JSON 条目。",
            BubbleLevel.Info,
            TimeSpan.FromSeconds(_config.Ui.BubbleDurationSeconds * 2));
    }

    /// <summary>用户显式新增条目（审核面板/设置面板调用）。程序自身不会主动调用它。</summary>
    public async Task<string> AddOrUpdateEntryAsync(KnowledgeEntry entry, CancellationToken cancellationToken = default)
    {
        var path = await Task.Run(() => KnowledgeFileStore.SaveEntry(DirectoryPath, entry), cancellationToken)
            .ConfigureAwait(false);

        _logger.Info($"用户写入知识条目：{entry.Id}（{entry.Type}）→ {path}");
        await ReloadAsync(cancellationToken).ConfigureAwait(false);
        return path;
    }

    public async Task DeleteEntryAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await Task.Run(() => KnowledgeFileStore.DeleteEntryFile(DirectoryPath, id), cancellationToken)
            .ConfigureAwait(false);

        _logger.Info($"用户删除知识条目：{id}");
        await ReloadAsync(cancellationToken).ConfigureAwait(false);
    }

    // ------------------------------------------------------------------ FileSystemWatcher

    private void SetupWatcher()
    {
        try
        {
            if (!Directory.Exists(DirectoryPath))
            {
                _logger.Warn($"知识库目录不存在，跳过热重载监听：{DirectoryPath}");
                return;
            }

            _watcher = new FileSystemWatcher(DirectoryPath, "*.json")
            {
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
                // 关键：不递归，因此 .pending 子目录不会进入正式知识库。
                IncludeSubdirectories = false,
                EnableRaisingEvents = false,
            };

            _watcher.Created += (_, e) => ScheduleReload($"新增 {e.Name}");
            _watcher.Changed += (_, e) => ScheduleReload($"修改 {e.Name}");
            _watcher.Deleted += (_, e) => ScheduleReload($"删除 {e.Name}");
            _watcher.Renamed += (_, e) => ScheduleReload($"重命名 {e.OldName} → {e.Name}");
            _watcher.Error += (_, e) => _logger.Warn($"知识库监听出错：{e.GetException().Message}");

            _watcher.EnableRaisingEvents = true;
            _logger.Info($"已启用知识库热重载监听：{DirectoryPath}");
        }
        catch (Exception ex)
        {
            _logger.Error($"启用知识库热重载失败（将退化为仅启动时加载）：{ex.Message}", ex);
        }
    }

    private void ScheduleReload(string reason)
    {
        lock (_reloadGate)
        {
            if (_disposed)
            {
                return;
            }

            _debounceCts?.Cancel();
            _debounceCts?.Dispose();

            var cts = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
            _debounceCts = cts;
            _ = DebouncedReloadAsync(cts.Token, reason);
        }
    }

    private async Task DebouncedReloadAsync(CancellationToken cancellationToken, string reason)
    {
        try
        {
            await Task.Delay(DebounceWindow, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;   // 去抖窗口内又有新事件，交给后来者处理。
        }

        _logger.Info($"检测到知识库变化（{reason}），开始热重载。");
        await ReloadAsync(cancellationToken).ConfigureAwait(false);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        try
        {
            if (_watcher is not null)
            {
                _watcher.EnableRaisingEvents = false;
                _watcher.Dispose();
                _watcher = null;
            }
        }
        catch
        {
            // 忽略。
        }

        lock (_reloadGate)
        {
            _debounceCts?.Cancel();
            _debounceCts?.Dispose();
            _debounceCts = null;
        }

        _lifetime.Cancel();
        _lifetime.Dispose();
    }
}
