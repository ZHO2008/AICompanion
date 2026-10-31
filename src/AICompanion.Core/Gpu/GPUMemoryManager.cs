using AICompanion.Core.Abstractions;
using AICompanion.Core.Configuration;

namespace AICompanion.Core.Gpu;

/// <summary>-ngl 计算结果（含完整推导过程，便于日志与气泡展示）。</summary>
public sealed record GpuPlan(
    int Ngl,
    int FreeVramMb,
    int TotalLayers,
    RuntimeMode Mode,
    double ConservativeAvailableMb,
    string Explanation);

/// <summary>
/// 显存管理器（手册 §3.3）。
///
/// 严格按决策 Q12 实现边界：<c>保守可用显存 ≤ 0</c> 或 <c>算出的层数 &lt; 1</c> 时
/// 直接 <c>ngl=0</c> 并切到 CPU 模式，而不是按手册字面的 <c>max(1, ...)</c> 强行上 1 层。
/// 原因：显存不足时 1 层同样会 OOM（KV Cache 与上下文仍占显存），
/// 强行上 1 层只会把"可解释的 CPU 降级"变成"不可解释的启动失败"。
///
/// 运行时不再监控或调整显存，只提供只读查询供气泡展示。
/// </summary>
public sealed class GPUMemoryManager
{
    /// <summary>无法从 GGUF 读到层数且配置也没指定时使用的兜底层数（Qwen2.5-7B 的公开数值）。</summary>
    public const int FallbackLayerCount = 28;

    private readonly AppConfig _config;
    private readonly NvidiaSmi _smi;
    private readonly RuntimeModeState _mode;
    private readonly IAppLogger _logger;
    private readonly IBubbleNotifier _bubble;

    public GPUMemoryManager(
        AppConfig config,
        NvidiaSmi smi,
        RuntimeModeState mode,
        IAppLogger logger,
        IBubbleNotifier bubble)
    {
        _config = config;
        _smi = smi;
        _mode = mode;
        _logger = logger.ForCategory("Gpu");
        _bubble = bubble;
    }

    public RuntimeModeState Mode => _mode;

