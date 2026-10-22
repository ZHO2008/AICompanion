using AICompanion.Core.Abstractions;
using AICompanion.Core.Configuration;
using AICompanion.Core.Interop;
using AICompanion.Core.Llm;
using AICompanion.Core.Tts;

namespace AICompanion.Core.Monitor;

/// <summary>
/// 游戏模式协调器（手册 §3.9 的"优雅卸载"与恢复流程，含决策 Q11 的冷却期）。
///
/// 职责是把"收到 unload/restore"翻译成一串具体动作：
///   unload  → 卸载 LLM（先试 /unload，失败则终止进程）→ 停掉 Chatterbox → 气泡"已让出显存"
///   restore → 重新拉起 llama-server 直到 /health 就绪 → 按需重启 Chatterbox
///             → 为"最后退出的那个游戏进程名"设置冷却期 → 把冷却期下发给 GameMonitor → 清除气泡
///
/// 冷却期的**权威在主程序**：即使 GameMonitor 因为版本不同没有正确执行冷却期，
/// <see cref="MonitorServer"/> 也会在同一进程的 unload 上直接拒绝（双保险）。
/// </summary>
public sealed class GameModeCoordinator
{
    private readonly AppConfig _config;
    private readonly IAppLogger _logger;
    private readonly IBubbleNotifier _bubble;
    private readonly LlamaServerController _llama;
    private readonly TTSManager _tts;
    private readonly MonitorServer _server;
    private readonly GameCooldownTracker _cooldown;

    private volatile bool _yielded;
    private volatile bool _chatterboxStopped;

    public GameModeCoordinator(
        AppConfig config,
        IAppLogger logger,
        IBubbleNotifier bubble,
        LlamaServerController llama,
        TTSManager tts,
        MonitorServer server,
        GameCooldownTracker cooldown)
    {
        _config = config;
        _logger = logger.ForCategory("GameMode");
        _bubble = bubble;
        _llama = llama;
        _tts = tts;
        _server = server;
        _cooldown = cooldown;
    }

    /// <summary>是否已让出显存（处于游戏模式）。</summary>
    public bool IsYielded => _yielded;

    /// <summary>是否因为让路而停掉了 Chatterbox（恢复时需要按需拉起）。</summary>

    /// <summary>重启 Chatterbox 的动作由表现层注入（它知道启动命令）。</summary>
    public Func<CancellationToken, Task<bool>>? RestartChatterboxCallback { get; set; }

    /// <summary>订阅 MonitorServer 的事件，开始响应 unload/restore。</summary>
    public void Attach()
    {
        _server.UnloadRequested += OnUnloadRequested;
        _server.RestoreRequested += OnRestoreRequested;
        _logger.Info("游戏模式协调器已接入 GameMonitor 事件。");
    }

    public void Detach()
    {
        _server.UnloadRequested -= OnUnloadRequested;
        _server.RestoreRequested -= OnRestoreRequested;
    }

