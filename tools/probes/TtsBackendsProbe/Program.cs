using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using AICompanion.Core.Abstractions;
using AICompanion.Core.Configuration;
using AICompanion.Core.Models;
using AICompanion.Core.Tts;

namespace TtsBackendsProbe;

/// <summary>
/// 语音合成后端实验：GPT-SoVITS（真服务）+ CosyVoice（本地桩服务）。
///
/// 为什么两家的验证方式不同：
///   - GPT-SoVITS 的整合包就在本机，可以真拉起 api_v2.py 做端到端合成，没必要造假；
///   - CosyVoice 依赖 Linux 侧的 pynini/WeTextProcessing，Windows 原生装不上，
///     所以这里用桩服务按官方 server.py 的**真实约定**（多部分表单 + 裸 PCM 响应）验证客户端，
///     等 WSL 里的服务起来后，同一个探针把端口指过去就是真实验证。
/// </summary>
internal static class Program
{
    private static int _passed;
    private static int _failed;

    private static async Task<int> Main()
    {
        Console.OutputEncoding = Encoding.UTF8;
        Console.WriteLine("=== 语音合成后端实验（GPT-SoVITS / CosyVoice） ===");
        Console.WriteLine();

        var logger = new ConsoleLogger();
        var root = FindRepositoryRoot();
        Console.WriteLine($"  仓库根目录：{root}");

        var configPath = Path.Combine(root, "config.yaml");
        AppPaths.Configure(configPath);
        var loaded = ConfigLoader.Load(configPath, logger);
        var config = loaded.Config;

        if (loaded.ParseError is not null)
        {
            Console.WriteLine($"  [警告] config.yaml 解析失败：{loaded.ParseError}");
        }

        TestWavHeader();
        TestUnavailableDetection(config, logger);
        await TestCosyVoiceStubAsync(config, logger).ConfigureAwait(false);
        await TestGptSoVitsRealAsync(config, logger, root).ConfigureAwait(false);
        await TestCosyVoiceRealAsync(config, logger, root).ConfigureAwait(false);
        await TestCosyVoiceInstructAsync(config, logger, root).ConfigureAwait(false);
        await TestManagerIntegrationAsync(config, logger).ConfigureAwait(false);

        Console.WriteLine();
        Console.WriteLine($"=== 结果：通过 {_passed}，失败 {_failed} ===");
        return _failed == 0 ? 0 : 1;
    }

    // ---------------------------------------------------------------- 1. WAV 封装

    private static void TestWavHeader()
    {
        Console.WriteLine("-- 1. 裸 PCM → WAV 封装 --");

        const int sampleRate = 24000;
        var pcm = new byte[4800]; // 0.1 秒，16 bit 单声道
        for (var i = 0; i + 1 < pcm.Length; i += 2)
        {
            var sample = (short)(Math.Sin(i / 20.0) * 8000);
            pcm[i] = (byte)(sample & 0xFF);
            pcm[i + 1] = (byte)((sample >> 8) & 0xFF);
        }

        var wav = HttpTtsEngineBase.WavFromPcm16(pcm, sampleRate);

        Check("RIFF 头", wav[0] == (byte)'R' && wav[1] == (byte)'I' && wav[2] == (byte)'F' && wav[3] == (byte)'F');
        Check("WAVE 标识", wav[8] == (byte)'W' && wav[9] == (byte)'A' && wav[10] == (byte)'V' && wav[11] == (byte)'E');
        Check("格式 PCM", BitConverter.ToInt16(wav, 20) == 1);
        Check("单声道", BitConverter.ToInt16(wav, 22) == 1);
        Check("采样率 24000", BitConverter.ToInt32(wav, 24) == sampleRate);
        Check("16 bit", BitConverter.ToInt16(wav, 34) == 16);
        Check("data 块长度 = PCM 长度", BitConverter.ToInt32(wav, 40) == pcm.Length);
        Check("总长度 = 44 + PCM", wav.Length == 44 + pcm.Length);
        Check("PCM 内容未损坏", wav[44] == pcm[0] && wav[wav.Length - 1] == pcm[pcm.Length - 1]);
    }

    // ---------------------------------------------------------- 2. 服务不可用时的行为

