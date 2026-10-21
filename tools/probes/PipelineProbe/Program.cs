using System.Diagnostics;
using System.Text.Json;
using AICompanion.Core.Abstractions;
using AICompanion.Core.Asr;
using AICompanion.Core.Audio;
using AICompanion.Core.Configuration;
using AICompanion.Core.Context;
using AICompanion.Core.Gpu;
using AICompanion.Core.Knowledge;
using AICompanion.Core.Llm;
using AICompanion.Core.Logging;
using AICompanion.Core.Models;
using AICompanion.Core.Pipeline;
using AICompanion.Core.Tts;

namespace PipelineProbe;

/// <summary>把气泡输出到控制台，便于在无人值守的验证里看到降级提示。</summary>
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

    public void SetStatus(string key, string value) => Console.WriteLine($"    [状态] {key} = {value}");

    public void ClearStatus(string key)
    {
    }
}

internal static class Program
{
    private static int _failures;

    private static async Task<int> Main()
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;

        Console.WriteLine("============================================================");
        Console.WriteLine("  对话管线端到端验证");
        Console.WriteLine("============================================================");
        Console.WriteLine($"工作目录：{Environment.CurrentDirectory}");
        Console.WriteLine();

        var bubbles = new ConsoleBubbleNotifier();
        var logger = FileLogger.CreateRoot(
            Path.Combine(Environment.CurrentDirectory, "logs", "app"),
            LogLevel.Info);

        // ---------------- 配置 ----------------
        var load = ConfigLoader.Load(null, logger);
        var config = load.Config;
        Console.WriteLine($"配置：{config.ConfigFilePath}");
        Console.WriteLine($"模型：{config.Llm.ModelPath}");

        // ---------------- 测试 0：日志轮转与清理（不依赖 llama-server，先跑）----------------
        Console.WriteLine();
        Console.WriteLine("--- 测试 0：日志轮转、清理与面板接口（手册 §5.1 / 阶段3 第6项）---");

        var logTestDir = Path.Combine(Path.GetTempPath(), "dsh-logtest-" + Guid.NewGuid().ToString("N").Substring(0, 8));
        try
        {
            var sink = new LogSink(logTestDir, LogLevel.Info, maxLogDays: 3);
            var log = new FileLogger(sink, "LogTest");

            log.Info("第一行");
            log.Warn("第二行");
            log.Error("第三行");
            log.Debug("调试行");

            var expectedName = $"app-{DateTime.Now:yyyyMMdd}.log";
            var logFile = Path.Combine(logTestDir, expectedName);
            Check("按日生成日志文件名 app-yyyyMMdd.log", File.Exists(logFile), expectedName);

            var logContent = ReadAllTextShared(logFile);
            Check("日志内容已落盘", logContent.Contains("第一行") && logContent.Contains("第三行"), $"{logContent.Length} 字符");
            Check("Debug 级别被过滤（minimum=Info）", !logContent.Contains("调试行"), logContent.Contains("调试行") ? "未过滤" : "已过滤");

            var recentLogs = sink.RecentSnapshot(10);
            Check("RecentSnapshot 返回最新在前", recentLogs.Count > 0 && recentLogs[0].Contains("第三行"), recentLogs.Count > 0 ? recentLogs[0][..Math.Min(40, recentLogs[0].Length)] : "空");

            var logDirSize = sink.GetDirectorySizeBytes();
            Check("GetDirectorySizeBytes 返回正数（面板展示占用）", logDirSize > 0, $"{logDirSize} 字节");

            // 造一个 10 天前的旧日志，验证按 max_log_days 清理
            var oldFile = Path.Combine(logTestDir, "app-20000101.log");
            File.WriteAllText(oldFile, "旧日志");
            File.SetLastWriteTime(oldFile, DateTime.Now.AddDays(-10));
            sink.CleanupOldFiles();
            Check("CleanupOldFiles 删除超期日志（保留 3 天）", !File.Exists(oldFile), File.Exists(oldFile) ? "未删除" : "已删除");
            Check("CleanupOldFiles 不误删当日日志", File.Exists(logFile), expectedName);

            var purged = sink.PurgeAll();
            Check("PurgeAll 返回删除数量", purged >= 0, $"{purged} 个（当日文件被占用属正常）");
            sink.Close();
        }
        finally
        {
            try
            {
                Directory.Delete(logTestDir, recursive: true);
            }
            catch
            {
                // 忽略
            }
        }

