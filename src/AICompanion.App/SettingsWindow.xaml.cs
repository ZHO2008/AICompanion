using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using AICompanion.App.Services;
using AICompanion.Core.Abstractions;
using AICompanion.Core.Asr;
using AICompanion.Core.Health;
using AICompanion.Core.Knowledge;

namespace AICompanion.App;

/// <summary>
/// 设置面板（手册 §3.4 / §3.5 / §3.7 / 阶段 3 第 6 项要求的各个入口）。
///
/// 说明：本面板只做**只读展示 + 明确的用户操作**，绝不代替用户改配置文件（准则 9）。
/// 因此这里的按钮只有"确认/丢弃候选""重启服务""清理日志"这类显式动作，
/// 没有"自动切换引擎"之类的隐式行为。
/// </summary>
public partial class SettingsWindow : Window
{
    private readonly AppHost _host;
    private readonly BubbleNotifier _bubbles;
    private readonly DispatcherTimer _refreshTimer;
    private readonly Action? _onAppearanceChanged;

    /// <summary>显存查询是否在飞行中（防止 2 秒定时器叠加 nvidia-smi 子进程）。</summary>
    private bool _gpuQuerying;
    private IReadOnlyList<PendingCandidate> _candidates = Array.Empty<PendingCandidate>();

    /// <summary>初始化滑块期间不响应 ValueChanged，避免把默认值当成用户修改。</summary>
    private bool _appearanceInitialized;

    /// <summary>初始化引擎下拉框期间不响应 SelectionChanged。</summary>
    private bool _ttsEngineInitialized;

    public SettingsWindow(AppHost host, BubbleNotifier bubbles, Action? onAppearanceChanged = null)
    {
        _host = host;
        _bubbles = bubbles;
        _onAppearanceChanged = onAppearanceChanged;

        InitializeComponent();

        _refreshTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher)
        {
            Interval = TimeSpan.FromSeconds(2),
        };
        _refreshTimer.Tick += (_, _) =>
        {
            RefreshStatus();
            RefreshVoiceTab();
            RefreshMonitorTab();
        };
        _refreshTimer.Start();

        Loaded += async (_, _) =>
        {
            RefreshStatus();
            await RefreshCandidatesAsync();
            OnRefreshLogs(this, new RoutedEventArgs());
        };

        Closed += (_, _) => _refreshTimer.Stop();

