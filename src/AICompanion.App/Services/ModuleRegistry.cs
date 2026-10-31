using System.IO;
using System.Text;
using System.Text.Json;
using AICompanion.Core.Abstractions;
using AICompanion.Core.Configuration;
using AICompanion.Core.Interop;

namespace AICompanion.App.Services;

/// <summary>模块类别。约定与 module.json 里的 kind 一致。</summary>
public enum ModuleKind
{
    Llm,
    Tts,
    Asr,
}

/// <summary>模块可用性识别结果。界面只显示这些结论，不显示任何性能数字。</summary>
public enum ModuleAvailability
{
    Available,
    MissingEntry,
    MissingModel,
    MissingNvidia,
    MissingVulkan,
    MissingMicrophone,
}

/// <summary>
/// module.json 的强类型映射：
/// <code>
/// { "name": "llm-cuda", "kind": "llm", "display": "CUDA 版对话模块",
///   "requires": "nvidia", "entry": "llama-server.exe" }
/// </code>
/// 所有字段都按"可缺失"处理：缺 kind 时按目录名推断，缺 requires 视为 none，
/// 缺 display 时回退到目录名，缺 entry 时取目录里第一个 *.exe。
/// </summary>
public sealed class ModuleManifest
{
    public string? Name { get; set; }

    public string? Kind { get; set; }

    public string? Display { get; set; }

    public string? Requires { get; set; }

    public string? Entry { get; set; }
}

/// <summary>一个已识别模块的只读快照。</summary>
public sealed class ModuleInfo
{
    public required string DirectoryName { get; init; }

    public required string DirectoryPath { get; init; }

    public required ModuleManifest Manifest { get; init; }

    public required ModuleKind Kind { get; init; }

    public required long SizeBytes { get; init; }

    public required ModuleAvailability Availability { get; init; }

    public string? EntryPath { get; init; }

    public string DisplayName => string.IsNullOrWhiteSpace(Manifest.Display)
        ? DirectoryName
        : Manifest.Display!.Trim();

    public string RequiresText => string.IsNullOrWhiteSpace(Manifest.Requires)
        ? "none"
        : Manifest.Requires!.Trim().ToLowerInvariant();

    public string SizeText
    {
        get
        {
            var mb = SizeBytes / 1024.0 / 1024.0;
            return mb >= 10 ? $"{mb:F0} MB" : $"{mb:F1} MB";
        }
    }

    public bool IsUsable => Availability == ModuleAvailability.Available;

    /// <summary>形如「已识别：可用」。界面文案固定成这个前缀，便于一眼扫读。</summary>
    public string StatusText => Availability switch
    {
        ModuleAvailability.Available => "已识别：可用",
        ModuleAvailability.MissingEntry => "已识别：缺 exe",
        ModuleAvailability.MissingModel => "已识别：缺模型",
        ModuleAvailability.MissingNvidia => "已识别：未检测到 NVIDIA 卡",
        ModuleAvailability.MissingVulkan => "已识别：未检测到 Vulkan 设备",
        ModuleAvailability.MissingMicrophone => "已识别：未检测到麦克风",
        _ => "已识别：未知",
    };

    public string Summary => $"目录 {DirectoryName} · {SizeText}";
}

/// <summary>运行环境识别结果：只用于判断模块能不能用，与性能无关。</summary>
public sealed class ModuleEnvironment
{
    public string? NvidiaGpu { get; init; }

    public string? VulkanDevice { get; init; }

    public string? Microphone { get; init; }

    public int CpuCores { get; init; } = Environment.ProcessorCount;

    public bool HasNvidia => !string.IsNullOrWhiteSpace(NvidiaGpu);

    public bool HasVulkan => !string.IsNullOrWhiteSpace(VulkanDevice);

    public bool HasMicrophone => !string.IsNullOrWhiteSpace(Microphone);

