using System.Net.Sockets;
using AICompanion.Core.Abstractions;
using AICompanion.Core.Configuration;
using AICompanion.Core.Logging;
using AICompanion.Core.Monitor;

namespace MonitorProbe;

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
/// 验证手册 §3.9 的 GameMonitor 通信协议与决策 Q11 的冷却期裁决。
///
/// 这里用的是**真实的** <see cref="MonitorServer"/> 与 <see cref="MonitorClient"/>
/// （后者正是 GameMonitor.exe 内部使用的那一个），通过回环 TCP 真跑一遍，
/// 而不是只调用方法看返回值。
/// </summary>
internal static class Program
{
    private static int _failures;

    private static async Task<int> Main()
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;

        Console.WriteLine("============================================================");
        Console.WriteLine("  GameMonitor 协议与冷却期验证（手册 §3.9 + 决策 Q11）");
        Console.WriteLine("============================================================");
        Console.WriteLine();

        // ---------------- 协议编解码（纯逻辑，先快速过一遍）----------------
        Console.WriteLine("--- 协议编解码 ---");

        foreach (var original in new[]
                 {
                     MonitorProtocol.Ping(),
                     MonitorProtocol.Unload("GenshinImpact", "GPU 85%"),
                     MonitorProtocol.Restore("GenshinImpact", "已退出"),
                     MonitorProtocol.Cooldown("GenshinImpact", DateTime.UtcNow.AddMinutes(5)),
                 })
        {
            var line = MonitorProtocol.Encode(original).TrimEnd('\n');
            var ok = MonitorProtocol.TryDecode(line, out var decoded, out var error) && decoded is not null;
            Check($"往返 {original.Type}", ok && decoded!.Type == original.Type,
                ok ? line : (error ?? "解析失败"));
        }

        var badOk = MonitorProtocol.TryDecode("{ not json", out _, out var badError);
        Check("非法 JSON 被拒绝且不抛异常", !badOk, badError ?? "(无错误信息)");

        // ---------------- 冷却期表语义 ----------------
        Console.WriteLine();
        Console.WriteLine("--- GameCooldownTracker 语义 ---");

        var tracker = new GameCooldownTracker();
        tracker.SetCooldown("GameA", TimeSpan.FromMinutes(5));
        Check("刚设置的进程处于冷却期", tracker.IsInCooldown("GameA"), "GameA");
        Check("大小写不敏感", tracker.IsInCooldown("gamea"), "gamea");
        Check("不同进程不受影响（手册明确要求）", !tracker.IsInCooldown("GameB"), "GameB");
        Check("剩余冷却时间为正", tracker.RemainingCooldown("GameA") > TimeSpan.Zero,
            tracker.RemainingCooldown("GameA").ToString(@"mm\:ss"));

        tracker.SetCooldownUntil("GameC", DateTime.UtcNow.AddSeconds(-1));
        Check("已过期的冷却自动失效", !tracker.IsInCooldown("GameC"), "GameC（已过期）");

        // ---------------- 真实 TCP 往返 ----------------
        Console.WriteLine();
        Console.WriteLine("--- 真实 TCP 往返（127.0.0.1:47777）---");

        var bubbles = new ConsoleBubbleNotifier();
        var logger = FileLogger.CreateRoot(Path.Combine(Environment.CurrentDirectory, "logs", "app"), LogLevel.Info);

        var config = new AppConfig();
        config.Monitor.Host = "127.0.0.1";
        config.Monitor.Port = 47777;
        config.Monitor.HeartbeatIntervalSeconds = 1;
        config.Monitor.DisconnectTimeoutSeconds = 30;
        config.Monitor.CooldownMinutes = 5;

        var serverCooldown = new GameCooldownTracker();
        await using var server = new MonitorServer(config, logger, bubbles, serverCooldown);

        var connected = 0;
        MonitorUnloadRequestEventArgs? lastUnload = null;
        MonitorRestoreRequestEventArgs? lastRestore = null;

        server.ClientConnected += (_, _) => Interlocked.Increment(ref connected);
        server.UnloadRequested += (_, e) => lastUnload = e;
        server.RestoreRequested += (_, e) => lastRestore = e;

        await server.StartAsync();
        Check("服务端已开始监听 47777", true, "TcpListener.Start 成功");

        var clientCooldown = new GameCooldownTracker();
        await using var client = new MonitorClient(config, logger, clientCooldown);
        client.Start();

        // 等客户端连上来（它会自动重试，这里最多等 15 秒）
        var waitSw = System.Diagnostics.Stopwatch.StartNew();
        while (!server.IsClientConnected && waitSw.ElapsedMilliseconds < 15000)
        {
            await Task.Delay(100);
        }

        Check("GameMonitor 客户端已连接", server.IsClientConnected, $"{waitSw.ElapsedMilliseconds} ms");
        Check("服务端收到 ClientConnected 事件", connected >= 1, $"{connected} 次");

        // 客户端心跳应刷新服务端的"最后收到消息时间"
        await Task.Delay(3000);
        Check("心跳（ping）已刷新最后收信时间",
            server.LastMessageUtc != DateTime.MinValue && (DateTime.UtcNow - server.LastMessageUtc).TotalSeconds < 5,
            $"{(DateTime.UtcNow - server.LastMessageUtc).TotalSeconds:N1} 秒前");