    /// <summary>
    /// 启动时一次性计算 -ngl（手册 §3.3 步骤 1-8）。
    /// </summary>
    public async Task<GpuPlan> CalculateNglAsync(CancellationToken cancellationToken = default)
    {
        var gpu = _config.Gpu;

        // ---- 步骤 1/2：查询空闲显存；失败即判定无可用 NVIDIA GPU ----
        // free 与 total 一次查出：分开查会各起一个 nvidia-smi 进程。
        var memory = await _smi.QueryFreeAndTotalMemoryMbAsync(cancellationToken).ConfigureAwait(false);
        var free = memory.Free;
        if (!free.Success)
        {
            var reason = $"无法获取显存信息（{free.FailureReason}），已切换为 CPU 模式";
            _logger.Warn($"nvidia-smi 查询失败：{free.FailureReason}");
            _bubble.Show("gpu-unavailable", "未检测到 NVIDIA GPU，已切换为 CPU 模式。", BubbleLevel.Warning);

            var cpuPlan = new GpuPlan(0, 0, 0, RuntimeMode.Cpu, 0, reason);
            _mode.Update(new RuntimeModeSnapshot(RuntimeMode.Cpu, 0, 0, 0, reason));
            _logger.Info("GPU 计划：" + Describe(cpuPlan));
            return cpuPlan;
        }

        var freeMb = free.Value;

        // ---- 步骤 6：总层数 L ----
        var totalLayers = ResolveTotalLayers(out var layerSource);

        /*
         * ---- 步骤 3/4/5：保守可用显存 ----
         *
         * 预留与安全系数都改成"按显存档位自动"（配置里写 0 = 自动）：
         *   1/2/3/4 GB 的卡用小预留（192–512 MB），不然扣掉 4 GB 直接算成"显存不足"走 CPU；
         *   8/12/16 GB 的卡留 1–2 GB 就够系统用，剩下的尽量全卸载。
         *
         * 每层占用也改成"按 GGUF 文件大小 ÷ 层数"估算：固定 150 MB 是按 7B 拍的，
         * 1.5B 每层只有 ~40 MB，会把 ngl 压掉一半（实测 8 GB 卡算成 14/28，只有 46.8 tok/s；
         * 全卸载能到 182.8 tok/s）。
         */
        var totalVramMb = memory.Total.Success ? memory.Total.Value : 0;
        var tier = VramTiers.For(totalVramMb);

        var reservedMb = gpu.ReservedMb > 0 ? gpu.ReservedMb : tier.ReservedMb;
        var safetyFactor = gpu.SafetyFactor > 0 ? gpu.SafetyFactor : tier.SafetyFactor;
        var perLayerMb = gpu.PerLayerMb > 0 ? gpu.PerLayerMb : EstimatePerLayerMb(totalLayers);
        if (gpu.MoeExtraFactor > 1.0)
        {
            perLayerMb = (int)Math.Ceiling(perLayerMb * gpu.MoeExtraFactor);
        }
        var kvReservedMb = gpu.KvCacheReservedMb > 0
            ? gpu.KvCacheReservedMb
            : EstimateKvReserveMb(totalLayers, totalVramMb);

        _logger.Info(
            $"显存档位：{tier.Label}（总 {totalVramMb} MB；预留 {(gpu.ReservedMb > 0 ? "配置" : "档位")} {reservedMb} MB，" +
            $"安全系数 {(gpu.SafetyFactor > 0 ? "配置" : "档位")} {safetyFactor:F2}，" +
            $"每层 {(gpu.PerLayerMb > 0 ? "配置" : "按 GGUF 估算")} {perLayerMb} MB，" +
            $"KV 预留 {(gpu.KvCacheReservedMb > 0 ? "配置" : "按层数×上下文估算")} {kvReservedMb} MB，" +
            $"层数 {totalLayers}（{layerSource}））");

        var usableMb = freeMb - reservedMb - kvReservedMb;
        var conservative = usableMb * safetyFactor;

        // ---- 决策 Q12 的边界：算不出正层数就直接走 CPU ----
        if (conservative <= 0 || perLayerMb <= 0)
        {
            var reason = $"空闲显存 {freeMb} MB 扣除预留 {reservedMb + kvReservedMb} MB 后不足，已切换为 CPU 模式";
            _logger.Warn($"显存不足：free={freeMb}MB，预留={reservedMb + kvReservedMb}MB，保守可用={conservative:N0}MB");
            _bubble.Show(
                "gpu-insufficient",
                $"可用显存不足（空闲 {freeMb} MB），已切换为 CPU 模式以保证可用性。",
                BubbleLevel.Warning);

            var insufficient = new GpuPlan(0, freeMb, totalLayers, RuntimeMode.Cpu, conservative, reason);
            _mode.Update(new RuntimeModeSnapshot(RuntimeMode.Cpu, 0, freeMb, totalLayers, reason));
            _logger.Info("GPU 计划：" + Describe(insufficient));
            return insufficient;
        }

        // ---- 步骤 7：ngl = max(1, min(L, 保守可用 / 每层占用)) ----
        var layersByMemory = (int)Math.Floor(conservative / perLayerMb);
        if (layersByMemory < 1)
        {
            var reason = $"保守可用显存 {conservative:N0} MB 不足一层（每层 {perLayerMb} MB），已切换为 CPU 模式";
            _logger.Warn(reason);
            _bubble.Show("gpu-insufficient", "显存不足以承载任何一层，已切换为 CPU 模式。", BubbleLevel.Warning);

            var none = new GpuPlan(0, freeMb, totalLayers, RuntimeMode.Cpu, conservative, reason);
            _mode.Update(new RuntimeModeSnapshot(RuntimeMode.Cpu, 0, freeMb, totalLayers, reason));
            _logger.Info("GPU 计划：" + Describe(none));
            return none;
        }

        var ngl = Math.Max(1, Math.Min(totalLayers, layersByMemory));

        var explanation =
            $"空闲 {freeMb} MB − 预留 {reservedMb + kvReservedMb} MB = {usableMb} MB，" +
            $"× {safetyFactor:F2} = {conservative:N0} MB 保守可用；" +
            $"÷ {perLayerMb} MB/层 = {layersByMemory} 层；" +
            $"层数上限 {totalLayers}（{layerSource}）⇒ ngl={ngl}";

        var plan = new GpuPlan(ngl, freeMb, totalLayers, RuntimeMode.Gpu, conservative, explanation);
        _mode.Update(new RuntimeModeSnapshot(RuntimeMode.Gpu, ngl, freeMb, totalLayers, explanation));

        // 部分卸载会明显掉速，提前告知用户，但不阻止启动（手册不做自动切换）。
        if (ngl < totalLayers)
        {
            _bubble.Show(
                "gpu-partial-offload",
                $"显存有限，仅 {ngl}/{totalLayers} 层在 GPU 上推理，其余层走 CPU，回复速度会变慢。" +
                $"建议关闭占用显存的程序。（{explanation}）",
                BubbleLevel.Warning);
        }

        _logger.Info("GPU 计划：" + Describe(plan));
        return plan;
    }

