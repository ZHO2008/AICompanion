using System;
using System.Threading;

namespace AICompanion.App;

/// <summary>
/// 单实例保护。
///
/// 为什么需要：程序会自己拉起 llama-server（GPU 常驻）并监听 GameMonitor 的端口。
/// 双击两次图标就等于两套 llama-server 抢同一块显存、两套服务抢同一个端口 ——
/// 实测第二个实例虽然能起，但 GameMonitor 通信直接失败，显存也被两份模型吃掉。
/// 桌宠这种东西用户很容易重复点，必须有保护。
///
/// 第二次启动不再静默退出：它会唤醒已在运行的那个实例把窗口显示出来，
/// 用户看到的结果才符合"点了图标，角色出现"的直觉。
/// </summary>
internal static class SingleInstance
{
    private const string MutexName = @"Local\AICompanion.Desktop.SingleInstance";
    private const string ActivateEventName = @"Local\AICompanion.Desktop.Activate";

    private static Mutex? _mutex;
    private static EventWaitHandle? _activateEvent;
    private static Thread? _listener;

    /// <summary>本进程是否是第一个实例（在 <see cref="Acquire"/> 之后有效）。</summary>
    public static bool IsFirst { get; private set; }

    /// <summary>尝试取得单实例所有权。</summary>
    public static void Acquire()
    {
        try
        {
            _mutex = new Mutex(initiallyOwned: true, MutexName, out var createdNew);
            IsFirst = createdNew;

            if (!createdNew)
            {
                // 已经有人持有了：留个句柄供 SignalExisting 用。
                _activateEvent = TryOpenActivateEvent();
            }
        }
        catch
        {
            // 拿不到互斥体（权限/命名冲突）时按"第一个实例"处理：
            // 宁可多开一个，也不要因为保护机制本身把人挡在门外。
            IsFirst = true;
        }
    }

    /// <summary>第二个实例调用：唤醒已在运行的那个实例。</summary>
    public static void SignalExisting()
    {
        try
        {
            _activateEvent ??= TryOpenActivateEvent();
            _activateEvent?.Set();
        }
        catch
        {
            // 唤醒失败不影响退出。
        }
    }

    /// <summary>第一个实例调用：起一个后台线程等唤醒信号。</summary>
    public static void StartListener(Action onActivate)
    {
        if (!IsFirst || onActivate is null)
        {
            return;
        }

        try
        {
            _activateEvent ??= new EventWaitHandle(false, EventResetMode.AutoReset, ActivateEventName);

            _listener = new Thread(() =>
            {
                while (true)
                {
                    try
                    {
                        if (!_activateEvent!.WaitOne())
                        {
                            return;
                        }

                        onActivate();
                    }
                    catch (ObjectDisposedException)
                    {
                        return;
                    }
                    catch
                    {
                        // 单次唤醒失败不该结束监听。
                    }
                }
            })
            {
                IsBackground = true,
                Name = "SingleInstanceListener",
            };

            _listener.Start();
        }
        catch
        {
            // 监听起不来只是"点第二下不会自动显示"，不影响主功能。
        }
    }

    /// <summary>退出时释放。</summary>
    public static void Release()
    {
        try
        {
            _activateEvent?.Dispose();
        }
        catch
        {
            // 忽略。
        }

        try
        {
            if (IsFirst)
            {
                _mutex?.ReleaseMutex();
            }
        }
        catch
        {
            // 忽略。
        }

        try
        {
            _mutex?.Dispose();
        }
        catch
        {
            // 忽略。
        }

        _activateEvent = null;
        _mutex = null;
    }

    private static EventWaitHandle? TryOpenActivateEvent()
    {
        try
        {
            return EventWaitHandle.OpenExisting(ActivateEventName);
        }
        catch
        {
            return null;
        }
    }
}