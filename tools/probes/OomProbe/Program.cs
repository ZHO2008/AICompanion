using AICompanion.Core.Abstractions;
using AICompanion.Core.Configuration;
using AICompanion.Core.Gpu;
using AICompanion.Core.Llm;
using AICompanion.Core.Logging;

namespace OomProbe;

internal sealed class ConsoleBubbleNotifier : IBubbleNotifier
{
    public List<string> Shown { get; } = new();

    public void Show(string key, string message, BubbleLevel level = BubbleLevel.Info, TimeSpan? duration = null)
    {
        Shown.Add($"[{level}] {message}");
        Console.WriteLine($"    [气泡/{level}] {message}");
    }

    public void Clear(string key)
    {
    }

    public void ClearAll()
    {
    }

    public void SetStatus(string key, string value)
    {
    }

    public void ClearStatus(string key)
    {
    }
}

/// <summary>
/// 验证手册 §3.3 的 OOM 回退循环。
///
/// 手法：把 <c>llm.server_executable_path</c> 指向 <c>FakeLlamaServer.exe</c>，
/// 它前 N 次启动会向 stderr 打印 "CUDA out of memory" 后退出，
/// 之后才真的起 HTTP 服务。这样就能在不损坏真实环境的前提下
/// 精确地走一遍"检测 OOM → 减 4 层 → 等待 → 重启 → 再检测"。
///
/// 两个场景：
///   A. 前 2 次 OOM，第 3 次成功 → 应回退两次（28→24→20）后启动成功；
///   B. 全部 OOM → 重试耗尽后应报 OutOfMemory 并给出"显存不足"气泡。
/// </summary>
internal static class Program
{
    private static int _failures;

    private static async Task<int> Main()
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;

        var root = Environment.GetEnvironmentVariable("AICOMPANION_ROOT")
                   ?? Directory.GetCurrentDirectory();
        var fakeDir = Path.Combine(root, "tools", "probes", "FakeLlamaServer", "bin", "Release", "net8.0");
        var fakeExe = Path.Combine(fakeDir, "FakeLlamaServer.exe");
        var modelPath = Path.Combine(root, "models", "llm", "qwen2.5-1.5b-instruct-q4_k_m.gguf");

        Console.WriteLine("============================================================");
        Console.WriteLine("  OOM 回退循环验证（手册 §3.3）");
        Console.WriteLine("============================================================");

        if (!File.Exists(fakeExe))
        {
            Console.WriteLine($"[FAIL] 未找到假服务：{fakeExe}");
            return 1;
        }

        if (!File.Exists(modelPath))
        {
            Console.WriteLine($"[FAIL] 未找到模型：{modelPath}");
            return 1;
        }

        Console.WriteLine($"假服务：{fakeExe}");
        Console.WriteLine();

        // ---------------- 场景 A：回退两次后成功 ----------------
        Console.WriteLine("--- 场景 A：前 2 次 OOM，第 3 次成功（应回退 28 → 24 → 20）---");
        var a = await RunScenarioAsync(fakeDir, fakeExe, modelPath, oomCount: 2);

        Check("场景 A 最终启动成功", a.Result.Success, a.Result.Outcome.ToString());
        Check("场景 A 最终 ngl 为 20（回退两次各 -4）", a.Result.Ngl == 20, $"ngl={a.Result.Ngl}");
        Check("场景 A 的 ngl 序列为 28,24,20", a.NglSequence == "28,24,20", a.NglSequence);
        Check("场景 A 只启动了 3 次", a.Attempts == 3, $"{a.Attempts} 次");
        Check("场景 A 告知用户已降层重试",
            a.Bubbles.Shown.Exists(x => x.Contains("已把 GPU 推理层数")),
            string.Join(" | ", a.Bubbles.Shown));
        Check("场景 A 不误报「LLM 服务已离线」",
            !a.Bubbles.Shown.Exists(x => x.Contains("已离线")),
            string.Join(" | ", a.Bubbles.Shown));
        Check("场景 A 运行模式反映真实 ngl（20 而非计划值 28）",
            a.Mode.Ngl == 20,
            $"mode={a.Mode.Mode} ngl={a.Mode.Ngl} L={a.Mode.TotalLayers} 说明={a.Mode.Explanation}");

