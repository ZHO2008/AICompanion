using AICompanion.Core.Abstractions;
using AICompanion.Core.Configuration;
using AICompanion.Core.Models;

namespace AICompanion.Core.Tts;

/// <summary>合成结果。用结果对象而不是抛异常，是为了让对话管线在"没声音"时仍能继续。</summary>
public sealed record TtsSynthesisResult(bool Success, byte[]? Audio, string? Error)
{
    public static TtsSynthesisResult Fail(string error) => new(false, null, error);
}

/// <summary>
/// TTS 管理器（手册 §3.4）。
///
/// 核心职责与手册条款对应关系：
///   - 启动时读 config 选引擎，Chatterbox 不可用则**降级一次**并提示（不做运行时自动切换，§2）；
///   - 合成时执行三级音色兜底：角色配置 → 全局默认 → 硬编码 "zh_CN-default"（准则 10）；
///   - 空闲软重启只针对 Chatterbox，且重启期间的新请求临时走 Piper（不改变主引擎标识）；
///   - 合成抛异常时只记日志 + 气泡"语音合成失败"，**不自动恢复**（手册 §3.4 崩溃处理）。
/// </summary>
public sealed class TTSManager : IDisposable
{
    /// <summary>三级音色兜底的第三级（准则 10 的硬编码内置）。</summary>
    public const string HardcodedFallbackVoice = AppConfig.HardcodedDefaultVoice;

    private readonly AppConfig _config;
    private readonly IAppLogger _logger;
    private readonly IBubbleNotifier _bubble;
    private readonly TtsEngineContext _context;

    private ITTSEngine _engine;
    private ITTSEngine? _piperFallback;

    /// <summary>
    /// "换引擎 / 重建 / 卸载"的临界区。
    ///
    /// 合成线程只读 <see cref="_engine"/> 的本地快照，而 ResetAsync / 降级 / Dispose 会
    /// Unload 旧引擎再装上新引擎。两者不互斥时会出现"同一个引擎被 Dispose 两次"或
    /// "对已释放的引擎发起合成"。所有 engine 字段的替换都走 <see cref="InstallEngineAsync"/>。
    /// </summary>
    private readonly SemaphoreSlim _engineLock = new(1, 1);

    /// <summary>已经尝试过自动拉起的外部引擎（每个引擎每进程只试一次，避免反复拉起进程）。</summary>
    private readonly HashSet<string> _launchAttempted = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _launchSync = new();

    private volatile bool _restarting;
    private DateTime _lastRequestUtc = DateTime.UtcNow;
    private bool _disposed;

    public TTSManager(AppConfig config, IAppLogger logger, IBubbleNotifier bubble)
    {
        _config = config;
        _logger = logger.ForCategory("TTS");
        _bubble = bubble;

        _context = new TtsEngineContext(config, logger, bubble);

        // 构造时就建一个实例，保证 InitializeAsync 之前也有可用对象。
        _engine = TTSEngineFactory.Create(config.Tts.Engine, _context);
    }

    public string CurrentEngineName => _engine.Name;

    /// <summary>是否因为 Chatterbox 不可用而降级到了 Piper。</summary>
    public bool IsDegraded { get; private set; }

    /// <summary>
    /// 引擎因故障/不可用而降级时触发，参数是新引擎名（"piper" / "sapi"）。
    ///
    /// 表现层订阅它来播放"疑惑"这类 Critical 动作 —— 语音换了引擎用户是听得出来的，
    /// 角色应当给出视觉反馈。
    /// </summary>
    public event EventHandler<string>? EngineFellBack;

    /// <summary>配置里用户选择的引擎。</summary>
    public string ConfiguredEngine => _config.Tts.Engine;

    /// <summary>Chatterbox 是否正在被实际使用（HealthMonitor 用它决定要不要探活 5000 端口）。</summary>
    public bool IsChatterboxActive => _engine is ChatterboxEngine;

    /// <summary>
    /// 当前实际使用的外部 HTTP 语音引擎（Chatterbox / GPT-SoVITS / CosyVoice）。
    /// Piper 与 SAPI 是进程内引擎，返回 null —— 游戏模式据此决定要不要按端口结束外部进程。
    /// </summary>
    public IHttpTtsEngine? ActiveHttpEngine => _engine as IHttpTtsEngine;

