using System.Diagnostics;
using System.Text;
using AICompanion.Core.Abstractions;
using AICompanion.Core.Configuration;
using AICompanion.Core.Gpu;

namespace AICompanion.Core.Llm;

public enum LlamaStartOutcome
{
    Started = 0,
    ModelFileMissing = 1,
    ExecutableMissing = 2,
    HealthTimeout = 3,
    OutOfMemory = 4,
    Cancelled = 5,
    Failed = 6,

    /// <summary>用户在配置里关掉了对话（llm.enabled=false），属正常状态，不是故障。</summary>
    Disabled = 7,
}

public sealed record LlamaStartResult(LlamaStartOutcome Outcome, int Ngl, string Message, GpuPlan? Plan)
{
    public bool Success => Outcome == LlamaStartOutcome.Started;
}

/// <summary>
/// llama-server 进程控制器。
///
/// 这是决策 Q5 的落点：**由主程序完全拥有 llama-server 的生命周期**，start_all.bat 不再启动它。
/// 这样一次解决手册里三个互相冲突的要求：
///   1. §1.6 的启动顺序 vs §3.3 的"GPUMemoryManager 启动时计算 -ngl" ——
///      只有主程序先算 ngl 再拉起进程，两者才能同时成立；
///   2. §3.3 的 OOM 回退需要解析 llama-server 的**标准错误输出** ——
///      只有自己 Start 的进程才拿得到 stderr 句柄；
///   3. §3.9 的"让出显存"需要能终止 llama-server ——
///      只有自己拥有的 Process 对象才能可靠地连子进程一起杀干净。
/// </summary>
public sealed class LlamaServerController : IAsyncDisposable
{
    private const int StderrHistoryLines = 60;

    private readonly AppConfig _config;
    private readonly GPUMemoryManager _gpu;
    private readonly RuntimeModeState _mode;
    private readonly IAppLogger _logger;
    private readonly IBubbleNotifier _bubble;
    private readonly object _sync = new();
    private readonly LinkedList<string> _stderrHistory = new();

    private readonly LlamaClient _client;
    private readonly CancellationTokenSource _lifetime = new();

    private Process? _process;
    // 在 Process.Exited 事件线程上无锁读取，必须 volatile，否则可能读到旧值而误报"服务已离线"。
    private volatile bool _intentionalStop;
    private volatile bool _oomDetected;

    /// <summary>
    /// 是否正在处理 OOM 回退。
    ///
    /// 为什么要单独一个标志：CUDA 显存不足时 llama-server 打印完 OOM 会**自己退出**，
    /// 触发 <see cref="OnProcessExited"/>。此时我们还没来得及调用 StopProcessAsync
    /// （也就没设置 _intentionalStop），于是会误报"LLM 服务已离线，请手动重启程序"——
    /// 而实际上程序正要自动重启它。实测确实出现了这个误报。
    /// </summary>
    private volatile bool _handlingOom;

    private volatile string? _oomEvidence;
    private volatile bool _gpuOffloadUnsupported;
    private int _currentNgl;

    public LlamaServerController(
        AppConfig config,
        GPUMemoryManager gpu,
        RuntimeModeState mode,
        IAppLogger logger,
        IBubbleNotifier bubble)
    {
        _config = config;
        _gpu = gpu;
        _mode = mode;
        _logger = logger.ForCategory("LlamaServer");
        _bubble = bubble;

        _client = new LlamaClient(config.Llm.Host, config.Llm.Port, logger);
    }

    public LlamaClient Client => _client;

    public bool IsRunning
    {
        get
        {
            lock (_sync)
            {
                try
                {
                    return _process is { HasExited: false };
                }
                catch
                {
                    return false;
                }
            }
        }
    }

    public int CurrentNgl => Volatile.Read(ref _currentNgl);

    /// <summary>最近若干行 stderr，用于错误上报与问题定位。</summary>
    public IReadOnlyList<string> RecentStderr
    {
        get
        {
            lock (_sync)
            {
                return _stderrHistory.ToList();
            }
        }
    }

