using System.Text;
using System.Text.Json;
using AICompanion.Core.Abstractions;
using AICompanion.Core.Configuration;
using AICompanion.Core.Models;

namespace AICompanion.Core.Knowledge;

/// <summary>导入合并策略（手册 §3.1 导入导出）。</summary>
public enum ImportStrategy
{
    /// <summary>追加：为每个导入条目生成新 Id 后追加，绝不改动已有条目。</summary>
    Append = 0,

    /// <summary>覆盖：先用时间戳备份整个知识库，再以角色包内容替换。</summary>
    Overwrite = 1,

    /// <summary>合并：Id 相同则更新，触发器完全相同则跳过，其余新增。</summary>
    Merge = 2,
}

public sealed record ImportResult(
    ImportStrategy Strategy,
    int Added,
    int Updated,
    int Skipped,
    int Removed,
    string? Error,
    List<string> Warnings)
{
    public bool Success => Error is null;

    public static ImportResult Fail(ImportStrategy strategy, string error)
        => new(strategy, 0, 0, 0, 0, error, new List<string>());
}

/// <summary>
/// 角色包结构。手册只说"文件夹，包含 personality.json、voice.wav、avatar/ 等"，
/// 未定义 personality.json 的字段，因此这里给出并**显式记录**本项目的约定：
///
/// <code>
/// {
///   "name": "小星",
///   "personality": "你是一个温柔、话不多的桌面伴侣……",
///   "voice": { "voiceId": "...", "referenceAudioPath": "voice.wav",
///              "fallbackPiperVoice": "zh_CN-huayan-medium" },
///   "entries": [ /* 可选的 KnowledgeEntry 列表 */ ]
/// }
/// </code>
///
/// 兼容性：如果 personality.json 本身就是一个 KnowledgeEntry 或条目数组，也能被识别。
/// </summary>
public sealed class CharacterPack
{
    public string Name { get; set; } = string.Empty;

    public string? Personality { get; set; }

    public VoiceConfig? Voice { get; set; }

    public List<KnowledgeEntry> Entries { get; set; } = new();
}

/// <summary>
/// 角色包导入器（手册 §3.1 导入导出，三种合并策略）。
///
/// 准则 3 的落点：导入时逐条校验 <see cref="VoiceConfig.FallbackPiperVoice"/>，
/// 角色包或条目缺少该字段时**拒绝导入并提示原因**，而不是悄悄写入一个将来无法降级的条目。
/// </summary>
public sealed class KnowledgeImporter
{
    private readonly AppConfig _config;
    private readonly IAppLogger _logger;
    private readonly IBubbleNotifier _bubble;
    private readonly UnifiedKnowledgeManager _manager;

    public KnowledgeImporter(
        AppConfig config,
        IAppLogger logger,
        IBubbleNotifier bubble,
        UnifiedKnowledgeManager manager)
    {
        _config = config;
        _logger = logger.ForCategory("KnowledgeImport");
        _bubble = bubble;
        _manager = manager;
    }