    private async void OnUnloadRequested(object? sender, MonitorUnloadRequestEventArgs e)
    {
        try
        {
            await HandleUnloadAsync(e.GameProcess, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // async void 事件处理器绝不能把异常抛到事件源线程上。
            _logger.Error($"处理 unload 时未捕获异常：{ex.Message}", ex);
        }
    }

    private async void OnRestoreRequested(object? sender, MonitorRestoreRequestEventArgs e)
    {
        try
        {
            await HandleRestoreAsync(e.LastGameProcess, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.Error($"处理 restore 时未捕获异常：{ex.Message}", ex);
        }
    }

    /// <summary>手册 §3.9 收到 unload 的流程。</summary>
    public async Task HandleUnloadAsync(string? process, CancellationToken cancellationToken = default)
    {
        if (_yielded)
        {
            _logger.Info("已经处于让出状态，忽略重复的 unload。");
            return;
        }

        _yielded = true;
        _logger.Info($"进入游戏模式，开始让出显存（游戏进程：{process ?? "未知"}）。");
        _bubble.Show("game-mode", $"检测到游戏运行（{process ?? "未知"}），正在让出显存…", BubbleLevel.Info);

        // 步骤 1-2：优先 /unload，失败则终止 llama-server 进程。
        await _llama.UnloadAsync(cancellationToken).ConfigureAwait(false);

        // 步骤 3：外部 HTTP 语音服务同样要停（它是外部进程，只能按端口反查后结束）。
        if (_tts.ActiveHttpEngine is { } httpEngine)
        {
            var port = httpEngine.Port;
            _logger.Info($"{httpEngine.Name} 正在使用中，尝试停止占用端口 {port} 的进程以释放显存。");
            _chatterboxStopped = await PortProcessLocator
                .TryKillListenerOnPortAsync(port, _logger, cancellationToken)
                .ConfigureAwait(false);

            if (!_chatterboxStopped)
            {
                _logger.Warn($"未能停止 {httpEngine.Name} 进程，其显存占用可能未被释放。");
            }
        }
        else
        {
            _chatterboxStopped = false;
        }

        // 步骤 4：等待相关进程完全退出后再报"已让出显存"，避免提示与事实不符。
        await Task.Delay(TimeSpan.FromMilliseconds(500), cancellationToken).ConfigureAwait(false);

        _bubble.Clear("game-mode");
        _bubble.Show(
            "gpu-yielded",
            "已让出显存，游戏结束后会自动恢复对话服务。",
            BubbleLevel.Info,
            TimeSpan.FromSeconds(_config.Ui.BubbleDurationSeconds * 2));

        _logger.Info("已让出显存。");
    }

    /// <summary>手册 §3.9 收到 restore 的流程 + 决策 Q11 的冷却期。</summary>
    public async Task HandleRestoreAsync(string? lastGameProcess, CancellationToken cancellationToken = default)
    {
        if (!_yielded)
        {
            _logger.Info("当前未处于让出状态，忽略 restore。");
            return;
        }

        _logger.Info($"游戏已退出（最后退出的进程：{lastGameProcess ?? "未知"}），开始恢复服务。");

        // 步骤 1：重新启动 llama-server 并等待 /health 就绪。
        var restored = await _llama.RestoreAsync(cancellationToken).ConfigureAwait(false);

        // 步骤 2：若之前停掉了 Chatterbox，按需重新启动。
        if (_chatterboxStopped && RestartChatterboxCallback is not null)
        {
            try
            {
                var ok = await RestartChatterboxCallback(cancellationToken).ConfigureAwait(false);
                _logger.Info($"重启 Chatterbox {(ok ? "成功" : "失败")}。");
            }
            catch (Exception ex)
            {
                _logger.Error($"重启 Chatterbox 异常：{ex.Message}", ex);
            }
        }

        _chatterboxStopped = false;
        _yielded = false;

        // 步骤 3 + 决策 Q11：为"最后退出的那个游戏进程名"设置冷却期，并下发给 GameMonitor。
        var cooldown = TimeSpan.FromMinutes(Math.Max(0.1, _config.Monitor.CooldownMinutes));
        if (!string.IsNullOrWhiteSpace(lastGameProcess))
        {
            _cooldown.SetCooldown(lastGameProcess, cooldown);
            var until = DateTime.UtcNow + cooldown;

            _logger.Info($"已为进程 {lastGameProcess} 设置 {cooldown.TotalMinutes:N0} 分钟冷却期（至 {until:HH:mm:ss}）。");

            try
            {
                await _server.SendCooldownAsync(lastGameProcess, until, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.Warn($"下发冷却期给 GameMonitor 失败（主程序侧仍会自行裁决）：{ex.Message}");
            }
        }
        else
        {
            _logger.Warn("restore 未携带进程名，无法绑定冷却期（主程序将不做同进程冷却）。");
        }

        _bubble.Clear("gpu-yielded");

        if (restored)
        {
            _bubble.Show(
                "game-mode-restored",
                "游戏已退出，对话服务已恢复。",
                BubbleLevel.Info,
                TimeSpan.FromSeconds(_config.Ui.BubbleDurationSeconds));
        }

        _logger.Info($"服务恢复完成（llama-server 就绪={restored}）。");
    }
}