        // ---------------- 组装核心组件（与 AppHost 相同的顺序）----------------
        var smi = new NvidiaSmi(logger);
        var runtimeMode = new RuntimeModeState();

        var knowledge = new UnifiedKnowledgeManager(config, logger, bubbles);
        await knowledge.StartAsync();

        var gpu = new GPUMemoryManager(config, smi, runtimeMode, logger, bubbles);
        var llama = new LlamaServerController(config, gpu, runtimeMode, logger, bubbles);
        var audio = new NullAudioPlayer(logger);
        var tts = new TTSManager(config, logger, bubbles);
        var context = new ContextManager(config, logger, bubbles, knowledge, llama);
        var pipeline = new ConversationPipeline(config, logger, bubbles, knowledge, context, llama, tts, audio);

        await tts.InitializeAsync();
        Console.WriteLine();

        // ---------------- 启动 llama-server ----------------
        Console.WriteLine("--- 启动 llama-server（主程序持有进程，决策 Q5）---");
        var sw = Stopwatch.StartNew();
        var start = await llama.StartAsync();
        sw.Stop();

        Console.WriteLine($"  结果      : {start.Outcome}");
        Console.WriteLine($"  就绪耗时  : {sw.Elapsed.TotalSeconds:N1} 秒");
        Console.WriteLine($"  ngl       : {start.Ngl}");
        Console.WriteLine($"  运行模式  : {runtimeMode.Describe()}");
        if (start.Plan is not null)
        {
            Console.WriteLine($"  ngl 推导  : {start.Plan.Explanation}");
        }

        if (!start.Success)
        {
            Console.WriteLine();
            Console.WriteLine($"[FAIL] llama-server 未能启动：{start.Message}");
            await llama.DisposeAsync();
            return 2;
        }

        Check("llama-server 已就绪", true, start.Outcome.ToString());

        // ---------------- 健康探活 ----------------
        var health = await llama.Client.CheckHealthAsync();
        Check("HTTP /health 探活成功", health.Healthy, $"status={(int)health.StatusCode} body={health.Body.Trim()}");

        // ---------------- 测试 1：预处理知识命中（应当不调用 LLM）----------------
        Console.WriteLine();
        Console.WriteLine("--- 测试 1：预处理知识命中（手册 §4 步骤 3-4）---");

        var probeEntry = new KnowledgeEntry
        {
            Type = KnowledgeType.Preprocess,
            Priority = 999,
            Triggers = new List<string> { "验证触发词" },
            ReplyTemplate = "这是预处理知识回复。",
            Animation = "wave",
            VoiceConfig = new VoiceConfig { FallbackPiperVoice = "zh_CN-default" },
            Source = KnowledgeSource.Manual,
        };

        await knowledge.AddOrUpdateEntryAsync(probeEntry);
        Console.WriteLine($"  已写入测试条目，知识库现有 {knowledge.Count} 条");
        Check("热重载后能查到条目", knowledge.Count >= 1, $"count={knowledge.Count}");

        var hit = knowledge.Query("请回应：验证触发词 谢谢");
        Check("Query 命中预处理条目", hit is not null, hit is null ? "null" : $"id={hit.Id}");

        var t1 = Stopwatch.StartNew();
        var r1 = await pipeline.ProcessUserTextAsync("验证触发词");
        t1.Stop();

        Console.WriteLine($"  回复      : \"{r1.ReplyText}\"");
        Console.WriteLine($"  动画指令  : {r1.Animation ?? "(无)"}");
        Console.WriteLine($"  来自知识库: {r1.FromKnowledge}");
        Console.WriteLine($"  端到端耗时: {t1.ElapsedMilliseconds} ms");