    private static void TestUnavailableDetection(AppConfig config, IAppLogger logger)
    {
        Console.WriteLine();
        Console.WriteLine("-- 2. 服务不可用时应快速判定 --");

        var saved = config.Tts.CosyVoice.Port;
        config.Tts.CosyVoice.Port = 1; // 保留端口，肯定连不上
        try
        {
            var engine = new CosyVoiceEngine(config, logger);
            var sw = Stopwatch.StartNew();
            var up = engine.RefreshAvailabilityAsync().GetAwaiter().GetResult();
            sw.Stop();

            Check("探活返回 false", !up);
            Check($"探活耗时 < 15 秒（实际 {sw.Elapsed.TotalSeconds:N1} 秒）", sw.Elapsed.TotalSeconds < 15);
            Check("IsAvailable 为 false", !engine.IsAvailable);
            engine.Unload();
        }
        finally
        {
            config.Tts.CosyVoice.Port = saved;
        }
    }

    // ------------------------------------------------- 3. CosyVoice：桩服务验证字段与封装

    private static async Task TestCosyVoiceStubAsync(AppConfig config, IAppLogger logger)
    {
        Console.WriteLine();
        Console.WriteLine("-- 3. CosyVoice 适配（本地桩服务，模拟官方 server.py） --");

        const int sampleRate = 24000;
        var pcm = BuildSinePcm(sampleRate, seconds: 1.0);

        using var stub = new StubServer { PcmResponse = pcm };

        var savedPort = config.Tts.CosyVoice.Port;
        var savedMode = config.Tts.CosyVoice.Mode;
        var savedPromptWav = config.Tts.CosyVoice.PromptWav;

        config.Tts.CosyVoice.Port = stub.Port;
        config.Tts.CosyVoice.SampleRate = sampleRate;
        config.Tts.CosyVoice.PromptWav = Path.Combine(FindRepositoryRoot(), "models", "tts", "ref_zh.wav");

        try
        {
            // ---- zero_shot：tts_text + prompt_text + prompt_wav 文件 ----
            config.Tts.CosyVoice.Mode = "zero_shot";
            var engine = new CosyVoiceEngine(config, logger);
            var up = await engine.RefreshAvailabilityAsync().ConfigureAwait(false);
            Check("桩服务 /docs 可达", up);

            var voice = new VoiceConfig { FallbackPiperVoice = "zh_CN-default", PromptText = "今天天气不错，我们一起去公园散步吧。" };
            var audio = await engine.SynthesizeAsync("你好，我是你的桌面伙伴。", voice).ConfigureAwait(false);

            Check("zero_shot 返回 WAV", audio.Length == 44 + pcm.Length);
            Check("zero_shot WAV 采样率 = 24000", BitConverter.ToInt32(audio, 24) == sampleRate);
            Check("zero_shot data 长度 = PCM", BitConverter.ToInt32(audio, 40) == pcm.Length);

            var body = Encoding.UTF8.GetString(stub.Bodies[^1]);
            Check("请求含 tts_text 字段", body.Contains("name=tts_text", StringComparison.Ordinal));
            Check("请求含合成文本", body.Contains("你好，我是你的桌面伙伴。", StringComparison.Ordinal));
            Check("请求含 prompt_text 字段", body.Contains("name=prompt_text", StringComparison.Ordinal));
            Check("请求含 prompt_wav 文件", body.Contains("name=prompt_wav", StringComparison.Ordinal) && body.Contains("ref_zh.wav", StringComparison.Ordinal));
            Check("请求打到 /inference_zero_shot", stub.Requests[^1].Contains("POST /inference_zero_shot", StringComparison.Ordinal));

            // ---- sft：tts_text + spk_id ----
            config.Tts.CosyVoice.Mode = "sft";
            config.Tts.CosyVoice.SpkId = "中文男";
            var sftEngine = new CosyVoiceEngine(config, logger);
            await sftEngine.RefreshAvailabilityAsync().ConfigureAwait(false);
            await sftEngine.SynthesizeAsync("第二句测试。", new VoiceConfig { FallbackPiperVoice = "zh_CN-default" }).ConfigureAwait(false);

            var sftBody = Encoding.UTF8.GetString(stub.Bodies[^1]);
            Check("sft 使用 /inference_sft", stub.Requests[^1].Contains("POST /inference_sft", StringComparison.Ordinal));
            Check("sft 携带 spk_id", sftBody.Contains("name=spk_id", StringComparison.Ordinal) && sftBody.Contains("中文男", StringComparison.Ordinal));
            Check("sft 不再携带 prompt_wav", !sftBody.Contains("name=prompt_wav", StringComparison.Ordinal));

            // ---- 缺参数时应给出可读的中文错误 ----
            config.Tts.CosyVoice.Mode = "zero_shot";
            config.Tts.CosyVoice.PromptText = "参考音频的原文。";
            config.Tts.CosyVoice.PromptWav = string.Empty;
            var badEngine = new CosyVoiceEngine(config, logger);
            try
            {
                await badEngine.SynthesizeAsync("不该成功。", new VoiceConfig { FallbackPiperVoice = "zh_CN-default" }).ConfigureAwait(false);
                Check("缺 prompt_wav 时抛异常", false);
            }
            catch (InvalidOperationException ex)
            {
                Check("缺 prompt_wav 时给出中文说明", ex.Message.Contains("prompt_wav", StringComparison.Ordinal));
            }

            engine.Unload();
            sftEngine.Unload();
        }
        finally
        {
            config.Tts.CosyVoice.Port = savedPort;
            config.Tts.CosyVoice.Mode = savedMode;
            config.Tts.CosyVoice.PromptWav = savedPromptWav;
        }
    }

