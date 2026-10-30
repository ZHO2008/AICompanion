using System.Collections.Concurrent;
using System.Diagnostics;
using AICompanion.Core.Abstractions;
using AICompanion.Core.Configuration;

namespace AICompanion.Core.Asr;

/// <summary>
/// ASR 管理器（手册 §3.5）。
///
/// 线程模型（对应准则 1 与手册"创建高优先级线程运行音频采集与 VAD 循环"）：
///   - 采集回调（NAudio 线程）→ 只做 float 转换并把数据塞进有界队列，绝不做重活；
///   - <b>ASR-Audio 线程（ThreadPriority.Highest）</b> → 固定分帧 + VAD 状态机（这是对时延敏感的循环）；
///   - 识别任务 → 独立后台任务消费语音段队列（识别是重活，不能拖慢音频线程的节拍）。
///   三个环节之间全部用有界队列连接，队列满时丢最旧数据而不是阻塞，保证音频线程永不被拖住。
/// </summary>
public sealed class ASRManager : IDisposable
{
    /// <summary>连续识别失败多少次判定为 Faulted（手册 §3.5"识别连续失败"）。</summary>
    public const int MaxConsecutiveFailures = 3;

    private readonly AppConfig _config;
    private readonly IAppLogger _logger;
    private readonly IBubbleNotifier _bubble;
    private readonly ISpeechRecognizer _recognizer;
    private readonly IAudioCapture _capture;
    private readonly object _sync = new();

    private IVadDetector? _vad;
    private Thread? _audioThread;
    private BlockingCollection<float[]>? _sampleQueue;
    private BlockingCollection<float[]>? _segmentQueue;
    private Task? _recognitionTask;
    private CancellationTokenSource? _listenCts;

    private AsrState _state = AsrState.NotInitialized;
    private AsrFaultReason _faultReason = AsrFaultReason.None;
    private string? _faultMessage;
    private int _consecutiveFailures;
    private bool _disposed;

    /// <summary>
    /// 采集事件是否已挂上。故障恢复会再次调用 StartListening，
    /// 若无条件 += 就会重复订阅，导致同一块音频被处理两次
    /// （表现为识别结果重复、VAD 段长翻倍，且很难从日志看出来）。
    /// </summary>
    private bool _captureHooked;

    // ---- 按住说话（PTT）状态 ----
    // 与常开监听互斥：PTT 模式下不跑 VAD 与识别循环，按下录音、松开整段识别。
    private readonly List<float> _pttSamples = new();

    private readonly object _pttLock = new();

    private bool _pttActive;

    private readonly Stopwatch _pttWatch = new();

    /// <summary>
    /// 设备故障自愈计时器。
    ///
    /// 为什么需要：麦克风/回环设备会因为驱动重载、USB 设备休眠、默认设备切换而瞬时消失，
    /// NAudio 抛 <c>NoDriver calling waveInPrepareHeader</c>。
    /// 之前一旦失败就永久停在 Faulted，只能手动重置——桌面伴侣这种常驻程序不能这样。
    /// 现在按 10 秒间隔重试，最多 <see cref="MaxDeviceRecoveryAttempts"/> 次。
    /// </summary>
    private Timer? _deviceRecoveryTimer;

    private int _deviceRecoveryAttempts;

    private const int MaxDeviceRecoveryAttempts = 6;

    private static readonly TimeSpan DeviceRecoveryInterval = TimeSpan.FromSeconds(10);

    public ASRManager(
        AppConfig config,
        IAppLogger logger,
        IBubbleNotifier bubble,
        ISpeechRecognizer recognizer,
        IAudioCapture capture)
    {
        _config = config;
        _logger = logger.ForCategory("ASR");
        _bubble = bubble;
        _recognizer = recognizer;
        _capture = capture;
    }

    public AsrState State => _state;

    public AsrFaultReason FaultReason => _faultReason;

    /// <summary>识别器当前状态说明（设置面板展示用）。</summary>

    /// <summary>VAD 实现名称（webrtc / energy），设置面板展示用。</summary>
    public string VadName => _vad?.Name ?? "未初始化";

    /// <summary>最近一次识别的耗时统计，用于验证"低延迟"目标。</summary>
    public AsrRecognition? LastRecognition { get; private set; }

