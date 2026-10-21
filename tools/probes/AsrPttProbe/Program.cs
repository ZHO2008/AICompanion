using AICompanion.Core.Abstractions;
using AICompanion.Core.Asr;
using AICompanion.Core.Configuration;
using AICompanion.Core.Input;

namespace AsrPttProbe;

/// <summary>
/// 按住说话（push-to-talk）的回归探针。
///
/// 验证的是这条链路：
///   按住 → 采集若干音频块进 PTT 缓冲 → 松开 → 整段送识别
///        → 繁体转简体 → 通过 SpeechRecognized 抛出
/// 以及两个边界：
///   - 按太短（低于 push_to_talk_min_ms）应判定误触、不触发识别
///   - PTT 模式不该走常开监听的 VAD 分段
/// </summary>
internal static class Program
{
    private static int _passed;
    private static int _failed;

    private static int Main()
    {
        Console.WriteLine("=== 按住说话（PTT）探针 ===");
        Console.WriteLine();

        var modelPath = Path.Combine(Environment.CurrentDirectory, "models", "asr", "ggml-base.bin");
        if (!File.Exists(modelPath))
        {
            Console.WriteLine($"  [跳过] 找不到模型文件：{modelPath}");
            return 0;
        }

        // ---------------------------------------------------------- 用例 1：正常按住
        Console.WriteLine("  [用例 1] 按住 1.2 秒后松开");
        {
            var logger = new FakeLogger();
            var bubble = new FakeBubble();
            var recognizer = new FakeRecognizer("今天天氣不錯,我們出去走吧");
            var capture = new FakeCapture(chunksPerPress: 60);   // 60 × 20ms = 1.2 秒

            var config = NewConfig(modelPath, minMs: 300);
            using var manager = new ASRManager(config, logger, bubble, recognizer, capture);

            var recognized = new List<string>();
            manager.SpeechRecognized += (_, text) =>
            {
                lock (recognized)
                {
                    recognized.Add(text);
                }
            };

            manager.InitializeAsync().GetAwaiter().GetResult();
            Check("初始化后 Stopped", manager.State == AsrState.Stopped, manager.State.ToString());
            Check("应识别为 PTT 模式", manager.IsPushToTalk, "IsPushToTalk=false");

            manager.BeginPushToTalk();
            Check("按下后进入 Running", manager.State == AsrState.Running, manager.State.ToString());
            Check("按下后开始采集", capture.IsCapturing, "IsCapturing=false");

            WaitFor(() => capture.EmittedChunks >= 60, TimeSpan.FromSeconds(5));

            manager.EndPushToTalk();
            Check("松开后回到 Stopped", manager.State == AsrState.Stopped, manager.State.ToString());
            Check("松开后停止采集", !capture.IsCapturing, "IsCapturing=true");

            WaitFor(() => { lock (recognized) { return recognized.Count > 0; } }, TimeSpan.FromSeconds(5));

            string? text = null;
            lock (recognized)
            {
                text = recognized.Count > 0 ? recognized[0] : null;
            }

            Check("应触发识别回调", text is not null, "未触发");
            Check("送识别的音频长度合理", recognizer.LastSamplesLength > 16000, $"样本数={recognizer.LastSamplesLength}");
            Check(
                "繁体应转成简体",
                text == "今天天气不错,我们出去走吧",
                text ?? "(null)");
            Check(
                "日志应有按下/松开记录",
                logger.All.Any(l => l.Contains("PTT 按下")) && logger.All.Any(l => l.Contains("PTT 松开")),
                "日志不全");
        }

        Console.WriteLine();

        // ---------------------------------------------------------- 用例 2：按太短
        Console.WriteLine("  [用例 2] 只按 100 毫秒");
        {
            var logger = new FakeLogger();
            var recognizer = new FakeRecognizer("不该被调用");
            var capture = new FakeCapture(chunksPerPress: 5);    // 5 × 20ms = 100ms

            var config = NewConfig(modelPath, minMs: 300);
            using var manager = new ASRManager(config, logger, new FakeBubble(), recognizer, capture);

            var called = false;
            manager.SpeechRecognized += (_, _) => called = true;

            manager.InitializeAsync().GetAwaiter().GetResult();
            manager.BeginPushToTalk();
            WaitFor(() => capture.EmittedChunks >= 5, TimeSpan.FromSeconds(3));
            manager.EndPushToTalk();

            Thread.Sleep(600);

            Check("误触不应触发识别", !called, "触发了识别");
            Check("误触不应调用识别器", recognizer.CallCount == 0, $"调用 {recognizer.CallCount} 次");
            Check(
                "日志应说明判定为误触",
                logger.All.Any(l => l.Contains("误触")),
                "无相关日志");
        }

        Console.WriteLine();

        // ---------------------------------------------------------- 用例 3：连续两次按住
        Console.WriteLine("  [用例 3] 连续两次按住（状态不残留）");
        {
            var recognizer = new FakeRecognizer("你好");
            var capture = new FakeCapture(chunksPerPress: 40);

            var config = NewConfig(modelPath, minMs: 300);
            using var manager = new ASRManager(config, new FakeLogger(), new FakeBubble(), recognizer, capture);

            var count = 0;
            manager.SpeechRecognized += (_, _) => Interlocked.Increment(ref count);

            manager.InitializeAsync().GetAwaiter().GetResult();

            for (var i = 0; i < 2; i++)
            {
                manager.BeginPushToTalk();
                WaitFor(() => capture.EmittedChunks >= 40 * (i + 1), TimeSpan.FromSeconds(5));
                manager.EndPushToTalk();
                WaitFor(() => Volatile.Read(ref count) > i, TimeSpan.FromSeconds(5));
            }

            Check("两次按住都应识别", Volatile.Read(ref count) == 2, $"识别 {count} 次");
            Check("结束后状态为 Stopped", manager.State == AsrState.Stopped, manager.State.ToString());
        }

        // ---------------------------------------------------------- 用例 4：按键解析
        Console.WriteLine();
        Console.WriteLine("  [用例 4] 按键解析（单键 / 双键 / 三键 / 非法）");
        {
            Check("Home 可解析", KeyCombo.TryParse("Home", out var m1, out var k1) && m1.Length == 0 && k1 == 0x24, "Home");

            Check("F2 可解析", KeyCombo.TryParse("F2", out var m2, out var k2) && m2.Length == 0 && k2 == 0x71, "F2");

            Check(
                "Alt+Q 可解析（双键）",
                KeyCombo.TryParse("Alt+Q", out var m3, out var k3) && m3.Length == 1 && m3[0] == KeyCombo.VkAlt && k3 == 0x51,
                "Alt+Q");

            Check(
                "Ctrl+Alt+Q 可解析（三键）",
                KeyCombo.TryParse("Ctrl+Alt+Q", out var m4, out var k4)
                    && m4.Length == 2 && m4[0] == KeyCombo.VkControl && m4[1] == KeyCombo.VkAlt && k4 == 0x51,
                "Ctrl+Alt+Q");

            Check("Ctrl+Shift+Home 可解析", KeyCombo.TryParse("Ctrl+Shift+Home", out var m5, out var k5) && m5.Length == 2 && k5 == 0x24, "Ctrl+Shift+Home");

            Check("非法键应拒绝", !KeyCombo.TryParse("NotAKey", out _, out _), "NotAKey 被接受了");
            Check("空值应拒绝", !KeyCombo.TryParse("", out _, out _), "空值被接受了");
            Check("未知修饰键应拒绝", !KeyCombo.TryParse("Hyper+Q", out _, out _), "Hyper+Q 被接受了");
        }

        // ---------------------------------------------------------- 用例 5：按住了但没收到音频
        Console.WriteLine();
        Console.WriteLine("  [用例 5] 按住 800ms 但只收到 40ms 音频（麦克风故障）");
        {
            var logger = new FakeLogger();
            var bubble = new FakeBubble();
            var recognizer = new FakeRecognizer("不该被调用");
            var capture = new FakeCapture(chunksPerPress: 2);    // 只有 40ms

            var config = NewConfig(modelPath, minMs: 300);
            using var manager = new ASRManager(config, logger, bubble, recognizer, capture);

            var called = false;
            manager.SpeechRecognized += (_, _) => called = true;

            manager.InitializeAsync().GetAwaiter().GetResult();
            manager.BeginPushToTalk();
            WaitFor(() => capture.EmittedChunks >= 2, TimeSpan.FromSeconds(3));

            // 按住时间拉长到 800ms，但采集只给了 40ms —— 这属于设备故障而不是误触
            Thread.Sleep(800);
            manager.EndPushToTalk();
            Thread.Sleep(400);

            Check("不应触发识别", !called, "触发了识别");
            Check("日志应指出没收到音频", logger.All.Any(l => l.Contains("只收到")), "没有该日志");
            Check("不应误报为误触", !logger.All.Any(l => l.Contains("判定为误触")), "误报为误触");
            Check("应弹出警告气泡", bubble.ShownCount > 0, "没有气泡");
        }

        return Report();
    }

