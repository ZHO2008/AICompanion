using System.Net;
using System.Net.Http;
using AICompanion.Core.Abstractions;
using AICompanion.Core.Configuration;

namespace AICompanion.Core.Health;

/// <summary>
/// 外部服务健康监控（手册 §3.8）。
///
/// 与手册逐条对应：
///   - 每 30 秒探活一次，单次超时 5 秒；
///   - 判定条件：状态码 200 **且** 主体包含 "status":"ok"；
///   - 失败累加 fail_count，达到 2（即连续失败 60 秒）才告警并置为 Offline；
///   - 成功则 fail_count 归零，并**立即清除**对应告警气泡；
///   - 休眠唤醒由表现层转发进来（<see cref="HandlePowerResumeAsync"/>），
///     立即全量检查 + fail_count 归零 + 对失败服务尝试重启一次。
/// </summary>
public sealed class HealthMonitor : IAsyncDisposable
{
    private const string LlamaKey = "llm";
    private const string ChatterboxKey = "chatterbox";

    private readonly AppConfig _config;
    private readonly IAppLogger _logger;
    private readonly IBubbleNotifier _bubble;
    private readonly HttpClient _http;
    private readonly object _sync = new();
    private readonly Dictionary<string, Probe> _probes = new(StringComparer.OrdinalIgnoreCase);

    private Timer? _timer;
    private volatile bool _checking;
    private bool _disposed;

    /// <param name="isChatterboxEnabled">
    /// 查询当前是否启用了 Chatterbox。由 TTSManager 提供，因为启动降级后
    /// 用户配置里写的是 chatterbox，但实际引擎可能已经是 Piper —— 那时不该继续探活 5000 端口。
    /// </param>
    public HealthMonitor(
        AppConfig config,
        IAppLogger logger,
        IBubbleNotifier bubble,
        Func<bool> isChatterboxEnabled)
    {
        _config = config;
        _logger = logger.ForCategory("Health");
        _bubble = bubble;

        // 只探测回环地址，禁用系统代理（本机 DNS 把外部域名指向 127.0.0.1，代理会干扰）。
        var handler = new HttpClientHandler { UseProxy = false, Proxy = null };
        _http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(5) };

        _probes[LlamaKey] = new Probe(
            LlamaKey,
            "LLM 服务",
            "llm-offline",
            "LLM 离线",
            $"http://{config.Llm.Host}:{config.Llm.Port}/health");

        _probes[ChatterboxKey] = new Probe(
            ChatterboxKey,
            "语音服务",
            "tts-offline",
            "语音服务离线",
            $"http://{config.Tts.Chatterbox.Host}:{config.Tts.Chatterbox.Port}/health");