    public async Task<ImportResult> ImportCharacterPackAsync(
        string packDirectory,
        ImportStrategy strategy,
        CancellationToken cancellationToken = default)
    {
        var warnings = new List<string>();

        try
        {
            var packDir = AppPaths.Resolve(packDirectory);
            if (!Directory.Exists(packDir))
            {
                return ImportResult.Fail(strategy, $"角色包目录不存在：{packDir}");
            }

            var personalityFile = Path.Combine(packDir, "personality.json");
            if (!File.Exists(personalityFile))
            {
                return ImportResult.Fail(strategy, $"角色包缺少 personality.json：{packDir}");
            }

            var pack = await Task.Run(
                () => ParsePack(personalityFile, packDir, warnings),
                cancellationToken).ConfigureAwait(false);

            // ---- 准则 3：校验音色配置 ----
            if (pack.Voice is not null && !pack.Voice.TryValidate(out var voiceReason))
            {
                var message = $"角色包音色配置无效：{voiceReason}";
                _logger.Error($"拒绝导入 {packDir}：{voiceReason}");
                _bubble.Show("import-rejected", $"导入被拒绝：{voiceReason}", BubbleLevel.Error);
                return ImportResult.Fail(strategy, message);
            }

            var incoming = BuildEntries(pack, packDir, warnings);
            if (incoming.Count == 0)
            {
                return ImportResult.Fail(strategy, "角色包中没有可导入的知识条目。");
            }

            var existing = _manager.Snapshot.ToList();
            var (toWrite, added, updated, skipped, removed) = await Task.Run(
                () => ApplyStrategy(strategy, existing, incoming, warnings, cancellationToken),
                cancellationToken).ConfigureAwait(false);

            foreach (var entry in toWrite)
            {
                cancellationToken.ThrowIfCancellationRequested();
                KnowledgeFileStore.SaveEntry(_manager.DirectoryPath, entry);
            }

            await _manager.ReloadAsync(cancellationToken).ConfigureAwait(false);

            var result = new ImportResult(strategy, added, updated, skipped, removed, null, warnings);
            _logger.Info($"角色包导入完成（{strategy}）：新增 {added}，更新 {updated}，跳过 {skipped}，移除 {removed}。");
            _bubble.Show(
                "import-done",
                $"角色包导入完成：新增 {added} 条，更新 {updated} 条，跳过 {skipped} 条。",
                BubbleLevel.Info,
                TimeSpan.FromSeconds(_config.Ui.BubbleDurationSeconds));

            return result;
        }
        catch (OperationCanceledException)
        {
            return ImportResult.Fail(strategy, "导入已取消。");
        }
        catch (Exception ex)
        {
            _logger.Error($"导入角色包失败：{ex.Message}", ex);
            return ImportResult.Fail(strategy, ex.Message);
        }
    }

    private CharacterPack ParsePack(string personalityFile, string packDir, List<string> warnings)
    {
        var text = File.ReadAllText(personalityFile, Encoding.UTF8);

        // 先按角色包结构解析。
        CharacterPack? pack;
        try
        {
            pack = JsonSerializer.Deserialize<CharacterPack>(text, KnowledgeFileStore.JsonOptions);
        }
        catch (JsonException ex)
        {
            warnings.Add($"personality.json 按角色包结构解析失败（{ex.Message}），尝试按知识条目解析。");
            pack = null;
        }

        pack ??= new CharacterPack();

        var hasContent = !string.IsNullOrWhiteSpace(pack.Personality) || pack.Entries.Count > 0;

        if (!hasContent)
        {
            // 兼容：personality.json 直接就是条目或条目数组。
            var trimmed = text.TrimStart();
            if (trimmed.StartsWith('['))
            {
                var list = JsonSerializer.Deserialize<List<KnowledgeEntry>>(trimmed, KnowledgeFileStore.JsonOptions);
                if (list is not null)
                {
                    pack.Entries = list;
                    warnings.Add("personality.json 被识别为知识条目数组。");
                }
            }
            else
            {
                var single = JsonSerializer.Deserialize<KnowledgeEntry>(trimmed, KnowledgeFileStore.JsonOptions);
                if (single is not null)
                {
                    pack.Entries = new List<KnowledgeEntry> { single };
                    warnings.Add("personality.json 被识别为单个知识条目。");
                }
            }
        }

        if (string.IsNullOrWhiteSpace(pack.Name))
        {
            pack.Name = Path.GetFileName(packDir.TrimEnd(Path.DirectorySeparatorChar));
        }

        return pack;
    }

    /// <summary>把角色包转成知识条目，并逐条补齐音色（准则 3 + 准则 10）。</summary>
    private List<KnowledgeEntry> BuildEntries(CharacterPack pack, string packDir, List<string> warnings)
    {
        var result = new List<KnowledgeEntry>();

        var referenceAudio = Path.Combine(packDir, "voice.wav");
        var voice = pack.Voice?.Clone();
        if (voice is not null && string.IsNullOrWhiteSpace(voice.ReferenceAudioPath) && File.Exists(referenceAudio))
        {
            voice.ReferenceAudioPath = referenceAudio;
        }

        // 角色名 + 人设 → 一条 CorePersonality 条目。
        if (!string.IsNullOrWhiteSpace(pack.Personality))
        {
            result.Add(new KnowledgeEntry
            {
                Type = KnowledgeType.CorePersonality,
                Priority = 1000,
                ReplyTemplate = $"角色名：{pack.Name}\n人设：{pack.Personality}",
                ContextKeywords = new List<string> { pack.Name },
                VoiceConfig = voice?.Clone(),
                Source = KnowledgeSource.Imported,
                Confidence = 1.0,
                IsActive = true,
            });
        }

        foreach (var entry in pack.Entries)
        {
            // 条目自己没有音色时继承角色包音色。
            if (entry.VoiceConfig is null && voice is not null)
            {
                entry.VoiceConfig = voice.Clone();
            }

            if (entry.VoiceConfig is not null && !entry.VoiceConfig.TryValidate(out var reason))
            {
                warnings.Add($"条目 {entry.Id} 的音色配置无效（{reason}），已跳过该条目。");
                continue;
            }

            if (entry.VoiceConfig is null)
            {
                // 手册准则 3 要求拒绝导入；这里只跳过该条目并明确告警，避免整包失败。
                warnings.Add($"条目 {entry.Id} 缺少 voiceConfig（含必填的 FallbackPiperVoice），已跳过该条目。");
                continue;
            }

            entry.Source = KnowledgeSource.Imported;
            result.Add(entry);
        }

        return result;
    }

