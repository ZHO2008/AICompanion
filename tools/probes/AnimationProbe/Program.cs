using System.IO;
using System.Text;
using System.Windows.Threading;
using AICompanion.App.Animation;
using AICompanion.Core.Abstractions;

namespace AnimationProbe;

/// <summary>
/// 动画调度 / 角色包映射 / 口型同步的实验。
///
/// 为什么要有它：AnimationManager 的防抖、优先级打断、映射兜底都是**时间相关**的行为，
/// 光靠"看一眼代码"或"跑起来感觉一下"无法确认。这里用可控的 Dispatcher 泵 + 假时钟推进
/// 把每条规则逐一验证，并打印实际播放到的动作名。
/// </summary>
internal static class Program
{
    private static int _passed;
    private static int _failed;

    private static int Main()
    {
        Console.OutputEncoding = Encoding.UTF8;
        Console.WriteLine("=== 动画调度 / 角色包映射 / 口型同步 实验 ===");
        Console.WriteLine();

        // WPF Dispatcher 是 AnimationManager 切线程的唯一依赖；
        // 控制台程序里用 CurrentDispatcher + 手动泵帧来驱动它。
        var dispatcher = Dispatcher.CurrentDispatcher;

        var tempRoot = Path.Combine(Path.GetTempPath(), "anim-probe-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(tempRoot);

        try
        {
            TestProfileLoading(tempRoot);
            TestSafePlay(dispatcher);
            TestPriorityAndDebounce(dispatcher);
            TestLipSync();
            TestRealModel(dispatcher);
        }
        finally
        {
            try
            {
                Directory.Delete(tempRoot, recursive: true);
            }
            catch
            {
                // 忽略
            }
        }

        Console.WriteLine();
        Console.WriteLine($"通过 {_passed} 项，失败 {_failed} 项。");
        return _failed == 0 ? 0 : 1;
    }

    // ------------------------------------------------------------------ 一、配置加载

    private static void TestProfileLoading(string tempRoot)
    {
        Section("一、AvatarProfile 加载与兜底");

        var logger = new RecordingLogger();

        // 1) 正常加载
        var dir = Path.Combine(tempRoot, "normal");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, AvatarProfile.FileName), """
        {
          "avatar_name": "Huayan",
          "model_file": "Huayan.model3.json",
          "motion_map": {
            "Think": "think_motion",
            "Confused": "question_motion",
            "Fallback": "idle_motion"
          },
          "parameter_map": { "MouthOpen": "ParamMouthOpenY" }
        }
        """);

        var profile = AvatarProfile.Load(dir, logger, out var source);
        Check("avatar_name 正确", profile.AvatarName == "Huayan", profile.AvatarName);
        Check("motion_map 读到 Think→think_motion",
            profile.GetMotionOrNull(AnimationType.Think) == "think_motion",
            profile.GetMotionOrNull(AnimationType.Think) ?? "null");
        Check("motion_map 读到 Confused→question_motion",
            profile.GetMotionOrNull(AnimationType.Confused) == "question_motion",
            profile.GetMotionOrNull(AnimationType.Confused) ?? "null");
        Check("未配置的 Speak 被默认值补齐",
            profile.GetMotionOrNull(AnimationType.Speak) is not null,
            profile.GetMotionOrNull(AnimationType.Speak) ?? "null");
        Check("parameter_map 读到 MouthOpen→ParamMouthOpenY",
            profile.MouthOpenParameter == "ParamMouthOpenY",
            profile.MouthOpenParameter);
        Check("来源是该文件", source.EndsWith(AvatarProfile.FileName, StringComparison.OrdinalIgnoreCase), source);

        // 2) 键名小写也要能查到（JSON 反序列化会换成大小写敏感的字典，必须重建）
        var lowerDir = Path.Combine(tempRoot, "lower");
        Directory.CreateDirectory(lowerDir);
        File.WriteAllText(Path.Combine(lowerDir, AvatarProfile.FileName), """
        { "motion_map": { "think": "lower_think" } }
        """);
        var lower = AvatarProfile.Load(lowerDir, logger, out _);
        Check("小写键名 think 仍能命中（字典大小写不敏感）",
            lower.GetMotionOrNull(AnimationType.Think) == "lower_think",
            lower.GetMotionOrNull(AnimationType.Think) ?? "null");