    // ------------------------------------------------------- 4. GPT-SoVITS：真实服务

    private static async Task TestGptSoVitsRealAsync(AppConfig config, IAppLogger logger, string root)
    {
        Console.WriteLine();
        Console.WriteLine("-- 4. GPT-SoVITS 真实服务合成 --");

        config.Tts.GptSoVits.RefAudioPath = Path.Combine(root, "models", "tts", "ref_zh.wav");
        config.Tts.GptSoVits.PromptText = "今天天气不错，我们一起去公园散步吧。";

        var engine = new GptSoVitsEngine(config, logger);
        var up = await engine.RefreshAvailabilityAsync().ConfigureAwait(false);
        Check($"服务 {config.Tts.GptSoVits.Host}:{config.Tts.GptSoVits.Port} 可达", up);

        if (!up)
        {
            Console.WriteLine("  （跳过合成：GPT-SoVITS 服务未启动）");
            return;
        }

        var voice = new VoiceConfig { FallbackPiperVoice = "zh_CN-default" };
        var sw = Stopwatch.StartNew();
        var audio = await engine.SynthesizeAsync("你好，我是你的桌面伙伴，很高兴认识你。", voice).ConfigureAwait(false);
        sw.Stop();

        var seconds = BitConverter.ToInt32(audio, 40) / (double)(BitConverter.ToInt32(audio, 24) * 2);
        Console.WriteLine($"  合成耗时 {sw.Elapsed.TotalSeconds:N1} 秒，音频 {seconds:N2} 秒，{audio.Length} 字节");

        Check("返回 WAV", audio.Length > 44 && audio[0] == (byte)'R' && audio[8] == (byte)'W');
        Check("音频时长 > 1 秒", seconds > 1.0);
        Check("采样率合理（>= 16000）", BitConverter.ToInt32(audio, 24) >= 16000);

        var outDir = Path.Combine(root, "artifacts", "tts");
        Directory.CreateDirectory(outDir);
        var outFile = Path.Combine(outDir, "gpt_sovits_probe.wav");
        await File.WriteAllBytesAsync(outFile, audio).ConfigureAwait(false);
        Console.WriteLine($"  已写出：{outFile}");

        // 第二次合成，验证不是"只能跑一次"（真实使用中每句都要合成）。
        var second = await engine.SynthesizeAsync("这是第二句话，用来确认可以连续合成。", voice).ConfigureAwait(false);
        Check("连续第二次合成成功", second.Length > 44);

        engine.Unload();
    }

    // ---------------------------------------------------------------- 工具

