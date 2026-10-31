// WPF 项目的隐式 using 刻意去掉了 System.IO（避免 System.IO.Path 与 System.Windows.Shapes.Path 撞名），
// 这里显式引一次。
using System.IO;
using Microsoft.Win32;

namespace AICompanion.App.Services;

/// <summary>写显卡偏好这一步的结果。</summary>
public enum GpuPreferenceState
{
    /// <summary>还没有尝试（判定尚未进行）。</summary>
    NotAttempted = 0,

    /// <summary>本次写入成功，重启后生效。</summary>
    Written = 1,

    /// <summary>本来就写着相同的值（可能是上次本程序写的），不需要重复写。</summary>
    AlreadyHighPerformance = 2,

    /// <summary>注册表里已有其他值 —— 判定为用户手动设置，绝不覆盖。</summary>
    SetByUser = 3,

    /// <summary>写入失败（权限/异常），已降级为提示。</summary>
    WriteFailed = 4,
}

/// <summary><see cref="UserGpuPreferences.EnsureHighPerformance"/> 的返回值。</summary>
public sealed record GpuPreferenceOutcome(GpuPreferenceState State, string ValuePath, string ExistingValue, string? Error)
{
    public bool Succeeded => State is GpuPreferenceState.Written or GpuPreferenceState.AlreadyHighPerformance;
}

/// <summary>
/// <c>HKCU\Software\Microsoft\DirectX\UserGpuPreferences</c> 的读写。
///
/// 这个键就是「设置 → 系统 → 屏幕 → 显示卡 → 图形设置」把某个程序设成「高性能」时
/// Windows 自己写的那一项：值名是程序的**完整 exe 路径**，值形如 <c>GpuPreference=2;</c>
/// （2 = 高性能，1 = 省电，0 = 让 Windows 决定）。
/// 之前只能让用户去图形设置里手点，正是因为这一步没有做。
///
/// 三条纪律：
///   1. 只写 HKCU —— 不需要管理员，也不会污染其它用户；
///   2. 先读后写：已存在**其它**值就认为用户手动设过，直接跳过（绝不覆盖用户的决定）；
///   3. 失败只返回错误文本，绝不抛异常、绝不影响启动。
/// </summary>
public static class UserGpuPreferences
{
    /// <summary>HKCU 下的显卡偏好键。图形设置界面写的也是这里。</summary>
    public const string KeyPath = @"Software\Microsoft\DirectX\UserGpuPreferences";

    /// <summary>「高性能」的取值。带分号是 Windows 自己的写法（多个设置以分号分隔）。</summary>
    public const string HighPerformanceValue = "GpuPreference=2;";

    /// <summary>日志/气泡里回显既有值时的长度上限（注册表值最长 16 K，原样回显会把界面撑坏）。</summary>
    private const int MaxDisplayValueLength = 120;

    /// <summary>读当前值（不存在返回空串）。读失败也返回空串 —— 需要区分两者时用 <see cref="TryRead"/>。</summary>
    public static string Read(string exePath)
        => TryRead(exePath, out var value) ? value : string.Empty;

    /// <summary>
    /// 读当前值，并区分"确实没有值"与"读失败"（键被 ACL/策略锁死）。
    ///
    /// 为什么必须区分：以前读失败与"没有值"都返回空串，于是
    /// <see cref="EnsureHighPerformance"/> 会认为"用户没设过"直接写入 <c>GpuPreference=2;</c>
    /// —— 读不到的那一刻正好把用户手动设的「省电」覆盖掉。现在读失败 → 不写。
    /// </summary>
    public static bool TryRead(string exePath, out string value)
    {
        value = string.Empty;

        if (string.IsNullOrWhiteSpace(exePath))
        {
            return false;
        }

        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(KeyPath);

            // 键不存在不算失败：这就是"从没设过"的正常状态。
            if (key is null)
            {
                return true;
            }

            value = (key.GetValue(exePath) as string)?.Trim() ?? string.Empty;
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>把可能很长的注册表值压成一行可显示文本（日志与气泡共用，避免两处各写一份截断）。</summary>
    public static string ShortenForDisplay(string? value, int max = MaxDisplayValueLength)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        return value.Length <= max ? value : value[..max] + "…";
    }

