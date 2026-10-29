using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

/// <summary>
/// ai_asr.dll 的功能性验证程序。
/// 只做一件事：用与主程序**完全相同**的 P/Invoke 契约（NativeLibrary + 函数指针）
/// 真实加载 whisper 模型并跑一次识别，证明桥接层的 ABI 是对的。
/// 这是对"不在 C# 里手工猜 whisper_full_params 布局"这一决策的直接验证。
/// </summary>
internal static class Probe
{
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int InitDelegate(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string modelPath, int nThreads, int useGpu, byte[] error, int errorCapacity);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int TranscribeDelegate(
        IntPtr samples, int nSamples, [MarshalAs(UnmanagedType.LPUTF8Str)] string language,
        int translate, byte[] outText, int outCapacity);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void VoidDelegate();

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate IntPtr SystemInfoDelegate();

    private static int Main(string[] args)
    {
        var root = Environment.GetEnvironmentVariable("AICOMPANION_ROOT")
                   ?? Directory.GetCurrentDirectory();
        var dll = args.Length > 0 ? args[0] : Path.Combine(root, "bin", "ai_asr.dll");
        var model = args.Length > 1 ? args[1] : Path.Combine(root, "models", "asr", "ggml-base.bin");
        Console.WriteLine("=== ai_asr.dll functional verification ===");
        Console.WriteLine($"dll   : {dll}");
        Console.WriteLine($"model : {model}");
        Console.WriteLine();

        if (!System.IO.File.Exists(dll)) { Console.WriteLine("[FAIL] dll not found"); return 1; }
        if (!System.IO.File.Exists(model)) { Console.WriteLine("[FAIL] model not found"); return 1; }

        var lib = NativeLibrary.Load(dll);
        var init = Get<InitDelegate>(lib, "ai_asr_init");
        var transcribe = Get<TranscribeDelegate>(lib, "ai_asr_transcribe");
        var reset = Get<VoidDelegate>(lib, "ai_asr_reset");
        var shutdown = Get<VoidDelegate>(lib, "ai_asr_shutdown");
        var sysinfo = Get<SystemInfoDelegate>(lib, "ai_asr_system_info");

        var info = Marshal.PtrToStringUTF8(sysinfo()) ?? "(null)";
        Console.WriteLine($"[OK] ai_asr_system_info -> {info.Split('\n')[0]}");

        // ---- 1. initialise (really loads the 141MB model) ----
        var sw = Stopwatch.StartNew();
        var err = new byte[512];
        var rc = init(model, 4, 0, err, err.Length);
        sw.Stop();

        if (rc != 0)
        {
            Console.WriteLine($"[FAIL] ai_asr_init rc={rc} err={Decode(err)}");
            return 2;
        }
        Console.WriteLine($"[OK] ai_asr_init rc=0, model loaded in {sw.ElapsedMilliseconds} ms");

        // ---- 2. transcribe near-silence; the point is that it does not crash ----
        const int seconds = 2;
        const int sampleRate = 16000;
        var samples = new float[sampleRate * seconds];
        for (var i = 0; i < samples.Length; i++)
        {
            samples[i] = (float)(Math.Sin(i * 0.01) * 0.0005);
        }

        var outBuf = new byte[8192];
        var pinned = GCHandle.Alloc(samples, GCHandleType.Pinned);
        try
        {
            sw.Restart();
            rc = transcribe(pinned.AddrOfPinnedObject(), samples.Length, "zh", 0, outBuf, outBuf.Length);
            sw.Stop();
        }
        finally
        {
            pinned.Free();
        }

        if (rc < 0)
        {
            Console.WriteLine($"[FAIL] ai_asr_transcribe rc={rc}");
            return 3;
        }

        Console.WriteLine($"[OK] ai_asr_transcribe rc={rc} (text: \"{DecodeN(outBuf, rc)}\")");
        Console.WriteLine($"     2s audio took {sw.ElapsedMilliseconds} ms, RTF={(sw.Elapsed.TotalSeconds / seconds):N3}");

        // ---- 3. reset / shutdown ----
        reset();
        Console.WriteLine("[OK] ai_asr_reset");

        shutdown();
        Console.WriteLine("[OK] ai_asr_shutdown");

        // 默认行为与主程序 WhisperCppRecognizer.Dispose() 保持一致：**不**卸载原生库。
        //
        // 对照实验（可复现）：
        //   本程序 withfree  -> 退出码 0xC0000005 (ACCESS_VIOLATION)
        //   本程序 默认      -> 退出码 0
        // 原因：ai_asr.dll 静态链接了 C 运行库与 ggml 全局注册表，
        // 卸载动态库会触发静态对象析构，顺序不受控，因而踩到已释放内存。
        var callFree = args.Length > 2 && args[2].Equals("withfree", StringComparison.OrdinalIgnoreCase);
        if (callFree)
        {
            Console.WriteLine("[info] calling NativeLibrary.Free (control experiment: expected ACCESS_VIOLATION on exit)");
            NativeLibrary.Free(lib);
        }
        else
        {
            Console.WriteLine("[info] not unloading the native library (same as the app; safe)");
        }

        Console.WriteLine();
        Console.WriteLine("=== ALL PASSED: shim ABI and whisper inference path both work ===");
        return 0;
    }

    private static T Get<T>(IntPtr lib, string name) where T : Delegate
    {
        if (!NativeLibrary.TryGetExport(lib, name, out var addr) || addr == IntPtr.Zero)
        {
            throw new EntryPointNotFoundException(name);
        }

        return Marshal.GetDelegateForFunctionPointer<T>(addr);
    }

    private static string Decode(byte[] buf)
    {
        var end = Array.IndexOf(buf, (byte)0);
        if (end < 0) { end = buf.Length; }
        return Encoding.UTF8.GetString(buf, 0, end).Trim();
    }

    private static string DecodeN(byte[] buf, int len)
    {
        if (len <= 0) { return string.Empty; }
        var usable = Math.Min(len, buf.Length);
        var end = Array.IndexOf(buf, (byte)0, 0, usable);
        if (end < 0) { end = usable; }
        return Encoding.UTF8.GetString(buf, 0, end).Trim();
    }
}