        LoadAppearanceTab();
        LoadTtsEngineTab();
        LoadAiToggleTab();
    }

    // ------------------------------------------------------------------ 语音引擎选择

    private void LoadTtsEngineTab()
    {
        _ttsEngineInitialized = false;
        TtsEngineCombo.Items.Clear();

        var current = AppearanceStore.LoadTtsEngine() ?? _host.Config.Tts.Engine;

        foreach (var engine in Core.Tts.TTSEngineFactory.UserSelectable)
        {
            var item = new ComboBoxItem
            {
                Content = Core.Tts.TTSEngineFactory.DisplayName(engine),
                Tag = engine,
            };

            TtsEngineCombo.Items.Add(item);

            if (string.Equals(engine, current, StringComparison.OrdinalIgnoreCase))
            {
                TtsEngineCombo.SelectedItem = item;
            }
        }

        if (TtsEngineCombo.SelectedItem is null && TtsEngineCombo.Items.Count > 0)
        {
            TtsEngineCombo.SelectedIndex = 0;
        }

        _ttsEngineInitialized = true;
        UpdateEngineHint();
    }

    private void UpdateEngineHint()
    {
        if (TtsEngineCombo.SelectedItem is not ComboBoxItem item)
        {
            return;
        }

        TtsEngineHintText.Text = item.Tag switch
        {
            Core.Tts.TTSEngineFactory.Piper => "本地 CPU，无需外部服务",
            Core.Tts.TTSEngineFactory.GptSoVits => "需先起 api_v2.py（9880）",
            Core.Tts.TTSEngineFactory.CosyVoice => "需先起 server.py（50000）",
            Core.Tts.TTSEngineFactory.Chatterbox => "需先起 HTTP 服务",
            _ => string.Empty,
        };
    }

    private async void OnTtsEngineChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_ttsEngineInitialized)
        {
            return;
        }

        if (TtsEngineCombo.SelectedItem is not ComboBoxItem item || item.Tag is not string engine)
        {
            return;
        }

        UpdateEngineHint();

        try
        {
            // 存界面偏好而不是回写 config.yaml（准则 9）。启动时 AppHost 会用它覆盖配置值。
            var saved = AppearanceStore.SaveTtsEngine(engine);
            _host.Config.Tts.Engine = engine;

            _host.Logger.Info(
                $"用户在设置面板选择 TTS 引擎：{engine}" +
                (saved ? "（已写入 Assets\\appearance.json，config.yaml 未改动）" : "（写入偏好失败，仅本次运行生效）"));

            await _host.Tts.ResetAsync();

            _bubbles.Show(
                "tts-engine-changed",
                $"语音引擎已切换为 {Core.Tts.TTSEngineFactory.DisplayName(engine)}。",
                BubbleLevel.Info,
                TimeSpan.FromSeconds(_host.Config.Ui.BubbleDurationSeconds));

            RefreshVoiceTab();
        }
        catch (Exception ex)
        {
            _host.Logger.Error($"切换语音引擎失败：{ex.Message}", ex);
        }
    }

    // ------------------------------------------------------------------ 外观页

    private void LoadAppearanceTab()
    {
        var seconds = AppearanceStore.LoadBubbleSeconds() ?? _host.Config.Ui.BubbleDurationSeconds;
        var opacity = AppearanceStore.LoadTransparentOpacity() ?? _host.Config.Ui.TransparentOpacity;
        var offset = AppearanceStore.LoadHorizontalOffset() ?? 0.0;

        _appearanceInitialized = false;

        BubbleSecondsSlider.Value = Math.Clamp(seconds, BubbleSecondsSlider.Minimum, BubbleSecondsSlider.Maximum);
        OpacitySlider.Value = Math.Clamp(opacity, OpacitySlider.Minimum, OpacitySlider.Maximum);
        HorizontalOffsetSlider.Value = Math.Clamp(offset, HorizontalOffsetSlider.Minimum, HorizontalOffsetSlider.Maximum);

        UpdateAppearanceLabels();
        _appearanceInitialized = true;
    }

    private void UpdateAppearanceLabels()
    {
        BubbleSecondsText.Text = $"{BubbleSecondsSlider.Value:F0} 秒";
        OpacityText.Text = $"{OpacitySlider.Value:P0}";

        var offset = HorizontalOffsetSlider.Value;
        HorizontalOffsetText.Text = Math.Abs(offset) < 0.0005
            ? "自动居中"
            : (offset > 0 ? $"左移 {offset:P0}" : $"右移 {-offset:P0}");
    }

    private void OnBubbleSecondsChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_appearanceInitialized)
        {
            UpdateAppearanceLabels();
        }
    }

    private void OnTransparentOpacityChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_appearanceInitialized)
        {
            UpdateAppearanceLabels();
        }
    }

    private void OnHorizontalOffsetChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_appearanceInitialized)
        {
            UpdateAppearanceLabels();
        }
    }

    private void OnResetHorizontalOffset(object sender, RoutedEventArgs e)
    {
        // 归零即"回到自动居中"
        HorizontalOffsetSlider.Value = 0;
        UpdateAppearanceLabels();
    }

    private void OnSaveAppearance(object sender, RoutedEventArgs e)
    {
        try
        {
            var seconds = Math.Round(BubbleSecondsSlider.Value);
            var opacity = Math.Round(OpacitySlider.Value, 2);
            var offset = Math.Round(HorizontalOffsetSlider.Value, 3);

            // 存到 Assets\appearance.json（不改 config.yaml，准则 9）
            var okSeconds = AppearanceStore.SaveBubbleSeconds(seconds);
            var okOpacity = AppearanceStore.SaveTransparentOpacity(opacity);
            var okOffset = AppearanceStore.SaveHorizontalOffset(offset);

            // 立刻生效：气泡时长作用于已有的 BubbleNotifier 实例
            _bubbles.DefaultDuration = TimeSpan.FromSeconds(Math.Max(1, seconds));

            // 不透明度需要主窗口重新套一次当前模式
            _onAppearanceChanged?.Invoke();

            var ok = okSeconds && okOpacity && okOffset;
            AppearanceHintText.Text = ok ? "已保存并应用" : "保存失败，详见日志";

            _host.Logger.Info(
                $"设置面板已保存外观：气泡 {seconds:F0} 秒，透明态不透明度 {opacity:P0}，" +
                $"模型水平偏移 {(Math.Abs(offset) < 0.0005 ? "自动居中" : offset.ToString("F3"))}（保存{(ok ? "成功" : "失败")}）");

            _bubbles.Show(
                "appearance-saved",
                $"已应用：气泡显示 {seconds:F0} 秒，透明态不透明度 {opacity:P0}",
                BubbleLevel.Info,
                TimeSpan.FromSeconds(3));
        }
        catch (Exception ex)
        {
            _host.Logger.Error($"保存外观设置失败：{ex.Message}", ex);
            AppearanceHintText.Text = "保存失败：" + ex.Message;
        }
    }

    // ------------------------------------------------------------------ 状态页

    private void RefreshStatus()
    {
        // 启动尚未完成时，核心组件还没构造出来，此时访问会 NRE。
        // 用户完全可能在启动过程中就打开设置面板，因此必须挡在这里。
        if (!_host.IsStarted)
        {
            ModeText.Text = "启动中，请稍候…";
            return;
        }

        try
        {
            ModeText.Text = $"运行模式：{_host.RuntimeMode.Describe()}";

            LlmText.Text = _host.LlmReady
                ? $"LLM：已就绪（ngl={_host.Llama.CurrentNgl}）"
                : "LLM：未就绪（详见下方问题列表与日志）";

            TtsText.Text = $"TTS：配置={_host.Tts.ConfiguredEngine}，实际={_host.Tts.CurrentEngineName}" +
                           (_host.Tts.IsDegraded ? "（已降级）" : string.Empty);

            // PTT 模式下空闲态就是 Stopped，直接显示状态会让人以为语音坏了。
            AsrText.Text = _host.Config.Asr.PushToTalk
                ? $"ASR：按住说话（{_host.Config.Asr.PushToTalkKey}）" +
                  (_host.Asr.State == AsrState.Running ? "，录音中…" : string.Empty)
                : $"ASR：{_host.Asr.State}" +
                  (_host.Asr.State == AsrState.Faulted ? $"（{_host.Asr.FaultReason}）" : string.Empty);

            KnowledgeText.Text = $"知识库：{_host.Knowledge.Count} 条（目录 {_host.Knowledge.DirectoryPath}）";
            PendingText.Text = $"待审核候选：{_host.Knowledge.Pending.CountCandidates()} 条";

            MonitorText.Text = $"GameMonitor：{(_host.Monitor.IsClientConnected ? "已连接" : "未连接")}" +
                               (_host.GameMode.IsYielded ? "，当前处于让出显存状态" : string.Empty);

            ProblemList.ItemsSource = _host.StartupProblems.Count > 0
                ? _host.StartupProblems
                : new[] { "无（配置与依赖均正常）" };

            BubbleList.ItemsSource = _bubbles.Current
                .Select(b => $"[{b.Level}] {b.Message}")
                .ToList();

            // HealthMonitor 的结果之前只写日志、界面上看不到（Describe() 因此成了零调用死代码）。
            HealthList.ItemsSource = _host.Health is null
                ? new[] { "尚未开始探测" }
                : _host.Health.Snapshots.Select(s => s.Describe()).ToList();

            // 状态栏标记：TTS 降级、游戏模式让出显存等都会写进来，之前写进去没人看。
            var statuses = _bubbles.Statuses;
            StatusList.ItemsSource = statuses.Count == 0
                ? new[] { "无" }
                : statuses.Select(kv => $"{kv.Key}：{kv.Value}").ToList();

            // 显存查询是异步的，这里只更新已有文本，避免阻塞 UI 线程（准则 1）。
            _ = UpdateGpuTextAsync();
        }
        catch (Exception ex)
        {
            _host.Logger.Warn($"刷新设置面板状态失败：{ex.Message}");
        }
    }

    private async Task UpdateGpuTextAsync()
    {
        // 防重入：一次查询要起两个 nvidia-smi 子进程，而定时器每 2 秒就调一次。
        // 显卡忙或驱动慢时查询可能超过 2 秒，没有这个门就会不断叠加子进程。
        if (_gpuQuerying)
        {
            return;
        }

        _gpuQuerying = true;

        try
        {
            var text = await _host.Gpu.DescribeCurrentMemoryAsync().ConfigureAwait(true);
            GpuText.Text = text + "（只读展示，程序不做任何自动调整）";
        }
        catch
        {
            GpuText.Text = "显存：不可用";
        }
        finally
        {
            _gpuQuerying = false;
        }
    }

    // ------------------------------------------------------------------ 待审核知识

    private async void OnRefreshCandidates(object sender, RoutedEventArgs e) => await RefreshCandidatesAsync();

    private async Task RefreshCandidatesAsync()
    {
        try
        {
            _candidates = await _host.Knowledge.Pending.ListCandidatesAsync();
            CandidateList.ItemsSource = _candidates;
            CandidateStatusText.Text = $"共 {_candidates.Count} 条";
        }
        catch (Exception ex)
        {
            CandidateStatusText.Text = $"读取失败：{ex.Message}";
        }
    }

    private async void OnConfirmCandidate(object sender, RoutedEventArgs e)
    {
        if (CandidateList.SelectedItem is not PendingCandidate candidate)
        {
            CandidateStatusText.Text = "请先选中一条候选。";
            return;
        }

        try
        {
            // 这是"程序绝不自行写入知识库"的唯一人工闸门（准则 9）。
            var ok = await _host.Knowledge.Pending.ConfirmAsync(candidate.FilePath);
            CandidateStatusText.Text = ok ? "已确认入库。" : "确认失败，详见日志。";
            await RefreshCandidatesAsync();
        }
        catch (Exception ex)
        {
            CandidateStatusText.Text = $"确认失败：{ex.Message}";
        }
    }

    private async void OnDiscardCandidate(object sender, RoutedEventArgs e)
    {
        if (CandidateList.SelectedItem is not PendingCandidate candidate)
        {
            CandidateStatusText.Text = "请先选中一条候选。";
            return;
        }

        try
        {
            var ok = await _host.Knowledge.Pending.DiscardAsync(candidate.FilePath);
            CandidateStatusText.Text = ok ? "已丢弃。" : "丢弃失败，详见日志。";
            await RefreshCandidatesAsync();
        }
        catch (Exception ex)
        {
            CandidateStatusText.Text = $"丢弃失败：{ex.Message}";
        }
    }

    /// <summary>
    /// 从文件夹导入角色包（手册 §3.1 的导入入口）。
    ///
    /// 导入器的参数是**角色包目录**（目录里必须有 personality.json），不是单个 JSON 文件，
    /// 因此这里选文件夹。策略用 <see cref="ImportStrategy.Append"/>：只新增条目、
    /// 绝不动已有条目，对用户最安全（Overwrite 会替换整个知识库）。
    /// </summary>
    private async void OnImportCharacterPack(object sender, RoutedEventArgs e)
    {
        if (!_host.IsStarted)
        {
            CandidateStatusText.Text = "启动未完成，稍后再试。";
            return;
        }

        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "选择角色包目录（内含 personality.json）",
            Multiselect = false,
        };

        // 默认从知识库目录开始找：角色包最常放在它的旁边。
        if (Directory.Exists(_host.Knowledge.DirectoryPath))
        {
            dialog.InitialDirectory = _host.Knowledge.DirectoryPath;
        }

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        var packDirectory = dialog.FolderName;

        try
        {
            CandidateStatusText.Text = "正在导入…";

            // AppHost 没有单独暴露导入器实例，这里用同样四个依赖现建一个（导入器本身无状态）。
            var importer = new KnowledgeImporter(_host.Config, _host.Logger, _bubbles, _host.Knowledge);
            var result = await importer.ImportCharacterPackAsync(packDirectory, ImportStrategy.Append);

            foreach (var warning in result.Warnings)
            {
                _host.Logger.Warn($"角色包导入警告：{warning}");
            }

            if (result.Success)
            {
                _host.Logger.Info(
                    $"角色包导入完成（{result.Strategy}）：新增 {result.Added} 条，更新 {result.Updated} 条，" +
                    $"跳过 {result.Skipped} 条，来源 {packDirectory}");

                CandidateStatusText.Text = $"导入完成：新增 {result.Added} 条，跳过 {result.Skipped} 条。";

                _bubbles.Show(
                    "knowledge-import",
                    $"角色包导入完成：新增 {result.Added} 条。",
                    BubbleLevel.Info,
                    TimeSpan.FromSeconds(_host.Config.Ui.BubbleDurationSeconds));

                await RefreshCandidatesAsync();
            }
            else
            {
                _host.Logger.Warn($"角色包导入失败：{result.Error}（来源 {packDirectory}）");
                CandidateStatusText.Text = $"导入失败：{result.Error}";
                _bubbles.Show("knowledge-import-failed", $"角色包导入失败：{result.Error}", BubbleLevel.Error);
            }
        }
        catch (Exception ex)
        {
            _host.Logger.Error($"导入角色包失败：{ex.Message}", ex);
            CandidateStatusText.Text = $"导入失败：{ex.Message}";
            _bubbles.Show("knowledge-import-failed", "角色包导入失败，详见日志。", BubbleLevel.Error);
        }
    }

    // ------------------------------------------------------------------ 语音页

    private void RefreshVoiceTab()
    {
        if (!_host.IsStarted)
        {
            return;
        }

        TtsEngineText.Text = AppearanceStore.LoadTtsEngine() is null
            ? $"配置引擎：{_host.Tts.ConfiguredEngine}（来源 config.yaml）"
            : $"配置引擎：{_host.Tts.ConfiguredEngine}（来源 Assets\\appearance.json，优先级高于 config.yaml）";
        TtsVoiceText.Text = _host.Tts.IsDegraded
            ? $"当前实际引擎：{_host.Tts.CurrentEngineName}（降级）"
            : $"当前实际引擎：{_host.Tts.CurrentEngineName}";
        TtsPiperText.Text = $"全局默认音色：{_host.Config.Tts.DefaultVoice}（为空时回退硬编码 " +
                            $"\"{Core.Tts.TTSManager.HardcodedFallbackVoice}\"）";

        AsrStateText.Text = _host.Config.Asr.PushToTalk
            ? $"状态：按住说话（按住 {_host.Config.Asr.PushToTalkKey} 录音，松开识别）" +
              (_host.Asr.State == AsrState.Running ? "｜录音中…" : string.Empty)
            : $"状态：{_host.Asr.State}";
        AsrModelText.Text = $"模型路径：{Core.Configuration.AppPaths.Resolve(_host.Config.Asr.ModelPath)}";
        AsrVadText.Text = $"VAD：{_host.Asr.VadName}（配置要求 {_host.Config.Asr.VadMode}）";

        AsrLastText.Text = _host.Asr.LastRecognition is { } last
            ? $"最近一次识别：\"{last.Text}\"（音频 {last.AudioDuration.TotalSeconds:N1}s，" +
              $"耗时 {last.Elapsed.TotalMilliseconds:N0} ms，RTF {last.RealTimeFactor:N2}）"
            : "最近一次识别：暂无";

        AsrHintText.Text = _host.Asr.State == AsrState.Faulted
            ? "语音输入当前不可用，请使用上方文本框输入文字。" +
              (_host.Asr.FaultReason == AsrFaultReason.ModelMissing
                  ? "可在 config.yaml 的 asr.model_path 指定新的模型路径，然后点\"重置 ASR\"。"
                  : "可点\"重启语音服务\"或\"重置 ASR\"重试。")
            : string.Empty;
    }

    private async void OnRestartVoice(object sender, RoutedEventArgs e)
    {
        try
        {
            await _host.Tts.ResetAsync();
            await _host.Asr.ResetAsync();
            _bubbles.Show("voice-restarted", "语音服务已重启。", Core.Abstractions.BubbleLevel.Info,
                TimeSpan.FromSeconds(_host.Config.Ui.BubbleDurationSeconds));
            RefreshVoiceTab();
        }
        catch (Exception ex)
        {
            _host.Logger.Error($"重启语音服务失败：{ex.Message}", ex);
        }
    }

    private async void OnResetAsr(object sender, RoutedEventArgs e)
    {
        try
        {
            await _host.Asr.ResetAsync();
            RefreshVoiceTab();
        }
        catch (Exception ex)
        {
            _host.Logger.Error($"重置 ASR 失败：{ex.Message}", ex);
        }
    }

    private void OnToggleListening(object sender, RoutedEventArgs e)
    {
        try
        {
            // 按住说话模式下没有"常开监听"可切换，手动开监听会和按键录音打架。
            if (_host.Config.Asr.PushToTalk)
            {
                _host.Logger.Info($"当前为按住说话模式（按键 {_host.Config.Asr.PushToTalkKey}），监听由按键触发，无需手动切换。");
                RefreshVoiceTab();
                return;
            }

            if (_host.Asr.State == AsrState.Running)
            {
                _host.Asr.StopListening();
            }
            else
            {
                _host.Asr.StartListening();
            }

            RefreshVoiceTab();
        }
        catch (Exception ex)
        {
            _host.Logger.Error($"切换监听状态失败：{ex.Message}", ex);
        }
    }

    // ------------------------------------------------------------------ 游戏检测页

    private void RefreshMonitorTab()
    {
        if (!_host.IsStarted)
        {
            return;
        }

        var last = _host.Monitor.LastMessageUtc;
        MonitorStateText.Text = _host.Monitor.IsClientConnected
            ? $"状态：已连接（最后收到消息：{(last == DateTime.MinValue ? "—" : last.ToLocalTime().ToString("HH:mm:ss"))}）"
            : "状态：未连接。GameMonitor.exe 未运行或已断开。";

        var cooldowns = _host.Cooldown.Snapshot();
        MonitorCooldownText.Text = cooldowns.Count == 0
            ? "冷却期：无"
            : "冷却期：" + string.Join("；", cooldowns.Select(kv =>
                $"{kv.Key} 至 {kv.Value.ToLocalTime():HH:mm:ss}"));

        GameListText.Text = $"列表文件：{Core.Configuration.AppPaths.Resolve(_host.Config.Monitor.GameListFile)}" +
                            Environment.NewLine +
                            $"TCP 端口：{_host.Config.Monitor.Host}:{_host.Config.Monitor.Port}" +
                            Environment.NewLine +
                            $"冷却时长：{_host.Config.Monitor.CooldownMinutes:N0} 分钟（绑定最后退出的游戏进程名）";
    }

    private async void OnReconnectMonitor(object sender, RoutedEventArgs e)
    {
        try
        {
            await _host.Monitor.ReconnectAsync();
            RefreshMonitorTab();
        }
        catch (Exception ex)
        {
            _host.Logger.Error($"重连游戏检测失败：{ex.Message}", ex);
        }
    }

    // ------------------------------------------------------------------ 日志页

    private void OnRefreshLogs(object sender, RoutedEventArgs e)
    {
        try
        {
            // LogSink 在配置加载后才有；启动早期回退到引导日志。
            var sink = _host.LogSink ?? _host.BootstrapSink;
            LogPathText.Text = $"应用日志目录：{sink?.DirectoryPath ?? "（未初始化）"}";
            LogSizeText.Text = sink is null
                ? "应用日志占用：—"
                : $"应用日志占用：{sink.GetDirectorySizeBytes() / 1024.0 / 1024.0:N2} MB" +
                  $"（保留 {_host.Config.Logging.MaxLogDays} 天）";

            ChatLogSizeText.Text = $"聊天日志占用：{_host.Context.GetChatLogSizeBytes() / 1024.0 / 1024.0:N2} MB" +
                                   $"（归档文件 {Path.GetFileName(_host.Context.ArchivePath)}）";

            LogList.ItemsSource = sink?.RecentSnapshot(300) ?? new List<string> { "（日志未初始化）" };
        }
        catch (Exception ex)
        {
            LogList.ItemsSource = new List<string> { $"读取日志失败：{ex.Message}" };
        }
    }

    private void OnPurgeAppLogs(object sender, RoutedEventArgs e)
    {
        try
        {
            var deleted = _host.LogSink?.PurgeAll() ?? 0;
            _bubbles.Show("logs-purged", $"已清理 {deleted} 个应用日志文件。", Core.Abstractions.BubbleLevel.Info,
                TimeSpan.FromSeconds(_host.Config.Ui.BubbleDurationSeconds));
            OnRefreshLogs(sender, e);
        }
        catch (Exception ex)
        {
            _host.Logger.Error($"清理应用日志失败：{ex.Message}", ex);
        }
    }

    private void OnCleanupChatLogs(object sender, RoutedEventArgs e)
    {
        try
        {
            var removed = _host.Context.CleanupOldChatLogs();
            _bubbles.Show("chatlogs-cleaned", $"已清理 {removed} 个过期聊天日志文件。",
                Core.Abstractions.BubbleLevel.Info,
                TimeSpan.FromSeconds(_host.Config.Ui.BubbleDurationSeconds));
            OnRefreshLogs(sender, e);
        }
        catch (Exception ex)
        {
            _host.Logger.Error($"清理聊天日志失败：{ex.Message}", ex);
        }
    }

    private void OnOpenLogFolder(object sender, RoutedEventArgs e)
    {
        try
        {
            var path = _host.LogSink?.DirectoryPath;
            if (string.IsNullOrEmpty(path) || !Directory.Exists(path))
            {
                return;
            }

            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            _host.Logger.Warn($"打开日志目录失败：{ex.Message}");
        }
    }

    // ------------------------------------------------------------------ AI 功能开关

    /// <summary>
    /// 把 appearance.json 里的覆盖值（没有就用 config.yaml 的值）铺到勾选框上。
    /// </summary>
    private void LoadAiToggleTab()
    {
        var toggle = AppearanceStore.LoadAiToggles();

        LlmEnabledCheck.IsChecked = toggle.Llm ?? _host.Config.Llm.Enabled;
        TtsEnabledCheck.IsChecked = toggle.Tts ?? _host.Config.Tts.Enabled;
        AsrEnabledCheck.IsChecked = toggle.Asr ?? _host.Config.Asr.Enabled;

        var fromStore = toggle.Llm is not null || toggle.Tts is not null || toggle.Asr is not null;
        AiToggleHintText.Text = fromStore
            ? "当前显示的是 Assets\appearance.json 里的偏好"
            : "当前跟随 config.yaml";
    }

    private void OnSaveAiToggles(object sender, RoutedEventArgs e)
    {
        try
        {
            var llm = LlmEnabledCheck.IsChecked == true;
            var tts = TtsEnabledCheck.IsChecked == true;
            var asr = AsrEnabledCheck.IsChecked == true;

            var ok = AppearanceStore.SaveAiToggles(llm, tts, asr);

            _host.Logger.Info(
                $"设置面板已保存 AI 功能开关：对话={(llm ? "开" : "关")}，语音输出={(tts ? "开" : "关")}，" +
                $"语音输入={(asr ? "开" : "关")}（保存{(ok ? "成功" : "失败")}，下次启动生效）。");

            AiToggleHintText.Text = ok ? "已保存，重启程序后生效" : "保存失败，详见日志";

            _bubbles.Show(
                "ai-toggles-saved",
                ok ? "AI 功能开关已保存，重启程序后生效。" : "AI 功能开关保存失败，详见日志。",
                ok ? BubbleLevel.Info : BubbleLevel.Warning,
                TimeSpan.FromSeconds(4));
        }
        catch (Exception ex)
        {
            AiToggleHintText.Text = $"保存失败：{ex.Message}";
            _host.Logger.Warn($"保存 AI 功能开关失败：{ex.Message}");
        }
    }

    /// <summary>清掉覆盖，三项都跟随 config.yaml。</summary>
    private void OnClearAiToggles(object sender, RoutedEventArgs e)
    {
        var ok = AppearanceStore.SaveAiToggles(null, null, null);
        LoadAiToggleTab();
        AiToggleHintText.Text = ok ? "已清除偏好，重启程序后按 config.yaml 运行" : "清除失败，详见日志";
        _host.Logger.Info($"设置面板已清除 AI 功能开关偏好（{(ok ? "成功" : "失败")}）。");
    }
}