        Check("回复等于模板", r1.ReplyText == "这是预处理知识回复。", r1.ReplyText);
        Check("标记为来自知识库", r1.FromKnowledge, r1.FromKnowledge.ToString());
        Check("取出动画指令", r1.Animation == "wave", r1.Animation ?? "(null)");
        Check("预处理延迟 <650ms（手册 §9）", t1.ElapsedMilliseconds < 650, $"{t1.ElapsedMilliseconds} ms");

        // ---------------- 测试 2：走 LLM 生成 ----------------
        Console.WriteLine();
        Console.WriteLine("--- 测试 2：LLM 生成（手册 §4 步骤 5）---");

        var t2 = Stopwatch.StartNew();
        var r2 = await pipeline.ProcessUserTextAsync("你好，请用一句话介绍你自己。");
        t2.Stop();

        Console.WriteLine($"  回复      : \"{Truncate(r2.ReplyText, 120)}\"");
        Console.WriteLine($"  成功      : {r2.Success}");
        Console.WriteLine($"  语音播放  : {r2.VoicePlayed}（Piper 未提供，预期为 False）");
        Console.WriteLine($"  端到端耗时: {t2.ElapsedMilliseconds} ms");

        Check("LLM 生成本次成功", r2.Success, r2.Error ?? "ok");
        Check("回复非空", !string.IsNullOrWhiteSpace(r2.ReplyText), $"{r2.ReplyText.Length} 字");
        Check("未误判为知识库回复", !r2.FromKnowledge, r2.FromKnowledge.ToString());

        // ---------------- 测试 3：上下文与日志 ----------------
        Console.WriteLine();
        Console.WriteLine("--- 测试 3：上下文与原文留存（手册 §3.2）---");

        var recent = context.RecentMessages;
        Check("RecentMessages 已累积", recent.Count >= 4, $"{recent.Count} 条");

        var built = context.BuildContext("你好");
        Check("BuildContext 非空", !string.IsNullOrWhiteSpace(built), $"{built.Length} 字符");
        Console.WriteLine("  BuildContext 预览：");
        foreach (var line in built.Split('\n').Take(6))
        {
            Console.WriteLine($"    | {Truncate(line, 90)}");
        }

        var chatLogDir = AppPaths.Resolve(config.Logging.ChatLogDir);
        var logFiles = Directory.Exists(chatLogDir)
            ? Directory.GetFiles(chatLogDir, "chat-*.jsonl")
            : Array.Empty<string>();
        Check("已写入当日聊天日志（原文永久保留）", logFiles.Length > 0,
            logFiles.Length > 0 ? Path.GetFileName(logFiles[0]) : "无");

        // ---------------- 测试 4：锚点记忆 ----------------
        Console.WriteLine();
        Console.WriteLine("--- 测试 4：锚点记忆（手册 §3.2）---");

        await pipeline.ProcessUserTextAsync("记住这个");
        Check("锚点记忆已记录", context.AnchorMemory.Count >= 1, $"{context.AnchorMemory.Count} 条");
        Check("锚点内容进入上下文", context.BuildContext(null).Contains("锚点记忆"), "含【必须牢记的锚点记忆】段");

        // ---------------- 测试 5：停止生成（断流取消，决策 Q6）----------------
        Console.WriteLine();
        Console.WriteLine("--- 测试 5：停止生成 / 断流取消（决策 Q6）---");
        Console.WriteLine("  说明：llama.cpp 没有 /stop 端点，官方取消方式是客户端断开连接。");
        Console.WriteLine("        这里验证管线在取消后能立刻返回，且服务端没有被弄坏。");

        var genTask = pipeline.ProcessUserTextAsync("请用中文详细写一篇关于春夏秋冬的长文，每个季节写满十句话。");

        var waitSw = Stopwatch.StartNew();
        while (!pipeline.IsGenerating && waitSw.ElapsedMilliseconds < 20000)
        {
            await Task.Delay(50);
        }

        Console.WriteLine($"  进入推理中耗时: {waitSw.ElapsedMilliseconds} ms（IsGenerating={pipeline.IsGenerating}）");
        Check("管线进入「推理中」状态", pipeline.IsGenerating, $"stage={pipeline.Stage}");