    private static AppConfig NewConfig(string modelPath, int minMs)
    {
        var config = new AppConfig();
        config.Asr.ModelPath = modelPath;
        config.Asr.VadMode = "energy";
        config.Asr.SampleRate = 16000;
        config.Asr.PushToTalk = true;
        config.Asr.PushToTalkKey = "Ctrl+Space";
        config.Asr.PushToTalkMinMs = minMs;
        config.Asr.PushToTalkMaxSeconds = 60;
        return config;
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

            Thread.Sleep(20);
        }

        return condition();
    }

    private static void Check(string name, bool ok, string detail)
    {
        if (ok)
        {
            _passed++;
            Console.WriteLine($"    [通过] {name}");
        }
        else
        {
            _failed++;
            Console.WriteLine($"    [失败] {name}（实际：{detail}）");
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

        public int ShownCount
        {
            get
            {
                lock (_shown)
                {
                    return _shown.Count;
                }
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
        private readonly string _text;

        public FakeRecognizer(string text) => _text = text;

        public bool IsReady => true;

        public string? StatusMessage => "假识别器";

        public int CallCount { get; private set; }

        public int LastSamplesLength { get; private set; }

        public Task<string> TranscribeAsync(float[] samples, int sampleRate, CancellationToken cancellationToken = default)
        {
            CallCount++;
            LastSamplesLength = samples.Length;
            return Task.FromResult(_text);
        }

        public void Reset()
        {
        }

        public void Dispose()
        {
        }
    }

    /// <summary>
    /// 假采集器：每次 Start 之后按 20ms 一块吐 <c>chunksPerPress</c> 块，模拟"按住期间持续录音"。
    /// </summary>
    private sealed class FakeCapture : IAudioCapture
    {
        private readonly int _chunksPerPress;

        public FakeCapture(int chunksPerPress) => _chunksPerPress = chunksPerPress;

        public int EmittedChunks { get; private set; }

        public bool IsCapturing { get; private set; }

        public int SampleRate { get; private set; }

        public event EventHandler<float[]>? SamplesAvailable;

        public event EventHandler<Exception>? CaptureError;

        public void Start(int sampleRate)
        {
            SampleRate = sampleRate;
            IsCapturing = true;

            var target = EmittedChunks + _chunksPerPress;

            _ = Task.Run(async () =>
            {
                // 20 ms 一块 16kHz 单声道 = 320 个样本
                var chunk = new float[320];

                for (var i = 0; i < chunk.Length; i++)
                {
                    chunk[i] = (float)(Math.Sin(i * 0.05) * 0.2);   // 一点非静音内容
                }

                while (IsCapturing && EmittedChunks < target)
                {
                    SamplesAvailable?.Invoke(this, chunk);
                    EmittedChunks++;
                    await Task.Delay(5).ConfigureAwait(false);
                }
            });
        }

        public void Stop() => IsCapturing = false;

        public void Dispose() => Stop();
    }
}
