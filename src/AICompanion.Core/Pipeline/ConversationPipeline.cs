using System.Text.RegularExpressions;
using AICompanion.Core.Abstractions;
using AICompanion.Core.Audio;
using AICompanion.Core.Configuration;
using AICompanion.Core.Context;
using AICompanion.Core.Knowledge;
using AICompanion.Core.Llm;
using AICompanion.Core.Models;
using AICompanion.Core.Tts;

namespace AICompanion.Core.Pipeline;

public enum PipelineStage
{
    Idle = 0,

    /// <summary>ASR 识别中。</summary>
    Recognizing = 1,

    /// <summary>查知识库。</summary>
    Querying = 2,

    /// <summary>LLM 推理中（UI 显示动态气泡并激活"停止生成"）。</summary>
    Thinking = 3,

    /// <summary>语音合成中。</summary>
    Synthesizing = 4,

    /// <summary>播放中。</summary>
    Speaking = 5,
}

public sealed record PipelineStatus(PipelineStage Stage, string Message)
{
    public bool CanStopGeneration => Stage == PipelineStage.Thinking;
}

/// <summary>一轮对话的处理结果。</summary>
public sealed record PipelineResult(
    bool Success,
    string UserText,
    string ReplyText,
    string? Animation,
    bool FromKnowledge,
    bool VoicePlayed,
    string? Error);