        // 让它先吐出一些 token，模拟用户看到内容后再点"停止生成"。
        await Task.Delay(1200);

        var cancelSw = Stopwatch.StartNew();
        await pipeline.StopGenerationAsync();
        var r5 = await genTask;
        cancelSw.Stop();

        Console.WriteLine($"  返回      : Success={r5.Success}，Error={r5.Error}");
        Console.WriteLine($"  取消耗时  : {cancelSw.ElapsedMilliseconds} ms");
        Check("取消后本次对话判定为未完成", !r5.Success, r5.Error ?? "(无错误信息)");
        Check("取消原因标注为已取消", r5.Error is not null && r5.Error.Contains("取消"), r5.Error ?? "null");
        Check("取消耗时 <5s（手册 §9）", cancelSw.ElapsedMilliseconds < 5000, $"{cancelSw.ElapsedMilliseconds} ms");

        var afterCancel = await llama.Client.CheckHealthAsync();
        Check("取消后服务仍然健康（未被打坏）", afterCancel.Healthy, afterCancel.FailureReason ?? "ok");

        // 关键证据：取消之后还能正常生成，说明断流没有把 llama-server 弄成半死状态。
        var r6 = await pipeline.ProcessUserTextAsync("只回复两个字：好的");
        Check("取消之后仍能正常生成新回复", r6.Success && !string.IsNullOrWhiteSpace(r6.ReplyText),
            r6.Success ? $"\"{Truncate(r6.ReplyText, 40)}\"" : (r6.Error ?? "失败"));

        // ---------------- 测试 6：WebRTC VAD（libfvad）----------------
        Console.WriteLine();
        Console.WriteLine("--- 测试 6：VAD 选择与判定（手册 §3.5 的 WebRTC VAD）---");

        var vadConfig = new AppConfig();
        vadConfig.Asr.VadMode = "auto";
        vadConfig.Asr.VadLibraryPath = "bin/libfvad.dll";
        vadConfig.Asr.SampleRate = 16000;
        vadConfig.Asr.VadAggressiveness = 2;

