using YamlDotNet.Serialization;

namespace AICompanion.Core.Configuration;

/// <summary>
/// config.yaml 的强类型映射（手册 §5.1）。
///
/// 与手册模板的关系：
///   - 手册给出的键（llm / tts / asr / live2d / knowledge / logging）**原样保留**，默认值也逐项照抄；
///   - 手册把大量数值写死在正文里（4GB 预留、1GB KV、150MB/层、0.9 系数、N=30、500 token、
///     30 条锚点、GameMonitor 端口等）。按准则 8"不得硬编码"，这些全部外提为下述配置节：
///     gpu / context / idle / monitor / ui。默认值仍等于手册正文的常量，行为不变。
/// </summary>
public sealed class AppConfig
{
    /// <summary>三级音色兜底的第三级（准则 10）。</summary>
    public const string HardcodedDefaultVoice = "zh_CN-default";

    /// <summary>硬编码兜底语言。</summary>
    public const string HardcodedDefaultLanguage = "zh";

    public LlmConfig Llm { get; set; } = new();

    public TtsConfig Tts { get; set; } = new();

    public AsrConfig Asr { get; set; } = new();

    public Live2dConfig Live2d { get; set; } = new();

    public KnowledgeConfig Knowledge { get; set; } = new();

    public LoggingConfig Logging { get; set; } = new();

    public MonitorConfig Monitor { get; set; } = new();

    public GpuConfig Gpu { get; set; } = new();

    public ContextConfig Context { get; set; } = new();

    public IdleConfig Idle { get; set; } = new();

    public UiConfig Ui { get; set; } = new();

    /// <summary>config.yaml 的绝对路径，用于解析相对路径（准则 8）。</summary>
    [YamlIgnore]
    public string ConfigFilePath { get; set; } = string.Empty;
}

public sealed class LlmConfig
{
    public string ModelPath { get; set; } = "models/qwen2.5-7b-q4_k_m.gguf";

    public string Host { get; set; } = "127.0.0.1";

    public int Port { get; set; } = 8080;

    public int ContextLength { get; set; } = 4096;

    /// <summary>模型层数 L。0 表示启动时从 GGUF 元数据自动探测（qwen2.block_count）。</summary>
    public int LayerCount { get; set; }

    /// <summary>llama-server 可执行文件路径，由主程序自行拉起（决策 Q5）。</summary>
    public string ServerExecutablePath { get; set; } = "bin/llama-server.exe";

    /// <summary>推理线程数，0 表示交给 llama.cpp 自行决定。</summary>
    public int Threads { get; set; }

    /// <summary>等待 /health 就绪的最长秒数。</summary>
    public int StartupTimeoutSeconds { get; set; } = 180;
}

public sealed class TtsConfig
{
    public string Engine { get; set; } = "piper";

    public string DefaultVoice { get; set; } = AppConfig.HardcodedDefaultVoice;

    public ChatterboxConfig Chatterbox { get; set; } = new();

    public PiperConfig Piper { get; set; } = new();

    /// <summary>
    /// GPT-SoVITS（api_v2.py 服务）对接配置。
    ///
    /// Alias 不能省：YamlDotNet 的 <c>UnderscoredNamingConvention</c> 会把 <c>GptSoVits</c>
    /// 推成 <c>gpt_so_vits</c>，而配置里写的是 <c>gpt_sovits</c>；不显式给别名的话，
    /// 这一节会被静默忽略（<c>IgnoreUnmatchedProperties</c>），运行时只看到默认值。
    /// </summary>
    [YamlMember(Alias = "gpt_sovits")]
    public GptSoVitsConfig GptSoVits { get; set; } = new();

    /// <summary>CosyVoice（官方 FastAPI server.py）对接配置。别名理由同上（<c>cosy_voice</c> ≠ <c>cosyvoice</c>）。</summary>
    [YamlMember(Alias = "cosyvoice")]
    public CosyVoiceConfig CosyVoice { get; set; } = new();

    /// <summary>Chatterbox 软重启阈值：连续运行超过该小时数才考虑重启（手册 §3.4）。</summary>
    public double SoftRestartHours { get; set; } = 4.0;

