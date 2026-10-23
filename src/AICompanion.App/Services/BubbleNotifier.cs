using System.Windows.Threading;
using AICompanion.Core.Abstractions;

namespace AICompanion.App.Services;

/// <summary>气泡里的一条内容。</summary>
public sealed record BubbleItem(string Key, string Message, BubbleLevel Level, DateTime CreatedUtc)
{
    public DateTime? ExpiresUtc { get; init; }

    public bool IsExpired => ExpiresUtc.HasValue && DateTime.UtcNow >= ExpiresUtc.Value;
}

/// <summary>
/// <see cref="IBubbleNotifier"/> 的 WPF 实现（手册准则 4：所有提示走气泡，绝不弹消息框）。
///
/// 线程契约：核心层的任何线程都可以调用，内部统一封送到 UI 线程；
/// 核心层不做封送，也不需要知道 WPF 的存在。
/// </summary>
public sealed class BubbleNotifier : IBubbleNotifier
{
    private readonly Dispatcher _dispatcher;
    private IAppLogger _logger;
    private readonly object _sync = new();
    private readonly Dictionary<string, BubbleItem> _items = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _statuses = new(StringComparer.OrdinalIgnoreCase);
    private readonly DispatcherTimer _expiryTimer;

    public BubbleNotifier(Dispatcher dispatcher, IAppLogger logger)
    {
        _dispatcher = dispatcher;
        _logger = logger.ForCategory("Bubble");

        // 定期清理过期气泡，避免一次性提示永久停留。
        _expiryTimer = new DispatcherTimer(DispatcherPriority.Background, dispatcher)
        {
            Interval = TimeSpan.FromSeconds(1),
        };
        _expiryTimer.Tick += (_, _) => PurgeExpired();
        _expiryTimer.Start();
    }

    /// <summary>气泡内容发生变化（UI 订阅后刷新显示）。</summary>
    public event EventHandler? Changed;

    /// <summary>
    /// 换掉内部日志器。
    /// 启动早期构造本类时用的是"引导期"日志器（目录来自默认值）；等配置解析完、
    /// 日志目录可能已经切换，原来的 sink 会被关闭。若不重绑，之后每写一条气泡日志
    /// 都会把旧目录的文件重新打开（日志分流 + 多一个文件句柄）。
    /// </summary>
    public void SetLogger(IAppLogger logger)
    {
        if (logger is null)
        {
            return;
        }

        _logger = logger.ForCategory("Bubble");
    }

    /// <summary>当前应显示的气泡（按创建时间排序，最新的在前）。</summary>
    public IReadOnlyList<BubbleItem> Current
    {
        get
        {
            lock (_sync)
            {
                return _items.Values
                    .Where(i => !i.IsExpired)
                    .OrderByDescending(i => i.CreatedUtc)
                    .ToList();
            }
        }
    }

    /// <summary>状态栏键值（设置面板展示，例如"当前实际引擎：Piper（降级）"）。</summary>
    public IReadOnlyDictionary<string, string> Statuses
    {
        get
        {
            lock (_sync)
            {
                return new Dictionary<string, string>(_statuses, StringComparer.OrdinalIgnoreCase);
            }
        }
    }

    /// <summary>
    /// 气泡默认显示时长。调用 <see cref="Show"/> 时没显式给时长就用它。
    ///
    /// 为什么必须有默认值：原来 <c>duration</c> 传 null 时 ExpiresUtc 为 null，
    /// 而 IsExpired 对 null 恒为 false —— 于是这类气泡**永不过期**，
    /// 界面上会一直挂着几条旧提示。默认值可由设置面板调整。
    /// </summary>
    public TimeSpan DefaultDuration { get; set; } = TimeSpan.FromSeconds(5);

    public void Show(string key, string message, BubbleLevel level = BubbleLevel.Info, TimeSpan? duration = null)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            key = "bubble-" + Guid.NewGuid().ToString("N");
        }

        var effective = duration ?? DefaultDuration;
        if (effective <= TimeSpan.Zero)
        {
            effective = TimeSpan.FromSeconds(5);
        }

        var item = new BubbleItem(key, message, level, DateTime.UtcNow)
        {
            ExpiresUtc = DateTime.UtcNow + effective,
        };

        lock (_sync)
        {
            _items[key] = item;
        }

        _logger.Info($"[气泡/{level}] {message}");
        RaiseChanged();
    }

    public void Clear(string key)
    {
        bool removed;

        lock (_sync)
        {
            removed = _items.Remove(key);
        }

        if (removed)
        {
            _logger.Info($"已清除气泡：{key}");
            RaiseChanged();
        }
    }

    public void ClearAll()
    {
        lock (_sync)
        {
            _items.Clear();
        }

        RaiseChanged();
    }

    public void SetStatus(string key, string value)
    {
        lock (_sync)
        {
            _statuses[key] = value;
        }

        RaiseChanged();
    }

    public void ClearStatus(string key)
    {
        lock (_sync)
        {
            _statuses.Remove(key);
        }

        RaiseChanged();
    }

    private void PurgeExpired()
    {
        bool changed;

        lock (_sync)
        {
            var expired = _items.Where(kv => kv.Value.IsExpired).Select(kv => kv.Key).ToList();
            foreach (var key in expired)
            {
                _items.Remove(key);
            }

            changed = expired.Count > 0;
        }

        if (changed)
        {
            RaiseChanged();
        }
    }

    private void RaiseChanged()
    {
        if (_dispatcher.CheckAccess())
        {
            Changed?.Invoke(this, EventArgs.Empty);
            return;
        }

        // 核心层线程调用进来时封送到 UI 线程。
        _dispatcher.BeginInvoke(new Action(() => Changed?.Invoke(this, EventArgs.Empty)));
    }
}
