using System.Text;
using AICompanion.Core.Abstractions;
using AICompanion.Core.Configuration;
using AICompanion.Core.Gpu;
using AICompanion.Core.Logging;
using AICompanion.Core.Monitor;

namespace GameMonitor;

/// <summary>
/// GameMonitor.exe（手册 §1.4 与 §3.9）。
///
/// 这是一个**完全独立**的后台进程：
///   - 不依赖主程序即可运行，主程序没启动时它会自动重连；
///   - 只做三件事：扫描游戏进程、查询 GPU 使用率、通过 TCP 通知主程序；
///   - 自身不做任何显存操作 —— 让路与恢复的决定权始终在主程序（决策 Q5/Q11）。
///
/// 已知指标差距（决策 Q13）：手册要求内存占用低于 10MB，
/// 而 .NET 运行时基线就在 25-45MB。本轮先跑通协议，指标留待阶段 4 评估。
/// </summary>
internal static class Program
{
    private static readonly ManualResetEventSlim Shutdown = new(false);
    private static LogSink? _sink;
    private static MonitorClient? _client;

    private static async Task<int> Main(string[] args)
    {
        // WinExe 没有控制台，这里设置编码只是为了让重定向到文件时中文不乱码。
        try
        {
            Console.OutputEncoding = Encoding.UTF8;
        }
        catch
        {
            // 无控制台时忽略。
        }

        // 前缀用 "gamemonitor" 而不是默认的 "app"：GameMonitor 与主程序写同一个
        // logs\app 目录，若文件名相同（都是 app-yyyyMMdd.log），两个进程各持一个写句柄，
        // 缓冲刷盘时会把两行日志交错成一行。实测确实出现过。
        var bootstrapSink = new LogSink(
            Path.Combine(AppContext.BaseDirectory, "logs", "app"),
            LogLevel.Info,
            30,
            "gamemonitor");
        var bootstrap = new FileLogger(bootstrapSink, "GameMonitor");

        bootstrap.Info("================ GameMonitor 启动 ================");
        bootstrap.Info($"可执行文件目录：{AppContext.BaseDirectory}");
        bootstrap.Info($"命令行：{string.Join(' ', args)}");

        /*
         * 退出信号有两条：ManualResetEventSlim（循环条件）与 CancellationToken（打断正在 await 的调用）。
         *
         * 之前 RunAsync 收的是 CancellationToken.None，于是它内部的
         * `catch (OperationCanceledException)` 与 `IsCancellationRequested` 分支全是死代码 ——
         * 进程退出时循环还得把当前这一轮 Task.Delay 睡完才结算。
         * WinExe 拿不到控制台 Ctrl+C，所以用已有的 ProcessExit 钩子同时置位句柄并 Cancel 这个 CTS。
         */
        using var cts = new CancellationTokenSource();

        try
        {
            var load = ConfigLoader.Load(explicitPath: null, bootstrap);
            var config = load.Config;

            // 用配置里的日志目录重建日志器。
            _sink = new LogSink(AppPaths.Resolve(config.Logging.AppLogDir), LogLevel.Info, config.Logging.MaxLogDays, "gamemonitor");
            var logger = new FileLogger(_sink, "GameMonitor");

            // 引导日志器与正式日志器的目录、前缀完全相同（发布后都是 logs\app\gamemonitor-yyyyMMdd.log）。
            // 同进程两个 StreamWriter 写同一文件会把行交错（主程序里有过实测记录），
            // 所以正式日志器一就绪就关掉引导句柄。
            bootstrapSink.Close();

            logger.Info($"配置：{config.ConfigFilePath}");
            logger.Info($"游戏列表：{ResolveGameListPath(config)}");
            logger.Info($"主程序地址：{config.Monitor.Host}:{config.Monitor.Port}");

            var cooldown = new GameCooldownTracker();
            var smi = new NvidiaSmi(logger);

            if (!await smi.IsAvailableAsync())
            {
                logger.Warn("未检测到 nvidia-smi：GPU 使用率条件将无法判定，unload/restore 都不会触发。");
            }

            var gameList = new GameListProvider(ResolveGameListPath(config), logger);
            var scanner = new GameProcessScanner(gameList, logger);

            var client = new MonitorClient(config, logger, cooldown);
            client.Start();
            _client = client;

            AppDomain.CurrentDomain.ProcessExit += (_, _) =>
            {
                logger.Info("GameMonitor 正在退出。");

                try
                {
                    // 真正唤醒 RunAsync 的取消路径：只 Set 句柄的话，
                    // 循环仍会在一整轮 Task.Delay（空闲时 30 秒）里睡到超时才退出。
                    cts.Cancel();
                }
                catch (ObjectDisposedException)
                {
                    // 进程退出时 Main 的 finally 可能已释放 CTS，忽略。
                }

                Shutdown.Set();
            };

            var runner = new MonitorRunner(config, logger, smi, scanner, client, cooldown);
            await runner.RunAsync(Shutdown, cts.Token);

            return 0;
        }
        catch (Exception ex)
        {
            bootstrap.Error($"GameMonitor 异常退出：{ex.Message}", ex);
            return 1;
        }
        finally
        {
            // 连接循环、TcpClient、心跳 CTS 都在 MonitorClient 里，不 Dispose 会活到进程结束。
            if (_client is not null)
            {
                try
                {
                    await _client.DisposeAsync().ConfigureAwait(false);
                }
                catch
                {
                    // 忽略。
                }
            }

            try
            {
                _sink?.Close();
            }
            catch
            {
                // 忽略。
            }

            try
            {
                bootstrapSink.Close();
            }
            catch
            {
                // 忽略。
            }

            Shutdown.Dispose();
        }
    }