    /// <summary>软重启要求的空闲时长（分钟）。</summary>
    public double SoftRestartIdleMinutes { get; set; } = 5.0;
}

public sealed class ChatterboxConfig
{
    public string Host { get; set; } = "127.0.0.1";

    public int Port { get; set; } = 5000;

    /// <summary>chatterbox-api 的启动命令；为空表示由外部自行启动。</summary>
    public string LaunchCommand { get; set; } = string.Empty;

    /// <summary>
    /// 合成接口路径。手册没有规定 chatterbox-api 的路由，因此做成配置项而不是写死，
    /// 便于用户按自己部署的版本调整（准则 8）。
    /// </summary>
    public string SynthesizePath { get; set; } = "/synthesize";

    /// <summary>软重启接口路径（手册 §3.4）。不可用时退化为重建本地客户端实例。</summary>
    public string RestartPath { get; set; } = "/restart";
}

public sealed class PiperConfig
{
    public string ModelPath { get; set; } = "models/piper/zh_CN/model.onnx";

    /// <summary>Piper C API 动态库（手册 §3.4 走 P/Invoke）。</summary>
    public string LibraryPath { get; set; } = "third_party/piper/build/piper.dll";

    /// <summary>Piper 命令行可执行程序，作为 P/Invoke 不可用时的进程外兜底。</summary>
    public string ExecutablePath { get; set; } = "tools/piper/piper.exe";

    /// <summary>espeak-ng 数据目录（Piper 做音素化时需要）。</summary>
    public string EspeakDataPath { get; set; } = "third_party/piper/espeak-ng-data";
}

/// <summary>
/// GPT-SoVITS 对接配置（对接 api_v2.py，默认端口 9880）。
///
/// 支持三种用法，可自由组合：
///   1. 用训练好的模型：填 <c>gpt_weights_path</c>（*.ckpt）+ <c>sovits_weights_path</c>（*.pth）；
///   2. 零样本：只填 <c>ref_audio_path</c> + <c>prompt_text</c>；
///   3. 训练模型 + 指定参考音频，音色最可控。
/// </summary>
public sealed class GptSoVitsConfig
{
    public string Host { get; set; } = "127.0.0.1";

    public int Port { get; set; } = 9880;

    /// <summary>合成接口路径（GET/POST 都支持，这里统一用 POST JSON）。</summary>
    public string TtsPath { get; set; } = "/tts";

    /// <summary>训练好的 GPT 权重（*.ckpt）。留空则沿用服务端当前模型。</summary>
    public string GptWeightsPath { get; set; } = string.Empty;

    /// <summary>训练好的 SoVITS 权重（*.pth）。留空则沿用服务端当前模型。</summary>
    public string SovitsWeightsPath { get; set; } = string.Empty;

    /// <summary>参考音频（3~10 秒干净人声）。零样本或需要锁定音色时必填。</summary>
    public string RefAudioPath { get; set; } = string.Empty;

    /// <summary>参考音频的原文。必须与音频内容一致，否则音色/语调会跑偏。</summary>
    public string PromptText { get; set; } = string.Empty;

    /// <summary>参考音频语种：zh / en / ja / ko / yue 等（GPT-SoVITS 的取值）。</summary>
    public string PromptLang { get; set; } = "zh";

    /// <summary>待合成文本语种。</summary>
    public string TextLang { get; set; } = "zh";

    /// <summary>切句策略，cut0~cut5，默认 cut5（标点切分，最稳）。</summary>
    public string TextSplitMethod { get; set; } = "cut5";

    /// <summary>全局语速系数，与角色 Speed 相乘。</summary>
    public double SpeedFactor { get; set; } = 1.0;

    public int TopK { get; set; } = 15;

    public double TopP { get; set; } = 1.0;

    public double Temperature { get; set; } = 1.0;

    /// <summary>合成超时。首次调用要建 CUDA 图，几十秒很正常，默认给足 120 秒。</summary>
    public int TimeoutSeconds { get; set; } = 120;

    /// <summary>启动命令；为空表示由用户自己先启动服务。</summary>
    public string LaunchCommand { get; set; } = string.Empty;
}