        // 3) 文件不存在 → 内置默认 + 警告
        var missingDir = Path.Combine(tempRoot, "missing");
        Directory.CreateDirectory(missingDir);
        logger.Clear();
        var missing = AvatarProfile.Load(missingDir, logger, out var missingSource);
        Check("缺失文件时回退内置默认", missing.AvatarName == "builtin-default", missing.AvatarName);
        Check("缺失文件时记了 Warning", logger.WarnCount() > 0, $"{logger.WarnCount()} 条");
        Check("来源标注为内置默认", missingSource.Contains("内置默认"), missingSource);

        // 4) 损坏 JSON → 内置默认 + 警告
        var brokenDir = Path.Combine(tempRoot, "broken");
        Directory.CreateDirectory(brokenDir);
        File.WriteAllText(Path.Combine(brokenDir, AvatarProfile.FileName), "{ this is not json ");
        logger.Clear();
        var broken = AvatarProfile.Load(brokenDir, logger, out _);
        Check("损坏 JSON 时回退内置默认", broken.AvatarName == "builtin-default", broken.AvatarName);
        Check("损坏 JSON 时记了 Warning", logger.WarnCount() > 0, $"{logger.WarnCount()} 条");

        // 5) 空 motion_map → 内置默认
        var emptyDir = Path.Combine(tempRoot, "empty");
        Directory.CreateDirectory(emptyDir);
        File.WriteAllText(Path.Combine(emptyDir, AvatarProfile.FileName), """{ "avatar_name": "X", "motion_map": {} }""");
        logger.Clear();
        var empty = AvatarProfile.Load(emptyDir, logger, out _);
        Check("空 motion_map 时回退内置默认", empty.GetMotionOrNull(AnimationType.Think) is not null,
            empty.GetMotionOrNull(AnimationType.Think) ?? "null");
    }

    // ------------------------------------------------------------------ 二、安全播放

    private static void TestSafePlay(Dispatcher dispatcher)
    {
        Section("二、安全播放与兜底");

        // 只配 Idle / Fallback 的最小档案：用来验证"映射缺失 → 走 Fallback"
        var profile = new AvatarProfile
        {
            AvatarName = "minimal",
            MotionMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["Idle"] = "idle_ok",
                ["Fallback"] = "fallback_ok",
            },
            ParameterMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
        };

        var logger = new RecordingLogger();
        var played = new List<(AnimationType Type, string Motion)>();

        var manager = new AnimationManager(dispatcher, logger, profile, (t, m) => played.Add((t, m)));

        // 1) 已配置的动作直接播
        manager.Request(AnimationType.Idle, AnimationPriority.Critical, "test");
        Pump(dispatcher);
        Check("已配置动作按映射播放", played.Any(p => p.Motion == "idle_ok"), Describe(played));

        // 2) 未配置的动作（Happy）→ 降级到 Fallback
        played.Clear();
        logger.Clear();
        manager.Request(AnimationType.Happy, AnimationPriority.Critical, "test");
        Pump(dispatcher);
        Check("映射缺失时降级到 Fallback", played.Any(p => p.Motion == "fallback_ok"), Describe(played));
        Check("降级时记录了 Warning", logger.WarnCount() > 0, $"{logger.WarnCount()} 条");

        // 3) 模型里没有该动作 → 也要降级
        played.Clear();
        logger.Clear();
        var strict = new AnimationManager(
            dispatcher, logger, profile,
            (t, m) => played.Add((t, m)),
            motionExists: motion => motion != "idle_ok");   // 故意让 idle_ok 不存在

        strict.Request(AnimationType.Idle, AnimationPriority.Critical, "test");
        Pump(dispatcher);
        Check("模型缺少动作时降级到 Fallback", played.Any(p => p.Motion == "fallback_ok"), Describe(played));
        Check("缺少动作时记录了 Warning", logger.WarnCount() > 0, $"{logger.WarnCount()} 条");

        // 4) 连 Fallback 都没配 → 不播放
        played.Clear();
        logger.Clear();
        var noFallback = new AvatarProfile
        {
            MotionMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["Idle"] = "idle_ok" },
        };
        var bare = new AnimationManager(dispatcher, logger, noFallback, (t, m) => played.Add((t, m)));
        bare.Request(AnimationType.Confused, AnimationPriority.Critical, "test");
        Pump(dispatcher);
        Check("无映射且无 Fallback 时不播放", played.Count == 0, $"{played.Count} 次");
        Check("并记录了 Warning", logger.WarnCount() > 0, $"{logger.WarnCount()} 条");
    }

    // ------------------------------------------------------------------ 三、优先级与防抖

    private static void TestPriorityAndDebounce(Dispatcher dispatcher)
    {
        Section("三、优先级、打断与 1 秒防抖");

        var profile = AvatarProfile.CreateBuiltInDefault();
        var logger = new RecordingLogger();
        var played = new List<(AnimationType Type, string Motion)>();
        var manager = new AnimationManager(dispatcher, logger, profile, (t, m) => played.Add((t, m)));

        // 1) 1 秒内同/低优先级的第二次请求被丢弃
        played.Clear();
        manager.Request(AnimationType.Idle, AnimationPriority.Idle, "t1");
        Pump(dispatcher);
        var afterFirst = played.Count;

        manager.Request(AnimationType.Idle, AnimationPriority.Idle, "t2");
        Pump(dispatcher);
        Check("防抖窗口内同优先级请求被丢弃", played.Count == afterFirst, $"{afterFirst} → {played.Count}");

        // 2) 更高优先级可以打断（即使仍在 1 秒窗口内）
        manager.Request(AnimationType.Speak, AnimationPriority.Speak, "t3");
        Pump(dispatcher);
        Check("高优先级可在防抖窗口内打断", played.Count == afterFirst + 1, $"{played.Count} 次，最后 {played[^1].Type}");

        // 3) Critical 无视防抖（与当前同/低优先级也照播）
        var beforeCritical = played.Count;
        manager.Request(AnimationType.Confused, AnimationPriority.Critical, "t4");
        Pump(dispatcher);
        Check("Critical 无视防抖", played.Count == beforeCritical + 1, $"{played.Count} 次");

        // 4) 低优先级不能打断高优先级
        var beforeLow = played.Count;
        manager.Request(AnimationType.Idle, AnimationPriority.Idle, "t5");
        Pump(dispatcher);
        Check("低优先级请求被忽略", played.Count == beforeLow, $"{played.Count} 次");

        // 5) 等过 1 秒后同优先级可以再播
        Console.Write("  等待 1.1 秒跨过防抖窗口…");
        Thread.Sleep(1100);
        Console.WriteLine(" 完成");
        var beforeWait = played.Count;
        manager.Request(AnimationType.Idle, AnimationPriority.Idle, "t6");
        Pump(dispatcher);
        Check("跨过防抖窗口后可再次播放", played.Count == beforeWait + 1, $"{played.Count} 次");
    }

    // ------------------------------------------------------------------ 四、口型同步

    private static void TestLipSync()
    {
        Section("四、口型同步（RMS 与归零）");

        // 1) 静音的 RMS 必须是 0
        var silence = BuildWav(Array.Empty<short>(), 16000);
        var silenceRms = LipSyncDriver.ComputeRms(silence, 44, silence.Length - 44, 1);
        Check("静音 RMS 为 0", silenceRms == 0.0, silenceRms.ToString("F3"));

        // 2) 满幅方波的 RMS 接近上限（映射后被 clamp 到 1）
        var loud = BuildWav(Enumerable.Repeat((short)32000, 3200).ToArray(), 16000);
        var loudRms = LipSyncDriver.ComputeRms(loud, 44, loud.Length - 44, 1);
        Check("满幅音频 RMS 被映射到 1.0", Math.Abs(loudRms - 1.0) < 0.001, loudRms.ToString("F3"));

        // 3) 包络：0.2 秒音频应得到 4 个 50ms 片段，且峰值 > 0
        var sine = BuildSine(16000, 0.2, 440, 0.6);
        var envelope = LipSyncDriver.BuildEnvelope(sine, 50);
        Check("包络片段数符合 50ms 分桶", envelope.Length == 4, $"{envelope.Length} 段");
        Check("包络峰值大于 0", envelope.Length > 0 && envelope.Max() > 0.1, envelope.Length > 0 ? envelope.Max().ToString("F3") : "空");

        // 4) Stop 必须把嘴归零
        var dispatcher = Dispatcher.CurrentDispatcher;
        var values = new List<double>();
        var driver = new LipSyncDriver(dispatcher, v => values.Add(v), new RecordingLogger());

        driver.Start(sine);
        Thread.Sleep(300);                       // 让定时器跑几拍
        Pump(dispatcher);
        var sawOpen = values.Any(v => v > 0.01);
        Check("播放期间上报过非零口型", sawOpen, values.Count == 0 ? "无上报" : $"最大值 {values.Max():F3}");

        values.Clear();
        driver.Stop(resetMouth: true, reason: "probe");
        Pump(dispatcher);
        Check("停止后立刻归零", values.Count > 0 && values[^1] == 0.0, values.Count == 0 ? "无上报" : values[^1].ToString("F3"));

        driver.Dispose();
    }

    // ------------------------------------------------------------------ 五、真实模型

    /// <summary>
    /// 用仓库里真实的 Hiyori 模型做端到端验证：
    /// 解析 model3.json → 读动作组 → 加载角色包配置 → 映射 → 存在性检查 → 播放/兜底。
    /// </summary>
    private static void TestRealModel(Dispatcher dispatcher)
    {
        Section("五、真实模型（Hiyori）端到端");

        var modelPath = FindUpwards(@"Hiyori\Hiyori.model3.json");

        if (modelPath is null)
        {
            Console.WriteLine("  [SKIP] 未找到 Hiyori\\Hiyori.model3.json，跳过本组（不影响其他断言）");
            return;
        }

        var logger = new RecordingLogger();
        var info = Live2dModelInfo.TryLoad(modelPath, logger);

        Check("模型文件可解析", info is not null, modelPath);
        if (info is null)
        {
            return;
        }

        var groups = info.MotionGroups.OrderBy(g => g, StringComparer.OrdinalIgnoreCase).ToArray();
        Check("动作组 = [Idle, TapBody]", groups.SequenceEqual(new[] { "Idle", "TapBody" }), string.Join(", ", groups));
        Check("存在 Idle 动作", info.HasMotion("Idle"), "HasMotion(Idle)");
        Check("存在 TapBody 动作", info.HasMotion("TapBody"), "HasMotion(TapBody)");
        Check("不存在 Think 动作", !info.HasMotion("Think"), "HasMotion(Think)");
        Check("不存在 talk 动作", !info.HasMotion("talk"), "HasMotion(talk)");
        Check("口型参数来自 LipSync 组", info.LipSyncParameter == "ParamMouthOpenY", info.LipSyncParameter ?? "null");
        Check("眨眼参数有左右两个", info.EyeBlinkParameters.Length == 2,
            string.Join(", ", info.EyeBlinkParameters));

        // 加载真实角色包配置
        var profileDir = Path.GetDirectoryName(modelPath)!;
        var profile = AvatarProfile.Load(profileDir, logger, out var source);
        Check("角色包名 = Hiyori", profile.AvatarName == "Hiyori", profile.AvatarName);
        Check("来源是该角色的 AvatarProfile.json", source.EndsWith("AvatarProfile.json", StringComparison.OrdinalIgnoreCase), source);

        // 每一个逻辑动作映射到的动作组都必须真实存在，否则运行时会走兜底
        var missing = new List<string>();
        foreach (var type in Enum.GetValues<AnimationType>())
        {
            var motion = profile.GetMotionOrNull(type);
            if (motion is null || !info.HasMotion(motion))
            {
                missing.Add($"{type}→{motion ?? "null"}");
            }
        }

        Check("8 个逻辑动作全部映射到真实存在的动作组", missing.Count == 0,
            missing.Count == 0 ? "无缺口" : string.Join(", ", missing));

        // 真播放：Think 应映射到 Idle（模型没有 think 组，但配置把它指到 Idle）
        var played = new List<(AnimationType Type, string Motion)>();
        var manager = new AnimationManager(dispatcher, logger, profile, (t, m) => played.Add((t, m)), info.HasMotion);

        manager.Request(AnimationType.Think, AnimationPriority.Critical, "probe");
        Pump(dispatcher);
        Check("Think 播放到映射动作 Idle", played.Any(p => p.Type == AnimationType.Think && p.Motion == "Idle"),
            Describe(played));

        played.Clear();
        manager.Request(AnimationType.Confused, AnimationPriority.Critical, "probe");
        Pump(dispatcher);
        Check("Confused 播放到 TapBody", played.Any(p => p.Type == AnimationType.Confused && p.Motion == "TapBody"),
            Describe(played));

        // 故意把 Speak 映射到一个模型里不存在的组 → 应降级到 Fallback
        played.Clear();
        logger.Clear();
        var brokenProfile = new AvatarProfile
        {
            AvatarName = "hiyori-broken",
            MotionMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["Speak"] = "talk",        // 模型没有这个组
                ["Fallback"] = "Idle",
            },
        };
        var broken = new AnimationManager(dispatcher, logger, brokenProfile, (t, m) => played.Add((t, m)), info.HasMotion);

        broken.Request(AnimationType.Speak, AnimationPriority.Critical, "probe");
        Pump(dispatcher);
        Check("映射到不存在的组时降级为 Fallback(Idle)",
            played.Any(p => p.Motion == "Idle"), Describe(played));
        Check("降级时记录了 Warning", logger.WarnCount() > 0, $"{logger.WarnCount()} 条");
    }

    /// <summary>从 exe 目录往上找仓库里的相对路径（探针在 bin 目录里跑）。</summary>
    private static string? FindUpwards(string relativePath)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);

        for (var i = 0; i < 8 && dir is not null; i++)
        {
            var candidate = Path.Combine(dir.FullName, relativePath);
            if (File.Exists(candidate))
            {
                return candidate;
            }

            dir = dir.Parent;
        }

        return null;
    }

    // ------------------------------------------------------------------ 工具

    /// <summary>把 Dispatcher 队列里已排队的回调执行掉（控制台程序没有消息循环）。</summary>
    private static void Pump(Dispatcher dispatcher)
    {
        var frame = new DispatcherFrame();
        dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);

        // 再做一次，确保回调内部又 BeginInvoke 的也能跑完
        var frame2 = new DispatcherFrame();
        dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(() => frame2.Continue = false));
        Dispatcher.PushFrame(frame2);
    }

    private static byte[] BuildWav(short[] samples, int sampleRate)
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);

        var dataBytes = samples.Length * 2;

        w.Write(Encoding.ASCII.GetBytes("RIFF"));
        w.Write(36 + dataBytes);
        w.Write(Encoding.ASCII.GetBytes("WAVE"));
        w.Write(Encoding.ASCII.GetBytes("fmt "));
        w.Write(16);
        w.Write((short)1);                 // PCM
        w.Write((short)1);                 // 单声道
        w.Write(sampleRate);
        w.Write(sampleRate * 2);           // byte rate
        w.Write((short)2);                 // block align
        w.Write((short)16);                // bits
        w.Write(Encoding.ASCII.GetBytes("data"));
        w.Write(dataBytes);

        foreach (var s in samples)
        {
            w.Write(s);
        }

        w.Flush();
        return ms.ToArray();
    }

    private static byte[] BuildSine(int sampleRate, double seconds, double frequency, double amplitude)
    {
        var count = (int)(sampleRate * seconds);
        var samples = new short[count];

        for (var i = 0; i < count; i++)
        {
            samples[i] = (short)(Math.Sin(2 * Math.PI * frequency * i / sampleRate) * amplitude * short.MaxValue);
        }

        return BuildWav(samples, sampleRate);
    }

    private static void Section(string title)
    {
        Console.WriteLine();
        Console.WriteLine($"--- {title} ---");
    }

    private static void Check(string name, bool ok, string detail)
    {
        if (ok)
        {
            _passed++;
            Console.WriteLine($"  [PASS] {name}  —— {detail}");
        }
        else
        {
            _failed++;
            Console.WriteLine($"  [FAIL] {name}  —— {detail}");
        }
    }

    private static string Describe(List<(AnimationType Type, string Motion)> played)
        => played.Count == 0 ? "没有播放" : string.Join(" → ", played.Select(p => $"{p.Type}:{p.Motion}"));
}

