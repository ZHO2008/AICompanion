using System;
using System.Runtime.InteropServices;

namespace AICompanion.App;

/// <summary>
/// 把本进程放进一个"随宿主关闭即杀子进程"的 Job Object。
///
/// 为什么需要：llama-server 与 GameMonitor 都是本程序拉起的子进程，正常退出时
/// AppHost.DisposeAsync 会显式杀掉 llama-server。但如果宿主是被强杀的
/// （任务管理器结束任务、崩溃、注销，或开发期直接 Stop-Process），
/// 清理代码根本不会执行，llama-server 就变成孤儿常驻，继续占着几 GB 显存 ——
/// 用户下次启动会看到"显存不足，切 CPU 模式"，实际是自己上次留下的进程。
/// 实测连续几次强杀后攒了 10 个 llama-server。
///
/// 放进 Job 之后由内核兜底：宿主一死（包括被强杀），Job 关闭，
/// JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE 会把 Job 里的进程一起收掉。
/// </summary>
internal static class ChildProcessJob
{
    private const int JobObjectExtendedLimitInformation = 9;
    private const uint JobObjectLimitKillOnJobClose = 0x2000;

    private static IntPtr _job;

    /// <summary>创建并绑定 Job。失败不抛异常：这只是兜底，不该挡住启动。</summary>
    public static bool TryAttach(Action<string>? onWarning = null)
    {
        try
        {
            if (_job != IntPtr.Zero)
            {
                return true;
            }

            // 调试时不要挂 Job：断点暂停超过限制时间、或重启调试都容易被连带杀掉，很碍事。
            if (System.Diagnostics.Debugger.IsAttached)
            {
                onWarning?.Invoke("检测到调试器，本次不启用子进程 Job 兜底。");
                return false;
            }

            var job = CreateJobObject(IntPtr.Zero, null);
            if (job == IntPtr.Zero)
            {
                onWarning?.Invoke($"创建 Job 失败：Win32 错误 {Marshal.GetLastWin32Error()}");
                return false;
            }

            var info = new JobObjectExtendedLimitInformation2
            {
                BasicLimitInformation = new JobObjectBasicLimitInformation
                {
                    LimitFlags = JobObjectLimitKillOnJobClose,
                },
            };

            var size = Marshal.SizeOf<JobObjectExtendedLimitInformation2>();
            var ptr = Marshal.AllocHGlobal(size);

            try
            {
                Marshal.StructureToPtr(info, ptr, fDeleteOld: false);

                if (!SetInformationJobObject(job, JobObjectExtendedLimitInformation, ptr, (uint)size))
                {
                    onWarning?.Invoke($"设置 Job 限制失败：Win32 错误 {Marshal.GetLastWin32Error()}");
                    return false;
                }
            }
            finally
            {
                Marshal.FreeHGlobal(ptr);
            }

            if (!AssignProcessToJobObject(job, GetCurrentProcess()))
            {
                // 已经有 Job（例如被父进程托管）时这里会失败，属于正常情况。
                onWarning?.Invoke($"把本进程加入 Job 失败：Win32 错误 {Marshal.GetLastWin32Error()}（可能已被父 Job 托管）");
                return false;
            }

            _job = job;
            return true;
        }
        catch (Exception ex)
        {
            onWarning?.Invoke($"启用子进程 Job 兜底失败：{ex.Message}");
            return false;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectBasicLimitInformation
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectExtendedLimitInformation2
    {
        public JobObjectBasicLimitInformation BasicLimitInformation;
        public IoCounters IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateJobObject(IntPtr securityAttributes, string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetInformationJobObject(
        IntPtr job,
        int infoClass,
        IntPtr info,
        uint infoLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();
}