    private static async Task TestCosyVoiceRealAsync(AppConfig config, IAppLogger logger, string root)
    {
        Console.WriteLine();
        Console.WriteLine("-- 5. CosyVoice 真实服务合成 --");

        var savedPort = config.Tts.CosyVoice.Port;
        var savedMode = config.Tts.CosyVoice.Mode;
        var savedWav = config.Tts.CosyVoice.PromptWav;
        var savedText = config.Tts.CosyVoice.PromptText;
        var savedPrefix = config.Tts.CosyVoice.PromptPrefix;

        config.Tts.CosyVoice.Port = 50000;
        config.Tts.CosyVoice.Mode = "zero_shot";
        config.Tts.CosyVoice.PromptWav = Path.Combine(root, "models", "tts", "ref_zh.wav");
        config.Tts.CosyVoice.PromptText = "今天天气不错，我们一起去公园散步吧。";
        // CosyVoice3 的 LLM 硬断言输入含 <|endofprompt|>，不带就是"生成 1 个 token"的假成功。
        config.Tts.CosyVoice.PromptPrefix = "You are a helpful assistant.<|endofprompt|>";

        var engine = new CosyVoiceEngine(config, logger);
        var up = await engine.RefreshAvailabilityAsync().ConfigureAwait(false);
        Check("服务 127.0.0.1:50000 可达", up);

        if (!up)
        {
            Console.WriteLine("  （跳过合成：CosyVoice 服务未启动）");
            Restore();
            return;
        }

        var voice = new VoiceConfig { FallbackPiperVoice = "zh_CN-default" };
        var sw = Stopwatch.StartNew();
        byte[] audio;
        try
        {
            audio = await engine.SynthesizeAsync("你好，我是你的桌面伙伴，今天想聊点什么？", voice).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Check($"CosyVoice 合成未抛异常：{ex.Message}", false);
            Restore();
            return;
        }

        sw.Stop();
        var seconds = audio.Length > 44 ? BitConverter.ToInt32(audio, 40) / (double)(BitConverter.ToInt32(audio, 24) * 2) : 0;
        Console.WriteLine($"  合成耗时 {sw.Elapsed.TotalSeconds:N1} 秒，音频 {seconds:N2} 秒，{audio.Length} 字节");

        Check("返回 WAV", audio.Length > 44 && audio[0] == (byte)'R' && audio[8] == (byte)'W');
        Check("音频时长 > 1 秒", seconds > 1.0);
        Check("采样率 24000", BitConverter.ToInt32(audio, 24) == 24000);
        Check("PCM 非全零（真的在说话）", audio.Skip(44).Any(b => b != 0));

        var outDir = Path.Combine(root, "artifacts", "tts");
        Directory.CreateDirectory(outDir);
        var outFile = Path.Combine(outDir, "cosyvoice_probe.wav");
        await File.WriteAllBytesAsync(outFile, audio).ConfigureAwait(false);
        Console.WriteLine($"  已写出：{outFile}");

        var second = await engine.SynthesizeAsync("这是第二句话，确认可以连续合成。", voice).ConfigureAwait(false);
        Check("连续第二次合成成功", second.Length > 44);

        engine.Unload();
        Restore();

        void Restore()
        {
            config.Tts.CosyVoice.Port = savedPort;
            config.Tts.CosyVoice.Mode = savedMode;
            config.Tts.CosyVoice.PromptWav = savedWav;
            config.Tts.CosyVoice.PromptText = savedText;
            config.Tts.CosyVoice.PromptPrefix = savedPrefix;
        }
    }

    /// <summary>
    /// 6. instruct2 的 endofprompt 自动补全：指令串故意不带 &lt;|endofprompt|&gt;，
    /// 引擎应当自己补在末尾，否则 CosyVoice3 的 LLM 断言会失败（表现为 500 或 1 个 token）。
    /// </summary>
    private static async Task TestCosyVoiceInstructAsync(AppConfig config, IAppLogger logger, string root)
    {
        Console.WriteLine();
        Console.WriteLine("-- 6. CosyVoice instruct2（自动补 endofprompt）--");

        var savedMode = config.Tts.CosyVoice.Mode;
        var savedWav = config.Tts.CosyVoice.PromptWav;
        var savedInstruct = config.Tts.CosyVoice.InstructText;
        var savedPrefix = config.Tts.CosyVoice.PromptPrefix;

        config.Tts.CosyVoice.Port = 50000;
        config.Tts.CosyVoice.Mode = "instruct2";
        config.Tts.CosyVoice.PromptWav = Path.Combine(root, "models", "tts", "ref_zh.wav");
        config.Tts.CosyVoice.PromptPrefix = "You are a helpful assistant.<|endofprompt|>";
        // 故意不带标记，验证引擎会补
        config.Tts.CosyVoice.InstructText = "用四川话说这句话";

        var engine = new CosyVoiceEngine(config, logger);
        var up = await engine.RefreshAvailabilityAsync().ConfigureAwait(false);
        Check("服务 127.0.0.1:50000 可达", up);

        if (!up)
        {
            Console.WriteLine("  （跳过：CosyVoice 服务未启动）");
            Restore();
            return;
        }

        var voice = new VoiceConfig { FallbackPiperVoice = "zh_CN-default" };
        var sw = Stopwatch.StartNew();
        byte[] audio;
        try
        {
            audio = await engine.SynthesizeAsync("今天天气真好，我们去河边走走吧。", voice).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Check($"instruct2 合成未抛异常：{ex.Message}", false);
            engine.Unload();
            Restore();
            return;
        }

        sw.Stop();
        var seconds = audio.Length > 44 ? BitConverter.ToInt32(audio, 40) / (double)(BitConverter.ToInt32(audio, 24) * 2) : 0;
        Console.WriteLine($"  合成耗时 {sw.Elapsed.TotalSeconds:N1} 秒，音频 {seconds:N2} 秒，{audio.Length} 字节");

        Check("返回 WAV", audio.Length > 44 && audio[0] == (byte)'R');
        Check("音频时长 > 0.5 秒（不是 1 个 token 的假成功）", seconds > 0.5);
        Check("采样率 24000", BitConverter.ToInt32(audio, 24) == 24000);

        var outFile = Path.Combine(root, "artifacts", "tts", "cosyvoice_instruct2.wav");
        Directory.CreateDirectory(Path.GetDirectoryName(outFile)!);
        await File.WriteAllBytesAsync(outFile, audio).ConfigureAwait(false);
        Console.WriteLine($"  已写出：{outFile}");

        engine.Unload();
        Restore();

        void Restore()
        {
            config.Tts.CosyVoice.Mode = savedMode;
            config.Tts.CosyVoice.PromptWav = savedWav;
            config.Tts.CosyVoice.InstructText = savedInstruct;
            config.Tts.CosyVoice.PromptPrefix = savedPrefix;
        }
    }