    /// <summary>判断一个既存值是否已经等价于「高性能」。</summary>
    public static bool LooksLikeHighPerformance(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        /*
         * Windows 可能写成 "GpuPreference=2" / "GpuPreference=2;" / 前后带空格；
         * 也可能在同一个值里带上别的设置（实测有 "GpuPreference=2;SwapChainBufferCount=3;"）。
         * 以前整串相等才认，遇到后者会误判成"用户手动设的"从而永远不自动修复。
         * 现在按 ';' 切开逐项比，空白容错与原来一致。
         */
        foreach (var token in value.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            if (string.Equals(token.Replace(" ", string.Empty), "GpuPreference=2", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// 确保该 exe 的显卡偏好是「高性能」：已相同则不写，写别的值（用户手动设的）则跳过。
    /// 本方法不抛异常。
    /// </summary>
    public static GpuPreferenceOutcome EnsureHighPerformance(string exePath)
    {
        if (string.IsNullOrWhiteSpace(exePath))
        {
            return new GpuPreferenceOutcome(
                GpuPreferenceState.WriteFailed, string.Empty, string.Empty, "取不到程序自身的完整路径");
        }

        /*
         * 先读后写，且必须区分"真的没有值"与"读失败"：
         * 读失败时若当成"用户没设过"直接写入 GpuPreference=2;，
         * 正好会把用户手动设的「省电」覆盖掉（读不到的那一刻就是覆盖的时刻）。
         */
        if (!TryRead(exePath, out var existing))
        {
            return new GpuPreferenceOutcome(
                GpuPreferenceState.WriteFailed,
                exePath,
                string.Empty,
                "读取现有显卡偏好失败；为避免覆盖你手动设置的显卡偏好，本次没有写入");
        }

        if (LooksLikeHighPerformance(existing))
        {
            return new GpuPreferenceOutcome(GpuPreferenceState.AlreadyHighPerformance, exePath, existing, null);
        }

        if (!string.IsNullOrEmpty(existing))
        {
            // 有值但不是「高性能」：可能是用户在图形设置里明确选了「省电」，不能覆盖。
            return new GpuPreferenceOutcome(GpuPreferenceState.SetByUser, exePath, existing, null);
        }

        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(KeyPath, writable: true);

            if (key is null)
            {
                return new GpuPreferenceOutcome(
                    GpuPreferenceState.WriteFailed, exePath, string.Empty, "无法打开/创建注册表键");
            }

            key.SetValue(exePath, HighPerformanceValue, RegistryValueKind.String);

            // 写完立刻回读确认：注册表虚拟化、策略拦截都可能让 SetValue 静默不生效。
            var written = Read(exePath);

            if (!LooksLikeHighPerformance(written))
            {
                return new GpuPreferenceOutcome(
                    GpuPreferenceState.WriteFailed, exePath, written, "写入后回读的值不是 GpuPreference=2;");
            }

            return new GpuPreferenceOutcome(GpuPreferenceState.Written, exePath, written, null);
        }
        catch (Exception ex)
        {
            return new GpuPreferenceOutcome(GpuPreferenceState.WriteFailed, exePath, string.Empty, ex.Message);
        }
    }

    /// <summary>本进程的完整 exe 路径（写进注册表的值名）。取不到返回空串。</summary>
    public static string CurrentExecutablePath()
    {
        try
        {
            // Environment.ProcessPath 在 .NET 6+ 上是 apphost 的真实路径，
            // 比 MainModule.FileName 稳（后者在某些沙箱/权限场景会抛）。
            var path = Environment.ProcessPath;

            if (!string.IsNullOrWhiteSpace(path))
            {
                return Path.GetFullPath(path);
            }
        }
        catch (Exception)
        {
            // 落到下面的兜底。
        }

        try
        {
            // Process 持有进程句柄：用完必须 Dispose，否则每次兜底都漏一个句柄。
            using var process = System.Diagnostics.Process.GetCurrentProcess();
            var module = process.MainModule?.FileName;

            if (!string.IsNullOrWhiteSpace(module))
            {
                return Path.GetFullPath(module);
            }
        }
        catch (Exception)
        {
            // 实在取不到就返回空串，由调用方记为"写入失败"并给出手动步骤。
        }

        return string.Empty;
    }
}