/// <summary>把日志收进内存，便于断言"是否记录了 Warning"。</summary>
internal sealed class RecordingLogger : IAppLogger
{
    private readonly List<(LogLevel Level, string Message)> _lines;
    private readonly object _sync;
    private readonly string _category;

    public RecordingLogger(string category = "Probe")
        : this(category, new List<(LogLevel, string)>(), new object())
    {
    }

    /// <summary>
    /// 共享同一个缓冲区：<see cref="ForCategory"/> 会派生新实例，
    /// 若各自持有独立列表，断言父实例上的 WarnCount 永远是 0（这是探针自身的 bug）。
    /// </summary>
    private RecordingLogger(string category, List<(LogLevel Level, string Message)> lines, object sync)
    {
        _category = category;
        _lines = lines;
        _sync = sync;
    }

    public void Debug(string message, Exception? exception = null) => Add(LogLevel.Debug, message);

    public void Info(string message, Exception? exception = null) => Add(LogLevel.Info, message);

    public void Warn(string message, Exception? exception = null) => Add(LogLevel.Warn, message);

    public void Error(string message, Exception? exception = null) => Add(LogLevel.Error, message);

    public IAppLogger ForCategory(string category) => new RecordingLogger(category, _lines, _sync);

    public int WarnCount()
    {
        lock (_sync)
        {
            return _lines.Count(l => l.Level == LogLevel.Warn);
        }
    }

    public void Clear()
    {
        lock (_sync)
        {
            _lines.Clear();
        }
    }

    private void Add(LogLevel level, string message)
    {
        lock (_sync)
        {
            _lines.Add((level, message));
        }
    }
}
