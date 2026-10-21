using System.Runtime.InteropServices;

namespace AICompanion.Core.Interop;

/// <summary>
/// Win32 互操作声明。
/// 只使用 Windows 自带 API，不引入任何 NuGet 依赖（准则 8：隔离外部依赖）。
/// </summary>
internal static class NativeMethods
{
    [StructLayout(LayoutKind.Sequential)]
    internal struct FILETIME
    {
        public uint dwLowDateTime;
        public uint dwHighDateTime;

        public readonly ulong ToUInt64() => ((ulong)dwHighDateTime << 32) | dwLowDateTime;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct SYSTEM_POWER_STATUS
    {
        public byte ACLineStatus;
        public byte BatteryFlag;
        public byte BatteryLifePercent;
        public byte SystemStatusFlag;
        public uint BatteryLifeTime;
        public uint BatteryFullLifeTime;
    }

    /// <summary>
    /// 获取系统累计的 idle / kernel / user 时间，用于零依赖计算 CPU 使用率。
    /// 注意：kernel 时间**包含** idle 时间，计算时必须减掉。
    /// </summary>
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetSystemTimes(out FILETIME idleTime, out FILETIME kernelTime, out FILETIME userTime);

    /// <summary>电源状态；用来判断是否使用电池（休眠/待机检测的辅助信号）。</summary>
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetSystemPowerStatus(out SYSTEM_POWER_STATUS status);
}