/// <summary>
/// 对话管线（手册 §4）。
///
/// 严格按手册的 10 步实现，并在两个地方做了明确的工程增强：
///   1. **"停止生成"用取消流实现**，而不是手册写的 /stop 接口 ——
///      因为 llama.cpp 并没有 /stop（见决策 Q6）。
///   2. **锚点指令识别**：手册 §3.2 要求"用户通过对话指令（例如'记住这个'）触发"
///      AddAnchorMemory，这里实现了一个保守的中文/英文指令匹配。
///
/// 所有 I/O、网络、推理都是 async（准则 1），UI 线程只通过事件接收状态。
/// </summary>
public sealed class ConversationPipeline
{
    /// <summary>锚点指令（保守匹配，避免误把普通句子当成指令）。</summary>
    private static readonly Regex AnchorCommandPattern = new(
        @"^\s*(记住这个|记住这条|请记住这个|记住：|remember this|remember that)\s*$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>从回复里提取动画指令，约定形如 [anim:wave] 或 #anim(wave)。</summary>
    private static readonly Regex AnimationPattern = new(
        @"\[anim:\s*(?<name>[\w\-]+)\s*\]|#anim\((?<name2>[\w\-]+)\)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private readonly AppConfig _config;
    private readonly IAppLogger _logger;
    private readonly IBubbleNotifier _bubble;
    private readonly UnifiedKnowledgeManager _knowledge;
    private readonly ContextManager _context;
    private readonly LlamaServerController _llama;
    private readonly TTSManager _tts;
    private readonly IAudioPlayer _player;
    private readonly Scheduling.IdleTaskScheduler? _idleScheduler;

    private readonly object _sync = new();
    private CancellationTokenSource? _generationCts;
    private ChatMessage? _lastUserMessage;
    private ChatMessage? _lastAssistantMessage;

    public ConversationPipeline(
        AppConfig config,
        IAppLogger logger,
        IBubbleNotifier bubble,
        UnifiedKnowledgeManager knowledge,
        ContextManager context,
        LlamaServerController llama,
        TTSManager tts,
        IAudioPlayer player,
        Scheduling.IdleTaskScheduler? idleScheduler = null)
    {
        _config = config;
        _logger = logger.ForCategory("Pipeline");
        _bubble = bubble;
        _knowledge = knowledge;
        _context = context;
        _llama = llama;
        _tts = tts;
        _player = player;
        _idleScheduler = idleScheduler;
    }

    public PipelineStage Stage { get; private set; } = PipelineStage.Idle;

    public event EventHandler<PipelineStatus>? StatusChanged;

    /// <summary>回复文本就绪（UI 更新对话文本用）。</summary>
    public event EventHandler<ChatMessage>? ReplyReady;

    /// <summary>用户消息已接受（UI 回显用）。</summary>
    public event EventHandler<ChatMessage>? UserMessageAccepted;

    /// <summary>是否正在生成（"停止生成"按钮的可用性）。</summary>
    public bool IsGenerating
    {
        get
        {
            lock (_sync)
            {
                return _generationCts is not null;
            }
        }
    }

    /// <summary>
    /// 处理一条用户文本（来自语音识别或文本框，手册 §4 步骤 3 之后）。
    /// </summary>
    public async Task<PipelineResult> ProcessUserTextAsync(string text, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return new PipelineResult(false, text, string.Empty, null, false, false, "输入为空。");
        }

        text = text.Trim();
        _idleScheduler?.NotifyUserActivity();

        var userMessage = ChatMessage.User(text);

        // 锚点指令要锚住的是"上一条"用户消息，所以必须在覆盖 _lastUserMessage **之前**取快照：
        // 原实现先覆盖再处理指令，"记住这个"就会把指令本身当成被记住的内容（时序 bug）。
        var previousUserMessage = _lastUserMessage;

        _lastUserMessage = userMessage;
        UserMessageAccepted?.Invoke(this, userMessage);

        // 锚点指令：把上一轮对话标记为"记住"。
        if (AnchorCommandPattern.IsMatch(text))
        {
            return await HandleAnchorCommandAsync(userMessage, previousUserMessage, cancellationToken).ConfigureAwait(false);
        }

        try
        {
            // ---------- 步骤 3：查知识库 ----------
            SetStage(PipelineStage.Querying, "查询知识库…");
            var hit = _knowledge.Query(text);

            string replyText;
            string? animation = null;
            VoiceConfig? voice = null;
            var fromKnowledge = false;

            if (hit is not null)
            {
                // ---------- 步骤 4：命中预处理知识，直接得到回复 ----------
                _logger.Info($"命中预处理知识 {hit.Id}（优先级 {hit.Priority}），跳过 LLM。");
                replyText = hit.ReplyTemplate;
                animation = hit.Animation;
                voice = hit.VoiceConfig;
                fromKnowledge = true;
            }
            else
            {
                // ---------- 步骤 5：走 LLM ----------
                var llmResult = await GenerateReplyAsync(text, cancellationToken).ConfigureAwait(false);

                if (llmResult.Cancelled)
                {
                    SetStage(PipelineStage.Idle, "已停止生成。");
                    _bubble.Show("gen-stopped", "已停止生成。", BubbleLevel.Info,
                        TimeSpan.FromSeconds(_config.Ui.BubbleDurationSeconds));
                    return new PipelineResult(false, text, string.Empty, null, false, false, "已取消。");
                }

                if (!llmResult.Success)
                {
                    SetStage(PipelineStage.Idle, "生成失败。");
                    return new PipelineResult(false, text, string.Empty, null, false, false, llmResult.FailureReason);
                }

                replyText = llmResult.Text;
                (replyText, animation) = ExtractAnimation(replyText);
            }

            var assistantMessage = ChatMessage.Assistant(replyText, animation, voice);
            _lastAssistantMessage = assistantMessage;

            // ---------- 步骤 6：播放动画 ----------
            // 动画的实际渲染由表现层订阅 ReplyReady 后处理（核心层不依赖 WPF）。
            ReplyReady?.Invoke(this, assistantMessage);

            // ---------- 步骤 7：TTS 合成（内部做三级音色兜底） ----------
            var voicePlayed = false;
            if (!string.IsNullOrWhiteSpace(replyText))
            {
                SetStage(PipelineStage.Synthesizing, "合成语音…");
                var synthesis = await _tts.SynthesizeAsync(replyText, voice, cancellationToken).ConfigureAwait(false);

                if (synthesis.Success && synthesis.Audio is not null)
                {
                    // ---------- 步骤 8：播放音频 ----------
                    SetStage(PipelineStage.Speaking, "播放中…");
                    try
                    {
                        await _player.PlayAsync(synthesis.Audio, cancellationToken).ConfigureAwait(false);
                        voicePlayed = true;
                    }
                    catch (OperationCanceledException)
                    {
                        _player.Stop();
                    }
                    catch (Exception ex)
                    {
                        _logger.Error($"播放语音失败：{ex.Message}", ex);
                    }
                }
            }

            // ---------- 步骤 9：写入上下文与日志 ----------
            _context.AddExchange(userMessage, assistantMessage);

            SetStage(PipelineStage.Idle, "待机");
            return new PipelineResult(true, text, replyText, animation, fromKnowledge, voicePlayed, null);
        }
        catch (OperationCanceledException)
        {
            SetStage(PipelineStage.Idle, "已取消。");
            return new PipelineResult(false, text, string.Empty, null, false, false, "已取消。");
        }
        catch (Exception ex)
        {
            _logger.Error($"对话管线异常：{ex.Message}", ex);
            SetStage(PipelineStage.Idle, "出错了。");
            _bubble.Show("pipeline-error", "处理这次对话时出错，请查看日志。", BubbleLevel.Error);
            return new PipelineResult(false, text, string.Empty, null, false, false, ex.Message);
        }
    }

    /// <summary>LLM 生成路径（手册 §4 步骤 5 的子步骤）。</summary>
    private async Task<ChatResult> GenerateReplyAsync(string userText, CancellationToken outerToken)
    {
        if (!_llama.IsRunning)
        {
            _bubble.Show("llm-offline", "LLM 服务未就绪，无法生成回复。可查看日志后手动重启程序。", BubbleLevel.Error);
            return ChatResult.Fail("llama-server 未运行。");
        }

        // 步骤 5a：构建上下文。
        var context = _context.BuildContext(userText);

        var messages = new List<ChatMessage>();
        if (!string.IsNullOrWhiteSpace(context))
        {
            messages.Add(ChatMessage.System(context));
        }

        messages.Add(ChatMessage.User(userText));

        // 步骤 5b-5c：进入"推理中"状态，并挂上可取消的令牌。
        CancellationTokenSource generationCts;
        lock (_sync)
        {
            _generationCts?.Dispose();
            generationCts = CancellationTokenSource.CreateLinkedTokenSource(outerToken);
            _generationCts = generationCts;
        }

        SetStage(PipelineStage.Thinking, "推理中…");

        try
        {
            var result = await _llama.Client
                .ChatStreamAsync(messages, onDelta: null, cancellationToken: generationCts.Token)
                .ConfigureAwait(false);

            return result;
        }
        finally
        {
            lock (_sync)
            {
                if (ReferenceEquals(_generationCts, generationCts))
                {
                    _generationCts = null;
                }
            }

            generationCts.Dispose();
        }
    }

    /// <summary>
    /// "停止生成"（手册 §4 步骤 5 的按钮）。
    ///
    /// 实现方式（决策 Q6）：取消流式请求 → 释放 HTTP 连接 → llama.cpp 服务端据此停止生成
    /// （llama.cpp PR #9679）。随后补一次 /slots erase 清空槽位，作为双保险。
    /// </summary>
    public async Task StopGenerationAsync()
    {
        CancellationTokenSource? cts;

        lock (_sync)
        {
            cts = _generationCts;
        }

        if (cts is null)
        {
            _logger.Info("当前没有正在进行的生成，无需停止。");
            return;
        }

        _logger.Info("用户请求停止生成。");

        try
        {
            await cts.CancelAsync().ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
            // 生成刚好结束。
        }

        await _llama.Client.TryEraseSlotsAsync().ConfigureAwait(false);
    }

    /// <summary>处理"记住这个"（手册 §3.2 锚点记忆）。</summary>
    private Task<PipelineResult> HandleAnchorCommandAsync(
        ChatMessage command,
        ChatMessage? previousUserMessage,
        CancellationToken cancellationToken)
    {
        // 把上一条用户消息与上一条回复作为锚点内容。
        // 上一条用户消息由调用方在覆盖 _lastUserMessage 之前快照传入（见 ProcessUserTextAsync）。
        var anchored = _lastAssistantMessage is not null && previousUserMessage is not null
            ? ChatMessage.User($"{previousUserMessage.Content} → {_lastAssistantMessage.Content}")
            : command;

        _context.AddAnchorMemory(anchored);

        const string reply = "好的，我记住了。";
        var assistant = ChatMessage.Assistant(reply);
        _context.AddExchange(command, assistant);

        ReplyReady?.Invoke(this, assistant);
        SetStage(PipelineStage.Idle, "已记住");

        _bubble.Show("anchor-added", $"已记住（当前锚点 {_context.AnchorMemory.Count} 条）。", BubbleLevel.Info,
            TimeSpan.FromSeconds(_config.Ui.BubbleDurationSeconds));

        _logger.Info("已按用户指令新增锚点记忆。");
        return Task.FromResult(new PipelineResult(true, command.Content, reply, null, false, false, null));
    }

    /// <summary>从 LLM 回复中提取动画指令，并把指令文本从回复里剥掉。</summary>
    private static (string Text, string? Animation) ExtractAnimation(string reply)
    {
        if (string.IsNullOrWhiteSpace(reply))
        {
            return (reply, null);
        }

        var match = AnimationPattern.Match(reply);
        if (!match.Success)
        {
            return (reply.Trim(), null);
        }

        var name = match.Groups["name"].Success ? match.Groups["name"].Value : match.Groups["name2"].Value;
        var cleaned = AnimationPattern.Replace(reply, string.Empty).Trim();

        return (cleaned, string.IsNullOrWhiteSpace(name) ? null : name);
    }

    private void SetStage(PipelineStage stage, string message)
    {
        Stage = stage;
        StatusChanged?.Invoke(this, new PipelineStatus(stage, message));
    }
}