    /// <summary>识别到文本时触发（已在后台线程，订阅方需自行封送到 UI 线程）。</summary>
    public event EventHandler<string>? SpeechRecognized;

    public event EventHandler<AsrState>? StateChanged;

    /// <summary>初始化（手册 §3.5 初始化）。</summary>
    public Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        // 0. 用户主动关掉：不载模型、不开麦克风，状态置 Disabled（不是故障）。
        if (!_config.Asr.Enabled)
        {
            _logger.Info("语音输入已关闭（asr.enabled=false）：不加载识别模型，也不打开麦克风。");
            _bubble.Show("asr-disabled", "语音输入已关闭（asr.enabled=false），打字仍然可用。", BubbleLevel.Info);
            SetState(AsrState.Disabled);
            return Task.CompletedTask;
        }

        // 1. 模型文件存在性校验 —— 缺失属"可降级缺失"，置 Faulted 但不阻断启动。
        var modelPath = AppPaths.Resolve(_config.Asr.ModelPath);
        if (!File.Exists(modelPath))
        {
            SetFault(AsrFaultReason.ModelMissing, $"语音识别模型缺失：{modelPath}");
            _logger.Error(_faultMessage!);
            _bubble.Show(
                "asr-fault",
                "语音识别模型缺失，语音输入不可用。可使用文字输入。",
                BubbleLevel.Warning);
            return Task.CompletedTask;
        }

        // 2. 动态库/识别器就绪校验。
        if (!_recognizer.IsReady)
        {
            SetFault(AsrFaultReason.LibraryMissing, _recognizer.StatusMessage ?? "语音识别引擎未就绪。");
            _logger.Error($"语音识别引擎未就绪：{_faultMessage}");
            _bubble.Show(
                "asr-fault",
                "语音识别引擎未就绪，语音输入不可用。可使用文字输入。",
                BubbleLevel.Warning);
            return Task.CompletedTask;
        }

        // 3. 建立 VAD。
        try
        {
            _vad?.Dispose();
            _vad = VadDetectorFactory.Create(_config, _logger);
        }
        catch (Exception ex)
        {
            SetFault(AsrFaultReason.InternalError, $"创建 VAD 失败：{ex.Message}");
            _logger.Error(_faultMessage!, ex);
            _bubble.Show("asr-fault", "语音识别异常，可切换文字输入。", BubbleLevel.Warning);
            return Task.CompletedTask;
        }

        _faultReason = AsrFaultReason.None;
        _faultMessage = null;
        SetState(AsrState.Stopped);
        _bubble.Clear("asr-fault");