    /// <summary>
    /// game_list.txt 的定位：优先用"与 GameMonitor.exe 同目录"（手册 §3.9 的原文），
    /// 否则退到 config.yaml 所在目录（更符合"所有路径来自配置"的准则 8）。
    /// </summary>
    private static string ResolveGameListPath(AppConfig config)
    {
        var beside = Path.Combine(AppContext.BaseDirectory, "game_list.txt");
        if (File.Exists(beside))
        {
            return beside;
        }

        return AppPaths.Resolve(config.Monitor.GameListFile);
    }
}

/// <summary>检测状态机（手册 §3.9 进程检测 / 触发条件 / 冷却期）。</summary>
internal sealed class MonitorRunner
{
    private readonly AppConfig _config;
    private readonly IAppLogger _logger;
    private readonly NvidiaSmi _smi;
    private readonly GameProcessScanner _scanner;
    private readonly MonitorClient _client;
    private readonly GameCooldownTracker _cooldown;

    private bool _yielded;
    private DateTime? _lowGpuSinceUtc;
    private string? _lastRunningGame;
    private string? _lastExitedGame;
    private DateTime _lastReportUtc = DateTime.MinValue;

    public MonitorRunner(
        AppConfig config,
        IAppLogger logger,
        NvidiaSmi smi,
        GameProcessScanner scanner,
        MonitorClient client,
        GameCooldownTracker cooldown)
    {
        _config = config;
        _logger = logger.ForCategory("Runner");
        _smi = smi;
        _scanner = scanner;
        _client = client;
        _cooldown = cooldown;
    }