        using (var vad = VadDetectorFactory.Create(vadConfig, logger))
        {
            Console.WriteLine($"  实际使用的 VAD: {vad.Name}，帧长 {vad.FrameSamples} 采样");
            Check("auto 模式下使用 WebRTC VAD（而非能量兜底）", vad.Name == "webrtc", vad.Name);

            // 帧长必须是 VAD 要求的固定长度（20ms @16kHz = 320）
            Check("帧长为 20ms（320 采样 @16kHz）", vad.FrameSamples == 320, $"{vad.FrameSamples}");

            var frame = new float[vad.FrameSamples];

            // 1) 纯静音：应判为非语音
            vad.Reset();
            var silenceSpeech = vad.IsSpeech(frame, frame.Length);
            Check("静音帧判为非语音", !silenceSpeech, silenceSpeech.ToString());

            // 2) 极低幅噪声：应判为非语音
            vad.Reset();
            for (var i = 0; i < frame.Length; i++)
            {
                frame[i] = (float)(Math.Sin(i * 1.7) * 0.0005);
            }

            var noiseSpeech = vad.IsSpeech(frame, frame.Length);
            Check("极低幅噪声判为非语音", !noiseSpeech, noiseSpeech.ToString());

            /*
             * 3) 纯正弦音：这里只做**信息性输出**，不做断言。
             *
             * 我最初断言"纯正弦应被判为非语音"，后来实测反了两次：
             *   - 采样率没生效时，一切输入都因帧长非法返回 -1 → 全部"非语音"；
             *   - 采样率修好后，300Hz 满幅正弦在激进度 2 下被判为语音。
             * 后者是本类 VAD 的正常表现：WebRTC VAD 用各频带对数能量的 GMM，
             * 强低频音确实可能落到语音分布里，它并不是语音识别器。
             * 因此这里只打印结果，不做对错判断 —— 假装它应该拒绝反而是错的预期。
             */
            vad.Reset();
            for (var i = 0; i < frame.Length; i++)
            {
                frame[i] = (float)(0.8 * Math.Sin(2 * Math.PI * 300 * i / 16000.0));
            }

            var toneSpeech = vad.IsSpeech(frame, frame.Length);
            Console.WriteLine($"  [信息] 300Hz 满幅正弦 @激进度2 → {(toneSpeech ? "语音" : "非语音")}（不作断言）");

            /*
             * 4) 真实语音：用 Windows SAPI 合成的中文语音（tools/probes/testdata/speech.wav，
             *    16kHz / 16bit / 单声道，约 8.4 秒）。
             *
             *    为什么不用合成正弦/谐波：我先试过"多共振峰谐波 + 音节包络"的假信号，
             *    结果 0/25 帧被判为语音 —— WebRTC VAD 的判据是真实语音在 6 个频带上的
             *    对数能量分布（GMM 模型），人工构造的信号很难骗过它，也不该骗过它。
             *    用真实语音测才有意义。
             */
            var speechPath = Path.Combine(Environment.CurrentDirectory, "tools", "probes", "testdata", "speech.wav");
            var realSpeech = TryLoadWavPcm16Mono(speechPath);

            if (realSpeech is null)
            {
                Console.WriteLine($"  [SKIP] 未找到真实语音样本：{speechPath}");
                Console.WriteLine("         可用 SAPI 生成，见 VERIFICATION.md 的复现步骤。");
            }
            else
            {
                vad.Reset();

                var peak = realSpeech.Max(Math.Abs);
                Console.WriteLine($"  样本：{realSpeech.Length} 采样（{realSpeech.Length / 16000.0:N2}s），峰值 {peak:N3}");

                var totalFrames = realSpeech.Length / vad.FrameSamples;
                var speechFrames = 0;
                var oneFrame = new float[vad.FrameSamples];

                // 统计原始返回值的分布，区分"真的非语音"与"帧长非法"
                var rawCounts = new Dictionary<int, int>();
                var fvad = vad as FvadDetector;

                for (var f = 0; f < totalFrames; f++)
                {
                    Array.Copy(realSpeech, f * vad.FrameSamples, oneFrame, 0, vad.FrameSamples);
                    if (vad.IsSpeech(oneFrame, oneFrame.Length))
                    {
                        speechFrames++;
                    }

                    if (fvad is not null)
                    {
                        rawCounts.TryGetValue(fvad.LastRawResult, out var n);
                        rawCounts[fvad.LastRawResult] = n + 1;
                    }
                }

                if (fvad is not null)
                {
                    Console.WriteLine(
                        "  fvad_process 原始返回值分布：" +
                        string.Join(", ", rawCounts.OrderBy(kv => kv.Key).Select(kv => $"{kv.Key} → {kv.Value} 帧")));
                }

                var ratio = totalFrames > 0 ? 100.0 * speechFrames / totalFrames : 0;

                Check(
                    "真实语音被识别出语音帧",
                    speechFrames > 0,
                    $"{speechFrames}/{totalFrames} 帧判为语音（{ratio:N0}%）");

                // 8.4 秒连续说话，语音帧占比应该可观；过高说明过度触发，过低说明漏检。
                Check(
                    "语音帧占比处于合理区间（10%-95%）",
                    ratio is >= 10 and <= 95,
                    $"{ratio:N0}%");

                /*
                 * 激进度单调性：激进度越高，判定越严格，检出的语音帧不应变多。
                 * 这条测试验证的是 fvad_set_mode 真的生效（而不是被忽略），
                 * 比"某个信号应该被判成什么"稳健得多。
                 */
                var countAtMode0 = CountSpeechFrames(vadConfig, realSpeech, mode: 0, logger);
                var countAtMode3 = CountSpeechFrames(vadConfig, realSpeech, mode: 3, logger);

                Console.WriteLine($"  激进度对比：mode0={countAtMode0} 帧，mode2={speechFrames} 帧，mode3={countAtMode3} 帧");
                Check(
                    "激进度越高检出越少（fvad_set_mode 生效）",
                    countAtMode3 <= countAtMode0,
                    $"mode0={countAtMode0} → mode3={countAtMode3}");
            }
        }

