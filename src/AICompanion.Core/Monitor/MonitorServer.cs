using System.Net;
using System.Net.Sockets;
using AICompanion.Core.Abstractions;
using AICompanion.Core.Configuration;

namespace AICompanion.Core.Monitor;

public sealed class MonitorRestoreRequestEventArgs : EventArgs
{
    /// <summary>最后退出的那个游戏进程名（决策 Q11 的冷却期绑定依据）。</summary>
    public string? LastGameProcess { get; init; }
}

public sealed class MonitorUnloadRequestEventArgs : EventArgs
{
    /// <summary>当前检测到的游戏进程名（可能为空）。</summary>
    public string? GameProcess { get; init; }
}

/// <summary>
/// 主程序侧的 GameMonitor 通信服务端（手册 §3.9 通信协议 + 主程序侧心跳）。
///
/// 设计要点：
///   - 主程序**监听** 127.0.0.1:47777，GameMonitor 主动连上来（手册明确的方向）；
///   - 帧格式为 NDJSON（一行一个 JSON 对象），见 <see cref="MonitorProtocol"/>；
///   - 每 10 秒期望一次 ping，连续 30 秒收不到任何消息即判定断连并气泡提示；
///   - 冷却期由主程序裁决，并通过 cooldown 消息回传给 GameMonitor（决策 Q11）。
/// </summary>
public sealed class MonitorServer : IAsyncDisposable
{
    private readonly AppConfig _config;
    private readonly IAppLogger _logger;
    private readonly IBubbleNotifier _bubble;
    private readonly GameCooldownTracker _cooldown;
    private readonly object _sync = new();

    private TcpListener? _listener;
    private TcpClient? _client;
    private StreamWriter? _writer;
    private CancellationTokenSource? _lifetime;
    private Task? _acceptTask;
    private Task? _readTask;
    private Timer? _watchdog;
    private DateTime _lastMessageUtc = DateTime.MinValue;
    private bool _disconnectNotified;

    /// <summary>
    /// 是否正在 Stop。跨线程读写（Stop 在 UI 线程，读循环在线程池）。
    ///
    /// 用于让"停止中发现的断连"一律不弹气泡：加了"限时等待接受循环"之后，
    /// 读循环的 finally 必定抢在 Stop 的 HandleDisconnect(notify:false) 之前执行，
    /// 若仍按默认 notify:true 处理，用户正常退出也会看到一句"游戏检测服务已断开"。
    /// </summary>
    private volatile bool _stopping;

    public MonitorServer(
        AppConfig config,
        IAppLogger logger,
        IBubbleNotifier bubble,
        GameCooldownTracker cooldown)
    {
        _config = config;
        _logger = logger.ForCategory("GameMonitor");
        _bubble = bubble;
        _cooldown = cooldown;
    }

    public bool IsClientConnected
    {
        get
        {
            lock (_sync)
            {
                return _client?.Connected == true;
            }
        }
    }

    public DateTime LastMessageUtc
    {
        get
        {
            lock (_sync)
            {
                return _lastMessageUtc;
            }
        }
    }

    /// <summary>"重连游戏检测"按钮的动作由表现层注入：通常是重新拉起 GameMonitor.exe。</summary>
    public Func<CancellationToken, Task<bool>>? RestartMonitorProcessCallback { get; set; }

    public event EventHandler<MonitorUnloadRequestEventArgs>? UnloadRequested;

    public event EventHandler<MonitorRestoreRequestEventArgs>? RestoreRequested;

    public event EventHandler? ClientConnected;

    public event EventHandler<string>? ClientDisconnected;

    /// <summary>
    /// 启动监听。**有意不 await 接受循环**：它必须活到 <see cref="Stop"/>，
    /// 因此这里只负责把任务挂到字段上就直接返回。
    /// （原实现结尾是一句 `await Task.CompletedTask;` 的空操作，只为凑出 async 修饰符。）
    /// </summary>
    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        Stop();

