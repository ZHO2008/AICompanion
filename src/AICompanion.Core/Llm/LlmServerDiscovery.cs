using System.Collections.Concurrent;
using System.IO;
using System.Text;
using System.Text.Json;
using AICompanion.Core.Configuration;

namespace AICompanion.Core.Llm;

/// <summary>一个可用的 llama-server 入口候选。</summary>
/// <param name="RelativePath">相对配置根目录的写法（正斜杠），可直接写进日志或内存配置。</param>
/// <param name="AbsolutePath">用于 File.Exists / 启动进程的绝对路径。</param>
/// <param name="Rank">优先级：0 = vulkan，1 = cuda/nvidia，2 = 其他 llm 模块。</param>
/// <param name="DirectoryName">模块目录名（modules\&lt;name&gt;），用于同一优先级下的稳定排序。</param>
public sealed record LlmServerCandidate(
    string RelativePath,
    string AbsolutePath,
    int Rank,
    string DirectoryName)
{
    /// <summary>形如「vulkan 模块」，写进日志用。</summary>
    public string RankLabel => LlmServerDiscovery.RankLabel(Rank);
}

/// <summary>
/// 「配置里的 llama-server.exe 不存在」时的兜底发现。
///
/// 存在的理由（真实缺陷）：出厂模板曾把 <c>llm.server_executable_path</c> 写成
/// <c>bin/llama-server.exe</c>，而发行包里的后端在 <c>modules\llm-vulkan\</c> /
/// <c>modules\llm-cuda\</c>，<c>bin\</c> 里只有 ai_asr.dll / libfvad.dll。
/// 结果是干净机器**首次启动必然**报「未找到 llama-server 可执行文件」，对话完全不可用 ——
/// 一份"开箱即用"的包，开箱即坏。
///
/// 两道防线：
///   1. 模板与 <see cref="LlmConfig.ServerExecutablePath"/> 的默认值都改成
///      <c>modules/llm-vulkan/llama-server.exe</c>（Vulkan 由显卡驱动提供，无 CUDA 依赖）；
///   2. 本类：配置里的路径万一仍不存在（用户改坏了、只留了 cuda 模块、模块改名……），
///      就扫描 <c>modules\*\module.json</c>，按 vulkan → cuda → 其他 的顺序挑第一个
///      入口真实存在的 llm 模块。
///
/// 覆盖只在**内存**里生效，绝不回写 config.yaml（准则 9），并把"改用了哪个、为什么"
/// 原样写进日志，避免又一次"悄悄换了个后端却没人知道"。
/// </summary>
public static class LlmServerDiscovery
{
    private const string DefaultEntryName = "llama-server.exe";

    /// <summary>module.json 解析结果的缓存容量（真实场景下 modules\ 里只有几个模块）。</summary>
    private const int ManifestCacheCapacity = 32;

    /// <summary>
    /// module.json 解析结果缓存。
    ///
    /// 为什么要缓存：<see cref="TryFind"/> 会被 <c>AppHost</c>（启动）与
    /// <c>LlamaServerController.StartAsync</c>（每次启动尝试、含 OOM 回退的每一轮）调用，
    /// 每次都把每个 module.json 重新读盘 + 解析。缓存键带"大小 + 最后写入时间"，
    /// 文件被改过就自动失效，因此不会藏住用户新写的模块清单。
    /// </summary>
    private static readonly ConcurrentDictionary<string, ManifestCacheEntry> ManifestCache =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>优先级文字，日志与界面共用一份，避免两处说法不一致。</summary>
    public static string RankLabel(int rank) => rank switch
    {
        0 => "vulkan 模块",
        1 => "cuda 模块",
        _ => "其他 llm 模块",
    };