    /// <summary>引擎标识 → 给人看的名字（日志与气泡用）。</summary>
    public static string EngineLabel(string name) => name switch
    {
        TTSEngineFactory.GptSoVits => "GPT-SoVITS",
        TTSEngineFactory.CosyVoice => "CosyVoice",
        TTSEngineFactory.Chatterbox => "Chatterbox",
        TTSEngineFactory.Piper => "Piper",
        TTSEngineFactory.Sapi => "Windows 内置语音",
        _ => name,
    };

    /// <summary>启动逻辑（手册 §3.4 启动逻辑）。</summary>
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        /*
         * 语音被关掉：连"探活"都不做。
         *
         * 注意这里**不能**只把 _engine 置空 —— GPT-SoVITS 那种外部引擎的探活
         * 会真的发 HTTP 请求，某些配置下还会 Process.Start 把服务拉起来。
         * 用户既然关了语音，就不该有任何进程被顺手拉起。
         */
        if (!_config.Tts.Enabled)
        {
            _logger.Info("语音合成已关闭（tts.enabled=false）：只显示文字气泡，不探活也不拉起 TTS 服务。");
            _bubble.Show("tts-disabled", "语音输出已关闭（tts.enabled=false），只显示文字。", BubbleLevel.Info);
            return;
        }

        _logger.Info($"按配置创建 TTS 引擎：{_config.Tts.Engine}");

        // 先取本地快照：探活要 await 网络，绝不能持着 _engineLock 等外部服务。
        var engine = _engine;

        if (engine is IHttpTtsEngine httpEngine)
        {
            var available = await httpEngine.RefreshAvailabilityAsync(cancellationToken).ConfigureAwait(false);

            // 外部引擎不健康且配置了 launch_command → 用 Process.Start 拉起一次（每进程仅一次）。
            if (!available && TryLaunchExternalEngine(engine.Name))
            {
                available = await WaitForExternalEngineAsync(httpEngine, cancellationToken).ConfigureAwait(false);
            }

            if (!available)
            {
                // 手册 §3.4 步骤 3：记录日志 + 气泡 + 改用 Piper + 状态栏标注降级。
                var label = EngineLabel(engine.Name);
                _logger.Warn($"{label} 不可用，按手册降级为 Piper。");
                _bubble.Show(
                    "tts-degraded",
                    $"{label} 启动失败，已降级为 Piper。请在设置中调整。",
                    BubbleLevel.Warning);

                // 卸载 + 换引擎必须在锁内完成；事件回调留在锁外（不允许持锁调外部代码）。
                var fallback = CreatePiper();
                await InstallEngineAsync(fallback).ConfigureAwait(false);
                IsDegraded = true;
                EngineFellBack?.Invoke(this, fallback.Name);
            }
            else
            {
                _logger.Info($"{EngineLabel(engine.Name)} 已就绪，使用外部语音服务合成。");
                _bubble.Clear("tts-degraded");
                IsDegraded = false;
            }
        }
        else
        {
            IsDegraded = false;

            if (!engine.IsAvailable)
            {
                /*
                 * Piper 不可用（模型或 piper.exe 缺失）。
                 *
                 * 原实现只弹一条"语音合成暂不可用"就结束了 —— 结果是程序在绝大多数机器上
                 * 从头到尾**不会说一句话**。对一个桌面伴侣而言这等于核心功能缺失。
                 *
                 * 因此这里再降一级到 Windows 内置的 SAPI：它不需要任何外部资源，
                 * 任何 Windows 机器都能立刻出声。仍然保留日志与气泡，不做静默替换（准则 10）。
                 */
                _logger.Warn("Piper 不可用，降级为 Windows 内置语音（SAPI）。");
                _bubble.Clear("tts-model-missing");

                var sapi = new SapiEngine(_logger);
                await InstallEngineAsync(sapi).ConfigureAwait(false);
                IsDegraded = true;
                EngineFellBack?.Invoke(this, sapi.Name);

                if (sapi.IsAvailable)
                {
                    _bubble.Show(
                        "tts-degraded",
                        "未找到 Piper 语音模型，已改用 Windows 内置语音。音质较简单，但可以正常说话。",
                        BubbleLevel.Warning);
                }
                else
                {
                    _bubble.Show(
                        "tts-model-missing",
                        "未找到 Piper 语音模型，且系统没有可用的语音引擎，语音合成不可用。请按 config.yaml 的 tts.piper.model_path 放置模型。",
                        BubbleLevel.Warning);
                }
            }
        }