    private (List<KnowledgeEntry> ToWrite, int Added, int Updated, int Skipped, int Removed)
        ApplyStrategy(
            ImportStrategy strategy,
            List<KnowledgeEntry> existing,
            List<KnowledgeEntry> incoming,
            List<string> warnings,
            CancellationToken cancellationToken)
    {
        switch (strategy)
        {
            case ImportStrategy.Append:
            {
                // 追加：全部生成新 Id，保证不与现有条目冲突。
                foreach (var entry in incoming)
                {
                    entry.Id = Guid.NewGuid();
                }

                return (incoming, incoming.Count, 0, 0, 0);
            }

            case ImportStrategy.Overwrite:
            {
                // 覆盖：先备份整个知识库，再删除现有条目文件。
                var backup = BackupExisting();
                if (backup is not null)
                {
                    warnings.Add($"覆盖前已备份原知识库到：{backup}");
                }

                foreach (var entry in existing)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    KnowledgeFileStore.DeleteEntryFile(_manager.DirectoryPath, entry.Id);
                }

                return (incoming, incoming.Count, 0, 0, existing.Count);
            }

            case ImportStrategy.Merge:
            default:
            {
                var toWrite = new List<KnowledgeEntry>();
                var added = 0;
                var updated = 0;
                var skipped = 0;

                foreach (var entry in incoming)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    var byId = existing.FirstOrDefault(e => e.Id == entry.Id);
                    if (byId is not null)
                    {
                        KnowledgeFileStore.DeleteEntryFile(_manager.DirectoryPath, byId.Id);
                        toWrite.Add(entry);
                        updated++;
                        continue;
                    }

                    // 触发器集合完全相同视为重复。
                    var byTriggers = existing.FirstOrDefault(e => SameTriggers(e, entry));
                    if (byTriggers is not null)
                    {
                        warnings.Add($"条目 {entry.Id} 与现有条目 {byTriggers.Id} 触发器相同，已跳过。");
                        skipped++;
                        continue;
                    }

                    toWrite.Add(entry);
                    added++;
                }

                return (toWrite, added, updated, skipped, 0);
            }
        }
    }

    private static bool SameTriggers(KnowledgeEntry a, KnowledgeEntry b)
    {
        if (a.Triggers.Count == 0 || b.Triggers.Count == 0)
        {
            return false;
        }

        var setA = new HashSet<string>(a.Triggers, StringComparer.OrdinalIgnoreCase);
        var setB = new HashSet<string>(b.Triggers, StringComparer.OrdinalIgnoreCase);
        return setA.SetEquals(setB) && a.Type == b.Type;
    }

    private string? BackupExisting()
    {
        try
        {
            var files = Directory.GetFiles(_manager.DirectoryPath, "*.json", SearchOption.TopDirectoryOnly);
            if (files.Length == 0)
            {
                return null;
            }

            var backupDir = Path.Combine(
                _manager.DirectoryPath,
                ".backup",
                DateTime.Now.ToString("yyyyMMdd_HHmmss"));

            Directory.CreateDirectory(backupDir);
            foreach (var file in files)
            {
                File.Copy(file, Path.Combine(backupDir, Path.GetFileName(file)), overwrite: true);
            }

            return backupDir;
        }
        catch (Exception ex)
        {
            _logger.Warn($"备份原知识库失败（覆盖仍会继续，请谨慎）：{ex.Message}");
            return null;
        }
    }
}