/// <summary>
/// CosyVoice 2 / 3 对接配置（对接官方 runtime/python/fastapi/server.py，默认端口 50000）。
///
/// 服务端返回的是**裸 PCM**（无 WAV 头），所以 <see cref="SampleRate"/> 必须与模型匹配：
/// CosyVoice2 / CosyVoice3 是 24000，CosyVoice 1 是 22050。
/// </summary>
public sealed class CosyVoiceConfig
{
    public string Host { get; set; } = "127.0.0.1";

    public int Port { get; set; } = 50000;

    /// <summary>推理模式：sft / zero_shot / cross_lingual / instruct / instruct2。</summary>
    public string Mode { get; set; } = "zero_shot";

    /// <summary>SFT 预训练音色的说话人 id（sft / instruct 模式使用）。</summary>
    public string SpkId { get; set; } = string.Empty;

    /// <summary>零样本模式的参考音频原文。</summary>
    public string PromptText { get; set; } = string.Empty;

    /// <summary>参考音频路径（zero_shot / cross_lingual / instruct2 使用）。</summary>
    public string PromptWav { get; set; } = string.Empty;

    /// <summary>指令文本（instruct / instruct2 使用），如"用四川话说这句话"。</summary>
    public string InstructText { get; set; } = string.Empty;

    /// <summary>
    /// CosyVoice3 必须的指令前缀。CosyVoice3 的 LLM 会硬断言输入里含 <c>&lt;|endofprompt|&gt;</c>，
    /// 官方示例把它和"翻译成什么"写在一起，如 <c>"You are a helpful assistant.&lt;|endofprompt|&gt;"</c>。
    /// 填了它，引擎会在 zero_shot 的 prompt_text、cross_lingual 的正文前自动补上；
    /// 留空则原样发送（CosyVoice1/2 不需要）。instruct / instruct2 的 instruct_text 仍然原样发送，
    /// 因为标记要放在指令**末尾**。
    /// </summary>
    public string PromptPrefix { get; set; } = string.Empty;

    /// <summary>返回 PCM 的采样率。CosyVoice2/3 = 24000，CosyVoice1 = 22050。</summary>
    public int SampleRate { get; set; } = 24000;

    public int TimeoutSeconds { get; set; } = 120;

    /// <summary>启动命令；为空表示由用户自己先启动服务。</summary>
    public string LaunchCommand { get; set; } = string.Empty;
}

public sealed class AsrConfig
{
    /// <summary>
    /// 采集源：microphone（麦克风，默认）或 loopback（系统音频回环）。
    /// loopback 用于识别电脑正在播放的声音，也方便端到端验证。
    /// </summary>
    public string Capture { get; set; } = "microphone";

    /// <summary>启动时是否自动开始监听。</summary>
    public bool AutoStart { get; set; }

    /// <summary>
    /// 按住说话（push-to-talk）。
    ///
    /// 为什么默认关掉常开监听、改成按住键才录音：
    /// 常开监听会把环境音、旁人对话、甚至 TTS 自己的回声都收进来，
    /// 桌面场景下"按住某个键说话"更可控，也符合用户直觉。
    /// 开启后 <see cref="AutoStart"/> 不再生效（不会进入常开监听）。
    /// </summary>
    public bool PushToTalk { get; set; }

    /// <summary>
    /// 按住说话的按键：单键（"Home"、"F2"）或组合键（"Alt+Q"、"Ctrl+Alt+Q"）。
    /// 修饰键支持 Ctrl / Alt / Shift / Win，主键支持字母、数字、F1-F24 与常用键。
    /// </summary>
    public string PushToTalkKey { get; set; } = "Home";

    /// <summary>短于该时长的按住判定为误触，直接丢弃（毫秒）。</summary>
    public int PushToTalkMinMs { get; set; } = 300;

    /// <summary>单次录音上限，防止按住不放把内存吃满（秒）。</summary>
    public int PushToTalkMaxSeconds { get; set; } = 60;
    /// <summary>whisper.cpp 的 ggml 模型文件。手册模板写作 "models/asr/..."，此处给出可用的具体默认值。</summary>
    public string ModelPath { get; set; } = "models/asr/ggml-base.bin";

