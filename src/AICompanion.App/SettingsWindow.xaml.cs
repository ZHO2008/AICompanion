using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using AICompanion.App.Services;
using AICompanion.Core.Abstractions;
using AICompanion.Core.Asr;
using AICompanion.Core.Configuration;
using AICompanion.Core.Health;
using AICompanion.Core.Knowledge;

namespace AICompanion.App;

/// <summary>
/// 设置面板（外观 / 对话 / 语音 / 互动 / 行为 / 诊断 / 拓展 / 关于）。
///
/// 布局参考 VPet-Simulator 的设置页：顶部一行标签、左侧搜索与分组、右侧一行一项。
/// 仍然遵守原有的两条准则：
///   - config.yaml 只读，界面上的「出厂默认」就是它，程序绝不回写（准则 9）；
///   - 界面改动写入 Assets\appearance.json 覆盖层，删掉该文件即恢复出厂默认。
/// </summary>
public partial class SettingsWindow : Window
{
    private enum SettingsPage
    {
        Appearance,
        Dialog,
        Voice,
        Interaction,
        Behavior,
        Diagnostic,
        Extend,
        About,
    }

    /// <summary>左侧 9 个分组与它们所属的顶部标签（顺序即界面顺序）。</summary>
    private static readonly (string Group, SettingsPage Page)[] GroupMap =
    {
        ("隐藏窗口", SettingsPage.Appearance),
        ("模型与渲染", SettingsPage.Appearance),
        ("对话与模型", SettingsPage.Dialog),
        ("语音与听写", SettingsPage.Voice),
        ("互动设置", SettingsPage.Interaction),
        ("桌宠移动", SettingsPage.Behavior),
        ("行为与游戏", SettingsPage.Behavior),
        ("数据与诊断", SettingsPage.Diagnostic),
        ("拓展模块", SettingsPage.Extend),
    };

    private readonly AppHost _host;
    private readonly BubbleNotifier _bubbles;
    private readonly DispatcherTimer _refreshTimer;

    /// <summary>诊断页「日志」清单显示的行数：只作为最近现场的窗口，不是全量日志。</summary>
    private const int LogTailLines = 300;

    /// <summary>外观下拉里最多列出多少个素材/模型：仓库里可能堆了成百上千个 png。</summary>
    private const int AssetScanLimit = 40;

    private const int ModelScanLimit = 60;

    /// <summary>滑块的自动保存防抖：拖动时每个像素都写一次 appearance.json 没有必要。</summary>
    private readonly DispatcherTimer _saveTimer;

    /// <summary>
    /// 外观项的"立即应用"防抖（180ms ≥ 要求的 150ms）。
    ///
    /// 为什么必须防抖：缩放/不透明度/立绘这几项一变就要让主窗口重算，
    /// 而立绘/Live2D 会触发 LoadAvatar —— 每像素重建一次 Live2D 是灾难。
    /// 与落盘的 400ms 防抖分开：应用要快（用户立刻看到），落盘可以合并。
    /// </summary>
    private readonly DispatcherTimer _applyTimer;

    /// <summary>
    /// 待落盘的改动，按"改的是哪一项"分槽。
    ///
    /// 以前是单槽（一个 Action 字段）：400ms 防抖窗口里先后动两个滑块时，
    /// 后一个会把前一个**覆盖掉**，直接丢掉一次修改（实测：先拖缩放再拖不透明度，
    /// 缩放值根本没写进 appearance.json）。分槽后同一个 key 只保留最后一次，
    /// 不同 key 互不影响。
    /// </summary>
    private readonly Dictionary<string, Action> _pendingSaves = new(StringComparer.Ordinal);

    /// <summary>待应用的变更（多次改动合并成一次）。</summary>
    private AppearanceChange? _pendingApply;

    /// <summary>窗口已关闭：之后到的异步收尾 / 定时器回调一律不再碰界面。</summary>
    private bool _closed;

    /// <summary>日志与待审核候选这两张清单是否已经按"启动已完成"刷过一次。</summary>
    private bool _startupListsLoaded;

    /// <summary>GPU 查询节流：2 秒定时器不该每次都起两个 nvidia-smi 子进程。</summary>
    private DateTime _lastGpuQueryUtc = DateTime.MinValue;

    /// <summary>正在导入模块：防止连点「添加」并发复制同一批文件。</summary>
    private bool _importingModule;

    private string _versionText = "0.0.0";

    /// <summary>显存查询是否在飞行中（防止 2 秒定时器叠加 nvidia-smi 子进程）。</summary>
    private bool _gpuQuerying;

    private IReadOnlyList<PendingCandidate> _candidates = Array.Empty<PendingCandidate>();

    /// <summary>初始化滑块期间不响应 ValueChanged，避免把默认值当成用户修改。</summary>
    private bool _appearanceInitialized;

    /// <summary>初始化引擎下拉框期间不响应 SelectionChanged。</summary>
    private bool _ttsEngineInitialized;

    /// <summary>初始化导航 / 下拉 / 开关期间不响应事件。</summary>
    private bool _loading = true;

    /// <summary>左侧分组选择是程序代选时，不触发滚动逻辑。</summary>
    private bool _navSyncing;

    private SettingsPage _page = SettingsPage.Appearance;

    /// <summary>
    /// 外观类的"立即生效"不再走构造参数里的回调，而是统一用
    /// <see cref="AppHost.RequestAppearanceChange"/> 通知已打开的桌宠窗口
    /// （主窗口没开时它会自己降级为"下次启动生效"并记 Debug）。
    /// </summary>
    public SettingsWindow(AppHost host, BubbleNotifier bubbles)
    {
        _host = host;
        _bubbles = bubbles;

        InitializeComponent();

        // 主题色板定义在 Window.Resources（Themes/SettingsTheme.xaml）里，
        // 而 Window 自身的属性不能 StaticResource 引用"稍后才解析"的资源，所以这里用代码取。
        Background = (Brush)FindResource("WindowBackgroundBrush");
        Foreground = (Brush)FindResource("PrimaryTextBrush");
        FontSize = 12.5;

        _refreshTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher)
        {
            Interval = TimeSpan.FromSeconds(2),
        };
        _refreshTimer.Tick += OnRefreshTick;
        _refreshTimer.Start();