        // 4) 库缺失时必须回退到能量 VAD，而不是抛异常或完全不可用
        var fallbackConfig = new AppConfig();
        fallbackConfig.Asr.VadMode = "auto";
        fallbackConfig.Asr.VadLibraryPath = "bin/this-does-not-exist.dll";
        using (var fallback = VadDetectorFactory.Create(fallbackConfig, logger))
        {
            Check("libfvad 缺失时自动回退能量 VAD", fallback.Name == "energy", fallback.Name);
        }

        // ---------------- 测试 7：上下文摘要压缩（手册 §3.2）----------------
        Console.WriteLine();
        Console.WriteLine("--- 测试 7：上下文摘要压缩与归档（手册 §3.2）---");

        var maxRounds = context.MaxRecentRounds;
        var archivePath = context.ArchivePath;
        var archiveLinesBefore = File.Exists(archivePath)
            ? File.ReadAllLines(archivePath).Length
            : 0;

        // 灌入 maxRounds + 10 条消息，制造"超出窗口"
        var injectCount = maxRounds + 10;
        for (var i = 1; i <= injectCount; i++)
        {
            context.AddMessage(i % 2 == 1
                ? ChatMessage.User($"注入测试消息 {i}：这是第 {i} 条，用于触发摘要压缩。")
                : ChatMessage.Assistant($"注入测试回复 {i}：收到第 {i} 条。"));
        }

        Console.WriteLine($"  注入后 RecentMessages = {context.RecentMessages.Count}（阈值 {maxRounds}）");
        Check("注入后消息数超出窗口", context.RecentMessages.Count > maxRounds, $"{context.RecentMessages.Count} > {maxRounds}");

        // 注意：压缩前窗口里除了本次注入的，还有前面几项测试累积的消息，
        // 因此"移出多少条"必须用 压缩前 − 压缩后 来算，不能用 注入数 − 剩余数。
        var countBeforeCompress = context.RecentMessages.Count;

        var compressSw = Stopwatch.StartNew();
        var compressed = await context.CompressIfNeededAsync();
        compressSw.Stop();

        Console.WriteLine($"  压缩结果 = {compressed}，耗时 {compressSw.ElapsedMilliseconds} ms");
        Console.WriteLine($"  压缩后 RecentMessages = {context.RecentMessages.Count}");
        Console.WriteLine($"  摘要栈条目数 = {context.SummaryStack.Count}");

        Check("压缩被触发", compressed, compressed.ToString());
        Check("窗口被收敛到阈值以内", context.RecentMessages.Count <= maxRounds,
            $"{context.RecentMessages.Count} <= {maxRounds}");

        var summaries = context.SummaryStack.Where(s => s.Source == SummarySource.Conversation).ToList();
        Check("摘要栈产生了近期摘要（L0）", summaries.Any(s => s.Level == 0),
            summaries.Count == 0 ? "无摘要" : string.Join(", ", summaries.Select(s => $"L{s.Level}({s.EstimatedTokens}tk)")));

        if (summaries.Count > 0)
        {
            var text = summaries.OrderBy(s => s.Level).First().Text;
            Console.WriteLine($"  摘要预览：\"{Truncate(text, 80)}\"");
            Check("摘要内容非空", !string.IsNullOrWhiteSpace(text), $"{text.Length} 字");
        }

        // 归档：被压出窗口的原文必须完整写入 chat_log_archive.jsonl
        var archiveLinesAfter = File.Exists(archivePath)
            ? File.ReadAllLines(archivePath).Length
            : 0;
        var archived = archiveLinesAfter - archiveLinesBefore;

        var removedCount = countBeforeCompress - context.RecentMessages.Count;

        Console.WriteLine($"  归档文件新增 {archived} 行（压缩移出 {removedCount} 条）");
        Check("被压缩的原文已归档（不丢数据）", archived > 0, $"+{archived} 行");
        Check("归档行数与移出窗口的消息数一致", archived == removedCount,
            $"归档 {archived} vs 移出 {removedCount}");