        // ---- unload（无冷却）应触发事件 ----
        await client.SendAsync(MonitorProtocol.Unload("GenshinImpact", "GPU 90%"));
        await Task.Delay(600);
        Check("unload 触发 UnloadRequested", lastUnload is not null, lastUnload?.GameProcess ?? "未触发");
        Check("unload 携带了游戏进程名", lastUnload?.GameProcess == "GenshinImpact", lastUnload?.GameProcess ?? "null");

        // ---- 冷却期裁决：同一进程在冷却期内应被拒绝 ----
        lastUnload = null;
        serverCooldown.SetCooldown("GenshinImpact", TimeSpan.FromMinutes(5));
        await client.SendAsync(MonitorProtocol.Unload("GenshinImpact", "GPU 90%"));
        await Task.Delay(600);
        Check("冷却期内的同进程 unload 被拒绝（决策 Q11 双保险）", lastUnload is null,
            lastUnload is null ? "已拒绝" : "竟然触发了");

        // ---- 不同进程不受冷却期影响（手册明确要求）----
        lastUnload = null;
        await client.SendAsync(MonitorProtocol.Unload("starrail", "GPU 90%"));
        await Task.Delay(600);
        Check("冷却期不影响其它进程的 unload", lastUnload?.GameProcess == "starrail",
            lastUnload?.GameProcess ?? "未触发");

        // ---- 服务端下发冷却期 → 客户端本地生效 ----
        await server.SendCooldownAsync("starrail", DateTime.UtcNow.AddMinutes(5));
        await Task.Delay(800);
        Check("客户端收到 cooldown 并写入本地表", clientCooldown.IsInCooldown("starrail"),
            clientCooldown.RemainingCooldown("starrail").ToString(@"mm\:ss"));
        Check("客户端不会误判无关进程", !clientCooldown.IsInCooldown("GenshinImpact"), "GenshinImpact");

        // ---- restore 携带"最后退出的进程名" ----
        await client.SendAsync(MonitorProtocol.Restore("GenshinImpact", "游戏已退出"));
        await Task.Delay(600);
        Check("restore 触发 RestoreRequested", lastRestore is not null, lastRestore?.LastGameProcess ?? "未触发");
        Check("restore 携带最后退出的进程名（决策 Q11 的绑定依据）",
            lastRestore?.LastGameProcess == "GenshinImpact", lastRestore?.LastGameProcess ?? "null");

        // ---- 心跳看门狗：静默客户端应被判为断连 ----
        Console.WriteLine();
        Console.WriteLine("--- 心跳看门狗（静默客户端应被判为断连）---");

        await client.DisposeAsync();
        await Task.Delay(1500);

        var disconnects = 0;
        server.ClientDisconnected += (_, _) => Interlocked.Increment(ref disconnects);

        // 用短阈值重新起一个服务端，配一个"连上后什么都不发"的裸客户端
        var shortConfig = new AppConfig();
        shortConfig.Monitor.Host = "127.0.0.1";
        shortConfig.Monitor.Port = 47778;
        shortConfig.Monitor.HeartbeatIntervalSeconds = 1;
        shortConfig.Monitor.DisconnectTimeoutSeconds = 3;

        await using var watchdogServer = new MonitorServer(shortConfig, logger, bubbles, new GameCooldownTracker());
        var watchdogDisconnects = 0;
        watchdogServer.ClientDisconnected += (_, reason) =>
        {
            Interlocked.Increment(ref watchdogDisconnects);
            Console.WriteLine($"    [看门狗] 判定断连：{reason}");
        };

        await watchdogServer.StartAsync();

        using var silent = new TcpClient();
        await silent.ConnectAsync("127.0.0.1", 47778);
        var silentSw = System.Diagnostics.Stopwatch.StartNew();
        while (!watchdogServer.IsClientConnected && silentSw.ElapsedMilliseconds < 5000)
        {
            await Task.Delay(100);
        }

        Check("静默客户端已接入", watchdogServer.IsClientConnected, $"{silentSw.ElapsedMilliseconds} ms");

        var timeoutSw = System.Diagnostics.Stopwatch.StartNew();
        while (watchdogDisconnects == 0 && timeoutSw.ElapsedMilliseconds < 20000)
        {
            await Task.Delay(200);
        }

        timeoutSw.Stop();
        Check("静默客户端被看门狗判定断连", watchdogDisconnects > 0, $"{timeoutSw.ElapsedMilliseconds} ms");
        Check("断连时给出了气泡提示",
            bubbles.Shown.Exists(b => b.Contains("游戏检测服务已断开")),
            string.Join(" | ", bubbles.Shown.Where(b => b.Contains("断开"))));
        Check("断连判定未过早（不早于阈值 3 秒）", timeoutSw.ElapsedMilliseconds >= 2500,
            $"{timeoutSw.ElapsedMilliseconds} ms");

        try
        {
            silent.Close();
        }
        catch
        {
            // 忽略
        }

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

    private static void Check(string name, bool ok, string detail)
    {
        if (!ok)
        {
            _failures++;
        }

        Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] {name}  —— {detail}");
    }
}