    /// <summary>
    /// 7. 管理器级集成：App 实际走的是 TTSManager（探活 → 降级链 → 合成 → 结果包装），
    /// 前面几项只测到引擎层，这里补上"管理器有没有把请求正确转给新引擎"。
    /// </summary>
    private static async Task TestManagerIntegrationAsync(AppConfig config, IAppLogger logger)
    {
        Console.WriteLine();
        Console.WriteLine("-- 7. TTSManager 集成（GPT-SoVITS） --");

        var savedEngine = config.Tts.Engine;
        config.Tts.Engine = "gpt_sovits";

        var bubble = new RecordingBubble();
        using var manager = new TTSManager(config, logger, bubble);

        await manager.InitializeAsync().ConfigureAwait(false);
        Check($"初始化后按配置选中 gpt_sovits（实际 {manager.CurrentEngineName}）",
            manager.CurrentEngineName == "gpt_sovits");
        Check("未降级到 Piper", !manager.IsDegraded);
        Check("ActiveHttpEngine 暴露端口 9880", manager.ActiveHttpEngine?.Port == 9880);

        var sw = Stopwatch.StartNew();
        var result = await manager.SynthesizeAsync("这句话来自管理器，不是直接调用引擎。").ConfigureAwait(false);
        sw.Stop();

        Check($"SynthesizeAsync 成功（{result.Error ?? "无错误"}）", result.Success);
        Check("返回的音频是 WAV", result.Audio is { Length: > 44 } && result.Audio[0] == (byte)'R');
        Console.WriteLine($"  管理器合成耗时 {sw.Elapsed.TotalSeconds:N1} 秒，{result.Audio?.Length ?? 0} 字节");

        var empty = await manager.SynthesizeAsync("   ").ConfigureAwait(false);
        Check("空文本被管理器拦下（不抛异常）", !empty.Success);

        manager.Dispose();
        config.Tts.Engine = savedEngine;
    }

    private static byte[] BuildSinePcm(int sampleRate, double seconds)
    {
        var samples = (int)(sampleRate * seconds);
        var pcm = new byte[samples * 2];
        for (var i = 0; i < samples; i++)
        {
            var value = (short)(Math.Sin(2 * Math.PI * 440 * i / sampleRate) * 12000);
            pcm[i * 2] = (byte)(value & 0xFF);
            pcm[i * 2 + 1] = (byte)((value >> 8) & 0xFF);
        }

        return pcm;
    }

    private static string FindRepositoryRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "AICompanion.sln")))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        return Directory.GetCurrentDirectory();
    }

    private static void Check(string name, bool ok)
    {
        if (ok)
        {
            _passed++;
            Console.WriteLine($"  [OK]   {name}");
        }
        else
        {
            _failed++;
            Console.WriteLine($"  [FAIL] {name}");
        }
    }
}

/// <summary>按官方 server.py 的约定返回裸 PCM 的最小 HTTP 桩。</summary>
internal sealed class StubServer : IDisposable
{
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _loop;