        _saveTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(400),
        };
        _saveTimer.Tick += (_, _) =>
        {
            _saveTimer.Stop();

            if (_pendingSaves.Count == 0)
            {
                return;
            }

            FlushPendingSave();
            MarkAutoSaved();
        };

        _applyTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(180),
        };
        _applyTimer.Tick += (_, _) => FlushPendingApply();

        Loaded += OnLoaded;
        Closed += OnClosed;

        LoadStaticInfo();
        LoadAppearanceTab();
        LoadTtsEngineTab();
        LoadAiToggleTab();
        LoadDialogCombos();
        LoadVoiceCombos();
        LoadInteractionControls();
        LoadBehaviorControls();

        InitializeNavigation();

        _loading = false;
    }

    /// <summary>
    /// 首次显示后的一次性加载。
    ///
    /// 必须是"包了 try/catch 的具名方法"，不能是 <c>async void</c> 匿名 lambda：
    /// 里面每一条都会在 await 之后继续碰控件，任何一条漏出来的异常都会变成
    /// "界面线程未处理异常"（程序能活，但用户看到一句内部错误，且后续初始化全部中断）。
    /// </summary>
    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoaded;

        try
        {
            RefreshStatus();
            await RefreshCandidatesAsync();
            OnRefreshLogs(this, new RoutedEventArgs());
            await RefreshEnvironmentAndModulesAsync();
        }
        catch (Exception ex)
        {
            _host.Logger.Error($"设置面板首次加载失败：{ex.Message}", ex);
            AppearanceHintText.Text = "部分信息加载失败，详见日志";
        }
    }

    /// <summary>
    /// 关窗收尾（必须可重入：FlushPendingApply 内部还会再调一次 FlushPendingSave）。
    ///
    /// 顺序：先落盘再应用（主窗口是从 appearance.json 读值的），最后停三个定时器。
    /// 之后任何迟到的异步回调看到 <see cref="_closed"/> 就直接返回，不会往已关闭的窗口写东西。
    /// </summary>
    private void OnClosed(object? sender, EventArgs e)
    {
        if (_closed)
        {
            return;
        }

        _closed = true;

        FlushPendingSave();
        FlushPendingApply();

        _refreshTimer.Stop();
        _saveTimer.Stop();
        _applyTimer.Stop();

        _pendingApply = null;
        _pendingSaves.Clear();
    }

    private void OnRefreshTick(object? sender, EventArgs e)
    {
        if (_closed)
        {
            _refreshTimer.Stop();
            return;
        }

        RefreshStatus();
        RefreshVoiceTab();
        RefreshMonitorTab();
        RefreshPositionAndRenderInfo();

        // 启动过程中打开面板时，日志/候选清单只能先显示"启动中"；
        // 启动完成后在这里补一次，否则面板会一直停在提示上。
        RefreshStartupListsOnce();
    }

    // ================================================================== 导航 / 搜索

    private void InitializeNavigation()
    {
        _navReady = false;

        NavTabs.SelectedIndex = 0;
        GroupList.SelectedIndex = 1; // 默认落在「模型与渲染」，与外观页首个内容分组一致

        _navReady = true;
        ShowPage(SettingsPage.Appearance);
    }

    private bool _navReady;

    private void OnNavTabChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_navReady || _loading || _navSyncing || NavTabs.SelectedItem is not ListBoxItem item)
        {
            return;
        }

        var page = item.Tag switch
        {
            "外观" => SettingsPage.Appearance,
            "对话" => SettingsPage.Dialog,
            "语音" => SettingsPage.Voice,
            "互动" => SettingsPage.Interaction,
            "行为" => SettingsPage.Behavior,
            "诊断" => SettingsPage.Diagnostic,
            "拓展" => SettingsPage.Extend,
            "关于" => SettingsPage.About,
            _ => SettingsPage.Appearance,
        };

        ShowPage(page);
        SyncGroupSelection(page);
        ApplySearchFilter();
    }

    private void OnGroupChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_navReady || _loading || _navSyncing || GroupList.SelectedItem is not ListBoxItem item)
        {
            return;
        }

        var group = item.Tag as string;
        var target = GroupMap.FirstOrDefault(g => g.Group == group);
        if (target.Group is null)
        {
            return;
        }

        if (_page != target.Page)
        {
            SelectTab(target.Page);
            ShowPage(target.Page);
        }

        ScrollToGroup(group);
        ApplySearchFilter();
    }

    /// <summary>把左侧分组选中项同步成"当前页的第一个分组"；「关于」没有分组，清空选中。</summary>
    private void SyncGroupSelection(SettingsPage page)
    {
        var first = GroupMap.FirstOrDefault(g => g.Page == page);

        _navSyncing = true;
        try
        {
            if (first.Group is null)
            {
                GroupList.SelectedIndex = -1;
                return;
            }

            for (var i = 0; i < GroupList.Items.Count; i++)
            {
                if (GroupList.Items[i] is ListBoxItem gi && Equals(gi.Tag, first.Group))
                {
                    GroupList.SelectedIndex = i;
                    return;
                }
            }
        }
        finally
        {
            _navSyncing = false;
        }
    }

    private void SelectTab(SettingsPage page)
    {
        _navSyncing = true;
        try
        {
            foreach (var item in NavTabs.Items)
            {
                if (item is ListBoxItem li && PageOfTab(li.Tag as string) == page)
                {
                    NavTabs.SelectedItem = li;
                    return;
                }
            }
        }
        finally
        {
            _navSyncing = false;
        }
    }

    private static SettingsPage PageOfTab(string? tag) => tag switch
    {
        "对话" => SettingsPage.Dialog,
        "语音" => SettingsPage.Voice,
        "互动" => SettingsPage.Interaction,
        "行为" => SettingsPage.Behavior,
        "诊断" => SettingsPage.Diagnostic,
        "拓展" => SettingsPage.Extend,
        "关于" => SettingsPage.About,
        _ => SettingsPage.Appearance,
    };

    private void ShowPage(SettingsPage page)
    {
        _page = page;

        PageAppearance.Visibility = page == SettingsPage.Appearance ? Visibility.Visible : Visibility.Collapsed;
        PageDialog.Visibility = page == SettingsPage.Dialog ? Visibility.Visible : Visibility.Collapsed;
        PageVoice.Visibility = page == SettingsPage.Voice ? Visibility.Visible : Visibility.Collapsed;
        PageInteraction.Visibility = page == SettingsPage.Interaction ? Visibility.Visible : Visibility.Collapsed;
        PageBehavior.Visibility = page == SettingsPage.Behavior ? Visibility.Visible : Visibility.Collapsed;
        PageDiagnostic.Visibility = page == SettingsPage.Diagnostic ? Visibility.Visible : Visibility.Collapsed;
        PageExtend.Visibility = page == SettingsPage.Extend ? Visibility.Visible : Visibility.Collapsed;
        PageAbout.Visibility = page == SettingsPage.About ? Visibility.Visible : Visibility.Collapsed;

        ContentScroll.ScrollToTop();
        RefreshPositionAndRenderInfo();
    }

    private StackPanel CurrentPage => _page switch
    {
        SettingsPage.Dialog => PageDialog,
        SettingsPage.Voice => PageVoice,
        SettingsPage.Interaction => PageInteraction,
        SettingsPage.Behavior => PageBehavior,
        SettingsPage.Diagnostic => PageDiagnostic,
        SettingsPage.Extend => PageExtend,
        SettingsPage.About => PageAbout,
        _ => PageAppearance,
    };

    private Border? SectionOf(string? group) => group switch
    {
        "隐藏窗口" => SectionHiddenWindow,
        "模型与渲染" => SectionModelRender,
        "对话与模型" => SectionDialog,
        "语音与听写" => SectionVoice,
        "互动设置" => SectionInteraction,
        "桌宠移动" => SectionPetMove,
        "行为与游戏" => SectionBehavior,
        "数据与诊断" => SectionDataDiag,
        "拓展模块" => SectionModules,
        _ => null,
    };

    private void ScrollToGroup(string? group)
    {
        var section = SectionOf(group);
        if (section is null || section.Visibility != Visibility.Visible)
        {
            return;
        }

        try
        {
            section.BringIntoView();
        }
        catch
        {
            // 尚未完成布局时 BringIntoView 可能失败，忽略即可。
        }
    }

    private void OnGoToExtend(object sender, RoutedEventArgs e)
    {
        SelectTab(SettingsPage.Extend);
        ShowPage(SettingsPage.Extend);
        SyncGroupSelection(SettingsPage.Extend);
        ApplySearchFilter();
    }

    // ------------------------------------------------------------------ 搜索过滤

    private void OnSearchChanged(object sender, TextChangedEventArgs e)
    {
        if (_loading || _closed)
        {
            return;
        }

        ApplySearchFilter();
    }

    /// <summary>
    /// 搜索词归一化：去空白、折叠成小写、全角折成半角。
    ///
    /// 为什么必须折全角：中文输入法下搜 "ｃｕｄａ"（全角）原先一个都匹配不到，
    /// 用户只会以为"没有这一项"。折叠规则只处理可逆的 ASCII 映射，
    /// 不走 <see cref="string.Normalize(NormalizationForm)"/>（遇到孤立代理项会抛异常）。
    /// </summary>
    private static string NormalizeQuery(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        var sb = new StringBuilder(text.Length);
        foreach (var ch in text)
        {
            if (char.IsWhiteSpace(ch) || ch == '\u3000')
            {
                continue;
            }

            sb.Append(char.ToLowerInvariant(FoldFullWidth(ch)));
        }

        return sb.ToString();
    }

    /// <summary>全角 ASCII（U+FF01–U+FF5E）折成半角；其余字符原样返回。</summary>
    private static char FoldFullWidth(char ch)
        => ch is >= '\uFF01' and <= '\uFF5E' ? (char)(ch - 0xFEE0) : ch;

    /// <summary>
    /// 按标签片段过滤当前页的设置项：大小写不敏感、空白不敏感。
    /// 命中判断用整行（标签 + 说明 + 控件内容）的文本，行内任一文本含片段即显示。
    /// </summary>
    private void ApplySearchFilter()
    {
        var query = NormalizeQuery(SearchBox.Text);
        var page = CurrentPage;

        var rows = new List<FrameworkElement>();
        CollectByTag(page, "row", rows);

        var anyRow = false;
        foreach (var row in rows)
        {
            var match = query.Length == 0 || NormalizeQuery(RowText(row)).Contains(query);
            row.Visibility = match ? Visibility.Visible : Visibility.Collapsed;
            anyRow |= match;
        }

        var anyCard = false;

        var cards = new List<FrameworkElement>();
        CollectByTag(page, "card", cards);

        foreach (var card in cards)
        {
            if (query.Length == 0)
            {
                card.Visibility = Visibility.Visible;
                continue;
            }

            var cardRows = new List<FrameworkElement>();
            CollectByTag(card, "row", cardRows);

            var cardVisible = cardRows.Count == 0
                ? NormalizeQuery(RowText(card)).Contains(query)
                : cardRows.Any(r => r.Visibility == Visibility.Visible);

            // 卡片里的动态内容（模块卡片、日志清单）也算命中依据。
            if (!cardVisible && cardRows.Count > 0)
            {
                cardVisible = NormalizeQuery(RowText(card)).Contains(query);
            }

            card.Visibility = cardVisible ? Visibility.Visible : Visibility.Collapsed;
            anyCard |= cardVisible;
        }

        SearchEmptyText.Visibility = query.Length > 0 && !anyRow && !anyCard
            ? Visibility.Visible
            : Visibility.Collapsed;

        // 搜索时把命中的卡片滚到可见位置，省得用户以为没结果。
        if (query.Length > 0 && anyRow)
        {
            var first = rows.FirstOrDefault(r => r.Visibility == Visibility.Visible);
            first?.BringIntoView();
        }
    }

    private static void CollectByTag(DependencyObject root, string tag, List<FrameworkElement> into)
    {
        if (root is FrameworkElement fe && Equals(fe.Tag, tag))
        {
            into.Add(fe);
        }

        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            CollectByTag(VisualTreeHelper.GetChild(root, i), tag, into);
        }
    }

    private static string RowText(DependencyObject node)
    {
        var sb = new StringBuilder();
        CollectText(node, sb);
        return sb.ToString();
    }

    private static void CollectText(DependencyObject node, StringBuilder sb)
    {
        switch (node)
        {
            case TextBlock tb:
                sb.Append(tb.Text).Append(' ');
                break;
            case TextBox box:
                sb.Append(box.Text).Append(' ');
                break;
            case ComboBox combo:
                foreach (var item in combo.Items)
                {
                    sb.Append(item switch
                    {
                        ComboBoxItem ci => ci.Content?.ToString(),
                        _ => item?.ToString(),
                    }).Append(' ');
                }

                break;
            case ContentControl cc when cc.Content is string content:
                sb.Append(content).Append(' ');
                break;
            case System.Windows.Documents.Run run:
                sb.Append(run.Text).Append(' ');
                break;
        }

        foreach (var child in LogicalTreeHelper.GetChildren(node))
        {
            if (child is DependencyObject d)
            {
                CollectText(d, sb);
            }
            else if (child is string s)
            {
                sb.Append(s).Append(' ');
            }
        }
    }

    // ================================================================== 静态信息 / 下拉初始化

    private void LoadStaticInfo()
    {
        var version = Assembly.GetExecutingAssembly().GetName().Version;

        VersionText.Text = version is null
            ? "未知"
            : $"{version.Major}.{version.Minor}.{version.Build}";
        _versionText = VersionText.Text;
        AutoSaveText.Text = $"自动保存 · v{_versionText}";

        RepoText.Text = "https://github.com/ZHO2008/AICompanion";
        StorePathText.Text = AppearanceStore.StorePath;
        ModulesPathText.Text = ModuleRegistry.ModulesRoot;
        AlwaysOnTopText.Text = _host.Config.Ui.AlwaysOnTop ? "开启（config.yaml）" : "关闭（config.yaml）";

        ShortcutText.Text =
            $"按住 {_host.Config.Asr.PushToTalkKey}（{(_host.Config.Asr.PushToTalk ? "已启用" : "已关闭")}）" +
            Environment.NewLine +
            $"悬停 {_host.Config.Ui.TransparentHoverDelayMs} ms · {_host.Config.Ui.TransparentAutoRevertSeconds} s 回透明";
    }

    private void LoadAppearanceTab()
    {
        var seconds = AppearanceStore.LoadBubbleSeconds() ?? _host.Config.Ui.BubbleDurationSeconds;
        var opacity = AppearanceStore.LoadTransparentOpacity() ?? _host.Config.Ui.TransparentOpacity;
        var offset = AppearanceStore.LoadHorizontalOffset() ?? 0.0;
        var scale = AppearanceStore.LoadScale() ?? (_host.Config.Ui.Scale > 0 ? _host.Config.Ui.Scale : 1.0);

        _appearanceInitialized = false;

        BubbleSecondsSlider.Value = Math.Clamp(seconds, BubbleSecondsSlider.Minimum, BubbleSecondsSlider.Maximum);
        OpacitySlider.Value = Math.Clamp(opacity, OpacitySlider.Minimum, OpacitySlider.Maximum);
        HorizontalOffsetSlider.Value = Math.Clamp(offset, HorizontalOffsetSlider.Minimum, HorizontalOffsetSlider.Maximum);
        ScaleSlider.Value = Math.Clamp(scale, ScaleSlider.Minimum, ScaleSlider.Maximum);

        UpdateAppearanceLabels();
        LoadAppearanceAssets();
        _appearanceInitialized = true;
    }

    private void UpdateAppearanceLabels()
    {
        BubbleSecondsText.Text = $"{BubbleSecondsSlider.Value:F0} 秒";
        OpacityText.Text = $"{OpacitySlider.Value:P0}";
        ScaleText.Text = $"{ScaleSlider.Value:P0}";

        var offset = HorizontalOffsetSlider.Value;
        HorizontalOffsetText.Text = Math.Abs(offset) < 0.0005
            ? "自动居中"
            : (offset > 0 ? $"左移 {offset:P0}" : $"右移 {-offset:P0}");
    }

    /// <summary>外观下拉：列出仓库里能用的 Live2D 模型与静态立绘，外加「跟随 config.yaml」。</summary>
    private void LoadAppearanceAssets()
    {
        AppearanceAssetCombo.Items.Clear();

        var live2dDefault = Path.GetFileName(AppPaths.Resolve(_host.Config.Live2d.DefaultModelPath));

        AppearanceAssetCombo.Items.Add(new ComboBoxItem
        {
            Content = $"跟随 config.yaml（{live2dDefault}）",
            Tag = new AppearanceAssetOption(null, null),
        });

        foreach (var model in SafeEnumerate("*.model3.json", 40))
        {
            var relative = RelativeToRoot(model);
            AppearanceAssetCombo.Items.Add(new ComboBoxItem
            {
                Content = $"Live2D · {relative}",
                Tag = new AppearanceAssetOption(relative, null),
            });
        }

        foreach (var image in SafeEnumerate("*.png", 40))
        {
            var relative = RelativeToRoot(image);
            AppearanceAssetCombo.Items.Add(new ComboBoxItem
            {
                Content = $"立绘 · {relative}",
                Tag = new AppearanceAssetOption(null, relative),
            });
        }

        var savedLive2d = AppearanceStore.LoadLive2dModelPath();
        var savedImage = AppearanceStore.LoadImagePath();

        var selected = AppearanceAssetCombo.Items
            .OfType<ComboBoxItem>()
            .FirstOrDefault(i => i.Tag is AppearanceAssetOption o &&
                                 (savedLive2d is not null ? o.Live2dPath == savedLive2d : o.ImagePath == savedImage));

        AppearanceAssetCombo.SelectedItem = selected ?? AppearanceAssetCombo.Items[0];

        Live2dCheck.IsChecked = AppearanceStore.LoadLive2dEnabled() ?? true;
    }

    private sealed record AppearanceAssetOption(string? Live2dPath, string? ImagePath);

    /// <summary>只枚举仓库根下的指定扩展名，跳过 bin/obj/logs/models 这些大目录。</summary>
    private static IEnumerable<string> SafeEnumerate(string pattern, int limit)
    {
        var found = new List<string>();

        try
        {
            foreach (var file in Directory.EnumerateFiles(AppPaths.Root, pattern, SearchOption.AllDirectories))
            {
                var relative = RelativeToRoot(file);
                if (relative.StartsWith("bin/", StringComparison.OrdinalIgnoreCase) ||
                    relative.StartsWith("obj/", StringComparison.OrdinalIgnoreCase) ||
                    relative.StartsWith("logs/", StringComparison.OrdinalIgnoreCase) ||
                    relative.StartsWith("models/", StringComparison.OrdinalIgnoreCase) ||
                    relative.StartsWith("third_party/", StringComparison.OrdinalIgnoreCase) ||
                    relative.StartsWith("pack/", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                found.Add(file);
                if (found.Count >= limit)
                {
                    break;
                }
            }
        }
        catch
        {
            // 枚举失败就当作没有素材，下拉里只剩「跟随 config.yaml」。
        }

        return found;
    }

    private static string RelativeToRoot(string path)
    {
        try
        {
            return Path.GetRelativePath(AppPaths.Root, path).Replace('\\', '/');
        }
        catch
        {
            return path;
        }
    }

    private void LoadDialogCombos()
    {
        LoadLlmModels();
        LoadLlmContext();
        LoadGpuStrategy();
        LoadLlmBackends();
    }

    private sealed record ModelOption(string? RelativePath);

    private sealed record IntOption(int Value);

    private void LoadLlmModels()
    {
        LlmModelCombo.Items.Clear();

        LlmModelCombo.Items.Add(new ComboBoxItem
        {
            Content = $"跟随 config.yaml（{Path.GetFileName(_host.Config.Llm.ModelPath)}）",
            Tag = new ModelOption(null),
        });

        foreach (var file in SafeEnumerateModels())
        {
            var relative = RelativeToRoot(file);
            LlmModelCombo.Items.Add(new ComboBoxItem
            {
                Content = $"{relative}（{new FileInfo(file).Length / 1024.0 / 1024.0:F0} MB）",
                Tag = new ModelOption(relative),
            });
        }

        var saved = AppearanceStore.LoadLlmModelPath();
        var selected = LlmModelCombo.Items.OfType<ComboBoxItem>()
            .FirstOrDefault(i => i.Tag is ModelOption o && o.RelativePath == saved);

        LlmModelCombo.SelectedItem = saved is null
            ? LlmModelCombo.Items[0]
            : selected ?? LlmModelCombo.Items[0];
    }

    private static IEnumerable<string> SafeEnumerateModels()
    {
        var found = new List<string>();

        try
        {
            var modelsRoot = Path.Combine(AppPaths.Root, "models", "llm");
            if (Directory.Exists(modelsRoot))
            {
                foreach (var file in Directory.EnumerateFiles(modelsRoot, "*.gguf", SearchOption.AllDirectories))
                {
                    found.Add(file);
                    if (found.Count >= 60)
                    {
                        break;
                    }
                }
            }
        }
        catch
        {
            // 忽略枚举失败。
        }

        return found.OrderBy(f => f, StringComparer.OrdinalIgnoreCase);
    }

    private void LoadLlmContext()
    {
        var saved = AppearanceStore.LoadLlmContextLength() ?? _host.Config.Llm.ContextLength;

        FillIntCombo(
            LlmContextCombo,
            new[] { 0, 2048, 4096, 8192, 16384, 32768 },
            value => value == 0 ? "自动 · 按模型上限" : value.ToString(),
            saved);
    }

    private void LoadGpuStrategy()
    {
        var saved = AppearanceStore.LoadGpuReservedMb() ?? _host.Config.Gpu.ReservedMb;

        FillIntCombo(
            GpuStrategyCombo,
            new[] { 0, 2048, 4096, 6144, 8192 },
            value => value == 0 ? "自动 · 按显存档位" : $"预留 {value} MB",
            saved);
    }

    /// <summary>把"一串整数选项"填进下拉框并选中与保存值相同的一项（两个下拉原本是逐字重复的两份代码）。</summary>
    private static void FillIntCombo(ComboBox combo, int[] values, Func<int, string> label, int selected)
    {
        combo.Items.Clear();

        foreach (var value in values)
        {
            combo.Items.Add(new ComboBoxItem
            {
                Content = label(value),
                Tag = new IntOption(value),
            });
        }

        combo.SelectedItem = combo.Items.OfType<ComboBoxItem>()
                                  .FirstOrDefault(i => i.Tag is IntOption o && o.Value == selected)
                              ?? combo.Items[0];
    }

    private void LoadLlmBackends()
    {
        var wasRefreshing = _refreshingModules;
        _refreshingModules = true;

        try
        {
            LlmBackendCombo.Items.Clear();
            LlmBackendCombo.Items.Add(new ComboBoxItem
            {
                Content = $"跟随 config.yaml · {Path.GetFileName(_host.Config.Llm.ServerExecutablePath)}",
                Tag = null,
            });

            foreach (var module in _llmModules)
            {
                LlmBackendCombo.Items.Add(new ComboBoxItem
                {
                    Content = $"{module.DisplayName} · {module.RequiresText} · {StatusWithoutPrefix(module)}",
                    Tag = module,
                });
            }

            var effective = ModuleRegistry.EffectiveModule(ModuleKind.Llm, _llmModules, _enabledNames.Llm);
            var selected = effective is null
                ? null
                : LlmBackendCombo.Items.OfType<ComboBoxItem>()
                    .FirstOrDefault(i => i.Tag is ModuleInfo m && m.DirectoryName == effective.DirectoryName);

            LlmBackendCombo.SelectedItem = selected ?? LlmBackendCombo.Items[0];
        }
        finally
        {
            _refreshingModules = wasRefreshing;
        }
    }

    /// <summary>状态文案去掉「已识别：」前缀（下拉框里重复出现这个前缀很吵）。</summary>
    private static string StatusWithoutPrefix(ModuleInfo module)
        => module.StatusText.Replace("已识别：", string.Empty, StringComparison.Ordinal);

    private void LoadVoiceCombos()
    {
        AsrCaptureCombo.Items.Clear();
        AsrCaptureCombo.Items.Add(new ComboBoxItem { Content = "麦克风（microphone）", Tag = "microphone" });
        AsrCaptureCombo.Items.Add(new ComboBoxItem { Content = "系统音频回环（loopback）", Tag = "loopback" });

        var capture = AppearanceStore.LoadAsrCapture() ?? _host.Config.Asr.Capture;
        AsrCaptureCombo.SelectedItem = AsrCaptureCombo.Items.OfType<ComboBoxItem>()
                                            .FirstOrDefault(i => Equals(i.Tag, capture))
                                        ?? AsrCaptureCombo.Items[0];

        var voice = AppearanceStore.LoadTtsVoice() ?? _host.Config.Tts.DefaultVoice;
        TtsVoiceBox.Text = voice;

        var speed = AppearanceStore.LoadTtsSpeed() ?? _host.Config.Tts.GptSoVits.SpeedFactor;
        TtsSpeedSlider.Value = Math.Clamp(speed, TtsSpeedSlider.Minimum, TtsSpeedSlider.Maximum);
        TtsSpeedText.Text = $"{TtsSpeedSlider.Value:F2}×";
    }

    private void LoadInteractionControls()
    {
        HoverSwitchCheck.IsChecked = AppearanceStore.LoadHoverSwitch() ?? _host.Config.Ui.TransparentHoverSwitch;
        ClickThroughCheck.IsChecked = AppearanceStore.LoadClickThrough() ?? _host.Config.Ui.ClickThrough;
        PushToTalkCheck.IsChecked = AppearanceStore.LoadPushToTalk() ?? _host.Config.Asr.PushToTalk;
    }

    private void LoadBehaviorControls()
    {
        var idle = AppearanceStore.LoadIdleMinutes() ?? _host.Config.Idle.IdleMinutes;
        var cooldown = AppearanceStore.LoadMaintenanceCooldownMinutes() ?? _host.Config.Idle.MaintenanceCooldownMinutes;
        var position = AppearanceStore.LoadStartPosition();

        IdleMinutesSlider.Value = Math.Clamp(idle, IdleMinutesSlider.Minimum, IdleMinutesSlider.Maximum);
        IdleCooldownSlider.Value = Math.Clamp(cooldown, IdleCooldownSlider.Minimum, IdleCooldownSlider.Maximum);

        IdleMinutesText.Text = $"{IdleMinutesSlider.Value:F0} 分钟";
        IdleCooldownText.Text = $"{IdleCooldownSlider.Value:F0} 分钟";

        if (position is { } p)
        {
            StartXBox.Text = p.X.ToString("F0");
            StartYBox.Text = p.Y.ToString("F0");
        }
        else if (PetWindow is { } pet)
        {
            // 没设过启动位置时，把"当前位置"填进输入框（只是显示，不会落盘）。
            StartXBox.Text = pet.Left.ToString("F0");
            StartYBox.Text = pet.Top.ToString("F0");
        }

        RefreshPositionAndRenderInfo();
    }

    private void RefreshPositionAndRenderInfo()
    {
        var pet = PetWindow;

        if (pet is null)
        {
            CurrentPositionText.Text = "未找到桌宠窗口";
            RenderResolutionText.Text = "—";
            return;
        }

        CurrentPositionText.Text = $"{pet.Left:F0}, {pet.Top:F0}";

        try
        {
            var dpi = VisualTreeHelper.GetDpi(pet);
            var width = Math.Round(pet.ActualWidth * dpi.DpiScaleX);
            var height = Math.Round(pet.ActualHeight * dpi.DpiScaleY);
            RenderResolutionText.Text = $"约 {width:F0} × {height:F0} px";
        }
        catch
        {
            RenderResolutionText.Text = "—";
        }
    }

    private MainWindow? PetWindow =>
        Application.Current?.Windows.OfType<MainWindow>().FirstOrDefault();

    // ================================================================== 语音引擎选择

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

        /*
         * 当前引擎不在可选项里，有两种情况，都不能假装"就是 Piper"：
         *   - 「拓展」页启用了 TTS 模块 → AppHost 把 tts.engine 改成了 module:<目录名>；
         *   - config.yaml 里写了别的值。
         * 前一种以前会在下拉框里显示 Piper（因为回退到第 0 项），而状态行写着 module:xxx，
         * 两处口径相反。这里把真实值作为一条不可选中的项显示出来。
         */
        if (TtsEngineCombo.SelectedItem is null)
        {
            if (Core.Tts.TTSEngineFactory.IsModuleEngine(current))
            {
                var moduleItem = new ComboBoxItem
                {
                    Content = $"模块：{current}（由「拓展」页决定，不在这里切换）",
                    Tag = current,
                    IsEnabled = false,
                };

                TtsEngineCombo.Items.Add(moduleItem);
                TtsEngineCombo.SelectedItem = moduleItem;
            }
            else if (TtsEngineCombo.Items.Count > 0)
            {
                TtsEngineCombo.SelectedIndex = 0;
            }
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
            Core.Tts.TTSEngineFactory.Piper => "本地 CPU",
            Core.Tts.TTSEngineFactory.GptSoVits => "需 api_v2.py:9880",
            Core.Tts.TTSEngineFactory.CosyVoice => "需 server.py:50000",
            Core.Tts.TTSEngineFactory.Chatterbox => "需 HTTP 服务",
            _ => string.Empty,
        };
    }

    private async void OnTtsEngineChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || !_ttsEngineInitialized)
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

    // ================================================================== 外观页

    private void OnAppearanceAssetChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || !_appearanceInitialized || AppearanceAssetCombo.SelectedItem is not ComboBoxItem item)
        {
            return;
        }

        if (item.Tag is not AppearanceAssetOption option)
        {
            return;
        }

        // 立绘 / Live2D 模型由主窗口重建形象（LoadAvatar 的三级兜底链）；这里只落盘并请求立即应用。
        AppearanceStore.SaveLive2dModelPath(option.Live2dPath);
        AppearanceStore.SaveImagePath(option.ImagePath);

        AppearanceHintText.Text = option.Live2dPath is not null
            ? "已选 Live2D · 换模型需重启"
            : option.ImagePath is not null
                ? "已选立绘 · 关掉 Live2D 后生效"
                : "已跟随 config.yaml · 正在应用";

        _host.Logger.Info($"设置面板选择外观资源：{option.Live2dPath ?? option.ImagePath ?? "跟随 config.yaml"}。");

        // 立绘与开关即时生效；换 Live2D **模型**仍需重启（渲染器不支持热重建，见 Live2DRenderer 的说明）。
        ScheduleApply(AppearanceChange.Avatar);
    }

    private void OnLive2dChanged(object sender, RoutedEventArgs e)
    {
        if (_loading || !_appearanceInitialized)
        {
            return;
        }

        var enabled = Live2dCheck.IsChecked == true;
        AppearanceStore.SaveLive2dEnabled(enabled);

        AppearanceHintText.Text = enabled
            ? "Live2D 已启用"
            : "已切回静态立绘";

        _host.Logger.Info($"设置面板{(enabled ? "启用" : "关闭")} Live2D。");

        ScheduleApply(AppearanceChange.Avatar);
    }

    private void OnScaleChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_appearanceInitialized)
        {
            return;
        }

        UpdateAppearanceLabels();
        AppearanceHintText.Text = $"缩放 {ScaleSlider.Value:P0} 已应用";
        ScheduleSave("scale", () => AppearanceStore.SaveScale(Math.Round(ScaleSlider.Value, 2)));
        ScheduleApply(AppearanceChange.Scale);
    }

    private void OnBubbleSecondsChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_appearanceInitialized)
        {
            UpdateAppearanceLabels();
            _bubbles.DefaultDuration = TimeSpan.FromSeconds(Math.Max(1, BubbleSecondsSlider.Value));
            AppearanceHintText.Text = "气泡时长已应用";
            ScheduleSave("bubbleSeconds", () => AppearanceStore.SaveBubbleSeconds(Math.Round(BubbleSecondsSlider.Value)));
            ScheduleApply(AppearanceChange.Preferences);
        }
    }

    private void OnTransparentOpacityChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_appearanceInitialized)
        {
            UpdateAppearanceLabels();
            AppearanceHintText.Text = "不透明度已应用";
            ScheduleSave("transparentOpacity", () => AppearanceStore.SaveTransparentOpacity(Math.Round(OpacitySlider.Value, 2)));
            ScheduleApply(AppearanceChange.Preferences);
        }
    }

    private void OnHorizontalOffsetChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_appearanceInitialized)
        {
            UpdateAppearanceLabels();
            AppearanceHintText.Text = "水平微调已应用";
            ScheduleSave("horizontalOffset", () => AppearanceStore.SaveHorizontalOffset(Math.Round(HorizontalOffsetSlider.Value, 3)));
            ScheduleApply(AppearanceChange.Preferences);
        }
    }

    private void OnResetHorizontalOffset(object sender, RoutedEventArgs e)
    {
        // 归零即"回到自动居中"
        HorizontalOffsetSlider.Value = 0;
        UpdateAppearanceLabels();

        // 值本来就是 0 时 ValueChanged 不会触发，这里补一次，保证"点归零就回正"。
        ScheduleSave("horizontalOffset", () => AppearanceStore.SaveHorizontalOffset(0));
        ScheduleApply(AppearanceChange.Preferences);
    }

    private void OnHidePet(object sender, RoutedEventArgs e)
    {
        try
        {
            PetWindow?.ToggleVisibility();
        }
        catch (Exception ex)
        {
            _host.Logger.Warn($"隐藏桌宠窗口失败：{ex.Message}");
        }
    }

    private void OnShowPet(object sender, RoutedEventArgs e)
    {
        try
        {
            var pet = PetWindow;
            if (pet is null)
            {
                return;
            }

            pet.ShowPet();
            pet.Activate();
        }
        catch (Exception ex)
        {
            _host.Logger.Warn($"显示桌宠窗口失败：{ex.Message}");
        }
    }

    // ================================================================== 对话页

    private void OnLlmEnabledChanged(object sender, RoutedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        var llm = LlmEnabledCheck.IsChecked == true;
        var saved = AppearanceStore.SaveAiToggles(llm, TtsEnabledCheck.IsChecked, AsrEnabledCheck.IsChecked);

        AiToggleHintText.Text = saved
            ? $"对话已{(llm ? "开启" : "关闭")} · 重启后生效"
            : "保存失败，详见日志";
    }

    private void OnTtsEnabledChanged(object sender, RoutedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        var tts = TtsEnabledCheck.IsChecked == true;
        var saved = AppearanceStore.SaveAiToggles(LlmEnabledCheck.IsChecked, tts, AsrEnabledCheck.IsChecked);

        AiToggleHintText.Text = saved
            ? $"语音输出已{(tts ? "开启" : "关闭")} · 重启后生效"
            : "保存失败，详见日志";
    }

    private void OnAsrEnabledChanged(object sender, RoutedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        var asr = AsrEnabledCheck.IsChecked == true;
        var saved = AppearanceStore.SaveAiToggles(LlmEnabledCheck.IsChecked, TtsEnabledCheck.IsChecked, asr);

        AiToggleHintText.Text = saved
            ? $"语音输入已{(asr ? "开启" : "关闭")} · 重启后生效"
            : "保存失败，详见日志";
    }

    private void OnLlmModelChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        if (LlmModelCombo.SelectedItem is not ComboBoxItem item || item.Tag is not ModelOption option)
        {
            return;
        }

        AppearanceStore.SaveLlmModelPath(option.RelativePath);
        DialogHintText.Text = option.RelativePath is null
            ? "已跟随 config.yaml · 下次启动生效"
            : "已保存 · 下次启动生效";
    }

    private void OnLlmContextChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        if (LlmContextCombo.SelectedItem is not ComboBoxItem item || item.Tag is not IntOption option)
        {
            return;
        }

        AppearanceStore.SaveLlmContextLength(option.Value);
        DialogHintText.Text = option.Value == 0
            ? "自动 · 下次启动生效"
            : $"{option.Value} · 下次启动生效";
    }

    private void OnGpuStrategyChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        if (GpuStrategyCombo.SelectedItem is not ComboBoxItem item || item.Tag is not IntOption option)
        {
            return;
        }

        AppearanceStore.SaveGpuReservedMb(option.Value);
        DialogHintText.Text = option.Value == 0
            ? "自动 · 下次启动生效"
            : $"预留 {option.Value} MB · 下次启动生效";
    }

    private void OnLlmBackendChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || _refreshingModules)
        {
            return;
        }

        if (LlmBackendCombo.SelectedItem is not ComboBoxItem item)
        {
            return;
        }

        if (item.Tag is ModuleInfo module)
        {
            var saved = AppearanceStore.SaveEnabledModule(ModuleRegistry.KindKey(ModuleKind.Llm), module.DirectoryName);
            _enabledNames.Set(ModuleKind.Llm, saved ? module.DirectoryName : _enabledNames.Llm);
            DialogHintText.Text = saved
                ? $"后端：{module.DisplayName} · 下次启动生效"
                : "保存失败，详见日志";

            RenderModules();
            ApplyGates();
            return;
        }

        var cleared = AppearanceStore.SaveEnabledModule(ModuleRegistry.KindKey(ModuleKind.Llm), null);
        _enabledNames.Set(ModuleKind.Llm, cleared ? null : _enabledNames.Llm);
        DialogHintText.Text = cleared
            ? "后端跟随 config.yaml · 下次启动生效"
            : "保存失败，详见日志";

        RenderModules();
        ApplyGates();
    }

    // ================================================================== 语音页

    private void OnTtsVoiceChanged(object sender, TextChangedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        var voice = TtsVoiceBox.Text.Trim();

        if (!string.IsNullOrEmpty(voice))
        {
            _host.Config.Tts.DefaultVoice = voice;
        }

        TtsVoiceText.Text = _host.Config.Tts.DefaultVoice;
        TtsVoiceText.ToolTip = "界面覆盖 · config.yaml 未改动";
        ScheduleSave("ttsVoice", () => AppearanceStore.SaveTtsVoice(TtsVoiceBox.Text.Trim()));
    }

    private void OnTtsSpeedChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_loading)
        {
            return;
        }

        TtsSpeedText.Text = $"{TtsSpeedSlider.Value:F2}×";
        var speed = Math.Round(TtsSpeedSlider.Value, 2);

        _host.Config.Tts.GptSoVits.SpeedFactor = speed;
        ScheduleSave("ttsSpeed", () => AppearanceStore.SaveTtsSpeed(Math.Round(TtsSpeedSlider.Value, 2)));
    }

    private void OnAsrCaptureChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        if (AsrCaptureCombo.SelectedItem is not ComboBoxItem item || item.Tag is not string capture)
        {
            return;
        }

        AppearanceStore.SaveAsrCapture(capture);
        _host.Logger.Info($"设置面板选择音频采集源：{capture}（下次启动生效，config.yaml 未改动）。");
    }

    // ================================================================== 互动页

    private void OnHoverSwitchChanged(object sender, RoutedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        var enabled = HoverSwitchCheck.IsChecked == true;
        _host.Config.Ui.TransparentHoverSwitch = enabled;
        AppearanceStore.SaveHoverSwitch(enabled);
        _host.Logger.Info($"设置面板{(enabled ? "启用" : "停用")}透明态悬停自动切换（即时生效）。");
    }

    private void OnClickThroughChanged(object sender, RoutedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        var clickThrough = ClickThroughCheck.IsChecked == true;
        AppearanceStore.SaveClickThrough(clickThrough);

        // 主窗口的 SetInteractive 是既有入口：不重构业务逻辑，只调用它。
        PetWindow?.SetInteractive(!clickThrough);

        _host.Logger.Info($"设置面板切换交互模式：{(clickThrough ? "透明（鼠标穿透）" : "覆盖（可交互）")}（即时生效）。");
    }

    private void OnPushToTalkChanged(object sender, RoutedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        var enabled = PushToTalkCheck.IsChecked == true;
        AppearanceStore.SavePushToTalk(enabled);
        _host.Logger.Info($"设置面板{(enabled ? "启用" : "关闭")}按住说话（下次启动生效）。");
    }

    // ================================================================== 行为页

    private void OnIdleMinutesChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_loading)
        {
            return;
        }

        IdleMinutesText.Text = $"{IdleMinutesSlider.Value:F0} 分钟";
        var minutes = Math.Round(IdleMinutesSlider.Value);

        _host.Config.Idle.IdleMinutes = minutes;
        ScheduleSave("idleMinutes", () => AppearanceStore.SaveIdleMinutes(Math.Round(IdleMinutesSlider.Value)));
    }

    private void OnIdleCooldownChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_loading)
        {
            return;
        }

        IdleCooldownText.Text = $"{IdleCooldownSlider.Value:F0} 分钟";
        var minutes = Math.Round(IdleCooldownSlider.Value);

        _host.Config.Idle.MaintenanceCooldownMinutes = minutes;
        ScheduleSave("idleCooldown", () => AppearanceStore.SaveMaintenanceCooldownMinutes(Math.Round(IdleCooldownSlider.Value)));
    }

    private void OnStartPositionChanged(object sender, TextChangedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        var (x, y) = ParseStartPosition();
        if (x is null || y is null)
        {
            return;
        }

        ScheduleSave("startPosition", () =>
        {
            var (currentX, currentY) = ParseStartPosition();
            if (currentX is not null && currentY is not null)
            {
                AppearanceStore.SaveStartPosition(currentX.Value, currentY.Value);
            }
        });
    }

    private (double? X, double? Y) ParseStartPosition()
    {
        var okX = double.TryParse(StartXBox.Text.Trim(), out var x);
        var okY = double.TryParse(StartYBox.Text.Trim(), out var y);

        return (okX ? x : null, okY ? y : null);
    }

    private void OnReadCurrentPosition(object sender, RoutedEventArgs e)
    {
        var pet = PetWindow;
        if (pet is null)
        {
            return;
        }

        _loading = true;
        try
        {
            StartXBox.Text = pet.Left.ToString("F0");
            StartYBox.Text = pet.Top.ToString("F0");
        }
        finally
        {
            _loading = false;
        }

        AppearanceStore.SaveStartPosition(pet.Left, pet.Top);
        RefreshPositionAndRenderInfo();
    }

    private void OnMovePetNow(object sender, RoutedEventArgs e)
    {
        var pet = PetWindow;
        var (x, y) = ParseStartPosition();

        if (pet is null || x is null || y is null)
        {
            return;
        }

        pet.Left = x.Value;
        pet.Top = y.Value;
        RefreshPositionAndRenderInfo();

        _host.Logger.Info($"设置面板把桌宠窗口移动到 {x.Value:F0}, {y.Value:F0}（DIP）。");
    }

    // ================================================================== 诊断页

    private async void OnRedetectAll(object sender, RoutedEventArgs e)
    {
        try
        {
            RefreshStatus();
            RefreshVoiceTab();
            RefreshMonitorTab();
            OnRefreshLogs(this, new RoutedEventArgs());
            await RefreshEnvironmentAndModulesAsync();
            _host.Logger.Info("设置面板触发重新检测（运行状态 / 语音 / 游戏检测 / 日志 / 模块）。");
        }
        catch (Exception ex)
        {
            _host.Logger.Warn($"设置面板重新检测失败：{ex.Message}");
        }
    }

    private void OnRefreshCandidates(object sender, RoutedEventArgs e) => _ = RefreshCandidatesAsync();

    private async Task RefreshCandidatesAsync()
    {
        // 启动没完成时 Knowledge 还没构造出来：以前这里直接 NRE，
        // 界面上显示"读取失败：Object reference not set…"，用户完全看不懂。
        if (!_host.IsStarted)
        {
            CandidateStatusText.Text = "启动中，稍候…";
            return;
        }

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
            CandidateStatusText.Text = "未选中候选";
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
            CandidateStatusText.Text = "未选中候选";
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
            CandidateStatusText.Text = "启动中，稍候…";
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

        if (string.IsNullOrWhiteSpace(packDirectory) || !Directory.Exists(packDirectory))
        {
            // 选完目录后立刻被删 / 移动（或权限不足看不到）也要给一句人话，别让它走到导入器里去炸。
            CandidateStatusText.Text = "目录不存在或无权限";

            _host.Logger.Warn($"角色包导入被取消：选中的目录不可用（{packDirectory}）。");
            return;
        }

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

                CandidateStatusText.Text = $"新增 {result.Added} · 跳过 {result.Skipped}";

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

    // ================================================================== 状态 / 语音 / 游戏检测刷新

    private void RefreshStatus()
    {
        // 启动尚未完成时，核心组件还没构造出来，此时访问会 NRE。
        // 用户完全可能在启动过程中就打开设置面板，因此必须挡在这里。
        if (!_host.IsStarted)
        {
            ModeText.Text = "启动中…";
            return;
        }

        try
        {
            ModeText.Text = _host.RuntimeMode.Describe();

            LlmText.Text = _host.LlmReady
                ? $"已就绪 · ngl {_host.Llama.CurrentNgl}"
                : "未就绪";

            TtsText.Text = $"{_host.Tts.ConfiguredEngine} → {_host.Tts.CurrentEngineName}" +
                           (_host.Tts.IsDegraded ? "（降级）" : string.Empty);

            // PTT 模式下空闲态就是 Stopped，直接显示状态会让人以为语音坏了。
            AsrText.Text = _host.Config.Asr.PushToTalk
                ? $"按住 {_host.Config.Asr.PushToTalkKey}" +
                  (_host.Asr.State == AsrState.Running ? " · 录音中" : string.Empty)
                : $"{_host.Asr.State}" +
                  (_host.Asr.State == AsrState.Faulted ? $"（{_host.Asr.FaultReason}）" : string.Empty);

            KnowledgeText.Text = $"{_host.Knowledge.Count} 条";
            KnowledgeText.ToolTip = _host.Knowledge.DirectoryPath;
            PendingText.Text = $"{_host.Knowledge.Pending.CountCandidates()} 条";

            MonitorText.Text = (_host.Monitor.IsClientConnected ? "已连接" : "未连接") +
                (_host.GameMode.IsYielded ? " · 已让出显存" : string.Empty);

            ProblemList.ItemsSource = _host.StartupProblems.Count > 0
                ? _host.StartupProblems
                : new[] { "无" };

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

            // 显存查询是异步 + 节流的，这里只发起，不阻塞 UI 线程（准则 1）。
            _ = UpdateGpuTextAsync();

            /*
             * 混合显卡一致性（只读）：GL 落在哪块显卡 / 主显示器在哪块 / 是否已自动修复。
             * 判定本身在启动期完成（Live2D 第一帧回填 GL 信息），这里只是把结论显示出来，
             * 不做任何写入 —— 文案由 HybridGpuAdvisor 生成，界面不维护第二套说法。
             */
            GpuAdapterText.Text = _host.GpuAdvisor.StatusLine;
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

        // 节流：开着设置面板时每 2 秒起两个 nvidia-smi 没有意义（显存数字不会那么快变），
        // 反而让 GPU 驱动多出一次设备枚举。每 10 秒最多查一次。
        if (!IsGpuQueryDue(_lastGpuQueryUtc, DateTime.UtcNow, GpuQueryMinInterval))
        {
            return;
        }

        _lastGpuQueryUtc = DateTime.UtcNow;
        _gpuQuerying = true;

        try
        {
            var text = await _host.Gpu.DescribeCurrentMemoryAsync().ConfigureAwait(true);
            if (!_closed)
            {
                GpuText.Text = text;
            }
        }
        catch
        {
            if (!_closed)
            {
                GpuText.Text = "显存：不可用";
            }
        }
        finally
        {
            _gpuQuerying = false;
        }
    }

    /// <summary>显存查询的最小间隔（2 秒定时器 → 实际每 10 秒查一次）。</summary>
    private static readonly TimeSpan GpuQueryMinInterval = TimeSpan.FromSeconds(10);

    /// <summary>
    /// 到点该不该查显存。抽成纯函数是为了能单独验证节流逻辑（见审查脚本），
    /// 否则要起真实 GPU 才能看出来。
    /// </summary>
    private static bool IsGpuQueryDue(DateTime lastQueryUtc, DateTime nowUtc, TimeSpan minInterval)
        => lastQueryUtc == DateTime.MinValue || nowUtc - lastQueryUtc >= minInterval;

    private void RefreshVoiceTab()
    {
        if (!_host.IsStarted)
        {
            return;
        }

        TtsEngineText.Text = AppearanceStore.LoadTtsEngine() is null
            ? $"{_host.Tts.ConfiguredEngine} · 出厂默认"
            : $"{_host.Tts.ConfiguredEngine} · 界面覆盖";
        TtsVoiceText.Text = _host.Config.Tts.DefaultVoice;
        TtsVoiceText.ToolTip = string.IsNullOrWhiteSpace(_host.Config.Tts.DefaultVoice)
            ? $"为空时回退硬编码音色 {Core.Tts.TTSManager.HardcodedFallbackVoice}"
            : null;
        TtsPiperText.Text = _host.Tts.IsDegraded
            ? $"{_host.Tts.CurrentEngineName}（降级）"
            : _host.Tts.CurrentEngineName;

        AsrStateText.Text = _host.Config.Asr.PushToTalk
            ? $"按住 {_host.Config.Asr.PushToTalkKey}" +
              (_host.Asr.State == AsrState.Running ? " · 录音中" : string.Empty)
            : $"{_host.Asr.State}";
        AsrModelText.Text = Core.Configuration.AppPaths.Resolve(_host.Config.Asr.ModelPath);
        AsrVadText.Text = $"{_host.Asr.VadName} · 要求 {_host.Config.Asr.VadMode}";

        AsrLastText.Text = _host.Asr.LastRecognition is { } last
            ? $"\"{last.Text}\" · {last.Elapsed.TotalMilliseconds:N0} ms" +
              $"（{last.AudioDuration.TotalSeconds:N1}s · RTF {last.RealTimeFactor:N2}）"
            : "暂无";

        // 文案精简：界面上只说结论（不可用 + 下一步），原因写日志。
        AsrHintText.Text = _host.Asr.State == AsrState.Faulted
            ? "语音输入不可用 · 改用文本输入"
            : string.Empty;

        if (_host.Asr.State == AsrState.Faulted)
        {
            _host.Logger.Debug(
                $"ASR 处于不可用状态（{_host.Asr.FaultReason}）：" +
                (_host.Asr.FaultReason == AsrFaultReason.ModelMissing
                    ? $"模型缺失，可在 config.yaml 的 asr.model_path 指定新路径后点「重置 ASR」（当前 {Core.Configuration.AppPaths.Resolve(_host.Config.Asr.ModelPath)}）。"
                    : "可点「重启语音服务」或「重置 ASR」重试。"));
        }
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

    private void RefreshMonitorTab()
    {
        if (!_host.IsStarted)
        {
            return;
        }

        var last = _host.Monitor.LastMessageUtc;
        MonitorStateText.Text = _host.Monitor.IsClientConnected
            ? $"已连接 · {(last == DateTime.MinValue ? "—" : last.ToLocalTime().ToString("HH:mm:ss"))}"
            : "未连接";

        var cooldowns = _host.Cooldown.Snapshot();
        MonitorCooldownText.Text = cooldowns.Count == 0
            ? "无"
            : string.Join("；", cooldowns.Select(kv => $"{kv.Key} 至 {kv.Value.ToLocalTime():HH:mm:ss}"));

        GameListText.Text = $"端口 {_host.Config.Monitor.Host}:{_host.Config.Monitor.Port} · 冷却 {_host.Config.Monitor.CooldownMinutes:N0} 分钟";
        GameListText.ToolTip = $"列表文件：{Core.Configuration.AppPaths.Resolve(_host.Config.Monitor.GameListFile)}";
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

    // ================================================================== 日志

    private void OnRefreshLogs(object sender, RoutedEventArgs e)
    {
        // 同上：_host.Context / LogSink 在启动阶段还不存在。
        if (!_host.IsStarted)
        {
            LogPathText.Text = _host.BootstrapSink.DirectoryPath;
            LogSizeText.Text = "启动中…";
            ChatLogSizeText.Text = "启动中…";
            LogList.ItemsSource = new List<string> { "启动中，稍候…" };
            return;
        }

        try
        {
            // LogSink 在配置加载后才有；启动早期回退到引导日志。
            var sink = _host.LogSink ?? _host.BootstrapSink;
            LogPathText.Text = sink?.DirectoryPath ?? "（未初始化）";
            LogSizeText.Text = sink is null
                ? "—"
                : $"{sink.GetDirectorySizeBytes() / 1024.0 / 1024.0:N2} MB · 保留 {_host.Config.Logging.MaxLogDays} 天";

            ChatLogSizeText.Text = $"{_host.Context.GetChatLogSizeBytes() / 1024.0 / 1024.0:N2} MB";
            ChatLogSizeText.ToolTip = $"归档：{Path.GetFileName(_host.Context.ArchivePath)}";

            LogList.ItemsSource = sink?.RecentSnapshot(LogTailLines) ?? new List<string> { "（日志未初始化）" };
            _startupListsLoaded = true;
        }
        catch (Exception ex)
        {
            LogList.ItemsSource = new List<string> { $"读取日志失败：{ex.Message}" };
        }
    }

    /// <summary>启动完成后补刷一次"启动阶段打不开"的清单（日志 / 待审核候选）。</summary>
    private void RefreshStartupListsOnce()
    {
        if (_startupListsLoaded || !_host.IsStarted)
        {
            return;
        }

        OnRefreshLogs(this, new RoutedEventArgs());
        _ = RefreshCandidatesAsync();
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
            OpenInExplorer(_host.LogSink?.DirectoryPath ?? _host.BootstrapSink.DirectoryPath);
        }
        catch (Exception ex)
        {
            _host.Logger.Warn($"打开日志目录失败：{ex.Message}");
        }
    }

    // ================================================================== AI 功能开关

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
            ? "界面偏好 · 重启后生效"
            : "当前跟随 config.yaml";
    }

    // ================================================================== 拓展页：模块管理器

    private ModuleEnvironment? _environment;
    private IReadOnlyList<ModuleInfo> _llmModules = Array.Empty<ModuleInfo>();
    private IReadOnlyList<ModuleInfo> _ttsModules = Array.Empty<ModuleInfo>();
    private IReadOnlyList<ModuleInfo> _asrModules = Array.Empty<ModuleInfo>();

    /// <summary>
    /// 三类"启用中的模块目录名"的本地快照。
    ///
    /// 为什么要缓存：<see cref="ModuleRegistry.EffectiveModule(ModuleKind, IReadOnlyList{ModuleInfo})"/>
    /// 每问一次都要把 appearance.json 读一遍并反序列化，而一次刷新要问 7 次
    /// （渲染 3 + 门禁 3 + 后端下拉 1）。这里一次读好、本窗口负责在写入后同步。
    /// </summary>
    private EnabledModuleNames _enabledNames = EnabledModuleNames.Load();

    private sealed class EnabledModuleNames
    {
        public string? Llm { get; set; }

        public string? Tts { get; set; }

        public string? Asr { get; set; }

        public static EnabledModuleNames Load() => new()
        {
            Llm = AppearanceStore.LoadEnabledModule(ModuleRegistry.KindKey(ModuleKind.Llm)),
            Tts = AppearanceStore.LoadEnabledModule(ModuleRegistry.KindKey(ModuleKind.Tts)),
            Asr = AppearanceStore.LoadEnabledModule(ModuleRegistry.KindKey(ModuleKind.Asr)),
        };

        public string? For(ModuleKind kind) => kind switch
        {
            ModuleKind.Llm => Llm,
            ModuleKind.Tts => Tts,
            _ => Asr,
        };

        public void Set(ModuleKind kind, string? directoryName)
        {
            switch (kind)
            {
                case ModuleKind.Llm:
                    Llm = directoryName;
                    break;
                case ModuleKind.Tts:
                    Tts = directoryName;
                    break;
                default:
                    Asr = directoryName;
                    break;
            }
        }
    }

    /// <summary>上一次真正重建模块卡片时的输入签名；一样就跳过重建（控件不重建就没有闪烁与布局开销）。</summary>
    private string? _moduleRenderSignature;

    /// <summary>正在刷新模块列表：此期间下拉框的 SelectionChanged 是程序代选，不算用户操作。</summary>
    private bool _refreshingModules;

    /// <summary>
    /// 环境识别是否在飞行中。
    ///
    /// 与 <see cref="_refreshingModules"/> 分开：后者是"下拉框正在被程序代填"的抑制标志，
    /// 而这里只用来防止两次识别叠加（各起 nvidia-smi / vulkaninfo / llama-server 子进程）。
    /// 之前两者共用一个 bool，导致识别期间点"重新识别"会静默什么都不做。
    /// </summary>
    private bool _envRefreshing;

    /// <summary>首次环境识别完成前不套门禁，避免打开窗口瞬间控件闪一下"置灰"。</summary>
    private bool _gatesReady;

    private async Task RefreshEnvironmentAndModulesAsync()
    {
        if (_envRefreshing)
        {
            ModuleHintText.Text = "识别中，稍候…";
            _host.Logger.Debug("设置面板重新识别请求被忽略：上一次环境识别还没结束。");
            return;
        }

        _envRefreshing = true;
        _refreshingModules = true;

        try
        {
            EnvHintText.Text = "识别中…";
            EnvGpuText.Text = "检测中…";
            EnvVulkanText.Text = "检测中…";
            EnvCpuText.Text = "检测中…";
            EnvMicText.Text = "检测中…";

            /*
             * 环境识别（nvidia-smi / vulkaninfo 子进程）与模块扫描（磁盘遍历 + 解析 module.json +
             * 统计每个模块的体积）全部放到线程池：以前这些都在 UI 线程上跑，
             * 模块目录大一点（llama.cpp 一套就是几百 MB、上万文件）面板会明显卡住。
             */
            var (environment, llmModules, ttsModules, asrModules, issues) = await Task.Run(async () =>
            {
                var env = await ModuleRegistry.ProbeAsync(_host.Logger).ConfigureAwait(false);
                var skipped = new List<string>();

                var llm = ModuleRegistry.Scan(ModuleKind.Llm, env, skipped);
                var tts = ModuleRegistry.Scan(ModuleKind.Tts, env, skipped);
                var asr = ModuleRegistry.Scan(ModuleKind.Asr, env, skipped);

                return (env, llm, tts, asr, skipped);
            }).ConfigureAwait(true);

            if (_closed)
            {
                return;
            }

            _environment = environment;
            _llmModules = llmModules;
            _ttsModules = ttsModules;
            _asrModules = asrModules;

            // 扫描完再读一次启用状态：用户可能刚在别的窗口改过 appearance.json。
            _enabledNames = EnabledModuleNames.Load();
            _moduleRenderSignature = null;

            EnvGpuText.Text = environment.GpuText;
            EnvVulkanText.Text = environment.VulkanText;
            EnvCpuText.Text = environment.CpuText;
            EnvMicText.Text = environment.MicrophoneText;

            UpdateMicDeviceText();

            EnvHintText.Text =
                $"模块 对话 {llmModules.Count} · 语音 {ttsModules.Count} · 识别 {asrModules.Count}";

            ReportSkippedModules(issues);

            LoadLlmBackends();
            RenderModules();

            _gatesReady = true;
            ApplyGates();
        }
        catch (Exception ex)
        {
            if (!_closed)
            {
                EnvHintText.Text = $"识别失败：{ex.Message}";
            }

            _host.Logger.Warn($"设置面板环境识别失败：{ex.Message}");
        }
        finally
        {
            _refreshingModules = false;
            _envRefreshing = false;
        }
    }

    /// <summary>
    /// 把被跳过的 modules 子目录原因逐条写日志；界面上只留"跳过 N 个"，详情进 ToolTip。
    ///
    /// 以前这些原因是静默丢弃的：用户把 module.json 写坏了，界面只显示"未发现模块"，
    /// 既不知道是哪个目录，也不知道错在哪。
    ///
    /// 去重是必要的：三类扫描各跑一遍，同一个坏目录会被三份扫描各报一次。
    /// </summary>
    private void ReportSkippedModules(List<string> issues)
    {
        if (issues.Count == 0)
        {
            return;
        }

        var unique = issues.Distinct(StringComparer.Ordinal).ToList();

        foreach (var issue in unique)
        {
            _host.Logger.Warn($"modules 跳过：{issue}");
        }

        ModuleHintText.Text = $"跳过 {unique.Count} 个 · 详见日志";
        ModuleHintText.ToolTip = string.Join(Environment.NewLine, unique.Take(10));
    }

    private void UpdateMicDeviceText()
    {
        MicDeviceText.Text = _environment is null
            ? "识别中…"
            : $"已识别：{_environment.MicrophoneText}";
    }

    /// <summary>
    /// 重建三类模块卡片。
    ///
    /// 输入没变就直接返回：以前每次调用都 Children.Clear() 再逐个 new Border/Grid/TextBlock/Button，
    /// 连点「重新识别」或切换后端下拉会把整页控件重建一遍（含一次完整布局），白白闪烁。
    /// </summary>
    private void RenderModules()
    {
        var signature = BuildModuleRenderSignature();
        if (string.Equals(signature, _moduleRenderSignature, StringComparison.Ordinal))
        {
            return;
        }

        _moduleRenderSignature = signature;

        RenderModuleList(LlmModuleList, ModuleKind.Llm, _llmModules);
        RenderModuleList(TtsModuleList, ModuleKind.Tts, _ttsModules);
        RenderModuleList(AsrModuleList, ModuleKind.Asr, _asrModules);
    }

    private string BuildModuleRenderSignature()
    {
        var sb = new StringBuilder();

        foreach (var (kind, modules) in new[]
                 {
                     (ModuleKind.Llm, _llmModules),
                     (ModuleKind.Tts, _ttsModules),
                     (ModuleKind.Asr, _asrModules),
                 })
        {
            sb.Append(ModuleRegistry.KindKey(kind)).Append(':')
              .Append(_enabledNames.For(kind) ?? "-").Append(':');

            foreach (var module in modules)
            {
                sb.Append(module.DirectoryName).Append('/')
                  .Append(module.Availability).Append('/')
                  .Append(module.SizeBytes).Append('/')
                  .Append(module.DisplayName).Append(';');
            }

            sb.Append('|');
        }

        return sb.ToString();
    }

    private void RenderModuleList(StackPanel container, ModuleKind kind, IReadOnlyList<ModuleInfo> modules)
    {
        container.Children.Clear();

        if (modules.Count == 0)
        {
            container.Children.Add(new TextBlock
            {
                Text = $"未发现{ModuleRegistry.KindLabel(kind)}模块 · kind={ModuleRegistry.KindKey(kind)}",
                Style = (Style)FindResource("RowHint"),
                Margin = new Thickness(0, 2, 0, 6),
            });

            return;
        }

        var enabled = ModuleRegistry.EffectiveModule(kind, modules, _enabledNames.For(kind));

        foreach (var module in modules)
        {
            var isEnabled = enabled is not null &&
                            string.Equals(module.DirectoryName, enabled.DirectoryName, StringComparison.OrdinalIgnoreCase);

            container.Children.Add(BuildModuleCard(module, isEnabled));
        }
    }

    private Border BuildModuleCard(ModuleInfo module, bool enabled)
    {
        var accent = (Brush)FindResource("AccentBrush");
        var surface = (Brush)FindResource("SurfaceBrush");
        var borderBrush = (Brush)FindResource("BorderBrush");
        var secondary = (Brush)FindResource("SecondaryTextBrush");
        var primary = (Brush)FindResource("PrimaryTextBrush");

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var dot = new System.Windows.Shapes.Ellipse
        {
            Width = 10,
            Height = 10,
            StrokeThickness = 1.5,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(2, 6, 9, 0),
            Fill = enabled ? accent : surface,
            Stroke = enabled ? accent : borderBrush,
        };
        Grid.SetColumn(dot, 0);

        var texts = new StackPanel();

        texts.Children.Add(new TextBlock
        {
            Text = module.DisplayName,
            FontSize = 12.5,
            FontWeight = FontWeights.SemiBold,
            Foreground = primary,
            TextWrapping = TextWrapping.Wrap,
        });

        texts.Children.Add(new TextBlock
        {
            Text = module.Summary,
            FontSize = 11,
            Foreground = secondary,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 2, 0, 0),
        });

        texts.Children.Add(new TextBlock
        {
            Text = module.StatusText,
            FontSize = 11.5,
            Foreground = module.IsUsable ? accent : secondary,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 2, 0, 0),
        });

        Grid.SetColumn(texts, 1);

        // 文案精简：卡片上只留「名称 / 目录+体积 / 已识别结论」三行，
        // 为什么不可用（缺哪个文件、entry 指向目录…）放进 ToolTip。
        var explanation = module.EntryNote ?? module.DirectoryPath;

        var button = new Button
        {
            Content = enabled ? "已启用" : "启用",
            Style = (Style)FindResource("GhostButton"),
            MinWidth = 70,
            Margin = new Thickness(10, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Tag = module,
            IsEnabled = module.IsUsable && !enabled,
            ToolTip = module.IsUsable ? explanation : $"{module.StatusText} · {explanation}",
        };
        button.Click += OnEnableModule;
        Grid.SetColumn(button, 2);

        grid.Children.Add(dot);
        grid.Children.Add(texts);
        grid.Children.Add(button);

        return new Border
        {
            Style = (Style)FindResource("ModuleCard"),

            // 标签用 "card" 而不是自定义的 "module"：
            // ApplySearchFilter 只认 "row" / "card" 两种标签，写成 "module" 等于把这张卡片
            // 排除在搜索之外 —— 搜 "cuda" 时别的卡片都藏起来了，模块卡片却还挂在页面上。
            Tag = "card",
            ToolTip = explanation,
            Child = grid,
        };
    }

    /// <summary>「启用」：同类只能启用一个，存进 appearance.json 的覆盖层。</summary>
    private void OnEnableModule(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: ModuleInfo module })
        {
            return;
        }

        var saved = AppearanceStore.SaveEnabledModule(ModuleRegistry.KindKey(module.Kind), module.DirectoryName);
        _enabledNames.Set(module.Kind, saved ? module.DirectoryName : _enabledNames.For(module.Kind));

        ModuleHintText.Text = saved
            ? $"已启用「{module.DisplayName}」· {module.StatusText}"
            : "保存失败，详见日志";

        _host.Logger.Info(
            $"设置面板启用{ModuleRegistry.KindLabel(module.Kind)}模块：{module.DirectoryName}（{module.StatusText}）。");

        RenderModules();
        LoadLlmBackends();
        ApplyGates();
    }

    /// <summary>
    /// 门禁：只有某一类**既没有可用模块、也没有内置实现**时，才把该区域置灰；
    /// 模块缺失本身不算缺陷，只给一条中性提示。
    ///
    /// 本次修掉的缺陷：默认分发包不带 tts / asr 模块，但内置实现（TTS = Windows SAPI，
    /// ASR = ai_asr.dll 桥接）本来就能用。旧实现只看模块，于是把整个「语音」页的
    /// TTS/ASR 开关、引擎下拉、重置 ASR 全部灰掉 —— 开箱就无法配置语音，与事实矛盾。
    ///
    /// 现在的规则：
    ///   1. 内置实现也算一条可用路径。TTS 恒有 SAPI；ASR 只要 ai_asr.dll 存在即可用；
    ///   2. 语音页的通用配置项按"模块可用 || 内置可用"决定是否禁用，正常情况下恒可用；
    ///   3. 模块缺失只用 <see cref="VoiceBuiltinHint"/> 做中性提示（可点进「拓展」，不拦操作）；
    ///   4. 「拓展」页的模块卡片按钮/状态仍按模块自身的 <see cref="ModuleInfo.IsUsable"/>
    ///      单独门禁 —— 那才是真正的"模块专属"控件。
    ///
    /// 幂等：只做赋值，重复调用结果完全相同（含 Content —— 不可见时不再改写它，
    /// 否则会留下"不可用：已识别：可用"这种自相矛盾的隐藏文案）。
    /// </summary>
    private void ApplyGates()
    {
        if (!_gatesReady || _closed)
        {
            return;
        }

        // 对话没有内置实现：模块是唯一路径，保持原来的整块门禁。
        var llm = ModuleRegistry.EffectiveModule(ModuleKind.Llm, _llmModules, _enabledNames.Llm);
        var llmUsable = llm is { IsUsable: true };
        DialogueGateArea.IsEnabled = llmUsable;
        DialogueGateBanner.Visibility = llmUsable ? Visibility.Collapsed : Visibility.Visible;

        if (!llmUsable)
        {
            DialogueGateBanner.Content = llm is null
                ? "尚未启用对话模块 · 去「拓展」添加"
                : $"「{llm.DisplayName}」{llm.StatusText} · 去「拓展」查看";
        }

        // 语音合成：内置 SAPI 永远是一条可用路径，所以默认包（无模块）也应可配置。
        var tts = ModuleRegistry.EffectiveModule(ModuleKind.Tts, _ttsModules, _enabledNames.Tts);
        var ttsUsable = tts is { IsUsable: true };
        var ttsBuiltin = HasBuiltinTts();
        var ttsAvailable = ttsUsable || ttsBuiltin;
        VoiceTtsArea.IsEnabled = ttsAvailable;
        TtsGateBanner.Visibility = ttsAvailable ? Visibility.Collapsed : Visibility.Visible;

        if (!ttsAvailable)
        {
            TtsGateBanner.Content = tts is null
                ? "语音合成不可用 · 去「拓展」添加模块"
                : $"「{tts.DisplayName}」{tts.StatusText} · 去「拓展」查看";
        }

        // 语音识别：内置实现是 ai_asr.dll 桥接，文件存在即算可用。
        var asr = ModuleRegistry.EffectiveModule(ModuleKind.Asr, _asrModules, _enabledNames.Asr);
        var asrUsable = asr is { IsUsable: true };
        var asrBuiltin = HasBuiltinAsr();
        var asrAvailable = asrUsable || asrBuiltin;
        VoiceAsrArea.IsEnabled = asrAvailable;
        AsrGateBanner.Visibility = asrAvailable ? Visibility.Collapsed : Visibility.Visible;

        if (!asrAvailable)
        {
            AsrGateBanner.Content = asr is null
                ? "语音识别不可用 · 去「拓展」添加模块"
                : $"「{asr.DisplayName}」{asr.StatusText} · 去「拓展」查看";
        }

        // 顶部中性提示：只要有一类在用内置路径就提示，点了去「拓展」，不阻断任何操作。
        var ttsOnBuiltin = ttsBuiltin && !ttsUsable;
        var asrOnBuiltin = asrBuiltin && !asrUsable;
        VoiceBuiltinHint.Visibility = ttsOnBuiltin || asrOnBuiltin ? Visibility.Visible : Visibility.Collapsed;

        if (VoiceBuiltinHint.Visibility == Visibility.Visible)
        {
            VoiceBuiltinHint.Content = ttsOnBuiltin
                ? "内置引擎 · 可在「拓展」添加模块"
                : "内置识别 · 可在「拓展」添加模块";
        }
    }

    /// <summary>
    /// 内置语音合成实现（SAPI）是否构成一条可用路径。
    ///
    /// 恒为 true：SAPI 是 Windows 自带组件，不依赖 modules\，它是产品的兜底引擎
    /// （见 <see cref="Core.Tts.SapiEngine"/>）。门禁不该因为"没装 tts 模块"就把语音页锁死，
    /// 那正是本次修掉的缺陷；即便个别系统上 SAPI 真的不可用，运行时也会在 TTS 状态里
    /// 如实降级提示，而不是提前把配置入口封死。
    /// </summary>
    private static bool HasBuiltinTts() => true;

    /// <summary>
    /// 内置语音识别实现（ai_asr.dll 桥接）是否可用：动态库存在即算可用。
    /// 路径来自 config.yaml 的 asr.library_path，不硬编码（准则 8）。
    /// </summary>
    private bool HasBuiltinAsr()
    {
        try
        {
            var libraryPath = AppPaths.Resolve(_host.Config.Asr.LibraryPath);
            return !string.IsNullOrWhiteSpace(libraryPath) && File.Exists(libraryPath);
        }
        catch
        {
            // 路径解析异常不构成"没有实现"的证据，按可用处理，避免把配置锁死。
            return true;
        }
    }

    private async void OnRedetectEnvironment(object sender, RoutedEventArgs e)
    {
        try
        {
            ModuleHintText.Text = string.Empty;
            await RefreshEnvironmentAndModulesAsync();
            _host.Logger.Info("设置面板重新识别运行环境与模块。");
        }
        catch (Exception ex)
        {
            // RefreshEnvironmentAndModulesAsync 内部已兜住异常；这里再兜一层，
            // 避免 async void 把异常甩给界面线程（用户会看到"内部错误"气泡且状态停在"识别中"）。
            ModuleHintText.Text = $"识别失败：{ex.Message}";
            _host.Logger.Warn($"设置面板重新识别失败：{ex.Message}");
        }
    }

    private void OnOpenModulesFolder(object sender, RoutedEventArgs e)
    {
        try
        {
            var root = ModuleRegistry.ModulesRoot;
            Directory.CreateDirectory(root);
            OpenInExplorer(root);
        }
        catch (Exception ex)
        {
            ModuleHintText.Text = $"打开目录失败：{ex.Message}";
            _host.Logger.Warn($"打开 modules 目录失败：{ex.Message}");
        }
    }

    /// <summary>用资源管理器打开一个目录（两处重复的 Process.Start 收在这里）。</summary>
    private static void OpenInExplorer(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
        {
            return;
        }

        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
    }

    private void OnAddLlmModule(object sender, RoutedEventArgs e) => _ = AddModuleAsync(ModuleKind.Llm);

    private void OnAddTtsModule(object sender, RoutedEventArgs e) => _ = AddModuleAsync(ModuleKind.Tts);

    private void OnAddAsrModule(object sender, RoutedEventArgs e) => _ = AddModuleAsync(ModuleKind.Asr);

    /// <summary>
    /// 「＋ 添加…模块」：选一个目录，整个复制到 modules/&lt;目录名&gt;，
    /// 校验 module.json 存在且 kind 匹配后提示成功并刷新列表。
    /// </summary>
    private async Task AddModuleAsync(ModuleKind kind)
    {
        // 连点两下「添加」会同时复制同一批文件（第二个必然半途失败并回滚），先挡住。
        if (_importingModule)
        {
            ModuleHintText.Text = "正在复制上一个模块…";
            return;
        }

        _importingModule = true;

        try
        {
            var label = ModuleRegistry.KindLabel(kind);

            var dialog = new Microsoft.Win32.OpenFolderDialog
            {
                Title = $"选择{label}模块目录（须含 module.json）",
                Multiselect = false,
            };

            if (Directory.Exists(ModuleRegistry.ModulesRoot))
            {
                dialog.InitialDirectory = ModuleRegistry.ModulesRoot;
            }

            if (dialog.ShowDialog(this) != true)
            {
                return;
            }

            var source = dialog.FolderName;
            if (string.IsNullOrWhiteSpace(source) || !Directory.Exists(source))
            {
                ModuleHintText.Text = "选中的目录不存在";
                return;
            }

            ModuleHintText.Text = $"正在复制{label}模块…";

            /*
             * 环境探测（nvidia-smi）与整目录复制都在线程池上做：
             * 以前环境探测是在 UI 线程 await 的（ProbeAsync 里起子进程），
             * 复制大模块时面板会卡住不刷新。
             */
            var (result, environment) = await Task.Run(async () =>
            {
                var env = _environment ?? await ModuleRegistry.ProbeAsync(_host.Logger).ConfigureAwait(false);
                return (ModuleRegistry.Import(source, kind, env, _host.Logger), env);
            }).ConfigureAwait(true);

            _environment ??= environment;
            ModuleHintText.Text = result.Message;

            if (result.Success)
            {
                _bubbles.Show(
                    $"module-imported-{ModuleRegistry.KindKey(kind)}",
                    result.Message,
                    BubbleLevel.Info,
                    TimeSpan.FromSeconds(_host.Config.Ui.BubbleDurationSeconds));

                await RefreshEnvironmentAndModulesAsync();
            }
        }
        catch (Exception ex)
        {
            ModuleHintText.Text = $"添加失败：{ex.Message}";
            _host.Logger.Error($"添加模块失败：{ex.Message}", ex);
        }
        finally
        {
            _importingModule = false;
        }
    }

    // ================================================================== 自动保存

    /// <summary>
    /// 自动保存（防抖）：滑块拖动与文本框输入会连续触发，等 400ms 再写一次文件。
    /// </summary>
    /// <param name="key">
    /// 改的是哪一项：同一项只保留最后一次改动，不同项各自保留
    /// （见 <see cref="_pendingSaves"/> 的说明）。
    /// </param>
    /// <param name="save">真正落盘的动作。</param>
    private void ScheduleSave(string key, Action save)
    {
        _pendingSaves[key] = save;
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    private void FlushPendingSave()
    {
        _saveTimer.Stop();

        if (_pendingSaves.Count == 0)
        {
            return;
        }

        var pending = _pendingSaves.Values.ToArray();
        _pendingSaves.Clear();

        // 逐项独立 try：一项写失败不能连累其它项。
        foreach (var save in pending)
        {
            try
            {
                save();
            }
            catch (Exception ex)
            {
                _host.Logger.Warn($"保存设置失败（已跳过该项，其余继续）：{ex.Message}");
            }
        }
    }

    /// <summary>
    /// 安排一次"立即应用"：拖动滑块期间连续调用只会应用最后一次（180ms 防抖）。
    ///
    /// 不同类型的改动合并策略：同一类就保持，出现两类就升级成 <see cref="AppearanceChange.All"/> ——
    /// 反正应用走的都是同一批既有方法，多做一次比漏做安全。
    /// </summary>
    private void ScheduleApply(AppearanceChange change)
    {
        var previous = _pendingApply;

        _pendingApply = previous is null || previous == change
            ? change
            : AppearanceChange.All;

        _applyTimer.Stop();
        _applyTimer.Start();
    }

    /// <summary>
    /// 立刻应用待处理的变更（防抖计时到点 / 关面板时调用）。
    ///
    /// 顺序很关键：主窗口是从 Assets\appearance.json 读值的，
    /// 所以必须**先**把这次拖动的结果落盘，再去通知它 — 否则它读到的是上一帧的值。
    /// </summary>
    private void FlushPendingApply()
    {
        _applyTimer.Stop();

        var change = _pendingApply;
        _pendingApply = null;

        if (change is null)
        {
            return;
        }

        FlushPendingSave();

        _host.RequestAppearanceChange(change.Value);

        if (_closed)
        {
            return;
        }

        // 如实标注：主窗口不在（已关闭 / 未就绪）时说明要等下次启动。
        AppearanceHintText.Text = !_host.HasAppearanceTarget
            ? "已保存 · 桌宠未打开，下次启动生效"
            : change.Value == AppearanceChange.Avatar
                ? "已保存并应用 · 换模型需重启"
                : "已保存并应用";
    }

    private void MarkAutoSaved()
    {
        if (_closed)
        {
            return;
        }

        AutoSaveText.Text = $"已保存 {DateTime.Now:HH:mm:ss} · v{_versionText}";
    }

    // ================================================================== 恢复默认

    /// <summary>
    /// 「恢复默认（清除覆盖）」：删掉 Assets\appearance.json，全部回到 config.yaml 的出厂默认。
    /// 只保留这一个弱化文字按钮 —— 原来那三套「保存 / 全部跟随 config.yaml」已经取消。
    /// </summary>
    private void OnRestoreDefaults(object sender, RoutedEventArgs e)
    {
        try
        {
            FlushPendingSave();

            var ok = AppearanceStore.ClearAll();
            _host.Logger.Info($"设置面板恢复默认：清除 Assets\\appearance.json 覆盖（{(ok ? "成功" : "失败")}）。");

            _loading = true;
            try
            {
                LoadAppearanceTab();
                LoadTtsEngineTab();
                LoadAiToggleTab();
                LoadDialogCombos();
                LoadVoiceCombos();
                LoadInteractionControls();
                LoadBehaviorControls();
                LoadStaticInfo();
            }
            finally
            {
                _loading = false;
            }

            // 运行时也回到 config.yaml 的值（能即时应用的那几项）。
            _host.Config.Ui.TransparentHoverSwitch = HoverSwitchCheck.IsChecked == true;
            _host.Config.Ui.ClickThrough = ClickThroughCheck.IsChecked == true;
            PetWindow?.SetInteractive(_host.Config.Ui.ClickThrough == false);

            _host.Config.Idle.IdleMinutes = IdleMinutesSlider.Value;
            _host.Config.Idle.MaintenanceCooldownMinutes = IdleCooldownSlider.Value;
            _host.Config.Tts.DefaultVoice = string.IsNullOrWhiteSpace(TtsVoiceBox.Text)
                ? _host.Config.Tts.DefaultVoice
                : TtsVoiceBox.Text.Trim();
            _host.Config.Tts.GptSoVits.SpeedFactor = Math.Round(TtsSpeedSlider.Value, 2);

            _bubbles.DefaultDuration = TimeSpan.FromSeconds(Math.Max(1, BubbleSecondsSlider.Value));

            // 覆盖层已删掉：取消排队中的变更，直接按"全部回到出厂默认"请求一次即时应用。
            _applyTimer.Stop();
            _pendingApply = null;
            _host.RequestAppearanceChange(AppearanceChange.All);

            // 启用状态缓存也要跟着复位，否则门禁/卡片还会按旧的启用项显示。
            _enabledNames = EnabledModuleNames.Load();
            _moduleRenderSignature = null;

            // 模块启用状态也在同一个文件里，清掉后重新按"第一个可用模块"识别。
            _ = RefreshEnvironmentAndModulesAsync();

            AppearanceHintText.Text = ok
                ? "已恢复出厂默认"
                : "清除失败，详见日志";
            ModuleHintText.Text = ok ? "模块启用已恢复默认" : string.Empty;
        }
        catch (Exception ex)
        {
            AppearanceHintText.Text = $"恢复默认失败：{ex.Message}";
            _host.Logger.Warn($"恢复默认失败：{ex.Message}");
        }
    }
}