        /*
         * JSONL 契约：每行必须能独立反序列化成一个 ChatMessage。
         *
         * 这条断言是补上一个真实缺陷后加的：原来这里用的是 WriteIndented=true 的选项，
         * 一个消息会展开成 6 行左右的漂亮 JSON，于是"一行一个对象"的前提失效。
         * 后果是 ConversationRefiner 逐行解析几乎全部失败，而失败被逐行 catch 掉，
         * 表现为"整理器永远认为聊天记录太少"—— 静默故障，很难从现象定位。
         * 只校验本次新增的部分，避免受历史遗留内容影响。
         */
        if (File.Exists(archivePath))
        {
            var newLines = File.ReadAllLines(archivePath)
                .Skip(archiveLinesBefore)
                .Where(l => !string.IsNullOrWhiteSpace(l))
                .ToList();

            var parseable = 0;
            var broken = 0;

            foreach (var line in newLines)
            {
                try
                {
                    var parsed = JsonSerializer.Deserialize<ChatMessage>(line, KnowledgeFileStore.JsonLineOptions);
                    if (parsed is not null)
                    {
                        parseable++;
                    }
                    else
                    {
                        broken++;
                    }
                }
                catch
                {
                    broken++;
                }
            }

            Check("归档是合法 JSONL（每行可独立反序列化）", broken == 0,
                $"{parseable} 行可解析，{broken} 行失败");
            Check("每个归档行恰好对应一条消息", parseable == archived, $"{parseable} 条消息 / {archived} 行");
        }

        // 再压一次应当无事可做
        var secondTry = await context.CompressIfNeededAsync();
        Check("未超窗时压缩直接返回 false", !secondTry, secondTry.ToString());

        // ---------------- 测试 8：语音合成（SAPI 兜底）----------------
        Console.WriteLine();
        Console.WriteLine("--- 测试 8：语音合成（SAPI / Piper 兜底链）---");

        try
        {
            // 直接构造 SAPI 引擎，验证"任何 Windows 机器都能出声"这条兜底路径。
            var sapi = new SapiEngine(logger);
            Check("SAPI 引擎报告可用", sapi.IsAvailable, sapi.IsAvailable.ToString());

            if (sapi.IsAvailable)
            {
                var voiceConfig = new VoiceConfig { Language = "zh", FallbackPiperVoice = string.Empty };
                var ttsSw = Stopwatch.StartNew();
                var wav = await sapi.SynthesizeAsync("你好，我是你的桌面伙伴。", voiceConfig);
                ttsSw.Stop();

                Console.WriteLine($"  合成耗时 {ttsSw.ElapsedMilliseconds} ms，WAV {wav.Length} 字节");

                Check("合成返回非空 WAV", wav.Length > 1024, $"{wav.Length} 字节");
                Check("WAV 头正确（RIFF/WAVE）",
                    wav.Length > 12 &&
                    wav[0] == (byte)'R' && wav[1] == (byte)'I' && wav[2] == (byte)'F' && wav[3] == (byte)'F' &&
                    wav[8] == (byte)'W' && wav[9] == (byte)'A' && wav[10] == (byte)'V' && wav[11] == (byte)'E',
                    wav.Length > 12 ? "RIFF....WAVE" : "太短");

                sapi.Unload();
            }

            // 工厂：配置成 sapi 时必须给出 SapiEngine
            var ttsConfig = new AppConfig();
            ttsConfig.Tts.Engine = "sapi";
            var ttsContext = new TtsEngineContext(ttsConfig, logger, bubbles);
            var made = TTSEngineFactory.Create("sapi", ttsContext);
            Check("工厂能把引擎类型 sapi 解析为 SapiEngine", made is SapiEngine, made.GetType().Name);
            made.Unload();
        }
        catch (Exception ex)
        {
            Console.WriteLine("  异常详情：" + ex);
            Check("语音合成未抛异常", false, ex.Message);
        }