        UpdateStatusLine();
        _logger.Info($"TTS 初始化完成：配置引擎={_config.Tts.Engine}，实际引擎={_engine.Name}，降级={IsDegraded}。");
    }

    /// <summary>
    /// 三级音色兜底（准则 10）。
    /// 返回的一定是一个 <see cref="VoiceConfig.FallbackPiperVoice"/> 非空的配置，
    /// 因为最差情况也会落到硬编码常量上。
    /// </summary>
    public VoiceConfig ResolveVoice(VoiceConfig? characterVoice)
    {
        // 第一级：角色配置。
        if (characterVoice is not null && characterVoice.TryValidate(out _))
        {
            return characterVoice.Clone();
        }

        if (characterVoice is not null)
        {
            characterVoice.TryValidate(out var reason);
            _logger.Warn($"角色音色配置无效（{reason}），进入下一级兜底。");
        }

        // 第二级：config.yaml 的 tts.default_voice。
        var globalDefault = _config.Tts.DefaultVoice;
        if (!string.IsNullOrWhiteSpace(globalDefault))
        {
            return new VoiceConfig
            {
                VoiceId = characterVoice?.VoiceId,
                ReferenceAudioPath = characterVoice?.ReferenceAudioPath,
                FallbackPiperVoice = globalDefault,
                Language = characterVoice?.Language ?? AppConfig.HardcodedDefaultLanguage,
                Speed = characterVoice?.Speed ?? 1.0,
                EmotionIntensity = characterVoice?.EmotionIntensity ?? 1.0,
                EmotionStyle = characterVoice?.EmotionStyle ?? "neutral",
            };
        }

        // 第三级：硬编码内置。
        _logger.Warn($"全局默认音色为空，使用硬编码兜底音色 \"{HardcodedFallbackVoice}\"。");
        return new VoiceConfig
        {
            FallbackPiperVoice = HardcodedFallbackVoice,
            Language = AppConfig.HardcodedDefaultLanguage,
        };
    }

    /// <summary>合成语音。异常一律被吸收为失败结果 + 气泡，绝不向上抛（手册 §3.4 崩溃处理）。</summary>
    public async Task<TtsSynthesisResult> SynthesizeAsync(
        string text,
        VoiceConfig? characterVoice = null,
        CancellationToken cancellationToken = default)
    {
        if (!_config.Tts.Enabled)
        {
            // 关掉语音时上层不该再走到这里；万一走了，立刻返回而不是等引擎超时。
            return TtsSynthesisResult.Fail("语音输出已关闭（tts.enabled=false）。");
        }

        var voice = ResolveVoice(characterVoice);

        if (string.IsNullOrWhiteSpace(text))
        {
            return TtsSynthesisResult.Fail("文本为空。");
        }

        // 软重启期间临时用 Piper 合成一次，但不改变主引擎标识（手册 §3.4 软重启）。
        //
        // 先取本地快照再判空：另一个线程可能正在 ResetAsync / 降级路径里换引擎，
        // 直接读 _engine 会在"取到引用之后、调用之前"被 Unload，变成对已释放引擎合成。
        var engine = _restarting ? GetPiperFallback() : _engine;
        if (engine is null)
        {
            return TtsSynthesisResult.Fail("TTS 引擎未就绪。");
        }

        try
        {
            var audio = await engine.SynthesizeAsync(text, voice, cancellationToken).ConfigureAwait(false);
            _lastRequestUtc = DateTime.UtcNow;

            if (_restarting && !ReferenceEquals(engine, _engine))
            {
                _logger.Info("软重启期间已临时使用 Piper 合成（主引擎标识未改变）。");
            }

            return new TtsSynthesisResult(true, audio, null);
        }
        catch (OperationCanceledException)
        {
            return TtsSynthesisResult.Fail("已取消。");
        }
        catch (Exception ex)
        {
            // 手册：记录详细错误日志 + 气泡提示，不做任何自动恢复。
            _logger.Error($"语音合成失败（引擎 {engine.Name}，音色 {voice.FallbackPiperVoice}）：{ex.Message}", ex);
            _bubble.Show("tts-failed", "语音合成失败。可在系统托盘点击\"重启语音服务\"重试。", BubbleLevel.Error);
            return TtsSynthesisResult.Fail(ex.Message);
        }
    }

    /// <summary>
    /// 空闲时检查是否需要软重启 Chatterbox（手册 §3.4 + §3.6 任务序列第 3 项）。
    /// 只在"引擎是 Chatterbox + 连续运行超阈值 + 用户与 TTS 均空闲"时触发。
    /// </summary>
    public async Task<bool> CheckSoftRestartAsync(TimeSpan userIdle, CancellationToken cancellationToken = default)
    {
        var engine = _engine;
        if (engine is not IHttpTtsEngine httpEngine || _restarting)
        {
            return false;
        }

        var uptimeThreshold = TimeSpan.FromHours(Math.Max(0.1, _config.Tts.SoftRestartHours));
        if (httpEngine.Uptime < uptimeThreshold)
        {
            return false;
        }

        var idleThreshold = TimeSpan.FromMinutes(Math.Max(0.1, _config.Tts.SoftRestartIdleMinutes));
        var ttsIdle = DateTime.UtcNow - _lastRequestUtc;

        if (ttsIdle < idleThreshold || userIdle < idleThreshold)
        {
            return false;
        }

        _logger.Info(
            $"{EngineLabel(engine.Name)} 已连续运行 {httpEngine.Uptime.TotalHours:N1} 小时且空闲超过 " +
            $"{idleThreshold.TotalMinutes:N0} 分钟，开始软重启。");

        _restarting = true;
        try
        {
            await httpEngine.TrySoftRestartAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex)
        {
            _logger.Error($"{EngineLabel(engine.Name)} 软重启失败（不影响当前服务）：{ex.Message}", ex);
            return false;
        }
        finally
        {
            _restarting = false;
        }
    }

    /// <summary>
    /// 系统托盘"重启语音服务"（手册 §3.4 系统托盘菜单）。
    /// 销毁并重新初始化当前引擎；ASR 的重置由调用方一并处理。
    /// </summary>
    public async Task ResetAsync(CancellationToken cancellationToken = default)
    {
        _logger.Info("收到\"重启语音服务\"请求，正在重建 TTS 引擎。");

        // 卸载旧引擎 + 装上新引擎在锁内一次完成（见 _engineLock 的说明），
        // 避免与合成线程 / 降级路径同时对同一个引擎 Dispose。
        await InstallEngineAsync(TTSEngineFactory.Create(_config.Tts.Engine, _context)).ConfigureAwait(false);
        IsDegraded = false;
        _lastRequestUtc = DateTime.UtcNow;

        await InitializeAsync(cancellationToken).ConfigureAwait(false);

        _bubble.Show("tts-reset", "语音服务已重启。", BubbleLevel.Info,
            TimeSpan.FromSeconds(_config.Ui.BubbleDurationSeconds));
    }

    /// <summary>停止当前播放。</summary>
    public void Stop()
    {
        try
        {
            _engine.Stop();
            _piperFallback?.Stop();
        }
        catch (Exception ex)
        {
            _logger.Warn($"停止 TTS 失败：{ex.Message}");
        }
    }

    private PiperEngine GetPiperFallback()
    {
        _piperFallback ??= new PiperEngine(_config, _logger);
        return (PiperEngine)_piperFallback;
    }

    private PiperEngine CreatePiper() => new(_config, _logger);

    /// <summary>
    /// 在锁内换引擎：装上新引擎，再卸载被替换掉的旧引擎。
    ///
    /// 构造完成之后，只有这里能改写 <see cref="_engine"/> —— 这样"卸载 + 替换"是原子的，
    /// 不会出现两个线程各自 Unload 同一个引擎（引擎被 Dispose 两次）。
    /// 卸载异常只记 Warn：换引擎本身必须成功。整个过程不 await 任何外部代码，不会死锁。
    /// </summary>
    private async Task InstallEngineAsync(ITTSEngine replacement)
    {
        await _engineLock.WaitAsync().ConfigureAwait(false);
        try
        {
            var previous = _engine;
            _engine = replacement;

            if (ReferenceEquals(previous, replacement))
            {
                return;
            }

            try
            {
                previous?.Unload();
            }
            catch (Exception ex)
            {
                _logger.Warn($"卸载旧 TTS 引擎时异常：{ex.Message}");
            }
        }
        finally
        {
            _engineLock.Release();
        }
    }

    /// <summary>
    /// 按引擎取配置里的 launch_command。<c>chatterbox</c> 不在这里处理：
    /// 它由 AppHost.RestartChatterboxAsync 负责拉起，两处都拉会起两个进程。
    /// </summary>
    private string? LaunchCommandFor(string engineName) => engineName switch
    {
        TTSEngineFactory.GptSoVits => _config.Tts.GptSoVits.LaunchCommand,
        TTSEngineFactory.CosyVoice => _config.Tts.CosyVoice.LaunchCommand,
        _ => null,
    };

    /// <summary>
    /// 外部 HTTP 引擎不健康且配置了 launch_command 时，用 <c>cmd /c</c> 拉起一次。
    ///
    /// 与 AppHost 里 Chatterbox 的自动拉起同一模式：命令原样交给 cmd，允许带参数或重定向；
    /// 每个引擎**每进程只尝试一次**（失败后反复重试只会堆出僵尸进程、并拖慢每次初始化）；
    /// 启动命令为空则直接跳过；拉起失败只记 Warn，绝不抛出 —— 外部服务起不来时
    /// 仍要按手册降级到 Piper，而不是让启动失败。
    /// </summary>
    private bool TryLaunchExternalEngine(string engineName)
    {
        var command = LaunchCommandFor(engineName);
        if (string.IsNullOrWhiteSpace(command))
        {
            return false;
        }

        lock (_launchSync)
        {
            if (!_launchAttempted.Add(engineName))
            {
                return false;
            }
        }

        try
        {
            _logger.Info($"按配置拉起 {EngineLabel(engineName)}：{command}");

            var psi = new System.Diagnostics.ProcessStartInfo("cmd.exe", $"/c {command}")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = AppPaths.Root,
            };

            using var launched = System.Diagnostics.Process.Start(psi);
            return true;
        }
        catch (Exception ex)
        {
            _logger.Warn($"拉起 {EngineLabel(engineName)} 失败（不影响启动，可手动启动后点\"重启语音服务\"）：{ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// 拉起后做几次短轮询探活（约 6 秒）—— 拉起成功却立刻降级等于白拉。
    /// 不做长时间等待（不拖住启动）：外部服务加载模型动辄几十秒，等不到也不报错，
    /// 用户仍可在服务起来后点"重启语音服务"切回配置的引擎（那时不会再重复拉起）。
    /// </summary>
    private async Task<bool> WaitForExternalEngineAsync(IHttpTtsEngine httpEngine, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return false;
            }

            if (await httpEngine.RefreshAvailabilityAsync(cancellationToken).ConfigureAwait(false))
            {
                _logger.Info($"{EngineLabel(httpEngine.Name)} 已由 launch_command 拉起并可用。");
                return true;
            }
        }

        _logger.Warn($"{EngineLabel(httpEngine.Name)} 拉起后 6 秒内仍未就绪，继续按降级判断处理。");
        return false;
    }

    private void UpdateStatusLine()
    {
        var text = IsDegraded
            ? $"当前实际引擎：Piper（降级，配置为 {_config.Tts.Engine}）"
            : $"当前实际引擎：{_engine.Name}";

        _bubble.SetStatus("tts-engine", text);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        // 与换引擎路径互斥：否则可能与降级/重置同时 Dispose 同一个引擎。
        // Dispose 是同步方法，这里只能用 Wait()（临界区里没有 await，不会死锁）。
        _engineLock.Wait();
        try
        {
            _engine?.Unload();
            _piperFallback?.Unload();
        }
        catch
        {
            // 忽略。
        }
        finally
        {
            _engineLock.Release();
        }
    }
}