    public async Task RunAsync(ManualResetEventSlim shutdown, CancellationToken cancellationToken)
    {
        var monitor = _config.Monitor;
        var idleInterval = TimeSpan.FromSeconds(Math.Max(5, monitor.ScanIntervalIdleSeconds));
        var activeInterval = TimeSpan.FromSeconds(Math.Max(1, monitor.ScanIntervalActiveSeconds));

        _logger.Info(
            $"开始检测：空闲采样 {idleInterval.TotalSeconds:N0}s / 游戏采样 {activeInterval.TotalSeconds:N0}s；" +
            $"unload 阈值 GPU>{monitor.UnloadGpuThresholdPercent}%，" +
            $"restore 阈值 GPU<{monitor.RestoreGpuThresholdPercent}% 持续 {monitor.RestoreLowGpuMinutes:N1} 分钟。");

        while (!shutdown.IsSet && !cancellationToken.IsCancellationRequested)
        {
            TimeSpan nextInterval;

            try
            {
                nextInterval = await TickAsync(idleInterval, activeInterval, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.Error($"检测循环异常：{ex.Message}", ex);
                nextInterval = idleInterval;
            }

            try
            {
                await Task.Delay(nextInterval, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        // 取消分支的收尾就是这里：RunAsync 返回后，Main 的 finally 会 DisposeAsync MonitorClient，
        // socket 一关，主程序那边的读循环立刻读到 EOF 并判定断连（协议里没有 offline 消息，
        // 断连本身就是"我要下线了"的信号，所以不需要额外发消息）。
        _logger.Info(cancellationToken.IsCancellationRequested
            ? "检测循环已退出（收到退出信号）。"
            : "检测循环已退出。");
    }

    private async Task<TimeSpan> TickAsync(TimeSpan idleInterval, TimeSpan activeInterval, CancellationToken cancellationToken)
    {
        var running = _scanner.GetRunningGames();

        // 冷却字典只在"被查询"时才会惰性删除，那些设过一次就再没被查过的进程名会一直留着
        // （长驻进程的慢泄漏）。每个检测周期顺手清一遍，代价是一次字典遍历。
        var pruned = _cooldown.PruneExpired();
        if (pruned > 0)
        {
            _logger.Debug($"已清理 {pruned} 条过期冷却记录。");
        }

        var gpu = await _smi.QueryUtilizationPercentAsync(cancellationToken).ConfigureAwait(false);
        var now = DateTime.UtcNow;

        if (running.Count > 0)
        {
            _lastRunningGame = running[0];
        }
        else if (_lastRunningGame is not null)
        {
            // 记住"最后退出的那个游戏进程名"—— 这是主程序设置冷却期的绑定依据（决策 Q11）。
            _lastExitedGame = _lastRunningGame;
            _logger.Info($"游戏 {_lastRunningGame} 已退出（记为最后退出的进程）。");
            _lastRunningGame = null;
        }

        // 周期性状态汇报，便于排查"为什么没触发"。
        if ((now - _lastReportUtc).TotalSeconds >= 60)
        {
            _lastReportUtc = now;
            _logger.Info(
                $"状态：运行中游戏=[{string.Join(", ", running)}]，GPU={(gpu.Success ? gpu.Value + "%" : "不可用")}，" +
                $"已让出={_yielded}，冷却中={string.Join(",", _cooldown.Snapshot().Keys)}");
        }

        if (!_yielded)
        {
            await TryUnloadAsync(running, gpu, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await TryRestoreAsync(running, gpu, now, cancellationToken).ConfigureAwait(false);
        }

        // 动态采样间隔：有游戏就加密（手册 §3.9）。
        return running.Count > 0 ? activeInterval : idleInterval;
    }

    private async Task TryUnloadAsync(IReadOnlyList<string> running, NvidiaSmiValue gpu, CancellationToken cancellationToken)
    {
        // 双重判断：进程在运行 且 GPU 使用率高于阈值。
        if (running.Count == 0)
        {
            return;
        }

        if (!gpu.Success)
        {
            _logger.Debug("GPU 使用率不可用，跳过 unload 判定。");
            return;
        }

        if (gpu.Value <= _config.Monitor.UnloadGpuThresholdPercent)
        {
            return;
        }

        var target = running[0];

        // 冷却期：同一进程在冷却期内不发送 unload；不同进程不受影响（手册 §3.9）。
        if (_cooldown.IsInCooldown(target))
        {
            _logger.Info(
                $"检测到 {target} 且 GPU {gpu.Value}%，但它仍在冷却期" +
                $"（剩余 {_cooldown.RemainingCooldown(target).TotalMinutes:N1} 分钟），不发送 unload。");
            return;
        }

        _logger.Info($"满足 unload 条件：{target} 运行中 且 GPU {gpu.Value}% > {_config.Monitor.UnloadGpuThresholdPercent}%。");

        if (await _client.SendAsync(MonitorProtocol.Unload(target, $"GPU {gpu.Value}%"), cancellationToken).ConfigureAwait(false))
        {
            _yielded = true;
            _lowGpuSinceUtc = null;
        }
        else
        {
            _logger.Warn("unload 发送失败（主程序未连接），将在下一轮重试。");
        }
    }

    private async Task TryRestoreAsync(
        IReadOnlyList<string> running,
        NvidiaSmiValue gpu,
        DateTime now,
        CancellationToken cancellationToken)
    {
        // 条件 1：列表中所有游戏都已退出。
        if (running.Count > 0)
        {
            _lowGpuSinceUtc = null;
            return;
        }

        if (!gpu.Success)
        {
            _lowGpuSinceUtc = null;
            return;
        }

        // 条件 2：GPU 使用率持续低于阈值。
        if (gpu.Value >= _config.Monitor.RestoreGpuThresholdPercent)
        {
            _lowGpuSinceUtc = null;
            return;
        }

        _lowGpuSinceUtc ??= now;

        var required = TimeSpan.FromMinutes(Math.Max(0.1, _config.Monitor.RestoreLowGpuMinutes));
        var elapsed = now - _lowGpuSinceUtc.Value;

        if (elapsed < required)
        {
            _logger.Debug($"GPU 已低于阈值，还需持续 {(required - elapsed).TotalSeconds:N0} 秒才恢复。");
            return;
        }

        _logger.Info($"满足 restore 条件：游戏已全部退出，且 GPU 持续低于 {_config.Monitor.RestoreGpuThresholdPercent}% " +
                     $"达 {elapsed.TotalMinutes:N1} 分钟。");

        // 携带"最后退出的那个游戏进程名"，供主程序绑定冷却期。
        if (await _client.SendAsync(MonitorProtocol.Restore(_lastExitedGame, "游戏已退出且 GPU 已回落"), cancellationToken)
                .ConfigureAwait(false))
        {
            _yielded = false;
            _lowGpuSinceUtc = null;
        }
        else
        {
            _logger.Warn("restore 发送失败（主程序未连接），将在下一轮重试。");
        }
    }
}
