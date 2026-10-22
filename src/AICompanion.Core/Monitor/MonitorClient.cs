using System.Net.Sockets;
using AICompanion.Core.Abstractions;
using AICompanion.Core.Configuration;

namespace AICompanion.Core.Monitor;

/// <summary>
/// GameMonitor.exe 侧的通信客户端（手册 §3.9 通信协议）。
///
/// 行为：
///   - 主动连接主程序的 127.0.0.1:47777，失败则每隔几秒重试（主程序可能尚未启动完）；
///   - 每 10 秒发送一次 ping（主程序按 30 秒无消息判定断连）；
///   - 接收主程序下发的 cooldown 消息并写入本地冷却表，避免在冷却期内盲发 unload。
/// </summary>
public sealed class MonitorClient : IAsyncDisposable
{
    private readonly AppConfig _config;
    private readonly IAppLogger _logger;
    private readonly GameCooldownTracker _cooldown;
    private readonly object _sync = new();

    private TcpClient? _client;
    private StreamWriter? _writer;
    private CancellationTokenSource? _lifetime;
    private Task? _runTask;

    public MonitorClient(AppConfig config, IAppLogger logger, GameCooldownTracker cooldown)
    {
        _config = config;
        _logger = logger.ForCategory("MonitorClient");
        _cooldown = cooldown;
    }

    public bool IsConnected
    {
        get
        {
            lock (_sync)
            {
                return _client?.Connected == true;
            }
        }
    }

    public event EventHandler? Connected;

    public event EventHandler<string>? Disconnected;

    /// <summary>收到主程序下发的冷却期通知。</summary>
    public event EventHandler<MonitorMessage>? CooldownReceived;

    public void Start(CancellationToken cancellationToken = default)
    {
        Stop();
        _lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _runTask = Task.Run(() => RunAsync(_lifetime.Token));
        _logger.Info($"已启动与主程序的连接循环（目标 {_config.Monitor.Host}:{_config.Monitor.Port}）。");
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await ConnectOnceAsync(cancellationToken).ConfigureAwait(false);
                await SessionAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.Warn($"连接主程序失败：{ex.Message}");
            }

            HandleDisconnect("连接中断");

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private async Task ConnectOnceAsync(CancellationToken cancellationToken)
    {
        var client = new TcpClient { NoDelay = true };

        try
        {
            await client.ConnectAsync(_config.Monitor.Host, _config.Monitor.Port, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // 连接失败时 _client 还没赋值，上层 HandleDisconnect 释放不到这个局部 client，
            // 不在这里 Dispose 就是每 5 秒漏一个 socket。
            client.Dispose();
            throw;
        }

        lock (_sync)
        {
            _client = client;
            _writer = new StreamWriter(client.GetStream(), MonitorProtocol.LineEncoding) { AutoFlush = true };
        }

        _logger.Info($"已连接主程序 {_config.Monitor.Host}:{_config.Monitor.Port}。");
        Connected?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>一次会话：心跳发送 + 消息接收。</summary>
    private async Task SessionAsync(CancellationToken cancellationToken)
    {
        TcpClient? client;
        lock (_sync)
        {
            client = _client;
        }

        if (client is null)
        {
            return;
        }

        using var reader = new StreamReader(client.GetStream(), MonitorProtocol.LineEncoding);
        using var heartbeatCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        var heartbeat = Task.Run(async () =>
        {
            var interval = TimeSpan.FromSeconds(Math.Max(1, _config.Monitor.HeartbeatIntervalSeconds));
            while (!heartbeatCts.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(interval, heartbeatCts.Token).ConfigureAwait(false);
                    await SendAsync(MonitorProtocol.Ping(), heartbeatCts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (Exception ex)
                {
                    _logger.Debug($"发送心跳失败：{ex.Message}");
                    return;
                }
            }
        }, heartbeatCts.Token);

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                if (line is null)
                {
                    break;
                }

                if (!MonitorProtocol.TryDecode(line, out var message, out var error) || message is null)
                {
                    _logger.Warn($"收到无法解析的消息（{error}）：{line}");
                    continue;
                }

                if (string.Equals(message.Type, MonitorMessageTypes.Cooldown, StringComparison.OrdinalIgnoreCase))
                {
                    if (message.Until.HasValue)
                    {
                        _cooldown.SetCooldownUntil(message.Process, message.Until.Value);
                        _logger.Info($"收到冷却期通知：进程 {message.Process} 至 {message.Until:O}。");
                        CooldownReceived?.Invoke(this, message);
                    }

                    continue;
                }

                _logger.Debug($"收到主程序消息：{message}");
            }
        }
        finally
        {
            await heartbeatCts.CancelAsync().ConfigureAwait(false);
        }
    }

    public async Task<bool> SendAsync(MonitorMessage message, CancellationToken cancellationToken = default)
    {
        StreamWriter? writer;
        lock (_sync)
        {
            writer = _writer;
        }

        if (writer is null)
        {
            return false;
        }

        try
        {
            await writer.WriteAsync(MonitorProtocol.Encode(message).AsMemory(), cancellationToken).ConfigureAwait(false);
            await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex)
        {
            _logger.Warn($"发送 {message.Type} 失败：{ex.Message}");
            return false;
        }
    }

    private void HandleDisconnect(string reason)
    {
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

        if (wasConnected)
        {
            _logger.Warn($"与主程序的连接已断开：{reason}");
            Disconnected?.Invoke(this, reason);
        }
    }

    public void Stop()
    {
        try
        {
            _lifetime?.Cancel();
        }
        catch
        {
            // 忽略。
        }

        HandleDisconnect("主动停止");
    }

    public ValueTask DisposeAsync()
    {
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