    /// <summary>把结果拼成界面上的一行：设备名与核数，不含任何性能指标。</summary>
    public string GpuText => HasNvidia ? NvidiaGpu! : "未检测到 NVIDIA 卡";

    public string VulkanText => HasVulkan ? VulkanDevice! : "未检测到 Vulkan 设备";

    public string CpuText => $"{CpuCores} 核";

    public string MicrophoneText => HasMicrophone ? Microphone! : "未检测到麦克风";
}

/// <summary>导入结果。</summary>
public sealed record ModuleImportResult(bool Success, string Message, ModuleInfo? Module);

/// <summary>
/// 模块目录（modules/&lt;名称&gt;/module.json）的扫描、可用性识别、导入与启用状态。
///
/// 这里刻意不做任何"性能评分"：模块是否可用只取决于三件事 ——
/// 运行环境（NVIDIA / Vulkan / 麦克风）、entry 指向的可执行文件、模型文件是否存在。
/// </summary>
public static class ModuleRegistry
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        WriteIndented = true,
    };

    public static string ModulesRoot => Path.Combine(AppPaths.Root, "modules");

    public static string KindKey(ModuleKind kind) => kind switch
    {
        ModuleKind.Llm => "llm",
        ModuleKind.Tts => "tts",
        _ => "asr",
    };

    public static string KindLabel(ModuleKind kind) => kind switch
    {
        ModuleKind.Llm => "对话",
        ModuleKind.Tts => "语音合成",
        _ => "语音识别",
    };

    /// <summary>扫描某一类模块；目录不存在时返回空列表（界面显示"未发现模块"）。</summary>
    public static IReadOnlyList<ModuleInfo> Scan(ModuleKind kind, ModuleEnvironment environment)
    {
        var result = new List<ModuleInfo>();

        try
        {
            if (!Directory.Exists(ModulesRoot))
            {
                return result;
            }

            foreach (var directory in Directory.EnumerateDirectories(ModulesRoot))
            {
                var manifestPath = Path.Combine(directory, "module.json");
                if (!File.Exists(manifestPath))
                {
                    // 没有 module.json 的目录不当作模块：无法确定 kind / entry，猜错比不显示更糟。
                    continue;
                }

                var manifest = ReadManifest(manifestPath);
                if (manifest is null || InferKind(manifest, directory) != kind)
                {
                    continue;
                }

                result.Add(BuildInfo(kind, directory, manifest, environment));
            }
        }
        catch
        {
            // 目录读取失败按"没有模块"处理，界面会给出去「拓展」添加的提示。
        }

        return result.OrderBy(m => m.DirectoryName, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>
    /// 取某一类"当前启用中"的模块：优先 appearance.json 里保存的目录名；
    /// 没保存过就取第一个可用模块（开箱即用），都不可用则取第一个。
    /// </summary>
    public static ModuleInfo? EffectiveModule(ModuleKind kind, IReadOnlyList<ModuleInfo> modules)
    {
        if (modules.Count == 0)
        {
            return null;
        }

        var stored = AppearanceStore.LoadEnabledModule(KindKey(kind));
        if (!string.IsNullOrWhiteSpace(stored))
        {
            var match = modules.FirstOrDefault(m =>
                string.Equals(m.DirectoryName, stored, StringComparison.OrdinalIgnoreCase));

            if (match is not null)
            {
                return match;
            }
        }

        return modules.FirstOrDefault(m => m.IsUsable) ?? modules[0];
    }

    /// <summary>
    /// 给 AppHost 用：按 appearance.json 里保存的启用模块解析出 entry 的配置相对路径。
    /// 没有保存过（或模块已不存在）时返回 null，表示继续用 config.yaml。
    /// </summary>
    public static string? ResolveEnabledEntryRelative(ModuleKind kind)
    {
        var stored = AppearanceStore.LoadEnabledModule(KindKey(kind));
        if (string.IsNullOrWhiteSpace(stored))
        {
            return null;
        }

        var directory = Path.Combine(ModulesRoot, stored);
        var manifestPath = Path.Combine(directory, "module.json");
        if (!File.Exists(manifestPath))
        {
            return null;
        }

        var manifest = ReadManifest(manifestPath);
        if (manifest is null)
        {
            return null;
        }

        var entry = ResolveEntryName(directory, manifest);
        if (entry is null)
        {
            return null;
        }

        return $"modules/{stored}/{entry}";
    }

    /// <summary>只读：列出某类模块（用于「后端选择」下拉）。</summary>
    public static IReadOnlyList<ModuleInfo> EnabledCandidates(ModuleKind kind, ModuleEnvironment environment)
        => Scan(kind, environment);

    // ------------------------------------------------------------------ 环境识别

    /// <summary>
    /// 识别 GPU / Vulkan / 麦克风 / CPU。
    /// 全部走异步子进程 + 超时，失败只写日志，不抛异常（设置窗口不能因为探测失败打不开）。
    /// </summary>
    public static async Task<ModuleEnvironment> ProbeAsync(IAppLogger logger, CancellationToken cancellationToken = default)
    {
        var nvidia = await ProbeNvidiaAsync(logger, cancellationToken).ConfigureAwait(false);
        var vulkan = await ProbeVulkanAsync(logger, cancellationToken).ConfigureAwait(false);
        var microphone = ProbeMicrophone(logger);

        return new ModuleEnvironment
        {
            NvidiaGpu = nvidia,
            VulkanDevice = vulkan,
            Microphone = microphone,
            CpuCores = Environment.ProcessorCount,
        };
    }

    private static async Task<string?> ProbeNvidiaAsync(IAppLogger logger, CancellationToken cancellationToken)
    {
        try
        {
            // 只查名字：nvidia-smi 的显存数字属于性能展示，界面上不出现。
            var result = await ProcessRunner.RunAsync(
                "nvidia-smi",
                "--query-gpu=name --format=csv,noheader",
                TimeSpan.FromSeconds(6),
                cancellationToken).ConfigureAwait(false);

            if (!result.Started || result.ExitCode != 0)
            {
                return null;
            }

            var name = result.StandardOutput
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .FirstOrDefault();

            return string.IsNullOrWhiteSpace(name) ? null : name;
        }
        catch (Exception ex)
        {
            logger.Debug($"识别 NVIDIA 显卡失败：{ex.Message}");
            return null;
        }
    }

    private static async Task<string?> ProbeVulkanAsync(IAppLogger logger, CancellationToken cancellationToken)
    {
        // llama.cpp 的 --list-devices 会枚举 Vulkan 设备（"Vulkan0: 设备名 (显存)"），
        // 比解析 vulkaninfo 的层级输出稳。优先用 modules 里现成的 llama-server。
        foreach (var candidate in FindLlamaServerCandidates())
        {
            try
            {
                var result = await ProcessRunner.RunAsync(
                    candidate,
                    "--list-devices",
                    TimeSpan.FromSeconds(8),
                    cancellationToken).ConfigureAwait(false);

                var devices = ParseVulkanDevices(result.StandardOutput + "\n" + result.StandardError);
                if (devices.Count > 0)
                {
                    return string.Join("、", devices);
                }
            }
            catch (Exception ex)
            {
                logger.Debug($"Vulkan 设备枚举失败（{candidate}）：{ex.Message}");
            }
        }

        // 兜底：vulkaninfo --summary 的 deviceName。
        try
        {
            var result = await ProcessRunner.RunAsync(
                "vulkaninfo",
                "--summary",
                TimeSpan.FromSeconds(8),
                cancellationToken).ConfigureAwait(false);

            var devices = ParseVulkanInfo(result.StandardOutput);
            if (devices.Count > 0)
            {
                return string.Join("、", devices);
            }
        }
        catch (Exception ex)
        {
            logger.Debug($"vulkaninfo 识别失败：{ex.Message}");
        }

        return null;
    }

    private static IEnumerable<string> FindLlamaServerCandidates()
    {
        var found = new List<string>();

        try
        {
            if (!Directory.Exists(ModulesRoot))
            {
                return found;
            }

            foreach (var directory in Directory.EnumerateDirectories(ModulesRoot))
            {
                var exe = Path.Combine(directory, "llama-server.exe");
                if (File.Exists(exe))
                {
                    found.Add(exe);
                }
            }
        }
        catch
        {
            // 忽略。
        }

        return found;
    }

    private static List<string> ParseVulkanDevices(string output)
    {
        var devices = new List<string>();

        foreach (var raw in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var line = raw.Trim();
            var index = line.IndexOf("Vulkan", StringComparison.OrdinalIgnoreCase);
            if (index < 0)
            {
                continue;
            }

            var rest = line[(index + "Vulkan".Length)..].TrimStart();
            var colon = rest.IndexOf(':');
            if (colon < 0)
            {
                continue;
            }

            var name = rest[(colon + 1)..].Trim();

            // "AMD Radeon 780M Graphics (8069 MiB, ...)" → 只留设备名。
            var paren = name.IndexOf('(');
            if (paren > 0)
            {
                name = name[..paren].Trim();
            }

            if (name.Length > 0)
            {
                devices.Add(name);
            }
        }

        return devices;
    }

    private static List<string> ParseVulkanInfo(string output)
    {
        var devices = new List<string>();

        foreach (var raw in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var line = raw.Trim();
            if (!line.StartsWith("deviceName", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var equals = line.IndexOf('=');
            var name = equals >= 0 ? line[(equals + 1)..].Trim() : string.Empty;
            if (name.Length > 0)
            {
                devices.Add(name);
            }
        }

        return devices;
    }

    private static string? ProbeMicrophone(IAppLogger logger)
    {
        try
        {
            // NAudio 的 WaveInEvent 与 ASR 采集用的是同一套设备枚举，结论最贴近实际。
            var count = NAudio.Wave.WaveInEvent.DeviceCount;
            if (count <= 0)
            {
                return null;
            }

            var names = new List<string>();

            for (var i = 0; i < count; i++)
            {
                try
                {
                    var capabilities = NAudio.Wave.WaveInEvent.GetCapabilities(i);
                    if (!string.IsNullOrWhiteSpace(capabilities.ProductName))
                    {
                        names.Add(capabilities.ProductName.Trim());
                    }
                }
                catch
                {
                    // 单个设备读不到就跳过。
                }
            }

            return names.Count == 0 ? null : string.Join("、", names);
        }
        catch (Exception ex)
        {
            logger.Debug($"识别麦克风设备失败：{ex.Message}");
            return null;
        }
    }

    // ------------------------------------------------------------------ 导入

    /// <summary>
    /// 把选中的目录整体复制到 modules/&lt;目录名&gt;，并校验 module.json 存在且 kind 匹配。
    /// 目标目录已存在时拒绝覆盖 —— 覆盖会悄悄丢掉用户原有的模块文件。
    /// </summary>
    public static ModuleImportResult Import(
        string sourceDirectory,
        ModuleKind expectedKind,
        ModuleEnvironment environment,
        IAppLogger logger)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(sourceDirectory) || !Directory.Exists(sourceDirectory))
            {
                return new ModuleImportResult(false, "选中的目录不存在。", null);
            }

            var trimmed = sourceDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var directoryName = Path.GetFileName(trimmed);

            if (string.IsNullOrWhiteSpace(directoryName))
            {
                return new ModuleImportResult(false, "无法从路径推断模块目录名。", null);
            }

            var manifestPath = Path.Combine(trimmed, "module.json");
            if (!File.Exists(manifestPath))
            {
                return new ModuleImportResult(false, "选中的目录里没有 module.json。", null);
            }

            var manifest = ReadManifest(manifestPath);
            if (manifest is null)
            {
                return new ModuleImportResult(false, "module.json 解析失败，请检查格式。", null);
            }

            var kind = InferKind(manifest, trimmed);
            if (kind != expectedKind)
            {
                return new ModuleImportResult(
                    false,
                    $"module.json 的 kind 不是 {KindKey(expectedKind)}，不能加到「{KindLabel(expectedKind)}模块」。",
                    null);
            }

            var destination = Path.Combine(ModulesRoot, directoryName);

            if (string.Equals(
                    Path.GetFullPath(destination).TrimEnd(Path.DirectorySeparatorChar),
                    Path.GetFullPath(trimmed).TrimEnd(Path.DirectorySeparatorChar),
                    StringComparison.OrdinalIgnoreCase))
            {
                return new ModuleImportResult(false, "选中的目录已经在 modules 目录里了。", null);
            }

            if (Directory.Exists(destination))
            {
                return new ModuleImportResult(false, $"modules/{directoryName} 已存在，请先手动删除旧目录。", null);
            }

            Directory.CreateDirectory(ModulesRoot);
            CopyDirectory(trimmed, destination);

            var modules = Scan(expectedKind, environment);
            var imported = modules.FirstOrDefault(m =>
                string.Equals(m.DirectoryName, directoryName, StringComparison.OrdinalIgnoreCase));

            if (imported is null)
            {
                return new ModuleImportResult(
                    false,
                    "复制完成，但重新扫描时没有识别到该模块（检查 module.json 的 kind）。",
                    null);
            }

            logger.Info(
                $"已导入{KindLabel(expectedKind)}模块 modules/{directoryName}（{imported.SizeText}，{imported.StatusText}）。");

            return new ModuleImportResult(
                true,
                $"已添加模块「{imported.DisplayName}」：{imported.StatusText}。",
                imported);
        }
        catch (Exception ex)
        {
            logger.Warn($"导入模块失败：{ex.Message}");
            return new ModuleImportResult(false, $"导入失败：{ex.Message}", null);
        }
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);

        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.TopDirectoryOnly))
        {
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), overwrite: false);
        }

        foreach (var directory in Directory.EnumerateDirectories(source, "*", SearchOption.TopDirectoryOnly))
        {
            CopyDirectory(directory, Path.Combine(destination, Path.GetFileName(directory)));
        }
    }

    // ------------------------------------------------------------------ 解析细节

    public static ModuleManifest? ReadManifest(string path)
    {
        try
        {
            var json = File.ReadAllText(path, Encoding.UTF8);
            return JsonSerializer.Deserialize<ModuleManifest>(json, JsonOptions);
        }
        catch
        {
            return null;
        }
    }

    public static ModuleKind? ParseKind(string? kind) => kind?.Trim().ToLowerInvariant() switch
    {
        "llm" => ModuleKind.Llm,
        "tts" => ModuleKind.Tts,
        "asr" => ModuleKind.Asr,
        _ => null,
    };

    /// <summary>kind 缺失时按目录名前缀推断（llm-* / tts-* / asr-* 等）。</summary>
    public static ModuleKind? InferKind(ModuleManifest manifest, string directory)
    {
        var parsed = ParseKind(manifest.Kind);
        if (parsed is not null)
        {
            return parsed;
        }

        var name = Path.GetFileName(directory).ToLowerInvariant();

        if (name.Contains("tts") || name.Contains("voice") || name.Contains("piper") || name.Contains("sovits"))
        {
            return ModuleKind.Tts;
        }

        if (name.Contains("asr") || name.Contains("whisper") || name.Contains("stt"))
        {
            return ModuleKind.Asr;
        }

        if (name.Contains("llm") || name.Contains("llama") || name.Contains("cuda") || name.Contains("vulkan"))
        {
            return ModuleKind.Llm;
        }

        return null;
    }

    private static ModuleInfo BuildInfo(
        ModuleKind kind,
        string directory,
        ModuleManifest manifest,
        ModuleEnvironment environment)
    {
        var entryPath = ResolveEntry(directory, manifest);
        var availability = CheckAvailability(kind, entryPath, manifest, environment);

        return new ModuleInfo
        {
            DirectoryName = Path.GetFileName(directory.TrimEnd(Path.DirectorySeparatorChar)),
            DirectoryPath = directory,
            Manifest = manifest,
            Kind = kind,
            SizeBytes = ComputeSize(directory),
            Availability = availability,
            EntryPath = entryPath,
        };
    }

    private static string? ResolveEntryName(string directory, ModuleManifest manifest)
    {
        if (!string.IsNullOrWhiteSpace(manifest.Entry))
        {
            return manifest.Entry!.Trim().Replace('/', Path.DirectorySeparatorChar);
        }

        try
        {
            var exe = Directory.EnumerateFiles(directory, "*.exe", SearchOption.TopDirectoryOnly).FirstOrDefault();
            return exe is null ? null : Path.GetFileName(exe);
        }
        catch
        {
            return null;
        }
    }

    private static string? ResolveEntry(string directory, ModuleManifest manifest)
    {
        var entryName = ResolveEntryName(directory, manifest);
        if (entryName is null)
        {
            return null;
        }

        var full = Path.Combine(directory, entryName);
        return File.Exists(full) ? full : null;
    }

    private static ModuleAvailability CheckAvailability(
        ModuleKind kind,
        string? entryPath,
        ModuleManifest manifest,
        ModuleEnvironment environment)
    {
        var requires = (manifest.Requires ?? "none").Trim().ToLowerInvariant();

        switch (requires)
        {
            case "nvidia":
                if (!environment.HasNvidia)
                {
                    return ModuleAvailability.MissingNvidia;
                }

                break;
            case "vulkan":
                if (!environment.HasVulkan)
                {
                    return ModuleAvailability.MissingVulkan;
                }

                break;
            case "mic":
                if (kind == ModuleKind.Asr && !environment.HasMicrophone)
                {
                    return ModuleAvailability.MissingMicrophone;
                }

                break;
        }

        if (entryPath is null)
        {
            return ModuleAvailability.MissingEntry;
        }

        return ModelPresent(kind) ? ModuleAvailability.Available : ModuleAvailability.MissingModel;
    }

    /// <summary>模型是否存在：只看目录里有没有模型文件，不读内容、不评估性能。</summary>
    private static bool ModelPresent(ModuleKind kind)
    {
        try
        {
            var modelsRoot = Path.Combine(AppPaths.Root, "models");

            switch (kind)
            {
                case ModuleKind.Llm:
                    return HasAny(modelsRoot, "*.gguf", "llm");

                case ModuleKind.Tts:
                    return HasAny(modelsRoot, "*.onnx", "piper") || HasAny(modelsRoot, "*.onnx", "tts") ||
                           HasAny(modelsRoot, "*.pth", "tts") || HasAny(modelsRoot, "*.ckpt", "tts");

                default:
                    return HasAny(modelsRoot, "*.bin", "asr") || HasAny(modelsRoot, "*.gguf", "asr");
            }
        }
        catch
        {
            return false;
        }
    }

    private static bool HasAny(string modelsRoot, string pattern, string subdirectory)
    {
        var directory = Path.Combine(modelsRoot, subdirectory);
        return Directory.Exists(directory) &&
               Directory.EnumerateFiles(directory, pattern, SearchOption.AllDirectories).Any();
    }

    private static long ComputeSize(string directory)
    {
        long total = 0;

        try
        {
            foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
            {
                try
                {
                    total += new FileInfo(file).Length;
                }
                catch
                {
                    // 单个文件读不到不影响整体统计。
                }
            }
        }
        catch
        {
            // 忽略。
        }

        return total;
    }
}