    /// <summary>只读查询当前显存使用量，供气泡/设置面板展示（手册 §3.3"运行时气泡"）。</summary>
    public async Task<string> DescribeCurrentMemoryAsync(CancellationToken cancellationToken = default)
    {
        var used = await _smi.QueryUsedMemoryMbAsync(cancellationToken).ConfigureAwait(false);
        var util = await _smi.QueryMemoryUtilizationPercentAsync(cancellationToken).ConfigureAwait(false);

        if (!used.Success)
        {
            return "显存：不可用";
        }

        return util.Success
            ? $"显存：已用 {used.Value} MB（{util.Value}%）"
            : $"显存：已用 {used.Value} MB";
    }

    /// <summary>
    /// OOM 回退时计算下一档 ngl（手册 §3.3："将 ngl 减少 4，但最小为 1"）。
    /// 手册明确要求下限为 1，因此这里不返回 0 —— 若 1 层仍 OOM，
    /// 由 <see cref="Llm.LlamaServerController"/> 在重试次数耗尽后按手册中止启动并提示用户。
    /// </summary>
    public int NextNglAfterOom(int currentNgl)
    {
        var step = Math.Max(1, _config.Gpu.OomNglStep);
        return Math.Max(1, currentNgl - step);
    }

    /// <summary>
    /// 按 GGUF 文件大小估算"每层占用（MB）"：文件大小 ÷ 层数 × 1.1（经验系数，含输出头与对齐）。
    /// 拿不到文件大小时回退 150 MB（7B 的经验值）。
    /// </summary>
    /// <summary>
    /// 按"层数 × 上下文长度"估算 KV Cache 预留（MB）。
    ///
    /// KV 大小 ≈ 2 × layers × ctx × n_kv_heads × head_dim × 2 字节，与层数和 ctx 都成线性；
    /// 这里用 7B/4096 的实测值（约 16 MB/层）做基准，再按 ctx 缩放，并夹在 [128 MB, 总显存 25%] 之间。
    /// 1.5B：28 层 → 448 MB（实际约 56 MB，偏保守但不会 OOM）；7B：32 层 → 512 MB（贴近实测）。
    /// </summary>
    private int EstimateKvReserveMb(int totalLayers, int totalVramMb)
    {
        var ctx = _config.Llm.ContextLength > 0 ? _config.Llm.ContextLength : 4096;
        var layers = totalLayers > 0 ? totalLayers : FallbackLayerCount;

        var estimate = (int)Math.Round(layers * 16.0 * ctx / 4096.0);
        estimate = Math.Max(128, estimate);

        if (totalVramMb > 0)
        {
            estimate = Math.Min(estimate, (int)(totalVramMb * 0.25));
        }

        return estimate;
    }

    private int EstimatePerLayerMb(int totalLayers)
    {
        try
        {
            var modelPath = AppPaths.Resolve(_config.Llm.ModelPath);
            if (totalLayers > 0 && File.Exists(modelPath))
            {
                var sizeMb = new FileInfo(modelPath).Length / 1024.0 / 1024.0;
                var perLayer = (int)Math.Ceiling(sizeMb / totalLayers * 1.1);
                if (perLayer > 0)
                {
                    return perLayer;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.Warn($"按 GGUF 估算每层占用失败，回退 150 MB：{ex.Message}");
        }

        return 150;
    }

    private int ResolveTotalLayers(out string source)
    {
        var configured = _config.Llm.LayerCount;
        if (configured > 0)
        {
            source = "config.yaml 的 llm.layer_count";
            return configured;
        }

        var modelPath = AppPaths.Resolve(_config.Llm.ModelPath);
        if (GgufMetadataReader.TryReadBlockCount(modelPath, out var detected, out var error))
        {
            source = $"从 GGUF 元数据探测（{Path.GetFileName(modelPath)}）";
            _logger.Info($"从 GGUF 探测到模型层数 L={detected}。");
            return detected;
        }

        source = $"兜底常量（GGUF 探测失败：{error}）";
        _logger.Warn($"无法从 GGUF 确定层数（{error}），回退为 {FallbackLayerCount} 层。" +
                     "建议在 config.yaml 中显式设置 llm.layer_count。");
        return FallbackLayerCount;
    }

    private static string Describe(GpuPlan plan) =>
        $"mode={plan.Mode}, ngl={plan.Ngl}, free={plan.FreeVramMb}MB, L={plan.TotalLayers}, " +
        $"conservative={plan.ConservativeAvailableMb:N0}MB | {plan.Explanation}";
}
