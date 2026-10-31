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
    /// </summary>
    public static LlmServerCandidate? TryFind(out string description)
    {
        description = string.Empty;

        var found = new List<LlmServerCandidate>();
        var skipped = new List<string>();

        try
        {
            var modulesRoot = Path.Combine(AppPaths.Root, "modules");

            if (!Directory.Exists(modulesRoot))
            {
                description = $"没有 modules 目录（{modulesRoot}）";
                return null;
            }

            foreach (var directory in Directory.EnumerateDirectories(modulesRoot))
            {
                var name = Path.GetFileName(directory);

                var manifestPath = Path.Combine(directory, "module.json");
                if (!File.Exists(manifestPath))
                {
                    // 与 ModuleRegistry 保持同一口径：没有 module.json 的目录不算模块，
                    // 因为无法确定 kind / entry，猜错比不用更糟。
                    skipped.Add($"modules/{name} 没有 module.json");
                    continue;
                }

                if (!TryReadManifest(manifestPath, out var kind, out var entry, out var requires))
                {
                    skipped.Add($"modules/{name}/module.json 解析失败");
                    continue;
                }

                var effectiveKind = string.IsNullOrWhiteSpace(kind) ? InferKindFromName(name) : kind!.Trim().ToLowerInvariant();
                if (!string.Equals(effectiveKind, "llm", StringComparison.OrdinalIgnoreCase))
                {
                    skipped.Add($"modules/{name} 的 kind={effectiveKind ?? "未知"}，不是 llm");
                    continue;
                }

                var entryName = string.IsNullOrWhiteSpace(entry) ? DefaultEntryName : entry!.Trim();
                if (!IsSafeRelativePath(entryName))
                {
                    // entry 来自模块自带的 module.json（第三方可写）：绝对路径 / .. 一律拒绝。
                    skipped.Add($"modules/{name} 的 entry \"{entryName}\" 越出模块目录");
                    continue;
                }

                var normalizedEntry = entryName.Replace('\\', '/');
                var absolute = Path.Combine(directory, normalizedEntry.Replace('/', Path.DirectorySeparatorChar));
                if (!File.Exists(absolute))
                {
                    skipped.Add($"modules/{name}/{normalizedEntry} 不存在");
                    continue;
                }

                found.Add(new LlmServerCandidate(
                    $"modules/{name}/{normalizedEntry}",
                    absolute,
                    Rank(name, requires),
                    name));
            }
        }
        catch (Exception ex)
        {
            // 扫描失败绝不能影响启动：调用方会退回"报缺 exe"的原有分支。
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
    /// 只读 module.json 里需要的三个字段。
    /// 用 JsonDocument 而不是强类型：Core 层不该为了三个字段多一个 DTO，
    /// 且缺字段 / 多字段 / 带注释的 module.json 都要能容忍。
    /// </summary>
    private static bool TryReadManifest(string path, out string? kind, out string? entry, out string? requires)
    {
        kind = null;
        entry = null;
        requires = null;

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
                return false;
            }

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

            return true;
        }
        catch
        {
            return false;
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
}
