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

    /// <summary>读当前值（不存在返回空串）。</summary>
    public static string Read(string exePath)
    {
        if (string.IsNullOrWhiteSpace(exePath))
        {
            return string.Empty;
        }

        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(KeyPath);
            return (key?.GetValue(exePath) as string)?.Trim() ?? string.Empty;
        }
        catch (Exception)
        {
            return string.Empty;
        }
    }

    /// <summary>判断一个既存值是否已经等价于「高性能」。</summary>
    public static bool LooksLikeHighPerformance(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        // Windows 可能写成 "GpuPreference=2" / "GpuPreference=2;" / 前后带空格，统一规模后再比。
        var normalized = value.Replace(" ", string.Empty).TrimEnd(';');

        return string.Equals(normalized, "GpuPreference=2", StringComparison.OrdinalIgnoreCase);
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

        var existing = Read(exePath);

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
            var module = System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName;

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
