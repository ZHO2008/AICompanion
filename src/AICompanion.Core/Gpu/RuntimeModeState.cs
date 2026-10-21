namespace AICompanion.Core.Gpu;

/// <summary>运行模式（手册 §2）。系统不做任何运行时自动切换，本状态只在启动阶段被写入一次。</summary>
public enum RuntimeMode
{
    Cpu = 0,
    Gpu = 1,
}

/// <summary>
/// 运行模式快照。做成不可变记录是为了让"模式 / ngl / 层数 / 空闲显存"这四个值
/// 永远保持一致 —— 分四个字段各自 Interlocked 会出现读到半新半旧组合的撕裂问题。
/// </summary>
public sealed record RuntimeModeSnapshot(
    RuntimeMode Mode,
    int Ngl,
    int FreeVramMb,
    int TotalLayers,
    string Explanation)
{
    public static RuntimeModeSnapshot Initial { get; } = new(
        RuntimeMode.Cpu,
        0,
        0,
        0,
        "尚未检测（启动阶段会立即计算）。");

    public bool IsGpu => Mode == RuntimeMode.Gpu;
}

public sealed class RuntimeModeChangedEventArgs : EventArgs
{
    public required RuntimeModeSnapshot Previous { get; init; }

    public required RuntimeModeSnapshot Current { get; init; }
}

/// <summary>
/// 全局运行模式持有者（手册 §2 的"全局变量 RuntimeMode"）。
/// 按准则 5，用 <see cref="Volatile"/> + <see cref="Interlocked"/> 保护；
/// 读取路径 <see cref="Current"/> 无锁，因为只是取一个不可变对象的引用。
/// </summary>
public sealed class RuntimeModeState
{
    private RuntimeModeSnapshot _snapshot = RuntimeModeSnapshot.Initial;

    public event EventHandler<RuntimeModeChangedEventArgs>? Changed;

    /// <summary>无锁读取当前快照（引用读取本身是原子的）。</summary>
    public RuntimeModeSnapshot Current => Volatile.Read(ref _snapshot);

    public RuntimeMode Mode => Current.Mode;

    public int Ngl => Current.Ngl;

    public void Update(RuntimeModeSnapshot snapshot)
    {
        var previous = Interlocked.Exchange(ref _snapshot, snapshot);
        if (previous.Mode != snapshot.Mode || previous.Ngl != snapshot.Ngl)
        {
            Changed?.Invoke(this, new RuntimeModeChangedEventArgs
            {
                Previous = previous,
                Current = snapshot,
            });
        }
    }

    /// <summary>面向气泡/状态栏的一行摘要。</summary>
    public string Describe()
    {
        var s = Current;
        return s.IsGpu
            ? $"GPU 模式（ngl={s.Ngl}/{s.TotalLayers}，空闲显存 {s.FreeVramMb} MB）"
            : $"CPU 模式（{s.Explanation}）";
    }
}