        // ---------------- 清理 ----------------
        Console.WriteLine();
        Console.WriteLine("--- 清理测试数据 ---");
        await knowledge.DeleteEntryAsync(probeEntry.Id);
        Console.WriteLine($"  已删除测试条目，知识库剩余 {knowledge.Count} 条");

        // ---------------- 关闭 ----------------
        Console.WriteLine();
        Console.WriteLine("--- 关闭 llama-server ---");
        var t3 = Stopwatch.StartNew();
        await llama.UnloadAsync();
        t3.Stop();
        Console.WriteLine($"  让出显存耗时: {t3.ElapsedMilliseconds} ms（手册 §9 要求 <5s）");
        Check("显存已让出（进程已终止）", !llama.IsRunning, $"IsRunning={llama.IsRunning}");

        await llama.DisposeAsync();

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

    /// <summary>
    /// 以共享方式读取正在被写入的文件。
    ///
    /// 不能用 File.ReadAllText：它内部以 FileShare.Read 打开，而写入端持有
    /// FileAccess.Write，Windows 的共享校验会要求读取方的 FileShare 也包含 Write，
    /// 否则报"文件正由另一进程使用"。这里显式声明 FileShare.ReadWrite 即可。
    /// </summary>
    private static string ReadAllTextShared(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    /// <summary>用指定激进度统计一段音频里被判为语音的帧数。</summary>
    private static int CountSpeechFrames(AppConfig config, float[] samples, int mode, IAppLogger logger)
    {
        var probeConfig = new AppConfig();
        probeConfig.Asr.VadMode = "auto";
        probeConfig.Asr.VadLibraryPath = config.Asr.VadLibraryPath;
        probeConfig.Asr.SampleRate = 16000;
        probeConfig.Asr.VadAggressiveness = mode;

        using var vad = VadDetectorFactory.Create(probeConfig, logger);
        vad.Reset();

        var frame = new float[vad.FrameSamples];
        var frames = samples.Length / vad.FrameSamples;
        var speech = 0;

        for (var f = 0; f < frames; f++)
        {
            Array.Copy(samples, f * vad.FrameSamples, frame, 0, vad.FrameSamples);
            if (vad.IsSpeech(frame, frame.Length))
            {
                speech++;
            }
        }

        return speech;
    }

    /// <summary>
    /// 读取 16bit PCM 的 WAV，返回 float 采样（-1..1）。
    /// 只支持单声道 16bit —— 测试样本就是按这个格式合成的。
    /// </summary>
    private static float[]? TryLoadWavPcm16Mono(string path)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            var bytes = File.ReadAllBytes(path);
            if (bytes.Length < 44 ||
                System.Text.Encoding.ASCII.GetString(bytes, 0, 4) != "RIFF" ||
                System.Text.Encoding.ASCII.GetString(bytes, 8, 4) != "WAVE")
            {
                return null;
            }

            var offset = 12;
            while (offset + 8 <= bytes.Length)
            {
                var chunkId = System.Text.Encoding.ASCII.GetString(bytes, offset, 4);
                var chunkSize = BitConverter.ToInt32(bytes, offset + 4);
                var body = offset + 8;

                if (chunkId == "data")
                {
                    var available = Math.Min(chunkSize, bytes.Length - body);
                    var count = available / 2;
                    var samples = new float[count];

                    for (var i = 0; i < count; i++)
                    {
                        var value = (short)(bytes[body + i * 2] | (bytes[body + i * 2 + 1] << 8));
                        samples[i] = value / 32768f;
                    }

                    return samples;
                }

                if (chunkSize < 0)
                {
                    return null;
                }

                offset = body + chunkSize + (chunkSize % 2);
            }

            return null;
        }
        catch
        {
            return null;
        }
    }

    private static void Check(string name, bool ok, string detail)
    {
        if (!ok)
        {
            _failures++;
        }

        Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] {name}  —— {detail}");
    }

    private static string Truncate(string value, int max)
        => string.IsNullOrEmpty(value) ? "(空)" : (value.Length <= max ? value : value[..max] + "…");
}
