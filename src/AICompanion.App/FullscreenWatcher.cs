using System;
using System.Runtime.InteropServices;
using System.Text;

namespace AICompanion.App;

/// <summary>
/// 判断「前台是不是有个全屏程序」。
///
/// 为什么需要它：角色窗口是 Topmost 的，而且悬停切换是按**窗口矩形**判定的。
/// 用户开游戏或看视频全屏时，那块矩形正好压在画面上，于是光标扫过就把模式
/// 改成「覆盖」（可交互）—— 那一块会吃掉本该给游戏的点击；光标离开三秒又
/// 退回「透明」，看起来就是"模式自己在乱跳、切不动"。
///
/// 判定方式（不需要钩子，也不会被游戏的反作弊盯上，只是读窗口位置）：
///   1. 取前台窗口；
///   2. 排除自己的窗口、桌面/任务栏这类外壳窗口；
///   3. 比较前台窗口的矩形与它所在显示器的**物理**矩形，四边都覆盖即视为全屏。
///
/// 边界情况：无边框全屏（borderless）的窗口矩形等于显示器矩形，同样命中；
/// 独占全屏的窗口往往只有客户区被用来渲染，但 GetWindowRect 仍返回屏幕大小，也能命中。
/// 唯一识别不了的是一些把渲染交给 DWM、自身窗口只有 1×1 的极端实现，这类很少见。
/// </summary>
internal sealed class FullscreenWatcher
{
    /// <summary>当前是否处于「前台是全屏程序」状态。</summary>
    public bool IsActive { get; private set; }

    /// <summary>命中的窗口标题，仅用于日志。</summary>
    public string Title { get; private set; } = string.Empty;

    /// <summary>
    /// 重新采样一次。返回 <c>true</c> 表示状态发生了变化（进入或退出全屏）。
    /// </summary>
    public bool Poll()
    {
        var active = false;
        var title = string.Empty;

        try
        {
            var hwnd = GetForegroundWindow();
            if (hwnd != IntPtr.Zero && IsWindowVisible(hwnd) && !IsOwnWindow(hwnd) && !IsShellWindow(hwnd))
            {
                if (CoversItsMonitor(hwnd))
                {
                    active = true;
                    title = GetTitle(hwnd);
                }
            }
        }
        catch
        {
            // 读窗口信息失败就当没有全屏：宁可多一次悬停切换，也不要卡在"全屏中"状态出不来。
            active = false;
        }

        var changed = active != IsActive;
        IsActive = active;
        Title = title;
        return changed;
    }

    private static bool IsOwnWindow(IntPtr hwnd)
    {
        GetWindowThreadProcessId(hwnd, out var pid);
        return pid == (uint)Environment.ProcessId;
    }

    private static bool IsShellWindow(IntPtr hwnd)
    {
        // 桌面、任务栏、开始菜单这些也会"铺满屏幕"，但它们不是用户要躲的全屏程序。
        try
        {
            var cls = new StringBuilder(64);
            GetClassName(hwnd, cls, cls.Capacity);
            var name = cls.ToString();
            return name is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd"
                or "Windows.UI.Core.CoreWindow" or "MultitaskingViewFrame";
        }
        catch
        {
            return false;
        }
    }

    private static bool CoversItsMonitor(IntPtr hwnd)
    {
        if (!GetWindowRect(hwnd, out var rect))
        {
            return false;
        }

        var monitor = MonitorFromWindow(hwnd, MonitorDefaultToNearest);
        if (monitor == IntPtr.Zero)
        {
            return false;
        }

        var info = new MonitorInfo { cbSize = Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(monitor, ref info))
        {
            return false;
        }

        var screen = info.rcMonitor;

        // 允许 1~2 像素误差：某些驱动会在边框上吃掉一行。
        return rect.Left <= screen.Left + 2
            && rect.Top <= screen.Top + 2
            && rect.Right >= screen.Right - 2
            && rect.Bottom >= screen.Bottom - 2;
    }

    private static string GetTitle(IntPtr hwnd)
    {
        var sb = new StringBuilder(256);
        GetWindowText(hwnd, sb, sb.Capacity);
        var text = sb.ToString().Trim();
        return text.Length > 60 ? text[..60] + "…" : text;
    }

    // ------------------------------------------------------------------ Win32

    private const uint MonitorDefaultToNearest = 2;

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
        public NativeRect rcMonitor;
        public NativeRect rcWork;
        public uint dwFlags;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out NativeRect rect);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int count);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hWnd, StringBuilder className, int count);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);
}