        // ---------------- 场景 B：重试耗尽 ----------------
        Console.WriteLine();
        Console.WriteLine("--- 场景 B：每次都 OOM（重试 2 次后应放弃）---");
        var b = await RunScenarioAsync(fakeDir, fakeExe, modelPath, oomCount: 99);

        Check("场景 B 判定为显存不足", b.Result.Outcome == LlamaStartOutcome.OutOfMemory, b.Result.Outcome.ToString());
        Check("场景 B 命中重试上限（只启动 3 次 = 1 次 + 2 次重试）", b.Attempts == 3, $"{b.Attempts} 次");
        Check("场景 B 提示用户关闭其他程序",
            b.Bubbles.Shown.Exists(x => x.Contains("显存不足") && x.Contains("关闭其他程序")),
            string.Join(" | ", b.Bubbles.Shown));

        // ---------------- 场景 C：显存不足以承载任何一层 ----------------
        Console.WriteLine();
        Console.WriteLine("--- 场景 C：显存不足以承载任何一层（应 ngl=0 并切 CPU，决策 Q6）---");
        await RunInsufficientMemoryScenarioAsync(modelPath);

        Console.WriteLine();
        Console.WriteLine("============================================================");
        if (_failures == 0)
        {
            Console.WriteLine("  全部通过");
            return 0;
        }

