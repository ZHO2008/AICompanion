using AICompanion.Core.Abstractions;
using AICompanion.Core.Asr;
using AICompanion.Core.Configuration;

namespace AsrRecoveryProbe;

/// <summary>
/// ASR 设备故障自愈的回归探针。
///
/// 背景（真实 bug，由 2 小时浸没测试抓到）：
///   麦克风因为驱动重载 / USB 设备休眠 / 默认设备切换而瞬断时，
///   NAudio 抛 <c>NoDriver calling waveInPrepareHeader</c>，
///   ASR 之前会**永久停在 Faulted**，只能手动重置。
///   修复后按 10 秒间隔重试，成功后自动恢复监听。
///
/// 本探针用假的采集器复现"启动成功 → 设备抛错 → 设备恢复"的序列，
/// 断言状态机确实能自己爬回 Running。
/// </summary>
internal static class Program
{
    private static int _passed;
    private static int _failed;

    private static int Main()
    {
        Console.WriteLine("=== ASR 设备故障自愈探针 ===");
        Console.WriteLine();

        var modelPath = Path.Combine(Environment.CurrentDirectory, "models", "asr", "ggml-base.bin");

        if (!File.Exists(modelPath))
        {
            Console.WriteLine($"  [跳过] 找不到模型文件：{modelPath}");
            Console.WriteLine("         本探针需要 models/asr/ggml-base.bin 才能进入可监听状态。");
            return 0;
        }

        var logger = new FakeLogger();
        var bubble = new FakeBubble();
        var recognizer = new FakeRecognizer();
        var capture = new FakeCapture();

        var config = new AppConfig();
        config.Asr.ModelPath = modelPath;
        config.Asr.VadMode = "energy";      // 纯托管，避免依赖原生 VAD
        config.Asr.SampleRate = 16000;

        using var manager = new ASRManager(config, logger, bubble, recognizer, capture);

        var states = new List<AsrState>();
        manager.StateChanged += (_, s) =>
        {
            lock (states)
            {
                states.Add(s);
            }
        };

        manager.InitializeAsync().GetAwaiter().GetResult();
        Check("初始化后应为 Stopped", manager.State == AsrState.Stopped, manager.State.ToString());

        // 让采集器在 Start 之后立刻抛设备错误（模拟 USB 瞬断）。
        capture.FailOnStart = true;
        manager.StartListening();

        // 等错误传播到状态机。
        if (!WaitFor(() => manager.State == AsrState.Faulted, TimeSpan.FromSeconds(5)))
        {
            Check("设备错误应导致 Faulted", false, manager.State.ToString());
            return Report();
        }

        Check("设备错误应导致 Faulted", true, manager.State.ToString());
        Check("故障原因应为 DeviceError", manager.FaultReason == AsrFaultReason.DeviceError, manager.FaultReason.ToString());
        Check("应提示过用户", bubble.Seen("asr-fault"), string.Join(",", bubble.Keys));

        // 设备"恢复"：下一次打开应当成功。
        capture.FailOnStart = false;

        Console.WriteLine("  等待自动恢复（约 10 秒）…");
        var recovered = WaitFor(() => manager.State == AsrState.Running, TimeSpan.FromSeconds(20));

        Check("设备恢复后应自动回到 Running", recovered, manager.State.ToString());
        Check("恢复后应清除故障原因", manager.FaultReason == AsrFaultReason.None, manager.FaultReason.ToString());
        Check("恢复后应清掉告警气泡", !bubble.Seen("asr-fault") || bubble.Cleared("asr-fault"), "气泡状态");

        var log = logger.All;
        Check("应记录重试日志", log.Any(l => l.Contains("自动恢复")), "未找到'自动恢复'日志");
        Check("应记录恢复成功", log.Any(l => l.Contains("已重新开始监听")), "未找到'已重新开始监听'");

        Check(
            "恢复后不应重复订阅采集事件",
            capture.SamplesSubscriptionCount <= 1,
            $"订阅数={capture.SamplesSubscriptionCount}");

        manager.StopListening();
        Check("停止后应为 Stopped", manager.State == AsrState.Stopped, manager.State.ToString());
        Check("停止后应完全退订", capture.SamplesSubscriptionCount == 0, $"订阅数={capture.SamplesSubscriptionCount}");

        Console.WriteLine();
        Console.WriteLine($"  状态轨迹：{string.Join(" → ", states)}");

        return Report();
    }

    private static bool WaitFor(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return true;
            }

