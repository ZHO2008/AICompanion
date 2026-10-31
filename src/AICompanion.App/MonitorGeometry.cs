using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace AICompanion.App;

/// <summary>
/// 多显示器几何计算 —— 全部在**屏幕物理像素**里做，不碰 WPF 的 DIP。
///
/// 为什么必须换掉 <c>SystemParameters.WorkArea</c>：它只返回**主屏**的工作区。
/// 以前所有夹取与定位都用它，后果是双屏时角色根本拖不到副屏 ——
/// 主屏 2560 宽、150% 缩放，工作区右边界是 1707 DIP，而
/// <c>Left = Math.Clamp(Left, …, work.Right - keepX)</c> 把 Left 锁在 ≈2400 物理像素以内，
/// 副屏从 2560 才开始，永远够不到。副屏在左边（负坐标）同理，也拖不过去。
///
/// 用物理像素还有一个好处：两块屏缩放率不同（这里主屏 150%、副屏 100%）时，
/// WPF 的 Left/Top 是"DIP"、每块屏的换算系数又不一样，混着算必然出错；
/// 物理像素只有一套坐标系，跨屏无需改换算。
///
/// 窗口移动统一走 SetWindowPos，WPF 会自己收到 WM_WINDOWPOSCHANGED 并同步 Left/Top。
/// </summary>
internal static class MonitorGeometry
{
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;

    /// <summary>所有显示器的工作区（物理像素，已排除任务栏）。</summary>
    public static List<Rect> WorkAreasPhysical()
    {
        var list = new List<Rect>();

        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (hMonitor, _, _, _) =>
        {
            var info = new MonitorInfo { cbSize = Marshal.SizeOf<MonitorInfo>() };
            if (GetMonitorInfo(hMonitor, ref info))
            {
                var r = info.rcWork;
                list.Add(new Rect(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top));
            }

            return true;
        }, IntPtr.Zero);

        return list;
    }

    /// <summary>全部显示器工作区的并集（物理像素）。一块屏都没取到时退回主屏。</summary>
    public static Rect UnionPhysical()
    {
        var areas = WorkAreasPhysical();
        if (areas.Count == 0)
        {
            // 极端兜底：用主屏的物理尺寸（96 DPI 假设）避免返回空矩形。
            return new Rect(0, 0, SystemParameters.PrimaryScreenWidth, SystemParameters.PrimaryScreenHeight);
        }

        var union = areas[0];
        foreach (var area in areas)
        {
            union.Union(area);
        }

        return union;
    }

    /// <summary>与给定矩形重叠面积最大的那块屏的工作区；完全不重叠时取距离最近的。</summary>
    public static Rect WorkAreaFor(Rect physical)
    {
        var areas = WorkAreasPhysical();
        if (areas.Count == 0)
        {
            return physical;
        }

        Rect? best = null;
        var bestIntersection = 0.0;

        foreach (var area in areas)
        {
            var inter = Rect.Intersect(area, physical);
            var size = inter.IsEmpty ? 0 : inter.Width * inter.Height;
            if (size > bestIntersection)
            {
                bestIntersection = size;
                best = area;
            }
        }

        if (best is not null)
        {
            return best.Value;
        }

        foreach (var area in areas)
        {
            if (Distance(area, physical) < Distance(best ?? areas[0], physical))
            {
                best = area;
            }
        }

        return best ?? areas[0];
    }

    /// <summary>窗口的物理矩形。</summary>
    public static Rect GetPhysicalRect(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero || !GetWindowRect(handle, out var r))
        {
            return new Rect(window.Left, window.Top, window.Width, window.Height);
        }

        return new Rect(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top);
    }

    /// <summary>把窗口整体挪到指定的物理位置（不改大小、不激活、不改变 Z 序）。</summary>
    public static void MoveToPhysical(Window window, Rect physical)
    {
        /*
         * 窗口还没显示过时 HWND 是 0，SetWindowPos 直接无事发生，窗口随后会出现在系统的
         * 默认级联位置（实测是 (177,177)）——功能栏第一次右键就跑到左上角去，与角色分家。
         * EnsureHandle 强制先把 HWND 建出来，位置才落得下去。
         */
        var helper = new WindowInteropHelper(window);
        var handle = helper.Handle;
        if (handle == IntPtr.Zero)
        {
            try
            {
                handle = helper.EnsureHandle();
            }
            catch
            {
                handle = IntPtr.Zero;
            }
        }

        if (handle == IntPtr.Zero)
        {
            return;
        }

        SetWindowPos(
            handle,
            IntPtr.Zero,
            (int)Math.Round(physical.Left),
            (int)Math.Round(physical.Top),
            0,
            0,
            SwpNoZOrder | SwpNoActivate | 0x0001 /* SWP_NOSIZE */);
    }

    /// <summary>
    /// 软钳制：只保证窗口至少有 <paramref name="keepRatio"/> 的比例落在某块屏的工作区内。
    ///
    /// 与"必须整窗可见"的区别：拖动时若强制整窗可见，贴边拖动会先跑出去、松手又被拉回来，
    /// 表现为松手瞬间"跳一下"。
    /// </summary>
    public static Rect ClampKeepVisible(Rect physical, double keepRatio = 0.3)
    {
        var area = WorkAreaFor(physical);

        var keepX = Math.Min(physical.Width * keepRatio, physical.Width);
        var keepY = Math.Min(physical.Height * keepRatio, physical.Height);

        var left = Math.Clamp(physical.Left, area.Left - physical.Width + keepX, area.Right - keepX);
        var top = Math.Clamp(physical.Top, area.Top - physical.Height + keepY, area.Bottom - keepY);

        return new Rect(left, top, physical.Width, physical.Height);
    }

    /// <summary>要求整窗落在某块屏的工作区内（改大小时用）。</summary>
    public static Rect ClampInside(Rect physical)
    {
        var area = WorkAreaFor(physical);

        var left = Math.Clamp(physical.Left, area.Left, Math.Max(area.Left, area.Right - physical.Width));
        var top = Math.Clamp(physical.Top, area.Top, Math.Max(area.Top, area.Bottom - physical.Height));

        return new Rect(left, top, physical.Width, physical.Height);
    }

    private static double Distance(Rect a, Rect b)
    {
        var dx = Math.Max(0, Math.Max(a.Left - b.Right, b.Left - a.Right));
        var dy = Math.Max(0, Math.Max(a.Top - b.Bottom, b.Top - a.Bottom));
        return dx + dy;
    }

    // ------------------------------------------------------------------ Win32

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int cbSize;

        // rcMonitor / dwFlags 本类不读取，但**不能删**：
        // GetMonitorInfo 会校验 cbSize 是否等于原生 MONITORINFO（40 字节），
        // 且 rcWork 的偏移由这两个字段决定 —— 删掉字段会让查询直接失败。
        public NativeRect rcMonitor;
        public NativeRect rcWork;
        public uint dwFlags;
    }

    private delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdc, IntPtr rect, IntPtr data);

    [DllImport("user32.dll")]
    private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc proc, IntPtr data);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out NativeRect rect);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
}