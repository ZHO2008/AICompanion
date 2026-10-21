using System.Runtime.InteropServices;

namespace AICompanion.Core.Tts;

public enum PiperNativeProbe
{
    /// <summary>配置里指向的动态库不存在。</summary>
    LibraryNotFound = 0,

    /// <summary>文件存在但加载失败（位数不匹配、缺依赖等）。</summary>
    LoadFailed = 1,

    /// <summary>库已成功加载，可以进入签名对接阶段。</summary>
    Loaded = 2,
}

/// <summary>
/// Piper C API 的原生库探测。
///
/// **重要说明（对应决策 Q9：Piper 本轮后置）**
///
/// 这里刻意**只做加载探测，不声明任何 P/Invoke 入口点**。原因是工程上的：
/// Piper 的 C ABI（libpiper 的 <c>piper_synthesizer</c> / <c>piper_create</c> /
/// <c>piper_synthesize</c> 系列）在不同版本之间字段与参数个数有差异，
/// 在没有对照"本机实际编译出来的头文件"之前就写死签名，
/// 最坏的结果不是编译报错，而是**运行时栈损坏**——这类问题极难定位。
///
/// 因此本轮的实现路径是：
///   1. 正式路径：<see cref="PiperNativeApi.Probe"/> 探测库是否就绪；
///   2. 可用路径：<see cref="PiperEngine"/> 走进程外 piper.exe（官方 Windows 发行版即可用）；
///   3. 待补路径：编译出 piper.dll 后，在此文件中按 <c>piper.h</c> 的真实声明补齐入口点，
///      并把 <see cref="PiperEngine"/> 的实现切到原生调用。
/// </summary>
public static class PiperNativeApi
{
    public static PiperNativeProbe Probe(string libraryPath, out string message)
    {
        if (string.IsNullOrWhiteSpace(libraryPath) || !File.Exists(libraryPath))
        {
            message = $"未找到 Piper 动态库：{libraryPath}";
            return PiperNativeProbe.LibraryNotFound;
        }

        var loaded = false;
        var handle = IntPtr.Zero;
        try
        {
            loaded = NativeLibrary.TryLoad(libraryPath, out handle);
        }
        catch (Exception ex)
        {
            message = $"加载 Piper 动态库失败：{ex.Message}";
            return PiperNativeProbe.LoadFailed;
        }

        if (!loaded || handle == IntPtr.Zero)
        {
            message = $"Piper 动态库无法加载（可能是位数不匹配或缺少依赖）：{libraryPath}";
            return PiperNativeProbe.LoadFailed;
        }

        /*
         * 探测成功后**不**立即 NativeLibrary.Free。
         *
         * 本项目在 whisper 桥接层上实测过：对静态链接了 C 运行库的原生库调用
         * NativeLibrary.Free，会在进程退出时以 0xC0000005（ACCESS_VIOLATION）崩溃
         * （见 tools/ExportProbe 的对照实验）。
         * 探测的代价只是持有一次加载句柄，而崩溃的代价是用户看到程序莫名退出，
         * 因此这里选择不卸载。
         */
        message = "Piper 动态库已可加载（托管入口点签名将在库编译完成后补齐）。";
        return PiperNativeProbe.Loaded;
    }
}
