using AICompanion.Core.Abstractions;
using AICompanion.Core.Asr;
using AICompanion.Core.Configuration;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace AsrPttRealProbe;

/// <summary>
/// 用「真实语音 + 真实 whisper 引擎」跑一遍按住说话链路。
///
/// 为什么需要它：按键模拟无法稳定保持数秒（Windows 会释放注入的按键），
/// 而纯假对象的探针又证明不了"真引擎真的能出字"。
/// 这里用 SAPI 合成一段中文语音写到 WAV（只写文件、不播放，不吵人），
/// 再把这段音频按 20 ms 一块喂进 ASRManager 的 PTT 缓冲，
/// 走的是和真机完全相同的路径：BeginPushToTalk → 缓冲 → EndPushToTalk → whisper 识别。
/// </summary>
internal static class Program
{
    private static int _passed;
    private static int _failed;

    private static int Main(string[] args)
    {
        Console.WriteLine("=== 按住说话（真实语音 + 真实引擎）探针 ===");
        Console.WriteLine();

        var root = AppContext.BaseDirectory;
        while (root is not null && !File.Exists(Path.Combine(root, "config.yaml")))
        {
            root = Path.GetDirectoryName(root.TrimEnd(Path.DirectorySeparatorChar));
        }

        root ??= Environment.CurrentDirectory;

        var wavPath = args.Length > 0
            ? args[0]
            : Path.Combine(root, "artifacts", "speech_zh.wav");

        var modelPath = Path.Combine(root, "models", "asr", "ggml-base.bin");

        Console.WriteLine($"  工作目录：{root}");
        Console.WriteLine($"  语音文件：{wavPath}");
        Console.WriteLine($"  模型文件：{modelPath}");
        Console.WriteLine();

        if (!File.Exists(wavPath))
        {
            Console.WriteLine("  [跳过] 找不到语音文件（先由 SAPI 生成 artifacts\\speech_zh.wav）。");
            return 0;
        }

        if (!File.Exists(modelPath))
        {
            Console.WriteLine("  [跳过] 找不到 whisper 模型。");
            return 0;
        }

        var samples = LoadWavAs16kMono(wavPath, out var duration);

        Console.WriteLine($"  音频：{duration.TotalSeconds:N2} 秒，{samples.Length} 个样本，RMS {Rms(samples):F4}");
        Console.WriteLine();

        var logger = new ConsoleLogger();
        var bubble = new SilentBubble();

        var config = new AppConfig();
        config.Asr.ModelPath = modelPath;
        config.Asr.LibraryPath = Path.Combine(root, "bin", "ai_asr.dll");
        config.Asr.VadMode = "webrtc";
        config.Asr.VadLibraryPath = Path.Combine(root, "bin", "libfvad.dll");
        config.Asr.SampleRate = 16000;
        config.Asr.PushToTalk = true;
        config.Asr.PushToTalkKey = "Home";
        config.Asr.PushToTalkMinMs = 300;
        config.Asr.PushToTalkMaxSeconds = 60;

        var recognizer = SpeechRecognizerFactory.Create(config, logger);
        if (!recognizer.IsReady)
        {
            Console.WriteLine($"  [跳过] 识别器不可用：{recognizer.StatusMessage}");
            return 0;
        }

        var capture = new ScriptedCapture(samples, chunkSize: 320);   // 20 ms @16k

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
        Check("识别器就绪", recognizer.IsReady, recognizer.StatusMessage ?? "?");

        // ---- 按下 → 整段音频进缓冲 ----
        manager.BeginPushToTalk();
        Check("按下后 Running", manager.State == AsrState.Running, manager.State.ToString());

        capture.PlayAll();

        // 等采集把数据全部交出去（假采集是同步吐，给一点缓冲时间）
        WaitFor(() => capture.Delivered >= samples.Length, TimeSpan.FromSeconds(10));
        Check("音频全部进入采集", capture.Delivered >= samples.Length, $"只送出了 {capture.Delivered}/{samples.Length}");

        // ---- 松开 → 识别 ----
        manager.EndPushToTalk();
        Check("松开后 Stopped", manager.State == AsrState.Stopped, manager.State.ToString());

        WaitFor(() => { lock (recognized) { return recognized.Count > 0; } }, TimeSpan.FromSeconds(60));

        string? text = null;
        lock (recognized)
        {
            text = recognized.Count > 0 ? recognized[0] : null;
        }

        Console.WriteLine();
        Console.WriteLine($"  识别结果：{(text is null ? "(无)" : $"\"{text}\"")}");
        Console.WriteLine($"  引擎耗时：{(manager.LastRecognition is { } r ? $"{r.Elapsed.TotalMilliseconds:N0} ms（RTF {r.RealTimeFactor:N2}）" : "无")}");
        Console.WriteLine();