        _logger.Info($"ASR 初始化完成：模型={modelPath}，VAD={_vad.Name}。");
        return Task.CompletedTask;
    }

    /// <summary>开始监听（手册 §3.5 启动监听）。</summary>
    public void StartListening()
    {
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            if (_state == AsrState.Faulted)
            {
                /*
                 * 设备类故障是"可自愈"的：驱动重载、USB 设备休眠之后设备会回来。
                 * 允许手动/自动重试，而不是让用户以为语音彻底坏了。
                 * 模型缺失、库缺失这类故障必须用户介入，仍然拒绝启动。
                 */
                if (_faultReason == AsrFaultReason.DeviceError)
                {
                    _logger.Info("ASR 之前因设备故障停止，本次启动将重新尝试打开设备。");
                    _faultReason = AsrFaultReason.None;
                    _faultMessage = null;
                    SetState(AsrState.Stopped);
                }
                else
                {
                    _logger.Warn("ASR 处于故障状态，拒绝启动监听。请在设置面板或托盘重置语音服务。");
                    _bubble.Show("asr-fault", "语音识别不可用，请使用文字输入，或在托盘重置语音服务。", BubbleLevel.Warning);
                    return;
                }
            }

            if (_state == AsrState.Running || _vad is null)
            {
                return;
            }

            try
            {
                _consecutiveFailures = 0;
                _sampleQueue = new BlockingCollection<float[]>(boundedCapacity: 64);
                _segmentQueue = new BlockingCollection<float[]>(boundedCapacity: 4);
                _listenCts = new CancellationTokenSource();

                if (!_captureHooked)
                {
                    _capture.SamplesAvailable += OnSamplesAvailable;
                    _capture.CaptureError += OnCaptureError;
                    _captureHooked = true;
                }

                _capture.Start(_config.Asr.SampleRate);

                // 手册要求的高优先级音频线程。
                _audioThread = new Thread(AudioLoop)
                {
                    IsBackground = true,
                    Priority = ThreadPriority.Highest,
                    Name = "ASR-Audio",
                };
                _audioThread.Start();

                _recognitionTask = Task.Run(() => RecognitionLoop(_listenCts.Token));

                SetState(AsrState.Running);
                _logger.Info("ASR 已开始监听。");
            }
            catch (Exception ex)
            {
                _logger.Error($"启动监听失败：{ex.Message}", ex);
                SetFault(AsrFaultReason.DeviceError, ex.Message);
                _bubble.Show("asr-fault", "语音识别异常，可切换文字输入。", BubbleLevel.Warning);
            }
        }
    }

    public void StopListening()
    {
        lock (_sync)
        {
            // 注意：不能因为 "状态不是 Running" 就提前返回。
            // 走到 Faulted 之后正是用户点"重启语音服务/重置 ASR"的时机，
            // 若这里直接 return，采集器会继续采、事件不退订、队列不收尾，
            // 麦克风指示灯常亮且每 20ms 分配一个 float[] 后被丢掉。
            var wasRunning = _state == AsrState.Running;
            var hasResources = _captureHooked || _listenCts is not null || _sampleQueue is not null;

            if (!wasRunning && !hasResources)
            {
                return;
            }

            try
            {
                if (_captureHooked)
                {
                    _capture.SamplesAvailable -= OnSamplesAvailable;
                    _capture.CaptureError -= OnCaptureError;
                    _captureHooked = false;
                }

                _capture.Stop();

                _listenCts?.Cancel();
                _sampleQueue?.CompleteAdding();
                _segmentQueue?.CompleteAdding();
            }
            catch (Exception ex)
            {
                _logger.Warn($"停止监听时异常：{ex.Message}");
            }

            if (wasRunning || _state == AsrState.Faulted)
            {
                SetState(_faultReason == AsrFaultReason.None ? AsrState.Stopped : AsrState.Faulted);
                _logger.Info("ASR 已停止监听。");
            }
        }
    }

    /// <summary>
    /// 重置（手册 §3.4 托盘"重启语音服务"与 §3.5 设置面板手动重置）。
    /// 完整地销毁 VAD / 释放采集设备 / 重建识别器状态，然后重新初始化。
    /// </summary>
    public async Task ResetAsync(CancellationToken cancellationToken = default)
    {
        _logger.Info("正在重置 ASR。");

        StopListening();

        // 顺序很重要：VAD 必须等音频线程退出之后才能释放。
        // 之前是先 _vad.Dispose() 再 Join，音频线程可能仍在 vad.IsSpeech() 里，
        // 那就是对已释放的 fvad 实例做调用（原生堆破坏，非受管异常）。
        try
        {
            _audioThread?.Join(TimeSpan.FromSeconds(2));
        }
        catch
        {
            // 忽略。
        }

        // 识别任务也要等：whisper 的 ai_asr_shutdown 会 whisper_free 全局 ctx，
        // 与在飞的 whisper_full 并发会直接踩内存。
        WaitRecognitionTask(TimeSpan.FromSeconds(2));

        try
        {
            _recognizer.Reset();
        }
        catch (Exception ex)
        {
            _logger.Warn($"重置识别器时异常：{ex.Message}");
        }

        try
        {
            _vad?.Dispose();
            _vad = null;
        }
        catch (Exception ex)
        {
            _logger.Warn($"释放 VAD 时异常：{ex.Message}");
        }

        _audioThread = null;

        _sampleQueue?.Dispose();
        _segmentQueue?.Dispose();
        _sampleQueue = null;
        _segmentQueue = null;

        _listenCts?.Dispose();
        _listenCts = null;

        SetState(AsrState.NotInitialized);
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
    }

    private void OnSamplesAvailable(object? sender, float[] samples)
    {
        // 按住说话：整段攒起来，松开后一次性识别（不走 VAD 切分）。
        if (_pttActive)
        {
            AppendPushToTalkSamples(samples);
            return;
        }

        // 采集线程上只做入队；队列满时丢弃最旧数据，绝不阻塞音频回调。
        var queue = _sampleQueue;
        if (queue is null || queue.IsAddingCompleted)
        {
            return;
        }

        if (!queue.TryAdd(samples))
        {
            if (queue.TryTake(out _))
            {
                queue.TryAdd(samples);
            }
        }
    }

    // ================= 按住说话（push-to-talk） =================

    /// <summary>是否处于按住说话模式（来自配置）。</summary>
    public bool IsPushToTalk => _config.Asr.PushToTalk;

    /// <summary>按住按键：开始录音。</summary>
    public void BeginPushToTalk()
    {
        lock (_sync)
        {
            if (!_config.Asr.PushToTalk || _disposed || _pttActive || _vad is null)
            {
                return;
            }

            lock (_pttLock)
            {
                _pttSamples.Clear();
            }

            try
            {
                if (!_captureHooked)
                {
                    _capture.SamplesAvailable += OnSamplesAvailable;
                    _capture.CaptureError += OnCaptureError;
                    _captureHooked = true;
                }

                _pttActive = true;
                _pttWatch.Restart();
                _capture.Start(_config.Asr.SampleRate);
                SetState(AsrState.Running);
                _logger.Info("PTT 按下：开始录音。");
            }
            catch (Exception ex)
            {
                _pttActive = false;
                _logger.Error($"PTT 启动录音失败：{ex.Message}", ex);
                SetFault(AsrFaultReason.DeviceError, ex.Message);
                ScheduleDeviceRecovery();
            }
        }
    }

    /// <summary>松开按键：停止录音并识别整段。</summary>
    public void EndPushToTalk()
    {
        float[] samples;

        lock (_sync)
        {
            if (!_pttActive)
            {
                return;
            }

            _pttActive = false;

            lock (_pttLock)
            {
                samples = _pttSamples.ToArray();
                _pttSamples.Clear();
            }

            try
            {
                _capture.Stop();
            }
            catch (Exception ex)
            {
                _logger.Warn($"PTT 停止录音时异常：{ex.Message}");
            }

            SetState(AsrState.Stopped);
        }

        var durationMs = samples.Length * 1000.0 / _config.Asr.SampleRate;
        var heldMs = _pttWatch.ElapsedMilliseconds;
        var minMs = Math.Max(100, _config.Asr.PushToTalkMinMs);

        if (durationMs < minMs)
        {
            /*
             * 两种完全不同的情况，日志必须分开，否则会误导用户：
             *   ① 按键真的只按了一下（heldMs 也短）→ 误触，忽略即可；
             *   ② 按住了很久却没收到音频（heldMs 长、durationMs 短）
             *      → 麦克风没在供数据（被占用/驱动异常/选错设备），这是故障，要提示。
             */
            if (heldMs >= minMs * 2)
            {
                _logger.Warn(
                    $"PTT 按住 {heldMs} ms，但只收到 {durationMs:N0} ms 音频——麦克风可能被其他程序占用或设备异常。");
                _bubble.Show(
                    "asr-fault",
                    "按住说话没有收到声音，检查麦克风是否被其他程序占用。",
                    BubbleLevel.Warning,
                    TimeSpan.FromSeconds(5));
            }
            else
            {
                _logger.Info($"PTT 松开：录音只有 {durationMs:N0} ms，判定为误触，已忽略。");
            }

            return;
        }

        _logger.Info($"PTT 松开：录音 {durationMs / 1000.0:N1} 秒，开始识别。");
        _ = Task.Run(() => TranscribePushToTalkAsync(samples));
    }

    private void AppendPushToTalkSamples(float[] samples)
    {
        var limit = Math.Max(1, _config.Asr.SampleRate) * Math.Max(1, _config.Asr.PushToTalkMaxSeconds);

        lock (_pttLock)
        {
            if (_pttSamples.Count >= limit)
            {
                return;   // 超过上限就不再收，避免按住不放吃满内存
            }

            _pttSamples.AddRange(samples);
        }
    }

    private async Task TranscribePushToTalkAsync(float[] samples)
    {
        var audioDuration = TimeSpan.FromSeconds((double)samples.Length / _config.Asr.SampleRate);
        var stopwatch = Stopwatch.StartNew();

        try
        {
            var text = await _recognizer
                .TranscribeAsync(samples, _config.Asr.SampleRate)
                .ConfigureAwait(false);

            stopwatch.Stop();
            LastRecognition = new AsrRecognition(text, audioDuration, stopwatch.Elapsed);

            if (string.IsNullOrWhiteSpace(text))
            {
                _logger.Debug($"PTT 识别结果为空（音频 {audioDuration.TotalSeconds:N1}s）。");
                return;
            }

            var normalized = AsrTextNormalizer.Normalize(text);
            _logger.Info(
                $"PTT 识别成功：\"{normalized}\"（音频 {audioDuration.TotalSeconds:N1}s，" +
                $"耗时 {stopwatch.ElapsedMilliseconds} ms，RTF {LastRecognition.RealTimeFactor:N2}）。");

            SpeechRecognized?.Invoke(this, normalized);
        }
        catch (Exception ex)
        {
            _logger.Error($"PTT 识别失败：{ex.Message}", ex);
            _bubble.Show(
                "asr-fault",
                "语音识别失败，可重试或改用文字输入。",
                BubbleLevel.Warning,
                TimeSpan.FromSeconds(4));
        }
    }


    private void OnCaptureError(object? sender, Exception ex)
    {
        _logger.Error($"麦克风设备异常：{ex.Message}", ex);

        SetFault(AsrFaultReason.DeviceError, ex.Message);
        _bubble.Show("asr-fault", "语音识别异常，可切换文字输入。", BubbleLevel.Warning);

        // 手册：不自动重启**引擎**（模型/库的重载确实该由用户决定）。
        // 但设备类故障值得自动重试，否则一次 USB 瞬断就让语音永久失效。
        try
        {
            _listenCts?.Cancel();
        }
        catch
        {
            // 忽略。
        }

        ScheduleDeviceRecovery();
    }

    /// <summary>
    /// 安排一次设备恢复重试（10 秒后，最多 <see cref="MaxDeviceRecoveryAttempts"/> 次）。
    /// </summary>
    private void ScheduleDeviceRecovery()
    {
        if (_disposed || _faultReason != AsrFaultReason.DeviceError)
        {
            return;
        }

        if (_deviceRecoveryAttempts >= MaxDeviceRecoveryAttempts)
        {
            _logger.Warn(
                $"ASR 自动恢复已放弃：连续 {MaxDeviceRecoveryAttempts} 次尝试都打不开音频设备。" +
                "请检查麦克风是否被拔出/禁用，然后重启语音服务。");
            return;
        }

        _deviceRecoveryAttempts++;

        _logger.Info(
            $"ASR 将在 {DeviceRecoveryInterval.TotalSeconds:N0} 秒后尝试自动恢复" +
            $"（第 {_deviceRecoveryAttempts}/{MaxDeviceRecoveryAttempts} 次）。");

        _deviceRecoveryTimer ??= new Timer(
            _ => TryRecoverFromDeviceError(),
            null,
            Timeout.InfiniteTimeSpan,
            Timeout.InfiniteTimeSpan);

        try
        {
            _deviceRecoveryTimer.Change(DeviceRecoveryInterval, Timeout.InfiniteTimeSpan);
        }
        catch (ObjectDisposedException)
        {
            // 已释放，忽略。
        }
    }

    /// <summary>重试用时：清掉设备故障状态并重新开始监听。</summary>
    private void TryRecoverFromDeviceError()
    {
        lock (_sync)
        {
            if (_disposed || _state != AsrState.Faulted || _faultReason != AsrFaultReason.DeviceError)
            {
                return;
            }

            _logger.Info($"ASR 自动恢复：重新打开音频设备（第 {_deviceRecoveryAttempts} 次尝试）…");

            _faultReason = AsrFaultReason.None;
            _faultMessage = null;
            SetState(AsrState.Stopped);

            // PTT 模式下没有"常开监听"这回事，等用户下一次按键即可。
            if (_config.Asr.PushToTalk)
            {
                _logger.Info("ASR 自动恢复：按住说话模式不需要恢复监听，等下一次按键。");
                SetState(AsrState.Stopped);
                return;
            }

            StartListening();

            if (_state == AsrState.Running)
            {
                _deviceRecoveryAttempts = 0;
                _bubble.Show("asr-recovered", "语音识别已恢复。", BubbleLevel.Info);
                _logger.Info("ASR 自动恢复成功，已重新开始监听。");
            }
            else
            {
                // StartListening 失败会再次 SetFault(DeviceError)，这里继续排下一次
                ScheduleDeviceRecovery();
            }
        }
    }

    /// <summary>高优先级音频线程：固定分帧 + VAD 状态机 + 语音段切分。</summary>
    private void AudioLoop()
    {
        var token = _listenCts?.Token ?? CancellationToken.None;
        var vad = _vad;
        var queue = _sampleQueue;
        var segments = _segmentQueue;

        if (vad is null || queue is null || segments is null)
        {
            return;
        }

        var sampleRate = _config.Asr.SampleRate;
        var frameSamples = vad.FrameSamples;
        var millisPerFrame = 1000.0 * frameSamples / sampleRate;

        var pending = new List<float>(frameSamples * 4);
        var frame = new float[frameSamples];
        var segment = new List<float>(sampleRate * 4);

        var inSpeech = false;
        var silenceMs = 0.0;
        var maxSegmentSamples = sampleRate * Math.Max(1, _config.Asr.MaxSegmentSeconds);

        try
        {
            foreach (var chunk in queue.GetConsumingEnumerable(token))
            {
                pending.AddRange(chunk);

                while (pending.Count >= frameSamples)
                {
                    pending.CopyTo(0, frame, 0, frameSamples);
                    pending.RemoveRange(0, frameSamples);

                    bool isSpeech;
                    try
                    {
                        isSpeech = vad.IsSpeech(frame, frameSamples);
                    }
                    catch (Exception ex)
                    {
                        _logger.Error($"VAD 处理失败：{ex.Message}", ex);
                        isSpeech = false;
                    }

                    if (isSpeech)
                    {
                        inSpeech = true;
                        silenceMs = 0;
                    }
                    else if (inSpeech)
                    {
                        silenceMs += millisPerFrame;
                    }

                    if (inSpeech)
                    {
                        segment.AddRange(frame);

                        var tooLong = segment.Count >= maxSegmentSamples;
                        var ended = silenceMs >= _config.Asr.SilenceMilliseconds;

                        if (ended || tooLong)
                        {
                            EmitSegment(segments, segment, sampleRate, tooLong);
                            segment.Clear();
                            inSpeech = false;
                            silenceMs = 0;
                            vad.Reset();
                        }
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            // 正常停止。
        }
        catch (ObjectDisposedException)
        {
            // 重置过程中队列被释放，正常。
        }
        catch (InvalidOperationException)
        {
            // CompleteAdding 之后 GetConsumingEnumerable 正常结束。
        }
        catch (Exception ex)
        {
            _logger.Error($"音频线程异常退出：{ex.Message}", ex);
            SetFault(AsrFaultReason.DeviceError, ex.Message);
            _bubble.Show("asr-fault", "语音识别异常，可切换文字输入。", BubbleLevel.Warning);
        }
    }

    private void EmitSegment(BlockingCollection<float[]> segments, List<float> segment, int sampleRate, bool forced)
    {
        var durationMs = 1000.0 * segment.Count / sampleRate;

        if (durationMs < _config.Asr.MinSpeechMilliseconds)
        {
            _logger.Debug($"丢弃过短语音段：{durationMs:N0} ms < {_config.Asr.MinSpeechMilliseconds} ms。");
            return;
        }

        if (forced)
        {
            _logger.Info($"语音段达到 {_config.Asr.MaxSegmentSeconds} 秒上限，强制切分送入识别。");
        }

        var copy = segment.ToArray();
        if (!segments.TryAdd(copy) && segments.TryTake(out _))
        {
            segments.TryAdd(copy);
        }
    }

    /// <summary>识别循环：消费语音段并调用识别器。</summary>
    private async Task RecognitionLoop(CancellationToken cancellationToken)
    {
        var segments = _segmentQueue;
        if (segments is null)
        {
            return;
        }

        try
        {
            foreach (var samples in segments.GetConsumingEnumerable(cancellationToken))
            {
                cancellationToken.ThrowIfCancellationRequested();

                var audioDuration = TimeSpan.FromSeconds((double)samples.Length / _config.Asr.SampleRate);
                var stopwatch = Stopwatch.StartNew();

                try
                {
                    var text = await _recognizer
                        .TranscribeAsync(samples, _config.Asr.SampleRate, cancellationToken)
                        .ConfigureAwait(false);

                    stopwatch.Stop();
                    _consecutiveFailures = 0;

                    LastRecognition = new AsrRecognition(text, audioDuration, stopwatch.Elapsed);

                    if (string.IsNullOrWhiteSpace(text))
                    {
                        _logger.Debug($"识别结果为空（音频 {audioDuration.TotalSeconds:N1}s，耗时 {stopwatch.ElapsedMilliseconds} ms）。");
                        continue;
                    }

                    // whisper 中文常输出繁体，这里统一成简体并清掉 CJK 之间的多余空格
                    var normalized = AsrTextNormalizer.Normalize(text);

                    _logger.Info(
                        $"识别成功：\"{normalized}\"（音频 {audioDuration.TotalSeconds:N1}s，" +
                        $"耗时 {stopwatch.ElapsedMilliseconds} ms，RTF {LastRecognition.RealTimeFactor:N2}）。");

                    SpeechRecognized?.Invoke(this, normalized);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (Exception ex)
                {
                    _consecutiveFailures++;
                    _logger.Error($"识别失败第 {_consecutiveFailures} 次：{ex.Message}", ex);

                    if (_consecutiveFailures >= MaxConsecutiveFailures)
                    {
                        SetFault(AsrFaultReason.RecognitionFailure, ex.Message);
                        _bubble.Show("asr-fault", "语音识别异常，可切换文字输入。", BubbleLevel.Warning);
                        return;   // 手册：不自动重启引擎。
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            // 正常停止。
        }
        catch (ObjectDisposedException)
        {
            // 正常停止。
        }
        catch (Exception ex)
        {
            _logger.Error($"识别循环异常退出：{ex.Message}", ex);
        }
    }

    private void SetState(AsrState state)
    {
        if (_state == state)
        {
            return;
        }

        _state = state;
        StateChanged?.Invoke(this, state);
    }

    private void SetFault(AsrFaultReason reason, string message)
    {
        _faultReason = reason;
        _faultMessage = message;
        SetState(AsrState.Faulted);
        _logger.Warn($"ASR 进入故障状态：{reason} —— {message}");
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        try
        {
            _deviceRecoveryTimer?.Dispose();
            _deviceRecoveryTimer = null;
        }
        catch
        {
            // 忽略。
        }

        try
        {
            StopListening();
        }
        catch
        {
            // 忽略。
        }

        try
        {
            _listenCts?.Cancel();
            _audioThread?.Join(TimeSpan.FromSeconds(2));
        }
        catch
        {
            // 忽略。
        }

        // 识别线程必须等：whisper 的 shutdown 会释放全局 ctx，
        // 与仍在跑的 whisper_full 并发会踩内存（原生崩溃，无法 catch）。
        WaitRecognitionTask(TimeSpan.FromSeconds(2));

        _sampleQueue?.Dispose();
        _segmentQueue?.Dispose();
        _listenCts?.Dispose();
        _vad?.Dispose();

        try
        {
            _capture.Dispose();
        }
        catch
        {
            // 忽略。
        }

        try
        {
            _recognizer.Dispose();
        }
        catch
        {
            // 忽略。
        }
    }

    /// <summary>尽力等待识别任务收尾，超时就放弃（不能让退出流程卡死）。</summary>
    private void WaitRecognitionTask(TimeSpan timeout)
    {
        var task = _recognitionTask;
        _recognitionTask = null;

        if (task is null || task.IsCompleted)
        {
            return;
        }

        try
        {
            if (!task.Wait(timeout))
            {
                _logger.Warn($"识别任务在 {timeout.TotalSeconds:0.#} 秒内未退出，继续释放资源。");
            }
        }
        catch (Exception ex)
        {
            // 任务本身以异常结束时这里是 AggregateException；只记一条，不再抛出。
            _logger.Debug($"等待识别任务结束时异常（忽略）：{ex.GetBaseException().Message}");
        }
    }
}
