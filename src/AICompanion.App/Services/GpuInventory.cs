using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;

namespace AICompanion.App.Services;

/// <summary>
/// 显卡厂商。前四家会决定 OpenGL / D3D 互操作路径；<see cref="Other"/> 是"识别出来不是这三家"
/// （Qualcomm、Microsoft Basic Render、VMware、DisplayLink 等），判定时与 Unknown 一样
/// **不做自动修复**：既不敢断言它有问题，也不该去改用户的注册表。
/// </summary>
public enum GpuVendor
{
    Unknown = 0,
    Nvidia = 1,
    Amd = 2,
    Intel = 3,
    Other = 4,
}

/// <summary>一块显示适配器的只读描述。</summary>
public sealed record GpuAdapterInfo(
    string Name,
    string Provider,
    string DriverVersion,
    string PnpId,
    GpuVendor Vendor)
{
    public bool IsNvidia => Vendor == GpuVendor.Nvidia;

    /// <summary>形如 <c>NVIDIA GeForce RTX 5060 Laptop GPU（驱动 32.0.16.2101）</c>。</summary>
    public string Describe()
        => string.IsNullOrWhiteSpace(DriverVersion) ? Name : $"{Name}（驱动 {DriverVersion}）";
}

/// <summary>
/// 本机显卡清单 + "主显示器在哪块适配器上"的只读探测。
///
/// 两个数据来源各有分工，都不是随便挑的：
///
///   1. **适配器清单**走注册表显示类 <c>HKLM\SYSTEM\CurrentControlSet\Control\Class\{4d36e968-…}</c> 下的
///      0000/0001/… 子键（DriverDesc / ProviderName / DriverVersion / MatchingDeviceId）。
///      这就是 <c>Get-CimInstance Win32_VideoController</c> 在同一份数据的等价读法，
///      但不需要引入 System.Management（WMI 库 ~1 MB 且首次调用要起 RPC，启动期会卡几百毫秒）。
///      注意：子键里混着虚拟显示器驱动（例如向日葵的 OrayIddDriver），
///      厂商识别不出来的条目一律丢弃 —— 它们不是显卡，计入"多适配器"会造成误判。
///
///   2. **主显示适配器**走 GDI：<c>EnumDisplayMonitors</c> 拿主显示器的设备名（\\.\DISPLAYn），
///      再用 <c>EnumDisplayDevices</c> 把这个设备名映射到适配器（DeviceID 里含 PCI\VEN_xxxx）。
///      这是 Windows 的实际显示拓扑，也就是 DWM 合成时用的那块适配器。
///      为什么不按手册建议用 DXGI 的 EnumOutputs：那要手写 IDXGIFactory1 / IDXGIAdapter1 / IDXGIOutput
///      的 COM 接口声明，vtable 槽位必须逐字对齐（含 IDXGIObject 4 个 + IDXGIAdapter 3 个 +
///      IDXGIOutput 13 个方法），错一个槽位就是读到"看着合理其实全错"的结构，且在 COM 层崩了没法 catch。
///      GDI 这条路径只有 3 个 user32 函数、结构体字段固定，稳定得多。
///
/// 所有方法都不抛异常：探测失败返回空清单 / null，由调用方降级。
/// </summary>
public static class GpuInventory
{
    /// <summary>显示类（Display class）注册表路径，<c>{4d36e968-e325-11ce-bfc1-08002be10318}</c> 是显示适配器的类 GUID。</summary>
    private const string DisplayClassKeyPath =
        @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}";

    /// <summary>
    /// 逐个尝试的子键号上限。
    ///
    /// 为什么不去枚举父键的子键名：实测该键下有一个 ACL 受保护的 <c>Properties</c> 子键，
    /// 非管理员枚举父键会直接抛 <see cref="UnauthorizedAccessException"/>（整个清单拿不到）。
    /// 显卡实例永远是 0000、0001… 这种四位数字，按号试读、每号单独 try/catch 最稳。
    /// </summary>
    private const int MaxAdapterSubKeys = 16;

    /// <summary>
    /// <see cref="EnumDisplayDevices"/> 的索引上限。与子键上限语义不同（这是 GDI 设备序号），
    /// 数值相同只是巧合 —— 枚举本身在第一个失败的序号处就 break，16 只是防御性上限。
    /// </summary>
    private const int MaxDisplayDevices = 16;

    /// <summary>
    /// 单个字符串字段的长度上限。注册表字符串值最大可到 16 K 字符，
    /// 原样拼进日志/气泡会把一行日志撑到几 KB；显卡名不可能超过这个长度。
    /// </summary>
    private const int MaxFieldLength = 160;

    /// <summary>0000…000F：显卡实例子键名。预生成，避免每次探测都做 16 次 <c>i.ToString("D4")</c>。</summary>
    private static readonly string[] AdapterSubKeyNames = CreateAdapterSubKeyNames();

    /// <summary>
    /// 虚拟显示器驱动的名字关键字。**必须是静态字段**：写在方法里就会在每次调用时
    /// 新分配一个数组，而本方法在一次探测里会被调 2×16 次。
    /// </summary>
    private static readonly string[] VirtualDisplayMarkers =
    {
        "Idd", "Indirect", "Basic Display", "Basic Render", "Mirror", "Remote Display",
        "Virtual Display", "DisplayLink", "Parsec", "Sunshine", "UsbDisplay",
    };

    /// <summary>与具体型号无关的品牌/通用词（<see cref="CoreTokens"/> 用）。同样只分配一次。</summary>
    private static readonly HashSet<string> GenericDeviceTokens = new(StringComparer.Ordinal)
    {
        "nvidia", "amd", "ati", "intel", "geforce", "radeon", "graphics", "controller",
        "family", "series", "laptop", "desktop", "gpu", "corporation", "inc", "corp", "ltd",
        "tm", "r", "c", "ve", "pcie", "sse2", "compatibility", "profile", "context", "mesa",
    };

    /// <summary>
    /// 枚举本机的显示适配器（不含虚拟显示器驱动）。失败返回空清单。
    /// </summary>
    /// <param name="diagnostics">
    /// 可选：收集"某一项为什么没读到"的说明（异常类型 + 消息）。不传就完全不记录 ——
    /// 每个 try/catch 仍然只降级不抛出，但不再把 ACL/结构异常静默吞掉。
    /// </param>
    public static IReadOnlyList<GpuAdapterInfo> ListAdapters(ICollection<string>? diagnostics = null)
    {
        var adapters = ReadAdaptersFromRegistry(diagnostics);

        if (adapters.Count == 0)
        {
            // 注册表读不到（权限/被清理工具改坏）时退回 GDI：只有名字与 PnP id，没有驱动版本。
            adapters.AddRange(ReadAdaptersFromGdi(diagnostics));
        }

        return adapters;
    }

    /// <summary>
    /// 主显示器所在的显示适配器（DWM 合成的那块）。探测失败返回 null。
    /// </summary>
    /// <param name="known">
    /// 可选：调用方已经读到的适配器清单。传进来就不会再把注册表 + GDI 走一遍
    /// （<see cref="ListAdapters"/> 每次都是 16 次 OpenSubKey + 最多 16 次 EnumDisplayDevices）。
    /// 传 null 时按需自行探测。
    /// </param>
    /// <param name="diagnostics">同 <see cref="ListAdapters"/>；只在真的自探测时才可能写入。</param>
    public static GpuAdapterInfo? FindPrimaryDisplayAdapter(
        IReadOnlyList<GpuAdapterInfo>? known = null,
        ICollection<string>? diagnostics = null)
    {
        string? deviceName = FindPrimaryMonitorDeviceName();

        if (deviceName is null)
        {
            return null;
        }

        var device = FindDisplayDevice(deviceName);

        if (device is null)
        {
            return null;
        }

        var pnpId = device.Value.DeviceId;
        var name = device.Value.Name;

        // 驱动版本只有注册表有：按 PnP id（其次按名字）把两边的同一条目合起来。
        var inventory = known ?? ListAdapters(diagnostics);
        var match = inventory.FirstOrDefault(a =>
                !string.IsNullOrWhiteSpace(a.PnpId) &&
                string.Equals(a.PnpId, pnpId, StringComparison.OrdinalIgnoreCase))
            ?? inventory.FirstOrDefault(a => string.Equals(a.Name, name, StringComparison.OrdinalIgnoreCase));

        var vendor = match?.Vendor ?? VendorOf($"{name} {pnpId}");

        return new GpuAdapterInfo(
            name,
            match?.Provider ?? string.Empty,
            match?.DriverVersion ?? string.Empty,
            string.IsNullOrWhiteSpace(pnpId) ? (match?.PnpId ?? string.Empty) : pnpId,
            vendor);
    }

    /// <summary>
    /// 从任意显卡字符串（GL_VENDOR / GL_RENDERER / 驱动名 / PnP id）识别厂商。
    ///
    /// 调用方必须把 GL_VENDOR 与 GL_RENDERER **一起**传进来再匹配一次：
    /// 两处自报不一致的机器上（例如 Vendor=Google Inc.、Renderer=Mesa Intel(R) …），
    /// 只看一处就会误判成"其他"。这里按 NVIDIA → AMD → Intel 的顺序匹配关键字，
    /// 三家都匹配不到但字符串非空时返回 <see cref="GpuVendor.Other"/>（识别到东西，但不是这三家）。
    /// </summary>
    public static GpuVendor VendorOf(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return GpuVendor.Unknown;
        }

        if (text.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("GeForce", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("Quadro", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("VEN_10DE", StringComparison.OrdinalIgnoreCase))
        {
            return GpuVendor.Nvidia;
        }

        if (text.Contains("AMD", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("ATI ", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("Radeon", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("Advanced Micro Devices", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("VEN_1002", StringComparison.OrdinalIgnoreCase))
        {
            return GpuVendor.Amd;
        }

        // Intel 的自报名五花八门：GL_VENDOR 可能是 "Intel" / "Intel Inc." / "Intel Corporation"，
        // GL_RENDERER 是 "Intel(R) UHD Graphics 620" / "Intel(R) Iris(R) Xe Graphics" /
        // "Intel(R) Arc(TM) A370M Graphics" / "Mesa Intel(R) UHD Graphics"，
        // 系统侧 Win32_VideoController.Name 同款。全部含 "Intel"，一处关键字即可覆盖。
        if (text.Contains("Intel", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("Iris", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("VEN_8086", StringComparison.OrdinalIgnoreCase))
        {
            return GpuVendor.Intel;
        }

        return GpuVendor.Other;
    }

    /// <summary>厂商的中文/英文标签（日志与界面共用，避免两处写法不一致）。</summary>
    public static string VendorLabel(GpuVendor vendor) => vendor switch
    {
        GpuVendor.Nvidia => "NVIDIA",
        GpuVendor.Amd => "AMD",
        GpuVendor.Intel => "Intel",
        _ => "其他厂商",
    };

    /// <summary>
    /// 是否是"虚拟显示器驱动"（向日葵 OrayIddDriver、串流/远程桌面用的 Indirect Display 等）。
    ///
    /// 这类驱动会出现在显卡清单里（<c>Get-CimInstance Win32_VideoController</c> 也会列出来），
    /// 但它们没有 3D 引擎、也没有 OpenGL 实现 —— 计入"多显卡"会让单显卡机器被误判成双显卡。
    /// </summary>
    public static bool IsVirtualDisplayDriver(string? name, string? pnpId)
    {
        if (!string.IsNullOrWhiteSpace(pnpId) &&
            pnpId.StartsWith(@"Root\", StringComparison.OrdinalIgnoreCase))
        {
            return true;    // 虚拟设备没有 PCI 位置，一定挂在 Root\ 下
        }

        if (string.IsNullOrWhiteSpace(name))
        {
            return true;
        }

        foreach (var marker in VirtualDisplayMarkers)
        {
            if (name.Contains(marker, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// 两个显卡名字是否指向**同一型号**（用于"厂商相同但型号不同 → 可能不一致"的判定）。
    ///
    /// 为什么要模糊比较：GL_RENDERER 与系统里的适配器名并不是同一份字符串 ——
    /// GL 侧是 "AMD Radeon 780M Graphics" 或 "NVIDIA GeForce RTX 5060 Laptop GPU/PCIe/SSE2"
    /// （调用方已剥掉斜杠后缀），系统侧是 "AMD Radeon(TM) 780M Graphics" 之类，
    /// 带 (R)/(TM)、大小写、多余空格。逐字比较会把同一块显卡判成两块。
    ///
    /// 规则（从强到弱）：
    ///   1. 归一化后完全相同 → 同型号；
    ///   2. 两边都带"型号号码"（含数字的 token：620 / 780M / 5060 / A370M）→ 有交集才算同型号；
    ///   3. 只有一边带号码 → 把 "graphics/controller/geforce/…" 这类通用词去掉后比 token 包含关系；
    ///   4. 两边都没有号码（如 Iris Xe / Arc Graphics）→ 归一化字符串互相包含即算同型号。
    /// </summary>
    public static bool SameDevice(string? a, string? b)
    {
        if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b))
        {
            return false;
        }

        // 归一化是这条路径里最贵的操作（小写 + 逐字符扫描 + 建字符串）。
        // 以前 ModelTokens / CoreTokens 各自内部又归一化一遍，一次比较最多归一化 6 次；
        // 现在只做 2 次，token 函数直接吃归一化结果。
        var na = NormalizeDeviceName(a);
        var nb = NormalizeDeviceName(b);

        if (na.Length == 0 || nb.Length == 0)
        {
            return false;
        }

        if (string.Equals(na, nb, StringComparison.Ordinal))
        {
            return true;
        }

        var ta = ModelTokens(na);
        var tb = ModelTokens(nb);

        if (ta.Count > 0 && tb.Count > 0)
        {
            return ta.Overlaps(tb);
        }

        if (ta.Count > 0 || tb.Count > 0)
        {
            var ca = CoreTokens(na);
            var cb = CoreTokens(nb);

            return ca.Count > 0 && cb.Count > 0 && (ca.IsSubsetOf(cb) || cb.IsSubsetOf(ca));
        }

        return na.Contains(nb, StringComparison.Ordinal) || nb.Contains(na, StringComparison.Ordinal);
    }

    /// <summary>
    /// 归一化：小写、只留字母数字与空格、压缩并去掉首尾空格。
    /// 单趟扫描 + <see cref="StringBuilder"/>，避免 Select/ToArray/new string/Split/Join 五次分配。
    /// </summary>
    private static string NormalizeDeviceName(string value)
    {
        var builder = new StringBuilder(value.Length);
        var pendingSpace = false;

        foreach (var ch in value)
        {
            if (char.IsLetterOrDigit(ch))
            {
                if (pendingSpace && builder.Length > 0)
                {
                    builder.Append(' ');
                }

                pendingSpace = false;
                builder.Append(char.ToLowerInvariant(ch));
            }
            else
            {
                pendingSpace = true;
            }
        }

        return builder.ToString();
    }

    /// <summary>型号号码：含数字的 token（620 / 780m / 5060 / a370m）。入参必须已归一化。</summary>
    private static HashSet<string> ModelTokens(string normalized)
        => normalized
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(t => t.Length >= 2 && t.Any(char.IsDigit))
            .ToHashSet(StringComparer.Ordinal);

    /// <summary>去掉品牌/通用词之后剩下的 token，用于"只有一边带号码"时的包含比较。入参必须已归一化。</summary>
    private static HashSet<string> CoreTokens(string normalized)
        => normalized
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(t => !GenericDeviceTokens.Contains(t))
            .ToHashSet(StringComparer.Ordinal);

    /// <summary>把适配器清单拼成一行日志文本。</summary>
    public static string DescribeAdapters(IReadOnlyList<GpuAdapterInfo> adapters)
        => adapters.Count == 0
            ? "未识别到显卡"
            : string.Join("、", adapters.Select(a => $"{a.Describe()}［{VendorLabel(a.Vendor)}］"));

    // ================================================================== 注册表

    private static List<GpuAdapterInfo> ReadAdaptersFromRegistry(ICollection<string>? diagnostics)
    {
        var adapters = new List<GpuAdapterInfo>();

        try
        {
            using var klass = Registry.LocalMachine.OpenSubKey(DisplayClassKeyPath);

            if (klass is null)
            {
                diagnostics?.Add($"显示类注册表键不存在或不可读：HKLM\\{DisplayClassKeyPath}");
                return adapters;
            }

            foreach (var subKeyName in AdapterSubKeyNames)
            {
                try
                {
                    using var sub = klass.OpenSubKey(subKeyName);

                    if (sub is null)
                    {
                        continue;
                    }

                    // 值可能不是字符串（REG_BINARY / REG_DWORD）→ as string 得到 null，
                    // 也可能异常地长（最长 16 K）→ 统一截断后再用，避免把日志行撑爆。
                    var name = ClampField((sub.GetValue("DriverDesc") as string)?.Trim());

                    if (string.IsNullOrWhiteSpace(name))
                    {
                        continue;
                    }

                    var provider = ClampField((sub.GetValue("ProviderName") as string)?.Trim());
                    var version = ClampField((sub.GetValue("DriverVersion") as string)?.Trim());
                    var pnpId = ClampField((sub.GetValue("MatchingDeviceId") as string)?.Trim());

                    if (IsVirtualDisplayDriver(name, pnpId))
                    {
                        // 虚拟显示器驱动（OrayIddDriver / Indirect Display）不是显卡，
                        // 计进去会让"多显卡"判断失真。
                        continue;
                    }

                    var vendor = VendorOf($"{name} {provider} {pnpId}");

                    if (vendor == GpuVendor.Unknown)
                    {
                        continue;
                    }

                    adapters.Add(new GpuAdapterInfo(name, provider, version, pnpId, vendor));
                }
                catch (Exception ex)
                {
                    // 单个子键读不了（ACL/被改成非标准结构）时跳过，不影响其他显卡；
                    // 但把类型与消息留给调用方，否则"少了一块卡"永远查不出来。
                    diagnostics?.Add($"显示类子键 {subKeyName} 读取失败：{ex.GetType().Name}：{ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            // HKLM 读不到（极少数被策略锁死的机器）：由调用方退回 GDI 枚举。
            diagnostics?.Add(
                $"显示类注册表键 HKLM\\{DisplayClassKeyPath} 读取失败：{ex.GetType().Name}：{ex.Message}");
        }

        return adapters;
    }

    /// <summary>把可能为 null / 过长的字符串压成可安全写进日志与界面的一段文本。</summary>
    private static string ClampField(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        return value.Length <= MaxFieldLength ? value : value[..MaxFieldLength] + "…";
    }

    private static string[] CreateAdapterSubKeyNames()
    {
        var names = new string[MaxAdapterSubKeys];

        for (var i = 0; i < names.Length; i++)
        {
            names[i] = i.ToString("D4");
        }

        return names;
    }

    // ================================================================== GDI

    private static string? FindPrimaryMonitorDeviceName()
    {
        // Device 显式声明为可空：FirstOrDefault 查不到时返回的是 default 元组，
        // 里面的 string 元素就是 null。声明成非空会让下面那句 null 判断变成"看起来多余"，
        // 一旦有人"顺手清理"就会变成 NullReferenceException。
        var monitors = new List<(string? Device, RECT Rect, bool Primary)>();

        try
        {
            EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (hMonitor, _, _, _) =>
            {
                var info = new MONITORINFOEX
                {
                    cbSize = Marshal.SizeOf<MONITORINFOEX>(),
                    szDevice = string.Empty,
                };

                if (!GetMonitorInfo(hMonitor, ref info) || string.IsNullOrWhiteSpace(info.szDevice))
                {
                    return true;      // 继续枚举下一个
                }

                monitors.Add((info.szDevice, info.rcMonitor, (info.dwFlags & MonitorInfoPrimary) != 0));
                return true;
            }, IntPtr.Zero);
        }
        catch (Exception)
        {
            return null;
        }

        if (monitors.Count == 0)
        {
            return null;
        }

        // 主显示器优先看 MONITORINFOF_PRIMARY；拿不到就退化为"矩形包含 (0,0) 的那块"。
        var primary = monitors.FirstOrDefault(m => m.Primary);

        if (primary.Device is null)
        {
            primary = monitors.FirstOrDefault(m =>
                m.Rect.Left <= 0 && m.Rect.Top <= 0 && m.Rect.Right > 0 && m.Rect.Bottom > 0);
        }

        return primary.Device ?? monitors[0].Device;
    }

    private static (string Name, string DeviceId)? FindDisplayDevice(string deviceName)
    {
        try
        {
            for (uint i = 0; i < MaxDisplayDevices; i++)
            {
                var device = new DISPLAY_DEVICE
                {
                    cb = Marshal.SizeOf<DISPLAY_DEVICE>(),
                    DeviceName = string.Empty,
                    DeviceString = string.Empty,
                    DeviceID = string.Empty,
                    DeviceKey = string.Empty,
                };

                if (!EnumDisplayDevices(null, i, ref device, 0))
                {
                    break;
                }

                if (!string.Equals(device.DeviceName, deviceName, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var name = string.IsNullOrWhiteSpace(device.DeviceString)
                    ? deviceName
                    : ClampField(device.DeviceString.Trim());
                return (name, ClampField(device.DeviceID?.Trim()));
            }
        }
        catch (Exception)
        {
            // 落回 null：调用方当作"没有主显示适配器信息"处理。
        }

        return null;
    }

    private static List<GpuAdapterInfo> ReadAdaptersFromGdi(ICollection<string>? diagnostics)
    {
        var adapters = new List<GpuAdapterInfo>();

        try
        {
            for (uint i = 0; i < MaxDisplayDevices; i++)
            {
                var device = new DISPLAY_DEVICE
                {
                    cb = Marshal.SizeOf<DISPLAY_DEVICE>(),
                    DeviceName = string.Empty,
                    DeviceString = string.Empty,
                    DeviceID = string.Empty,
                    DeviceKey = string.Empty,
                };

                if (!EnumDisplayDevices(null, i, ref device, 0))
                {
                    break;
                }

                var name = device.DeviceString?.Trim();

                if (string.IsNullOrWhiteSpace(name))
                {
                    continue;
                }

                var pnpId = ClampField(device.DeviceID?.Trim());

                if (IsVirtualDisplayDriver(name, pnpId))
                {
                    continue;
                }

                var vendor = VendorOf($"{name} {pnpId}");

                if (vendor == GpuVendor.Unknown)
                {
                    continue;
                }

                // 同一块显卡会有多个显示设备条目（\\.\DISPLAY1 / DISPLAY2 …），按名字去重。
                if (adapters.Any(a => string.Equals(a.Name, name, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                adapters.Add(new GpuAdapterInfo(ClampField(name), string.Empty, string.Empty, pnpId, vendor));
            }
        }
        catch (Exception ex)
        {
            // 返回已读到的部分清单，但把失败原因留给调用方。
            diagnostics?.Add($"GDI 适配器枚举失败：{ex.GetType().Name}：{ex.Message}");
        }

        return adapters;
    }

    // ================================================================== P/Invoke

    private const uint MonitorInfoPrimary = 0x1;

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MONITORINFOEX
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string szDevice;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DISPLAY_DEVICE
    {
        public int cb;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string DeviceName;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string DeviceString;

        public int StateFlags;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string DeviceID;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string DeviceKey;
    }

    private delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdc, IntPtr lprcMonitor, IntPtr dwData);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool EnumDisplayDevices(string? lpDevice, uint iDevNum, ref DISPLAY_DEVICE lpDisplayDevice, uint dwFlags);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr lprcClip, MonitorEnumProc lpfnEnum, IntPtr dwData);

    // 注意 EntryPoint：CharSet=Unicode 时会自动加 W 后缀，但这里显式写出来，
    // 免得将来有人把它改成 CharSet.Ansi 时静默解析到 GetMonitorInfoA（结构体布局不同）。
    [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFOEX lpmi);
}