        Check("应触发识别回调", text is not null, "未触发");
        Check("识别结果非空", !string.IsNullOrWhiteSpace(text), text ?? "(null)");

        // whisper base 对合成语音偶尔会漏字，只要抓到核心词就算通过
        var okWords = text is not null &&
                      (text.Contains("天气") || text.Contains("今天") || text.Contains("出去") || text.Contains("走"));

        Check("识别内容与原文相关", okWords, text ?? "(null)");

        Console.WriteLine();
        Console.WriteLine($"  通过 {_passed} 项，失败 {_failed} 项。");
        return _failed == 0 ? 0 : 1;
    }

    /// <summary>读 WAV → 重采样到 16 kHz 单声道 float。</summary>
    private static float[] LoadWavAs16kMono(string path, out TimeSpan duration)
    {
        using var reader = new WaveFileReader(path);
        ISampleProvider provider = reader.ToSampleProvider();

        if (provider.WaveFormat.Channels > 1)
        {
            provider = new MultiplexingSampleProvider(new[] { provider }, 1);
        }

        if (provider.WaveFormat.SampleRate != 16000)
        {
            provider = new WdlResamplingSampleProvider(provider, 16000);
        }

        var buffer = new float[16000 * 60];
        var total = 0;

        while (total < buffer.Length)
        {
            var read = provider.Read(buffer, total, buffer.Length - total);
            if (read <= 0)
            {
                break;
            }

            total += read;
        }

        var samples = new float[total];
        Array.Copy(buffer, samples, total);
        duration = TimeSpan.FromSeconds((double)total / 16000);
        return samples;
    }

    private static double Rms(float[] samples)
    {
        if (samples.Length == 0)
        {
            return 0;
        }

        double sum = 0;
        foreach (var s in samples)
        {
            sum += s * s;
        }

        return Math.Sqrt(sum / samples.Length);
    }

    private static void WaitFor(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return;
            }

            Thread.Sleep(20);
        }
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

    // ------------------------------------------------------------------ 假件
    /// <summary>按块把一段完整音频"播放"给 ASRManager，模拟按住期间持续采集。</summary>
    private sealed class ScriptedCapture : IAudioCapture
    {
        private readonly float[] _samples;
        private readonly int _chunkSize;
        private int _offset;

        public ScriptedCapture(float[] samples, int chunkSize)
        {
            _samples = samples;
            _chunkSize = chunkSize;
        }

        public int Delivered => Volatile.Read(ref _offset);

        public bool IsCapturing { get; private set; }

        public int SampleRate => 16000;

        public event EventHandler<float[]>? SamplesAvailable;

        public event EventHandler<Exception>? CaptureError;

        public void PlayAll()
        {
            while (Interlocked.CompareExchange(ref _offset, 0, 0) < _samples.Length && IsCapturing)
            {
                var start = _offset;
                var count = Math.Min(_chunkSize, _samples.Length - start);
                if (count <= 0)
                {
                    break;
                }

                var chunk = new float[count];
                Array.Copy(_samples, start, chunk, 0, count);
                Interlocked.Add(ref _offset, count);
                SamplesAvailable?.Invoke(this, chunk);
            }
        }

        public void Start(int sampleRate) => IsCapturing = true;

        public void Stop()
        {
            IsCapturing = false;
            _ = CaptureError;   // 探针里不会触发
        }

        public void Dispose() => Stop();
    }

    private sealed class ConsoleLogger : IAppLogger
    {
        private void Write(string level, string message)
        {
            if (level is "WRN" or "ERR")
            {
                Console.WriteLine($"    [{level}] {message}");
            }
        }

        public void Debug(string message, Exception? exception = null) => Write("DBG", message);

        public void Info(string message, Exception? exception = null) => Write("INF", message);

        public void Warn(string message, Exception? exception = null) => Write("WRN", message);

        public void Error(string message, Exception? exception = null) => Write("ERR", message);

        public IAppLogger ForCategory(string category) => this;
    }

    private sealed class SilentBubble : IBubbleNotifier
    {
        public void Show(string key, string message, BubbleLevel level = BubbleLevel.Info, TimeSpan? duration = null)
        {
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
}