            Thread.Sleep(100);
        }

        return condition();
    }

    private static void Check(string name, bool ok, string detail)
    {
        if (ok)
        {
            _passed++;
            Console.WriteLine($"  [通过] {name}");
        }
        else
        {
            _failed++;
            Console.WriteLine($"  [失败] {name}（实际：{detail}）");
        }
    }

    private static int Report()
    {
        Console.WriteLine();
        Console.WriteLine($"  通过 {_passed} 项，失败 {_failed} 项。");
        return _failed == 0 ? 0 : 1;
    }

    // ------------------------------------------------------------------ 假实现
    private sealed class FakeLogger : IAppLogger
    {
        private readonly List<string> _lines = new();

        public IReadOnlyList<string> All
        {
            get
            {
                lock (_lines)
                {
                    return _lines.ToList();
                }
            }
        }

        private void Add(string level, string message)
        {
            lock (_lines)
            {
                _lines.Add($"[{level}] {message}");
            }
        }

        public void Debug(string message, Exception? exception = null) => Add("DBG", message);

        public void Info(string message, Exception? exception = null) => Add("INF", message);

        public void Warn(string message, Exception? exception = null) => Add("WRN", message);

        public void Error(string message, Exception? exception = null) => Add("ERR", message);

        public IAppLogger ForCategory(string category) => this;
    }

    private sealed class FakeBubble : IBubbleNotifier
    {
        private readonly Dictionary<string, string> _shown = new();
        private readonly HashSet<string> _cleared = new();

        public IEnumerable<string> Keys
        {
            get
            {
                lock (_shown)
                {
                    return _shown.Keys.ToList();
                }
            }
        }

        public bool Seen(string key)
        {
            lock (_shown)
            {
                return _shown.ContainsKey(key);
            }
        }

        public bool Cleared(string key)
        {
            lock (_shown)
            {
                return _cleared.Contains(key);
            }
        }

        public void Show(string key, string message, BubbleLevel level = BubbleLevel.Info, TimeSpan? duration = null)
        {
            lock (_shown)
            {
                _shown[key] = message;
            }
        }

        public void Clear(string key)
        {
            lock (_shown)
            {
                _shown.Remove(key);
                _cleared.Add(key);
            }
        }

        public void ClearAll()
        {
            lock (_shown)
            {
                _shown.Clear();
            }
        }

        public void SetStatus(string key, string value)
        {
        }

        public void ClearStatus(string key)
        {
        }
    }

    private sealed class FakeRecognizer : ISpeechRecognizer
    {
        public bool IsReady => true;

        public string? StatusMessage => "假识别器";

        public Task<string> TranscribeAsync(float[] samples, int sampleRate, CancellationToken cancellationToken = default)
            => Task.FromResult(string.Empty);

        public void Reset()
        {
        }

        public void Dispose()
        {
        }
    }

    /// <summary>
    /// 假采集器：可以设定"打开就失败"，用来模拟设备瞬断。
    /// </summary>
    private sealed class FakeCapture : IAudioCapture
    {
        public bool FailOnStart { get; set; }

        public bool IsCapturing { get; private set; }

        public int SampleRate { get; private set; }

        private EventHandler<float[]>? _samples;

        private EventHandler<Exception>? _errors;

        /// <summary>当前挂了多少个 SamplesAvailable 处理器（用来抓"重复订阅"）。</summary>
        public int SamplesSubscriptionCount { get; private set; }

        public event EventHandler<float[]>? SamplesAvailable
        {
            add
            {
                _samples += value;
                SamplesSubscriptionCount++;
            }

            remove
            {
                _samples -= value;
                SamplesSubscriptionCount--;
            }
        }

        public event EventHandler<Exception>? CaptureError
        {
            add => _errors += value;
            remove => _errors -= value;
        }

        public void Start(int sampleRate)
        {
            SampleRate = sampleRate;

            if (FailOnStart)
            {
                IsCapturing = false;

                // 真实设备是异步报错的（NAudio 在自己的录音线程里抛），这里也异步。
                _ = Task.Run(async () =>
                {
                    await Task.Delay(100).ConfigureAwait(false);
                    _errors?.Invoke(this, new InvalidOperationException("NoDriver calling waveInPrepareHeader"));
                });

                return;
            }

            IsCapturing = true;
        }

        public void Stop()
        {
            IsCapturing = false;
        }

        public void Dispose()
        {
            Stop();
        }
    }
}