    /// <summary>
    /// ASR 引擎动态库。指向 third_party/ai_asr 构建出的 ai_asr.dll
    /// （whisper.cpp 的桥接层，见 ai_asr_shim.h 里的设计说明）。
    /// </summary>
    public string LibraryPath { get; set; } = "bin/ai_asr.dll";

    /// <summary>ASR 推理线程数，0 表示由引擎自行决定。</summary>
    public int Threads { get; set; }

    public string Language { get; set; } = "zh";

    public int SampleRate { get; set; } = 16000;
    /// <summary>VAD 模式：auto / webrtc / energy。auto 表示优先 WebRTC，不可用则回退能量 VAD。</summary>
    public string VadMode { get; set; } = "auto";

    /// <summary>WebRTC VAD 动态库（由 third_party/fvad_shim 把 libfvad 打成带导出符号的 DLL）。</summary>
    public string VadLibraryPath { get; set; } = "bin/libfvad.dll";

    /// <summary>WebRTC VAD 激进度 0-3。</summary>
    public int VadAggressiveness { get; set; } = 2;

    /// <summary>能量 VAD 的判定阈值（0-1）。</summary>
    public double EnergyThreshold { get; set; } = 0.02;

    /// <summary>静音多久判定一句话结束（毫秒）。</summary>
    public int SilenceMilliseconds { get; set; } = 700;

    /// <summary>短于该时长的语音段被丢弃（毫秒）。</summary>
    public int MinSpeechMilliseconds { get; set; } = 250;

    /// <summary>单个语音段的最大长度，防止一直不说话导致缓冲无限增长（秒）。</summary>
    public int MaxSegmentSeconds { get; set; } = 15;
}

public sealed class Live2dConfig
{
    public string DefaultModelPath { get; set; } = "./Assets/Default/default.model3.json";

    public string DefaultTexturePath { get; set; } = "./Assets/Default/default.png";

    /// <summary>表情贴图目录，用于静态图降级方案的表情切换。</summary>
    public string ExpressionDirectory { get; set; } = "./Assets/Default/Expressions";
}

public sealed class KnowledgeConfig
{
    public string Directory { get; set; } = "./Knowledge";

    /// <summary>待审核候选目录名（相对于 Knowledge 目录）。</summary>
    public string PendingDirectoryName { get; set; } = ".pending";

    /// <summary>待审核文件数量达到该值时提醒（手册 §3.7）。</summary>
    public int NotifyThreshold { get; set; } = 10;

    /// <summary>候选文件保留天数，超期在启动时清理（手册 §3.7）。</summary>
    public int PendingRetentionDays { get; set; } = 90;

    /// <summary>每天最多提醒一次的持久化状态文件名。</summary>
    public string NotifyStateFileName { get; set; } = ".notify_state.json";
}

public sealed class LoggingConfig
{
    public string ChatLogDir { get; set; } = "./logs/chat";

    public string AppLogDir { get; set; } = "./logs/app";

    public int MaxLogDays { get; set; } = 90;

    public string MinimumLevel { get; set; } = "Info";
}

/// <summary>
/// GameMonitor 通信与检测参数（手册 §3.9）。
/// 端口 47777 是决策 Q10 确定的默认值，写进配置以满足准则 8。
/// </summary>
public sealed class MonitorConfig
{
    public string Host { get; set; } = "127.0.0.1";

    public int Port { get; set; } = 47777;

    /// <summary>GameMonitor 发送 ping 的间隔（秒）。</summary>
    public int HeartbeatIntervalSeconds { get; set; } = 10;

    /// <summary>主程序多久收不到任何消息就判定断连（秒）。</summary>
    public int DisconnectTimeoutSeconds { get; set; } = 30;

    /// <summary>恢复后的冷却期时长（分钟），决策 Q11。</summary>
    public double CooldownMinutes { get; set; } = 5.0;

    public string GameListFile { get; set; } = "game_list.txt";