    /// <summary>
    /// 找多模态投影器：优先 llm.mmproj_path；为空时在模型同目录找 mmproj*.gguf（常见命名）。
    /// 找不到返回 null（纯文本模型，正常情况）。
    /// </summary>
    private string? ResolveMmprojPath()
    {
        try
        {
            var configured = _config.Llm.MmprojPath;
            if (!string.IsNullOrWhiteSpace(configured))
            {
                var resolved = AppPaths.Resolve(configured);
                if (File.Exists(resolved))
                {
                    return resolved;
                }

                _logger.Warn($"配置的 mmproj 不存在：{resolved}，按纯文本模型运行。");
                return null;
            }

            var modelPath = AppPaths.Resolve(_config.Llm.ModelPath);
            var dir = Path.GetDirectoryName(modelPath);
            if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir))
            {
                return null;
            }

            var candidate = Directory.EnumerateFiles(dir, "*mmproj*.gguf", SearchOption.TopDirectoryOnly)
                .OrderBy(f => f.Length)
                .FirstOrDefault();

            return candidate;
        }
        catch (Exception ex)
        {
            _logger.Warn($"查找 mmproj 失败（按纯文本运行）：{ex.Message}");
            return null;
        }
    }
    /// <summary>按 GGUF 元数据确定本次使用的上下文长度（config 写 0 = 自动）。</summary>
    private void ApplyModelContextLength(string modelPath)
    {
        const int autoCap = 8192;

        var configured = _config.Llm.ContextLength;
        var hasInfo = GgufMetadataReader.TryReadModelInfo(modelPath, out var arch, out var modelCtx, out var experts, out var error);

        if (!hasInfo)
        {
            if (configured <= 0)
            {
                _config.Llm.ContextLength = 4096;
            }

            _logger.Warn($"读不到模型元数据（{error}），上下文长度按 {_config.Llm.ContextLength} 处理。");
            return;
        }

        var archLabel = string.IsNullOrWhiteSpace(arch) ? "未知架构" : arch;
        if (experts > 0)
        {
            _logger.Info($"MoE 模型：{archLabel} 有 {experts} 个专家/层 —— 显存预算会额外留 15% 余量（激活缓冲比稠密模型大）。");
            _config.Gpu.MoeExtraFactor = 1.15;
        }

        var modelLabel = modelCtx > 0 ? modelCtx.ToString() : "未声明";

        if (configured > 0)
        {
            if (modelCtx > 0 && configured > modelCtx)
            {
                _logger.Warn($"配置的上下文 {configured} 超过模型训练上限 {modelCtx}，已夹回 {modelCtx}（{archLabel}）。");
                _config.Llm.ContextLength = modelCtx;
            }
            else
            {
                _logger.Info($"模型元数据：架构 {archLabel}，训练上下文 {modelLabel}；按配置使用 {configured}。");
            }

            return;
        }

        var chosen = modelCtx > 0 ? Math.Min(modelCtx, autoCap) : 4096;
        _config.Llm.ContextLength = Math.Max(2048, chosen);
        _logger.Info(
            $"模型元数据：架构 {archLabel}，训练上下文 {modelLabel}；上下文自动取 {_config.Llm.ContextLength}" +
            (modelCtx > autoCap ? $"（上限 {autoCap}，再大会把 KV Cache 撑爆显存）" : string.Empty) + "。");
    }

    /// <summary>
    /// 启动 llama-server（手册 §3.3 启动计算逻辑 + OOM 回退逻辑）。
    /// 返回失败时**不会**抛异常，调用方据此阻断或降级。
    /// </summary>
    public async Task<LlamaStartResult> StartAsync(CancellationToken cancellationToken = default)
    {
        /*
         * 对话被关掉是**正常配置**，不是故障：
         * 不碰模型文件、不查显存、不拉进程，直接返回 Disabled。
         * 上层据此跳过"LLM 未就绪"的告警，避免把用户的主动选择当成错误来吓唬人。
         */
        if (!_config.Llm.Enabled)
        {
            var off = "对话已关闭（llm.enabled=false）：不加载模型，也不拉起 llama-server。";
            _logger.Info(off);
            _bubble.Show("llm-disabled", "对话功能已关闭（llm.enabled=false）。角色照常显示，语音与文字输入不受影响。", BubbleLevel.Info);
            return new LlamaStartResult(LlamaStartOutcome.Disabled, 0, off, null);
        }

        /*
         * 先算 -ngl 再校验模型文件。
         *
         * 顺序理由：CalculateNglAsync 会写全局 RuntimeMode（GPU / CPU 与 ngl）。
         * 如果把它放在模型校验之后，一旦模型缺失就提前返回，RuntimeMode 会停留在
         * "尚未检测"的初始值，UI 与日志里就会显示一个含糊的模式 —— 而 GPU 是否可用
         * 与模型文件在不在是两件互相独立的事，显存查询本身不受影响。
         * 因此先算模式，再按手册 §6 的顺序做"致命缺失"校验。
         */
        /*
         * 顺序：**先定模型路径与上下文长度，再算 ngl**。
         *
         * 之前是反的：CalculateNglAsync 用旧的 ContextLength（4096）算 KV 预留，
         * 长上下文模型首启时 KV 被低估 -> ngl 偏高 -> 直接 OOM。
         * 模型路径解析本身不依赖显存，提前做不影响"模型缺失也要有运行模式"的初衷。
         */
        var modelPathEarly = AppPaths.Resolve(_config.Llm.ModelPath);
        if (File.Exists(modelPathEarly))
        {
            ApplyModelContextLength(modelPathEarly);
        }

        var plan = await _gpu.CalculateNglAsync(cancellationToken).ConfigureAwait(false);

        // ---- 手册 §6：模型文件缺失属"致命缺失"，必须阻断并清晰提示 ----
        var modelPath = AppPaths.Resolve(_config.Llm.ModelPath);
        if (!File.Exists(modelPath))
        {
            var message = $"LLM 模型文件缺失或路径错误：{modelPath}";
            _logger.Error(message);
            _bubble.Show("llm-model-missing", "LLM 模型文件缺失或路径错误，无法启动对话服务。请检查 config.yaml 的 llm.model_path。", BubbleLevel.Error);
            return new LlamaStartResult(LlamaStartOutcome.ModelFileMissing, 0, message, plan);
        }


        var exePath = AppPaths.Resolve(_config.Llm.ServerExecutablePath);
        if (!File.Exists(exePath))
        {
            var message = $"未找到 llama-server 可执行文件：{exePath}";
            _logger.Error(message);
            _bubble.Show("llm-exe-missing", "未找到 llama-server，无法启动对话服务。请检查 config.yaml 的 llm.server_executable_path。", BubbleLevel.Error);
            return new LlamaStartResult(LlamaStartOutcome.ExecutableMissing, 0, message, plan);
        }

        var ngl = plan.Ngl;

        var maxRetries = Math.Max(0, _config.Gpu.MaxOomRetries);
        LlamaStartResult? lastResult = null;

        try
        {
        for (var attempt = 0; attempt <= maxRetries; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            _oomDetected = false;
            _oomEvidence = null;
            _gpuOffloadUnsupported = false;
            _handlingOom = false;
            ClearStderrHistory();

            _logger.Info($"启动 llama-server（第 {attempt + 1} 次尝试，ngl={ngl}）...");

            var started = await StartProcessAsync(exePath, modelPath, ngl).ConfigureAwait(false);
            if (!started.Success)
            {
                return new LlamaStartResult(LlamaStartOutcome.Failed, ngl, started.Message, plan);
            }

            Volatile.Write(ref _currentNgl, ngl);

            var healthy = await WaitForHealthyAsync(
                TimeSpan.FromSeconds(_config.Llm.StartupTimeoutSeconds),
                cancellationToken).ConfigureAwait(false);

            if (healthy)
            {
                _bubble.Clear("llm-offline");

                // 实际生效的 ngl 可能低于启动前的计划值（OOM 回退会逐次调低）。
                // 必须把真实值写回全局运行模式，否则 UI 会继续显示启动前算出来的层数，
                // 而服务其实跑在更少的层上 —— 又是一次"谎报"。
                if (ngl != plan.Ngl && _mode.Mode == RuntimeMode.Gpu)
                {
                    var actualReason = $"OOM 回退后实际 ngl={ngl}（启动前计划 {plan.Ngl}）";
                    _logger.Warn(actualReason);
                    _mode.Update(new RuntimeModeSnapshot(
                        RuntimeMode.Gpu,
                        ngl,
                        plan.FreeVramMb,
                        plan.TotalLayers,
                        actualReason));

                    // 已经通过"降层"提示告知过用户，这里收掉那条临时气泡。
                    _bubble.Clear("gpu-oom-retry");
                }

                // 只读真实能力：如果这个 llama-server 是 CPU-only 构建，它会把 -ngl 忽略掉。
                // 那种情况下必须把运行模式如实改回 CPU，而不是让 UI 继续显示"GPU 模式（ngl=N）"——
                // 否则用户会以为模型跑在 GPU 上，与手册 §2 的"运行模式"语义直接冲突。
                if (_gpuOffloadUnsupported && _mode.Mode == RuntimeMode.Gpu)
                {
                    var reason = "llama-server 未编译 GPU 支持，-ngl 被忽略，已改用 CPU 推理";
                    _logger.Warn(reason);
                    _mode.Update(new RuntimeModeSnapshot(RuntimeMode.Cpu, 0, plan.FreeVramMb, plan.TotalLayers, reason));
                    _bubble.Show(
                        "llm-cpu-fallback",
                        "当前 llama-server 没有 GPU 支持，已按 CPU 模式运行。如需 GPU 推理请用 CUDA 重新构建（build_native.bat）。",
                        BubbleLevel.Warning);
                }

                _logger.Info($"llama-server 已就绪（ngl={ngl}）。");
                return new LlamaStartResult(LlamaStartOutcome.Started, ngl, "已就绪", plan);
            }

            // 判定失败原因：OOM 还是单纯没起来。
            if (_oomDetected)
            {
                var evidence = _oomEvidence ?? "CUDA out of memory";
                _logger.Error($"检测到显存不足：{evidence}");
                await StopProcessAsync(cancellationToken).ConfigureAwait(false);

                if (attempt >= maxRetries)
                {
                    var message = $"显存不足，回退 {maxRetries} 次后仍失败（ngl={ngl}）。";
                    _logger.Error(message);
                    _bubble.Show("llm-oom", "显存不足，请关闭其他程序后重试。", BubbleLevel.Error);
                    return new LlamaStartResult(LlamaStartOutcome.OutOfMemory, ngl, message, plan);
                }

                var nextNgl = _gpu.NextNglAfterOom(ngl);
                var delay = TimeSpan.FromSeconds(Math.Max(1, _config.Gpu.OomRetryDelaySeconds));
                _logger.Warn($"OOM 回退：ngl {ngl} → {nextNgl}，等待 {delay.TotalSeconds:N0} 秒后重启。");

                // 必须告诉用户：GPU 层数被调低了，直接影响回复速度。
                // 否则程序会"静默地变慢"，用户只会觉得莫名其妙。
                _bubble.Show(
                    "gpu-oom-retry",
                    $"显存不足，已把 GPU 推理层数从 {ngl} 降到 {nextNgl} 后重试（共可重试 {maxRetries} 次）。",
                    BubbleLevel.Warning,
                    TimeSpan.FromSeconds(_config.Ui.BubbleDurationSeconds * 3));

                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
                ngl = nextNgl;
                continue;
            }

            // 非 OOM 的启动失败：端口被占用、参数错误、模型损坏等。
            var dump = string.Join(" | ", RecentStderr.TakeLast(5));
            lastResult = new LlamaStartResult(
                LlamaStartOutcome.HealthTimeout,
                ngl,
                $"等待 /health 就绪超时（{_config.Llm.StartupTimeoutSeconds} 秒）。最近输出：{dump}",
                plan);

            _logger.Error(lastResult.Message);
            await StopProcessAsync(cancellationToken).ConfigureAwait(false);
            break;
        }

        var final = lastResult ?? new LlamaStartResult(LlamaStartOutcome.Failed, ngl, "启动失败。", plan);
        _bubble.Show("llm-offline", "LLM 服务未就绪。可查看日志后手动重启程序。", BubbleLevel.Error);
        return final;
        }
        catch (OperationCanceledException)
        {
            // 启动过程被取消（例如用户退出程序）：必须把已经拉起来的子进程收掉，
            // 否则 llama-server 会变成一个占着显存的孤儿进程，而且没人再能管它。
            _logger.Warn("LLM 启动被取消，正在终止已启动的 llama-server。");
            try
            {
                await StopProcessAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.Warn($"取消启动时终止 llama-server 失败：{ex.Message}");
            }

            throw;
        }
    }

    /// <summary>轮询 /health 直到就绪或超时（手册 §1.6 步骤 4）。</summary>
    public async Task<bool> WaitForHealthyAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        var deadline = DateTime.UtcNow + timeout;
        var interval = TimeSpan.FromSeconds(1);

        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // 进程若已退出，就没必要继续等了。
            lock (_sync)
            {
                if (_process is { HasExited: true })
                {
                    return false;
                }
            }

            var health = await _client.CheckHealthAsync(cancellationToken).ConfigureAwait(false);
            if (health.Healthy)
            {
                return true;
            }

            if (_oomDetected)
            {
                return false;
            }

            await Task.Delay(interval, cancellationToken).ConfigureAwait(false);
        }

        return false;
    }

    /// <summary>
    /// 让出显存（手册 §3.9 的优雅卸载，按决策 Q6 落地）：
    /// 先尝试 /unload（真实 llama-server 会返回 404），失败则终止进程。
    /// </summary>
    public async Task<bool> UnloadAsync(CancellationToken cancellationToken = default)
    {
        _logger.Info("收到 unload，开始让出显存。");

        // 步骤 1：优先走官方卸载接口（带 5 秒超时）。
        var unloadedViaApi = await _client.TryUnloadModelAsync(TimeSpan.FromSeconds(5), cancellationToken)
            .ConfigureAwait(false);

        // 步骤 2：无论接口是否成功，进程都需要退出，否则显存不会真正释放。
        //         （/unload 只卸载模型，llama-server 本体仍占用显存。）
        await StopProcessAsync(cancellationToken).ConfigureAwait(false);

        // 步骤 3：等进程彻底退出后再报告，避免"提示已让出但显存还占着"。
        var exited = await WaitForExitAsync(TimeSpan.FromSeconds(10), cancellationToken).ConfigureAwait(false);
        if (!exited)
        {
            _logger.Warn("llama-server 未在 10 秒内退出，显存可能尚未完全释放。");
        }

        _bubble.Clear("llm-offline");
        _bubble.Show(
            "gpu-yielded",
            unloadedViaApi
                ? "已让出显存（通过 /unload 接口）。"
                : "已让出显存（llama-server 进程已终止）。",
            BubbleLevel.Info,
            TimeSpan.FromSeconds(_config.Ui.BubbleDurationSeconds));

        _logger.Info($"已让出显存（/unload 接口可用={unloadedViaApi}，进程已退出={exited}）。");
        return true;
    }

    /// <summary>恢复服务（手册 §3.9 的 restore 流程）。</summary>
    public async Task<bool> RestoreAsync(CancellationToken cancellationToken = default)
    {
        _logger.Info("收到 restore，开始恢复 llama-server。");

        if (IsRunning)
        {
            _logger.Info("llama-server 已在运行，无需恢复。");
            _bubble.Clear("gpu-yielded");
            return true;
        }

        var result = await StartAsync(cancellationToken).ConfigureAwait(false);
        if (result.Success)
        {
            _bubble.Clear("gpu-yielded");
            _bubble.Show("gpu-restored", "已恢复对话服务。", BubbleLevel.Info,
                TimeSpan.FromSeconds(_config.Ui.BubbleDurationSeconds));
            _logger.Info("llama-server 已恢复。");
            return true;
        }

        _logger.Error($"恢复 llama-server 失败：{result.Message}");
        _bubble.Show("llm-offline", "恢复 LLM 服务失败，请手动重启程序。", BubbleLevel.Error);
        return false;
    }

    /// <summary>停止并释放进程（不返回气泡，供卸载与退出流程复用）。</summary>
    public async Task StopAsync(CancellationToken cancellationToken = default)
        => await StopProcessAsync(cancellationToken).ConfigureAwait(false);

    // ------------------------------------------------------------------ 进程管理

    /// <summary>
    /// 启动子进程。
    ///
    /// 注意这里是 <c>async</c> 而不是同步方法：原先内部用
    /// <c>StopProcessAsync().GetAwaiter().GetResult()</c> 同步等待，
    /// 而本方法是在 WPF 的 UI 线程上被调用的（OOM 回退会连续重启进程，正是高频路径），
    /// 那属于准则 1 明令禁止的"UI 线程阻塞"。改成真异步后不再阻塞任何线程。
    /// </summary>
    private async Task<(bool Success, string Message)> StartProcessAsync(string exePath, string modelPath, int ngl)
    {
        await StopProcessAsync(CancellationToken.None).ConfigureAwait(false);

        var psi = new ProcessStartInfo(exePath)
        {
            WorkingDirectory = Path.GetDirectoryName(exePath) ?? AppPaths.Root,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        // 用 ArgumentList 而不是拼字符串，避免路径含空格/中文时的引号问题。
        psi.ArgumentList.Add("-m");
        psi.ArgumentList.Add(modelPath);
        psi.ArgumentList.Add("--host");
        psi.ArgumentList.Add(_config.Llm.Host);
        psi.ArgumentList.Add("--port");
        psi.ArgumentList.Add(_config.Llm.Port.ToString());
        psi.ArgumentList.Add("-ngl");

        // 多模态投影器（可选）：视觉/音频塔是独立文件，存在才传。
        var mmprojPath = ResolveMmprojPath();
        if (mmprojPath is not null)
        {
            psi.ArgumentList.Add("--mmproj");
            psi.ArgumentList.Add(mmprojPath);
            _logger.Info($"已加载多模态投影器：{Path.GetFileName(mmprojPath)}");
        }
        psi.ArgumentList.Add(ngl.ToString());
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add(_config.Llm.ContextLength.ToString());

        if (_config.Llm.Threads > 0)
        {
            psi.ArgumentList.Add("-t");
            psi.ArgumentList.Add(_config.Llm.Threads.ToString());
        }

        var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        process.OutputDataReceived += (_, e) => CaptureLine(e.Data, isError: false);
        process.ErrorDataReceived += (_, e) => CaptureLine(e.Data, isError: true);

        lock (_sync)
        {
            _intentionalStop = false;
            _process = process;
        }

        try
        {
            if (!process.Start())
            {
                lock (_sync)
                {
                    if (ReferenceEquals(_process, process))
                    {
                        _process = null;
                    }
                }

                process.Dispose();
                return (false, "Process.Start 返回 false。");
            }

            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            process.Exited += OnProcessExited;

            _logger.Info($"已启动进程 PID={process.Id}：{exePath} {string.Join(' ', psi.ArgumentList)}");
            return (true, string.Empty);
        }
        catch (Exception ex)
        {
            _logger.Error($"启动 llama-server 失败：{ex.Message}", ex);
            lock (_sync)
            {
                _process = null;
            }

            process.Dispose();
            return (false, ex.Message);
        }
    }

    /// <summary>每次启动尝试前清空上一轮的 stderr，避免诊断串混入上一次进程的输出。</summary>
    private void ClearStderrHistory()
    {
        lock (_sync)
        {
            _stderrHistory.Clear();
        }
    }

    /// <summary>
    /// 捕获子进程输出。这里是手册 §3.3"解析其标准错误输出，检测是否包含 CUDA out of memory"的实现点。
    /// </summary>
    private void CaptureLine(string? line, bool isError)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return;
        }

        if (isError)
        {
            lock (_sync)
            {
                _stderrHistory.AddLast(line);
                while (_stderrHistory.Count > StderrHistoryLines)
                {
                    _stderrHistory.RemoveFirst();
                }
            }

            if (LooksLikeOutOfMemory(line))
            {
                _oomDetected = true;
                _oomEvidence ??= line.Trim();

                // 提前置位：显存不足的进程通常会立刻自行退出，
                // 必须在退出事件到达之前就标记"这是预期内的下线"，否则会误报离线。
                _handlingOom = true;
            }

            if (LooksLikeMissingGpuSupport(line))
            {
                _gpuOffloadUnsupported = true;
            }
        }

        _logger.Debug($"[llama-server{(isError ? " err" : string.Empty)}] {line}");
    }

    /// <summary>识别显存不足。手册点名 "CUDA out of memory"，同时兼容其它后端的措辞。</summary>
    private static bool LooksLikeOutOfMemory(string line) =>
        line.Contains("CUDA out of memory", StringComparison.OrdinalIgnoreCase) ||
        line.Contains("out of memory", StringComparison.OrdinalIgnoreCase) ||
        line.Contains("failed to allocate", StringComparison.OrdinalIgnoreCase) ||
        line.Contains("ggml_cuda_host_malloc", StringComparison.OrdinalIgnoreCase) ||
        line.Contains("insufficient memory", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 识别"这个 llama-server 根本没有可用的 GPU 后端"。
    ///
    /// 下面的字符串是**实测**得到的，不是猜的：本机 CPU 版 llama-server 在收到 -ngl 13 时
    /// 实际打印（`bin\llama-server.exe … -ngl 13`，stderr）：
    ///
    ///     warning: no usable GPU found, --gpu-layers option will be ignored
    ///     warning: one possible reason is that llama.cpp was compiled without GPU support
    ///     warning: consult docs/build.md for compilation instructions
    ///
    /// 不识别它，UI 就会在模型其实跑在 CPU 上时显示"GPU 模式（ngl=N）"，
    /// 与手册 §2 的运行模式语义直接冲突。
    /// </summary>
    private static bool LooksLikeMissingGpuSupport(string line) =>
        // 实测文本（llama.cpp 0.7.0-dev）
        line.Contains("no usable GPU found", StringComparison.OrdinalIgnoreCase) ||
        line.Contains("gpu-layers option will be ignored", StringComparison.OrdinalIgnoreCase) ||
        line.Contains("compiled without GPU support", StringComparison.OrdinalIgnoreCase) ||
        // 兼容其它版本的措辞
        line.Contains("not compiled with GPU offload", StringComparison.OrdinalIgnoreCase) ||
        line.Contains("not compiled with GPU support", StringComparison.OrdinalIgnoreCase) ||
        line.Contains("no GPU offload support", StringComparison.OrdinalIgnoreCase) ||
        line.Contains("built without CUDA", StringComparison.OrdinalIgnoreCase) ||
        line.Contains("ggml_cuda_init: failed", StringComparison.OrdinalIgnoreCase) ||
        line.Contains("CUDA devices found: 0", StringComparison.OrdinalIgnoreCase);

    private void OnProcessExited(object? sender, EventArgs e)
    {
        if (_intentionalStop || _handlingOom || _lifetime.IsCancellationRequested)
        {
            // OOM 回退期间进程会自行退出，这是预期内的下线，不能报"服务已离线"。
            return;
        }

        // 手册 §2：不做任何运行时自动切换/自动恢复，只记录日志并提示用户。
        var exitCode = -1;
        try
        {
            exitCode = (sender as Process)?.ExitCode ?? -1;
        }
        catch
        {
            // 忽略。
        }

        _logger.Error($"llama-server 意外退出（exit code {exitCode}），按设计不做自动恢复。");
        _bubble.Show("llm-offline", "LLM 服务已离线。请查看日志后手动重启程序。", BubbleLevel.Error);
    }

    private async Task StopProcessAsync(CancellationToken cancellationToken)
    {
        Process? process;
        lock (_sync)
        {
            process = _process;
            _intentionalStop = true;
        }

        if (process is null)
        {
            return;
        }

        try
        {
            if (!process.HasExited)
            {
                _logger.Info($"正在终止 llama-server（PID={process.Id}）...");
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            _logger.Warn($"终止 llama-server 时出现异常：{ex.Message}");
        }
        finally
        {
            lock (_sync)
            {
                if (ReferenceEquals(_process, process))
                {
                    _process = null;
                }
            }

            try
            {
                process.Dispose();
            }
            catch
            {
                // 忽略。
            }
        }
    }

    private async Task<bool> WaitForExitAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!IsRunning)
            {
                return true;
            }

            await Task.Delay(200, cancellationToken).ConfigureAwait(false);
        }

        return !IsRunning;
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            _lifetime.Cancel();
        }
        catch
        {
            // 忽略。
        }

        await StopProcessAsync(CancellationToken.None).ConfigureAwait(false);
        _client.Dispose();
        _lifetime.Dispose();
    }
}