        IsChatterboxEnabled = isChatterboxEnabled;
    }

    /// <summary>探活间隔（手册正文写死 30 秒，这里保留为常量以便测试加速）。</summary>
    public TimeSpan Interval { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>判定离线所需的连续失败次数（手册正文为 2）。</summary>
    public int FailureThreshold { get; init; } = 2;

    public Func<bool> IsChatterboxEnabled { get; }

    /// <summary>休眠唤醒时用来"尝试重启一次"的回调，由表现层注入。</summary>
    public Func<string, CancellationToken, Task<bool>>? ServiceRestartCallback { get; set; }

    public event EventHandler<ServiceHealthSnapshot>? StateChanged;

    public IReadOnlyList<ServiceHealthSnapshot> Snapshots
    {
        get
        {
            lock (_sync)
            {
                return _probes.Values.Select(p => p.ToSnapshot()).ToList();
            }
        }
    }

    /// <summary>启动监控并立即做一次全量检查（手册 §1.6 步骤 7：二次确认）。</summary>
    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        await CheckAllAsync(cancellationToken).ConfigureAwait(false);

        _timer = new Timer(_ => _ = CheckAllAsync(CancellationToken.None), null, Interval, Interval);
        _logger.Info($"健康监控已启动（间隔 {Interval.TotalSeconds:N0} 秒，离线阈值 {FailureThreshold} 次）。");
    }

    /// <summary>全量检查所有服务（手册 §3.8"立即调用 HealthMonitor 的全量检查方法"）。</summary>
    public async Task CheckAllAsync(CancellationToken cancellationToken = default)
    {
        if (_disposed)
        {
            return;
        }

        // 上一轮还没跑完就跳过本轮，避免休眠唤醒后探活请求堆积。
        if (_checking)
        {
            return;
        }

        _checking = true;
        try
        {
            var chatterboxEnabled = IsChatterboxEnabled();

            foreach (var probe in _probes.Values.ToList())
            {
                // 用户在配置里关掉的服务不探活：日志里刷"LLM 服务 探活失败"会把人吓一跳，
                // 而实际上是他自己关的。
                if (probe.Key == LlamaKey && !_config.Llm.Enabled)
                {
                    UpdateProbe(probe, success: null, error: null);
                    continue;
                }

                if (probe.Key == ChatterboxKey && (!chatterboxEnabled || !_config.Tts.Enabled))
                {
                    UpdateProbe(probe, success: null, error: null);
                    continue;
                }

                HealthProbeResult result;
                try
                {
                    result = await ProbeAsync(probe.Url, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    result = new HealthProbeResult(false, ex.Message);
                }

                UpdateProbe(probe, result.Success, result.Error);
            }
        }
        finally
        {
            _checking = false;
        }
    }

    /// <summary>把所有服务的 fail_count 归零（手册 §3.8 休眠唤醒流程第 2 步）。</summary>
    public void ResetFailCounts()
    {
        lock (_sync)
        {
            foreach (var probe in _probes.Values)
            {
                probe.FailCount = 0;
            }
        }

        _logger.Info("已重置所有服务的失败计数。");
    }

    /// <summary>
    /// 休眠唤醒后的处理（手册 §3.8）：
    ///   1. 立即全量检查；
    ///   2. fail_count 归零；
    ///   3. 对检查失败的服务尝试重启一次（仅一次）。
    /// </summary>
    public async Task HandlePowerResumeAsync(CancellationToken cancellationToken = default)
    {
        _logger.Info("检测到系统从休眠/待机恢复，开始重新探活。");

        ResetFailCounts();
        await CheckAllAsync(cancellationToken).ConfigureAwait(false);

        var failed = Snapshots.Where(s => s.State == ServiceState.Offline).ToList();
        if (failed.Count == 0)
        {
            _logger.Info("唤醒后全部服务正常。");
            return;
        }

        if (ServiceRestartCallback is null)
        {
            _logger.Warn("有服务在唤醒后不可用，但没有注入重启回调，仅提示用户。");
            return;
        }

        foreach (var service in failed)
        {
            try
            {
                _logger.Info($"唤醒后 {service.DisplayName} 不可用，尝试重启一次。");
                var ok = await ServiceRestartCallback(service.Key, cancellationToken).ConfigureAwait(false);
                _logger.Info($"重启 {service.DisplayName} {(ok ? "成功" : "失败")}（按设计只尝试一次）。");
            }
            catch (Exception ex)
            {
                _logger.Error($"重启 {service.DisplayName} 时异常：{ex.Message}", ex);
            }
        }

        await CheckAllAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<HealthProbeResult> ProbeAsync(string url, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await _http.GetAsync(url, cancellationToken).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

            if (response.StatusCode != HttpStatusCode.OK)
            {
                return new HealthProbeResult(false, $"HTTP {(int)response.StatusCode}");
            }

            var compact = body.Replace(" ", string.Empty, StringComparison.Ordinal);
            if (!compact.Contains("\"status\":\"ok\"", StringComparison.OrdinalIgnoreCase))
            {
                return new HealthProbeResult(false, $"响应未包含 status:ok：{Truncate(body)}");
            }

            return new HealthProbeResult(true, null);
        }
        catch (TaskCanceledException)
        {
            return new HealthProbeResult(false, "探活超时（5 秒）。");
        }
        catch (Exception ex)
        {
            return new HealthProbeResult(false, ex.Message);
        }
    }

    /// <summary>更新单个服务的状态机，并在跨过阈值/恢复时驱动气泡。</summary>
    private void UpdateProbe(Probe probe, bool? success, string? error)
    {
        ServiceHealthSnapshot? changed = null;

        lock (_sync)
        {
            probe.LastCheckedUtc = DateTime.UtcNow;

            if (success is null)
            {
                probe.State = ServiceState.Disabled;
                probe.FailCount = 0;
                probe.LastError = null;
            }
            else if (success.Value)
            {
                // 恢复：fail_count 归零并立即清除告警（手册 §3.8）。
                var wasOffline = probe.State == ServiceState.Offline;
                probe.FailCount = 0;
                probe.LastError = null;
                probe.State = ServiceState.Online;

                if (wasOffline)
                {
                    _bubble.Clear(probe.BubbleKey);
                    _logger.Info($"{probe.DisplayName} 已恢复在线，告警已清除。");
                }

                changed = probe.ToSnapshot();
            }
            else
            {
                probe.FailCount++;
                probe.LastError = error;

                if (probe.FailCount >= FailureThreshold && probe.State != ServiceState.Offline)
                {
                    probe.State = ServiceState.Offline;
                    _bubble.Show(
                        probe.BubbleKey,
                        $"{probe.OfflineMessage}（{error}）请在检查后手动重启程序。",
                        BubbleLevel.Error);
                    _logger.Error($"{probe.DisplayName} 判定离线：连续 {probe.FailCount} 次失败，原因：{error}");
                }
                else
                {
                    _logger.Warn($"{probe.DisplayName} 探活失败第 {probe.FailCount} 次：{error}");
                }

                changed = probe.ToSnapshot();
            }
        }

        if (changed is not null)
        {
            StateChanged?.Invoke(this, changed);
        }
    }

    private static string Truncate(string value) => value.Length <= 120 ? value : value[..120] + "...";

    private sealed record HealthProbeResult(bool Success, string? Error);

    private sealed class Probe
    {
        public Probe(string key, string displayName, string bubbleKey, string offlineMessage, string url)
        {
            Key = key;
            DisplayName = displayName;
            BubbleKey = bubbleKey;
            OfflineMessage = offlineMessage;
            Url = url;
        }

        public string Key { get; }

        public string DisplayName { get; }

        public string BubbleKey { get; }

        public string OfflineMessage { get; }

        public string Url { get; }

        public int FailCount { get; set; }

        public ServiceState State { get; set; } = ServiceState.Unknown;

        public string? LastError { get; set; }

        public DateTime LastCheckedUtc { get; set; }

        public ServiceHealthSnapshot ToSnapshot()
            => new(Key, DisplayName, State, FailCount, LastError, LastCheckedUtc);
    }

    public ValueTask DisposeAsync()
    {
        _disposed = true;

        try
        {
            _timer?.Dispose();
            _timer = null;
        }
        catch
        {
            // 忽略。
        }

        _http.Dispose();
        return ValueTask.CompletedTask;
    }
}