    /// <summary>未检测到游戏时的扫描间隔（秒）。</summary>
    public int ScanIntervalIdleSeconds { get; set; } = 60;

    /// <summary>检测到游戏后的加密扫描间隔（秒）。</summary>
    public int ScanIntervalActiveSeconds { get; set; } = 10;

    /// <summary>GPU 使用率高于该值才允许触发 unload（手册 §3.9 双重判断）。</summary>
    public int UnloadGpuThresholdPercent { get; set; } = 80;

    /// <summary>恢复所需的 GPU 使用率上限。</summary>
    public int RestoreGpuThresholdPercent { get; set; } = 50;

    /// <summary>GPU 持续低于上限多久才恢复（分钟）。</summary>
    public double RestoreLowGpuMinutes { get; set; } = 3.0;
}

/// <summary>显存计算参数（手册 §3.3 的常量外提，默认值与手册一致）。</summary>
public sealed class GpuConfig
{
    public int ReservedMb { get; set; } = 4096;

    public int KvCacheReservedMb { get; set; } = 1024;

    public int PerLayerMb { get; set; } = 150;

    public double SafetyFactor { get; set; } = 0.9;

    /// <summary>OOM 回退最大次数。</summary>
    public int MaxOomRetries { get; set; } = 2;

    /// <summary>每次回退减少的层数。</summary>
    public int OomNglStep { get; set; } = 4;

    /// <summary>回退重启前的等待秒数。</summary>
    public int OomRetryDelaySeconds { get; set; } = 5;
}

/// <summary>上下文管理参数（手册 §3.2 的常量外提）。</summary>
public sealed class ContextConfig
{
    public int MaxRecentRounds { get; set; } = 30;

    public int SendRecentRounds { get; set; } = 10;

    public int SummaryTokenLimit { get; set; } = 500;

    public int AnchorLimit { get; set; } = 30;

    /// <summary>ConversationRefiner 读取最近聊天记录的窗口大小（手册 §3.7）。</summary>
    public int RefineWindowSize { get; set; } = 200;

    public string ArchiveFileName { get; set; } = "chat_log_archive.jsonl";

    /// <summary>BuildContext 中注入的角色设定条数上限。</summary>
    public int PersonalityEntryLimit { get; set; } = 10;
}

/// <summary>空闲调度参数（手册 §3.6 的常量外提）。</summary>
public sealed class IdleConfig
{
    public int PollIntervalMs { get; set; } = 500;

    /// <summary>距最后一次用户输入超过该分钟数才允许触发维护。</summary>
    public double IdleMinutes { get; set; } = 5.0;

    /// <summary>触发维护要求的 CPU 使用率上限（百分比）。</summary>
    public double CpuLowPercent { get; set; } = 50.0;

    /// <summary>执行期间 CPU 超过该值即请求取消（百分比）。</summary>
    public double CpuHighPercent { get; set; } = 70.0;

    /// <summary>ConversationRefiner 要求的显存使用率上限（百分比）。</summary>
    public double RefineGpuMemoryPercent { get; set; } = 60.0;
}

/// <summary>表现层参数。</summary>
public sealed class UiConfig
{
    /// <summary>Live2D 全部降级失败后的最后一级：纯色背景 + 角色名（手册 §6）。</summary>
    public string FallbackDisplayName { get; set; } = "AI 伴侣";

    public string FallbackBackgroundColor { get; set; } = "#FF2B2B3C";

    /// <summary>静态图角色形象路径。</summary>
    public string PetImagePath { get; set; } = "./Assets/Default/default.png";

    public bool AlwaysOnTop { get; set; } = true;

    /// <summary>
    /// 启动时是否处于「透明」（鼠标穿透）模式。
    ///
    /// 默认 **false**，即启动时是「覆盖」（可交互）模式。
    /// 这是必须的：穿透状态下窗口收不到任何鼠标事件，
    /// 左键点角色开功能栏、右键切换模式的交互就全都触发不了。
    /// 运行中随时可用右键（或托盘菜单）在两种模式间切换，该值只决定启动时的初始状态。
    /// </summary>
    public bool ClickThrough { get; set; }