        Console.WriteLine($"  {_failures} 项失败");
        return 1;
    }

    private sealed record ScenarioResult(
        LlamaStartResult Result,
        ConsoleBubbleNotifier Bubbles,
        string NglSequence,
        int Attempts,
        RuntimeModeSnapshot Mode);

    private static async Task<ScenarioResult> RunScenarioAsync(
        string fakeDir,
        string fakeExe,
        string modelPath,
        int oomCount)
    {
        var attemptsFile = Path.Combine(fakeDir, "fake_attempts.txt");
        var oomFile = Path.Combine(fakeDir, "fake_oom_count.txt");
        var nglLogFile = Path.Combine(fakeDir, "fake_ngl_log.txt");

        // 重置假服务状态
        TryDelete(attemptsFile);
        TryDelete(nglLogFile);
        File.WriteAllText(oomFile, oomCount.ToString());

        var bubbles = new ConsoleBubbleNotifier();
        var logger = FileLogger.CreateRoot(Path.Combine(Environment.CurrentDirectory, "logs", "oomprobe"), LogLevel.Info);

        // 构造一份专用配置：把 gpu 参数调成"必然算出 ngl = 层数上限 28"，
        // 这样回退步长就是纯粹的 28 → 24 → 20，便于断言。
        var config = new AppConfig();
        config.Llm.ModelPath = modelPath;
        config.Llm.ServerExecutablePath = fakeExe;
        config.Llm.Host = "127.0.0.1";
        config.Llm.Port = 8099;
        config.Llm.LayerCount = 28;
        config.Llm.ContextLength = 2048;
        config.Llm.StartupTimeoutSeconds = 8;

        config.Gpu.ReservedMb = 0;
        config.Gpu.KvCacheReservedMb = 0;
        config.Gpu.PerLayerMb = 100;
        config.Gpu.SafetyFactor = 1.0;
        config.Gpu.MaxOomRetries = 2;
        config.Gpu.OomNglStep = 4;
        config.Gpu.OomRetryDelaySeconds = 1;

        var mode = new RuntimeModeState();
        var smi = new NvidiaSmi(logger);
        var gpu = new GPUMemoryManager(config, smi, mode, logger, bubbles);
        var llama = new LlamaServerController(config, gpu, mode, logger, bubbles);

        var result = await llama.StartAsync();

        Console.WriteLine($"  结果     : {result.Outcome}");
        Console.WriteLine($"  最终 ngl : {result.Ngl}");
        Console.WriteLine($"  运行模式 : {mode.Describe()}");
        Console.WriteLine($"  消息     : {Truncate(result.Message, 100)}");

        // 读回假服务记录的 ngl 序列
        var sequence = string.Empty;
        var attempts = 0;
        try
        {
            if (File.Exists(nglLogFile))
            {
                var lines = File.ReadAllLines(nglLogFile)
                    .Where(l => l.Contains("ngl="))
                    .Select(l => l.Split("ngl=")[1].Trim())
                    .ToList();

                sequence = string.Join(",", lines);
                attempts = lines.Count;
            }
        }
        catch
        {
            // 忽略
        }

        Console.WriteLine($"  ngl 序列 : {sequence}（共 {attempts} 次启动）");

        // 收尾：如果假服务还在跑，把它停掉
        var modeSnapshot = mode.Current;
        await llama.StopAsync();
        await llama.DisposeAsync();

        return new ScenarioResult(result, bubbles, sequence, attempts, modeSnapshot);
    }

    /// <summary>
    /// 场景 C：验证决策 Q6 —— <c>保守可用 ≤ 0</c> 或算出的层数 &lt; 1 时，
    /// 直接 <c>ngl=0</c> 并切到 CPU 模式，而不是按手册字面的 max(1, ...) 强行上 1 层。
    ///
    /// 手法：把预留量设成远大于空闲显存，使 (空闲 − 预留 − KV) × 系数 为负。
    /// 这一条只调用 <see cref="GPUMemoryManager.CalculateNglAsync"/>，不需要真的起服务。
    /// </summary>
    private static async Task RunInsufficientMemoryScenarioAsync(string modelPath)
    {
        var bubbles = new ConsoleBubbleNotifier();
        var logger = FileLogger.CreateRoot(Path.Combine(Environment.CurrentDirectory, "logs", "oomprobe"), LogLevel.Info);

        var config = new AppConfig();
        config.Llm.ModelPath = modelPath;
        config.Llm.LayerCount = 28;

        // 预留 100 GB，必然让"保守可用显存"变成负数
        config.Gpu.ReservedMb = 100_000;
        config.Gpu.KvCacheReservedMb = 1024;
        config.Gpu.PerLayerMb = 150;
        config.Gpu.SafetyFactor = 0.9;

        var mode = new RuntimeModeState();
        var smi = new NvidiaSmi(logger);
        var gpu = new GPUMemoryManager(config, smi, mode, logger, bubbles);

        var plan = await gpu.CalculateNglAsync();

        Console.WriteLine($"  空闲显存   : {plan.FreeVramMb} MB");
        Console.WriteLine($"  保守可用   : {plan.ConservativeAvailableMb:N0} MB");
        Console.WriteLine($"  计划 ngl   : {plan.Ngl}");
        Console.WriteLine($"  运行模式   : {mode.Mode}");
        Console.WriteLine($"  说明       : {plan.Explanation}");

        Check("显存不足时 ngl 置为 0", plan.Ngl == 0, $"ngl={plan.Ngl}");
        Check("显存不足时运行模式切为 CPU", mode.Mode == RuntimeMode.Cpu, mode.Mode.ToString());
        Check("显存不足时告知用户", bubbles.Shown.Exists(b => b.Contains("显存")),
            string.Join(" | ", bubbles.Shown));
        Check("未按 max(1,…) 强行上 1 层", plan.Ngl != 1, $"ngl={plan.Ngl}");
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // 忽略
        }
    }

    private static void Check(string name, bool ok, string detail)
    {
        if (!ok)
        {
            _failures++;
        }

        Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] {name}  —— {detail}");
    }

    private static string Truncate(string value, int max)
        => string.IsNullOrEmpty(value) ? "(空)" : (value.Length <= max ? value : value[..max] + "…");
}