        _lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        var endpoint = new IPEndPoint(IPAddress.Parse(_config.Monitor.Host), _config.Monitor.Port);
        _listener = new TcpListener(endpoint);
        _listener.Start();

        _logger.Info($"GameMonitor 通信服务已监听 {endpoint}。");

        _stopping = false;

        _acceptTask = Task.Run(() => AcceptLoopAsync(_lifetime.Token));
        _watchdog = new Timer(_ => CheckHeartbeat(), null, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5));

        return Task.CompletedTask;
    }

    private async Task AcceptLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var listener = _listener;
                if (listener is null)
                {
                    return;
                }

                var client = await listener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
                client.NoDelay = true;

                lock (_sync)
                {
                    // 旧的 writer 必须先释放：只 Dispose 旧 client 而不处理 writer，
                    // 那个 StreamWriter 仍持有已关闭的流，且会一直被字段覆盖后交给 GC。
                    try
                    {
                        _writer?.Dispose();
                    }
                    catch
                    {
                        // 忽略。
                    }

                    _client?.Dispose();
                    _client = client;
                    _writer = new StreamWriter(client.GetStream(), MonitorProtocol.LineEncoding) { AutoFlush = true };
                    _lastMessageUtc = DateTime.UtcNow;
                    _disconnectNotified = false;
                }

                _logger.Info($"GameMonitor 已连接：{client.Client.RemoteEndPoint}");
                _bubble.Clear("gamemonitor-offline");
                ClientConnected?.Invoke(this, EventArgs.Empty);

                // 同一时刻只服务一个 GameMonitor，因此这里串行等待它的读循环结束。
                _readTask = Task.Run(() => ReadLoopAsync(client, cancellationToken), cancellationToken);
                await _readTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (ObjectDisposedException)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.Warn($"接受 GameMonitor 连接时异常：{ex.Message}");
                try
                {
                    await Task.Delay(1000, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }
    }

    private async Task ReadLoopAsync(TcpClient client, CancellationToken cancellationToken)
    {
        try
        {
            using var reader = new StreamReader(client.GetStream(), MonitorProtocol.LineEncoding);

            while (!cancellationToken.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                if (line is null)
                {
                    break;   // 对端关闭。
                }

                lock (_sync)
                {
                    _lastMessageUtc = DateTime.UtcNow;
                }

                if (!MonitorProtocol.TryDecode(line, out var message, out var error) || message is null)
                {
                    _logger.Warn($"收到无法解析的 GameMonitor 消息（{error}）：{Truncate(line)}");
                    continue;
                }

                HandleMessage(message);
            }
        }
        catch (OperationCanceledException)
        {
            // 正常停止。
        }
        catch (Exception ex)
        {
            // 停止过程里读循环被强关连接打断是预期内的（Stop 等超时后会直接 Dispose 掉 client），
            // 降级为 Debug，免得正常退出时日志里留一条吓人的 Warn。
            if (_stopping)
            {
                _logger.Debug($"停止时读取 GameMonitor 消息中断：{ex.Message}");
            }
            else
            {
                _logger.Warn($"读取 GameMonitor 消息时异常：{ex.Message}");
            }
        }
        finally
        {
            // 只清理自己这一条连接：新客户端接入时旧的会被 Dispose，
            // 旧读循环随即走到这里；若无条件清理，会把刚建立的新连接和新 writer 一起关掉。
            bool isCurrent;
            lock (_sync)
            {
                isCurrent = ReferenceEquals(_client, client);
            }

            if (isCurrent)
            {
                HandleDisconnect("连接已关闭");
            }
        }
    }

    private void HandleMessage(MonitorMessage message)
    {
        _logger.Debug($"收到 GameMonitor 消息：{message}");

        switch (message.Type.ToLowerInvariant())
        {
            case MonitorMessageTypes.Ping:
                // 心跳本身已刷新 _lastMessageUtc，无需额外处理。
                break;

            case MonitorMessageTypes.Unload:
                _logger.Info($"收到 unload 请求（游戏进程 {message.Process ?? "未知"}）。");

                // 冷却期裁决在主程序侧（决策 Q11）：同进程在冷却期内一律忽略。
                if (_cooldown.IsInCooldown(message.Process))
                {
                    var remaining = _cooldown.RemainingCooldown(message.Process);
                    _logger.Info(
                        $"忽略 unload：进程 {message.Process} 仍在冷却期（剩余 {remaining.TotalMinutes:N1} 分钟）。");
                    break;
                }

                UnloadRequested?.Invoke(this, new MonitorUnloadRequestEventArgs { GameProcess = message.Process });
                break;

            case MonitorMessageTypes.Restore:
                _logger.Info($"收到 restore 请求（最后退出的游戏进程 {message.Process ?? "未知"}）。");
                RestoreRequested?.Invoke(this, new MonitorRestoreRequestEventArgs { LastGameProcess = message.Process });
                break;

            default:
                _logger.Debug($"忽略未知消息类型：{message.Type}");
                break;
        }
    }

    /// <summary>把冷却期下发给 GameMonitor，避免它在冷却期内盲发 unload。</summary>
    public async Task SendCooldownAsync(string? processName, DateTime untilUtc, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(processName))
        {
            return;
        }

        await SendAsync(MonitorProtocol.Cooldown(processName, untilUtc), cancellationToken).ConfigureAwait(false);
    }

    public async Task SendAsync(MonitorMessage message, CancellationToken cancellationToken = default)
    {
        StreamWriter? writer;
        lock (_sync)
        {
            writer = _writer;
        }

        if (writer is null)
        {
            _logger.Warn($"无法发送 {message.Type}：GameMonitor 未连接。");
            return;
        }

        try
        {
            await writer.WriteAsync(MonitorProtocol.Encode(message).AsMemory(), cancellationToken).ConfigureAwait(false);
            await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.Warn($"发送 {message.Type} 失败：{ex.Message}");
        }
    }

    /// <summary>心跳看门狗：连续超过阈值收不到任何消息就判定断连（手册 §3.9）。</summary>
    private void CheckHeartbeat()
    {
        bool silenceExceeded;
        double silentSeconds;

        lock (_sync)
        {
            if (_client is null || _lastMessageUtc == DateTime.MinValue)
            {
                return;
            }

            silentSeconds = (DateTime.UtcNow - _lastMessageUtc).TotalSeconds;
            var threshold = Math.Max(_config.Monitor.HeartbeatIntervalSeconds * 2, _config.Monitor.DisconnectTimeoutSeconds);
            silenceExceeded = silentSeconds >= threshold;
        }

        if (!silenceExceeded)
        {
            return;
        }

        _logger.Error($"已有 {silentSeconds:N0} 秒未收到 GameMonitor 消息，判定断连。");

        // 注意：不要在这里预先置 _disconnectNotified。
        // HandleDisconnect 自己会用该标志去重，提前置位会让它直接 return，
        // 结果 ClientDisconnected 永远不会触发，托盘的"重连游戏检测"就成了摆设。
        HandleDisconnect($"{silentSeconds:N0} 秒未收到心跳");
    }

    /// <param name="notify">
    /// 是否弹气泡并触发 <see cref="ClientDisconnected"/>。
    /// 传 null（默认）表示按当前状态自动判断：正在 Stop 就不弹 —— 正常退出不该告警。
    /// </param>
    private void HandleDisconnect(string reason, bool? notify = null)
    {
        var shouldNotify = notify ?? !_stopping;

        bool wasConnected;

        lock (_sync)
        {
            wasConnected = _client is not null;
            try
            {
                _writer?.Dispose();
            }
            catch
            {
                // 忽略。
            }

            try
            {
                _client?.Dispose();
            }
            catch
            {
                // 忽略。
            }

            _writer = null;
            _client = null;
        }

        if (!wasConnected || _disconnectNotified)
        {
            return;
        }

        _disconnectNotified = true;

        if (!shouldNotify)
        {
            _logger.Info($"GameMonitor 已断开：{reason}");
            return;
        }
        _logger.Warn($"GameMonitor 断开：{reason}");
        _bubble.Show(
            "gamemonitor-offline",
            "游戏检测服务已断开。可点\"重连游戏检测\"重新启动它。",
            BubbleLevel.Warning);

        ClientDisconnected?.Invoke(this, reason);
    }

    /// <summary>托盘/设置面板的"重连游戏检测"按钮。</summary>
    public async Task<bool> ReconnectAsync(CancellationToken cancellationToken = default)
    {
        _logger.Info("收到\"重连游戏检测\"请求。");

        _bubble.Clear("gamemonitor-offline");

        if (RestartMonitorProcessCallback is null)
        {
            _logger.Warn("未注入 GameMonitor 重启回调，无法重连。");
            return false;
        }

        var ok = await RestartMonitorProcessCallback(cancellationToken).ConfigureAwait(false);

        _logger.Info($"GameMonitor 重连{(ok ? "成功" : "失败")}。");
        if (!ok)
        {
            _bubble.Show("gamemonitor-offline", "重连游戏检测失败，请检查 GameMonitor.exe 是否可运行。", BubbleLevel.Error);
        }

        return ok;
    }

    public void Stop()
    {
        // 先置位：下面的 Cancel/Stop 会让读循环的 finally 跑在本次 Stop 内部（等待接受循环时），
        // 那条路径必须同样按"不弹气泡"处理。
        _stopping = true;

        try
        {
            _watchdog?.Dispose();
            _watchdog = null;
        }
        catch
        {
            // 忽略。
        }

        try
        {
            _lifetime?.Cancel();
        }
        catch
        {
            // 忽略。
        }

        try
        {
            _listener?.Stop();
            _listener = null;
        }
        catch
        {
            // 忽略。
        }

        // 限时等待接受循环真正结束：_acceptTask 之前只赋值从不等待，
        // DisposeAsync/Stop 返回后它可能仍在跑（甚至还在给 _logger 写日志），
        // 进程退出时就成了"对象已释放但后台线程仍在用"的隐患。
        // 顺序很重要：先 Cancel + Listener.Stop 把 AcceptTcpClientAsync 打断，再等，否则必等满超时。
        if (_acceptTask is not null)
        {
            try
            {
                // 不无参 Wait：它会一直等到本轮读循环结束（对端不关连接时可能是几分钟），
                // 而 Stop 可能运行在 UI 线程上。
                if (!_acceptTask.Wait(TimeSpan.FromSeconds(1)))
                {
                    _logger.Warn("GameMonitor 接受循环 1 秒内未退出，放弃等待（后台线程可能仍未结束）。");
                }
            }
            catch (AggregateException ex)
            {
                _logger.Debug($"接受循环退出时异常：{ex.InnerException?.Message ?? ex.Message}");
            }

            _acceptTask = null;
        }

        // 自己在停止，不该给用户弹"游戏检测服务已断开"的告警气泡。
        HandleDisconnect("服务停止", notify: false);
    }

    private static string Truncate(string value) => value.Length <= 200 ? value : value[..200] + "...";

    public ValueTask DisposeAsync()
    {
        // Stop 已经按 notify:false 做完收尾（关 watchdog → 取消 CTS → 停 listener → 等接受循环 → 关 writer/client）。
        // 这里只补 CTS 本身的释放，避免与 Stop 重复释放 socket / writer。
        Stop();

        try
        {
            _lifetime?.Dispose();
        }
        catch
        {
            // 忽略。
        }

        return ValueTask.CompletedTask;
    }
}