    /// <summary>
    /// 角色大小倍率。<b>0 表示按屏幕自动适配</b>（推荐，默认），否则为固定倍率。
    ///
    /// 为什么需要它：分辨率与 DPI 换一台机器就变。开发机是 2560×1440 @175%，
    /// 逻辑分辨率只有 1463×823 —— 460 DIP 的角色窗口占屏幕高度的 56%，
    /// 而在 4K 100% 的机器上又只占 25%。固定尺寸必然在一端不合适。
    ///
    /// 合法范围 0.5–2.5。运行中可用功能栏的 −/＋ 或 Ctrl+滚轮调整，
    /// 调整结果存到 Assets\appearance.json（不改本文件，准则 9）。
    /// </summary>
    public double Scale { get; set; }

    /// <summary>
    /// 透明（穿透）态下，鼠标悬停到角色身上时是否自动切回「覆盖」。
    ///
    /// 为什么需要它：<c>WS_EX_TRANSPARENT</c> 不仅让点击穿透，连 WM_MOUSEMOVE 都不会
    /// 送到窗口 —— 穿透态下拿不到任何悬停事件。所以只能用"定时轮询光标位置 +
    /// 像素级命中判定"来实现悬停检测（见 MainWindow 的 OnHoverTick）。
    /// </summary>
    public bool TransparentHoverSwitch { get; set; } = true;

    /// <summary>悬停需要持续多少毫秒才切换。设 0 表示一进入就切换。</summary>
    public int TransparentHoverDelayMs { get; set; } = 250;

    /// <summary>
    /// 由悬停自动切到「覆盖」之后，鼠标离开角色多少秒自动回到「透明」。
    /// 设为 0 表示不自动回退（那样透明态一旦被悬停触发就永久失效）。
    ///
    /// 只对"悬停触发的切换"生效：用户右键手动切换的模式不会被自动改掉。
    /// </summary>
    public int TransparentAutoRevertSeconds { get; set; } = 3;

    /// <summary>
    /// 检测到前台有全屏程序（游戏、全屏视频）时，是否自动切成「透明」并暂停悬停自动切换。
    ///
    /// 为什么需要它：角色窗口是 Topmost，覆盖态下会吃掉落在自身矩形里的点击。
    /// 用户全屏玩游戏时那块矩形正压在画面上，光标扫过就把模式切成覆盖，
    /// 于是点怪、点技能被角色窗口截走；光标离开几秒又退回透明，看着像"模式自己在跳"。
    /// 打开该开关后：进入全屏 → 切透明 + 暂停悬停；退出全屏 → 恢复原模式。
    /// 全屏期间手动点右上角按钮仍然有效，且以用户的选择为准。
    /// </summary>
    public bool FullscreenQuietMode { get; set; } = true;

    public int BubbleDurationSeconds { get; set; } = 5;

    /// <summary>
    /// 透明（穿透）态下角色整体的不透明度。
    ///
    /// 用途是让两种模式**一眼可辨**：覆盖态恒为 1.0（完全不透明），
    /// 透明态整体降到该值（默认 0.5），不用点一下才知道当前是哪种模式。
    /// </summary>
    public double TransparentOpacity { get; set; } = 0.5;

    /// <summary>
    /// 表情停留时间（秒）。表情图是整张替换立绘，若永不回退，角色会一直停在
    /// 上一次的表情上（例如说过"挥手"之后就永远保持挥手的立绘）。
    /// 0 表示不回退。
    /// </summary>
    public int ExpressionHoldSeconds { get; set; } = 8;

    /// <summary>
    /// 闲置多久没交互就把表情切回默认立绘（秒），0 表示关闭这条规则。
    ///
    /// 与 <see cref="ExpressionHoldSeconds"/> 的区别：Hold 是"每次表情播完固定等几秒就退"，
    /// 这里是"最后一次交互（说话、点击、拖动、悬停诊断）之后 N 秒，无论表情是何时贴上的都退"。
    /// 用它兜住 Hold 被配成 0（永不回退）或配得比待机时间更长的情况。
    /// </summary>
    public int IdleExpressionSeconds { get; set; } = 20;
}