    public StubServer()
    {
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _loop = Task.Run(LoopAsync);
    }

    public int Port { get; }

    public List<string> Requests { get; } = new();

    public List<byte[]> Bodies { get; } = new();

    public byte[] PcmResponse { get; set; } = Array.Empty<byte>();

    private async Task LoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(_cts.Token).ConfigureAwait(false);
            }
            catch
            {
                return;
            }

            _ = Task.Run(() => HandleAsync(client));
        }
    }

    private async Task HandleAsync(TcpClient client)
    {
        using (client)
        {
            using var stream = client.GetStream();
            var buffer = new MemoryStream();
            var chunk = new byte[8192];
            var headerEnd = -1;

            while (headerEnd < 0)
            {
                var read = await stream.ReadAsync(chunk).ConfigureAwait(false);
                if (read <= 0)
                {
                    return;
                }

                buffer.Write(chunk, 0, read);
                headerEnd = FindHeaderEnd(buffer.GetBuffer(), (int)buffer.Length);
            }

            var all = buffer.ToArray();
            var headerText = Encoding.ASCII.GetString(all, 0, headerEnd);
            var contentLength = ParseContentLength(headerText);
            var body = all[(headerEnd + 4)..];

            while (body.Length < contentLength)
            {
                var read = await stream.ReadAsync(chunk).ConfigureAwait(false);
                if (read <= 0)
                {
                    break;
                }

                var merged = new byte[body.Length + read];
                body.CopyTo(merged, 0);
                Array.Copy(chunk, 0, merged, body.Length, read);
                body = merged;
            }

            lock (Requests)
            {
                Requests.Add(headerText);
                Bodies.Add(body);
            }

            var isDocs = headerText.StartsWith("GET /docs", StringComparison.Ordinal);
            var payload = isDocs ? Encoding.UTF8.GetBytes("{\"openapi\":\"3.0.0\"}") : PcmResponse;

            var head = Encoding.ASCII.GetBytes(
                "HTTP/1.1 200 OK\r\n" +
                $"Content-Type: {(isDocs ? "application/json" : "audio/wav")}\r\n" +
                $"Content-Length: {payload.Length}\r\n" +
                "Connection: close\r\n\r\n");

            await stream.WriteAsync(head).ConfigureAwait(false);
            await stream.WriteAsync(payload).ConfigureAwait(false);
            await stream.FlushAsync().ConfigureAwait(false);
        }
    }

    private static int FindHeaderEnd(byte[] data, int length)
    {
        for (var i = 0; i + 3 < length; i++)
        {
            if (data[i] == '\r' && data[i + 1] == '\n' && data[i + 2] == '\r' && data[i + 3] == '\n')
            {
                return i;
            }
        }

        return -1;
    }

    private static int ParseContentLength(string headerText)
    {
        foreach (var line in headerText.Split("\r\n"))
        {
            if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
            {
                return int.TryParse(line["Content-Length:".Length..].Trim(), out var value) ? value : 0;
            }
        }

        return 0;
    }

    public void Dispose()
    {
        _cts.Cancel();
        try
        {
            _listener.Stop();
        }
        catch
        {
            // 忽略。
        }

        _cts.Dispose();
    }
}

internal sealed class ConsoleLogger : IAppLogger
{
    public void Debug(string message, Exception? exception = null) => Write("DBG", message);

    public void Info(string message, Exception? exception = null) => Write("INF", message);

    public void Warn(string message, Exception? exception = null) => Write("WRN", message);

    public void Error(string message, Exception? exception = null) => Write("ERR", message);

    public IAppLogger ForCategory(string category) => new ConsoleLogger();

    private static void Write(string level, string message) =>
        Console.WriteLine($"    [{level}] {message}");
}

/// <summary>探针用的气泡实现：只记录，不弹任何 UI。</summary>
internal sealed class RecordingBubble : IBubbleNotifier
{
    public List<string> Messages { get; } = new();

    public void Show(string key, string message, BubbleLevel level = BubbleLevel.Info, TimeSpan? duration = null)
    {
        Messages.Add($"{level}:{key}:{message}");
        Console.WriteLine($"    [气泡/{level}] {message}");
    }

    public void Clear(string key)
    {
    }

    public void ClearAll()
    {
    }

    public void SetStatus(string key, string value) => Console.WriteLine($"    [状态] {key} = {value}");

    public void ClearStatus(string key)
    {
    }
}