    /// <summary>
    /// 扫描 modules\ 找出第一个可用的 llm 后端入口。
    /// 找不到时返回 null，并通过 <paramref name="description"/> 说明"扫到了什么、为什么都不行"。
    ///
    /// 关于"每次调用都重新扫盘"：目录枚举本身只有毫秒级，且 modules\ 是用户可增删的目录
    /// （「拓展」页会导入模块）；把整个扫描结果缓存起来会把新装的后端藏住 ——
    /// 那种"装了却没用上"的问题比重复枚举难查得多。真正贵的是逐个 module.json 的
    /// 读取与 JSON 解析，那一层按文件指纹缓存（见 <see cref="ManifestCache"/>）。
    /// </summary>
    public static LlmServerCandidate? TryFind(out string description)
    {
        description = string.Empty;

        var found = new List<LlmServerCandidate>();
        var skipped = new List<string>();

        var modulesRoot = Path.Combine(AppPaths.Root, "modules");

        if (!Directory.Exists(modulesRoot))
        {
            description = $"没有 modules 目录（{modulesRoot}）";
            return null;
        }

        try
        {
            foreach (var directory in Directory.EnumerateDirectories(modulesRoot))
            {
                var name = Path.GetFileName(directory);

                /*
                 * 一个模块出问题（坏符号链接、权限不足、目录名怪）不能让整次发现失败：
                 * 以前整个循环包在一个 try 里，任何一处抛异常就直接返回 null，
                 * 已经找到的候选也一起丢掉 —— 明明有一个能用的后端，却报"没有可用后端"。
                 * 现在按目录隔离，坏的那个记进 skipped，其余照常评估。
                 */
                try
                {
                    var candidate = EvaluateDirectory(directory, name, skipped);
                    if (candidate is not null)
                    {
                        found.Add(candidate);
                    }
                }
                catch (Exception ex)
                {
                    skipped.Add($"modules/{name} 检查失败：{ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            // 枚举本身失败（modules\ 被并发删除等）：调用方会退回"报缺 exe"的原有分支。
            description = $"扫描 modules 失败：{ex.Message}";
            return null;
        }

        if (found.Count == 0)
        {
            description = skipped.Count == 0
                ? "modules 下没有任何 llm 模块目录"
                : string.Join("；", skipped);
            return null;
        }

        // 排序必须**确定**：同样优先级下按目录名（序数忽略大小写），
        // 否则多模块机器上"这次用 vulkan、下次用 cuda"会让排障变成猜谜。
        var ordered = found
            .OrderBy(c => c.Rank)
            .ThenBy(c => c.DirectoryName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var best = ordered[0];

        var parts = new List<string>
        {
            "候选 " + string.Join("、", ordered.Select(c => $"{c.RelativePath}（{c.RankLabel}）")),
        };

        if (skipped.Count > 0)
        {
            parts.Add("跳过 " + string.Join("；", skipped));
        }

        description = string.Join("；", parts);
        return best;
    }

    /// <summary>
    /// 评估单个模块目录：不是 llm、清单坏了、入口不存在时返回 null 并记下原因。
    /// 拆成方法是为了让"按目录隔离异常"只包一层 try，逻辑本身保持直读。
    /// </summary>
    private static LlmServerCandidate? EvaluateDirectory(string directory, string name, List<string> skipped)
    {
        var manifestPath = Path.Combine(directory, "module.json");
        if (!File.Exists(manifestPath))
        {
            // 与 ModuleRegistry 保持同一口径：没有 module.json 的目录不算模块，
            // 因为无法确定 kind / entry，猜错比不用更糟。
            skipped.Add($"modules/{name} 没有 module.json");
            return null;
        }

        var manifest = ReadManifest(manifestPath);
        if (manifest.Error is not null)
        {
            skipped.Add($"modules/{name}/module.json 解析失败：{manifest.Error}");
            return null;
        }

        var effectiveKind = string.IsNullOrWhiteSpace(manifest.Kind)
            ? InferKindFromName(name)
            : manifest.Kind!.Trim().ToLowerInvariant();

        if (!string.Equals(effectiveKind, "llm", StringComparison.OrdinalIgnoreCase))
        {
            skipped.Add($"modules/{name} 的 kind={effectiveKind ?? "未知"}，不是 llm");
            return null;
        }

        var entryName = string.IsNullOrWhiteSpace(manifest.Entry) ? DefaultEntryName : manifest.Entry!.Trim();
        if (!IsSafeRelativePath(entryName))
        {
            // entry 来自模块自带的 module.json（第三方可写）：绝对路径 / .. 一律拒绝。
            skipped.Add($"modules/{name} 的 entry \"{entryName}\" 越出模块目录");
            return null;
        }

        var normalizedEntry = entryName.Replace('\\', '/');
        var absolute = Path.Combine(directory, normalizedEntry.Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(absolute))
        {
            skipped.Add($"modules/{name}/{normalizedEntry} 不存在");
            return null;
        }

        return new LlmServerCandidate(
            $"modules/{name}/{normalizedEntry}",
            absolute,
            Rank(name, manifest.Requires),
            name);
    }

    /// <summary>
    /// 优先级：vulkan 先（干净机器上只要显卡驱动在就能跑，不依赖 CUDA 运行时），
    /// cuda 次之（NVIDIA 用户装了 CUDA 才可用），其余 llm 模块最后。
    /// 判据同时看 module.json 的 requires 与目录名 —— 老模块常常只写其中一处。
    /// </summary>
    private static int Rank(string directoryName, string? requires)
    {
        var haystack = ((requires ?? string.Empty) + " " + directoryName).ToLowerInvariant();

        if (haystack.Contains("vulkan"))
        {
            return 0;
        }

        if (haystack.Contains("cuda") || haystack.Contains("nvidia"))
        {
            return 1;
        }

        return 2;
    }

    private static string? InferKindFromName(string directoryName)
    {
        var name = directoryName.ToLowerInvariant();

        if (name.Contains("llm") || name.Contains("llama") || name.Contains("cuda") || name.Contains("vulkan"))
        {
            return "llm";
        }

        if (name.Contains("tts") || name.Contains("voice") || name.Contains("piper") || name.Contains("sovits"))
        {
            return "tts";
        }

        if (name.Contains("asr") || name.Contains("whisper") || name.Contains("stt"))
        {
            return "asr";
        }

        return null;
    }

    /// <summary>
    /// 读 module.json 里需要的三个字段（带文件指纹缓存）。
    ///
    /// 用 JsonDocument 而不是强类型：Core 层不该为了三个字段多一个 DTO，
    /// 且缺字段 / 多字段 / 带注释的 module.json 都要能容忍。
    /// </summary>
    private static ManifestParse ReadManifest(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists)
            {
                return new ManifestParse(null, null, null, "文件不存在（可能刚被删除）");
            }

            var length = info.Length;
            var ticks = info.LastWriteTimeUtc.Ticks;

            if (ManifestCache.TryGetValue(path, out var cached)
                && cached.Length == length
                && cached.LastWriteTicks == ticks)
            {
                return cached.Parsed;
            }

            var parsed = ParseManifest(path);

            // 条目数超限就整体清空：真实场景只有几个模块，不值得维护 LRU。
            if (ManifestCache.Count >= ManifestCacheCapacity && !ManifestCache.ContainsKey(path))
            {
                ManifestCache.Clear();
            }

            ManifestCache[path] = new ManifestCacheEntry(length, ticks, parsed);
            return parsed;
        }
        catch (Exception ex)
        {
            return new ManifestParse(null, null, null, $"读不到文件信息：{ex.Message}");
        }
    }

    private static ManifestParse ParseManifest(string path)
    {
        try
        {
            var json = File.ReadAllText(path, Encoding.UTF8);

            var options = new JsonDocumentOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip,
            };

            using var document = JsonDocument.Parse(json, options);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return new ManifestParse(null, null, null, "根节点不是 JSON 对象");
            }

            string? kind = null;
            string? entry = null;
            string? requires = null;

            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (property.Value.ValueKind != JsonValueKind.String)
                {
                    continue;
                }

                var value = property.Value.GetString();

                if (string.Equals(property.Name, "kind", StringComparison.OrdinalIgnoreCase))
                {
                    kind = value;
                }
                else if (string.Equals(property.Name, "entry", StringComparison.OrdinalIgnoreCase))
                {
                    entry = value;
                }
                else if (string.Equals(property.Name, "requires", StringComparison.OrdinalIgnoreCase))
                {
                    requires = value;
                }
            }

            return new ManifestParse(kind, entry, requires, null);
        }
        catch (Exception ex)
        {
            // 坏 JSON / 编码错误：把原因带回给调用方，日志里就能看到"到底哪里不对"，
            // 而不是一句没有上下文的"解析失败"。
            return new ManifestParse(null, null, null, ex.Message);
        }
    }

    private static bool IsSafeRelativePath(string value)
    {
        var normalized = value.Trim().Replace('\\', '/');

        if (normalized.Length == 0 ||
            Path.IsPathRooted(value) ||
            normalized.StartsWith('/') ||
            normalized.Contains(':'))
        {
            return false;
        }

        foreach (var segment in normalized.Split('/'))
        {
            if (segment.Length == 0 || segment is "." or "..")
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>module.json 的解析结果（<paramref name="Error"/> 非空表示读/解析失败）。</summary>
    private sealed record ManifestParse(string? Kind, string? Entry, string? Requires, string? Error);

    private sealed record ManifestCacheEntry(long Length, long LastWriteTicks, ManifestParse Parsed);
}
