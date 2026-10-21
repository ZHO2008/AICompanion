using System.Runtime.InteropServices;
using System.Text;
using AICompanion.Core.Abstractions;
using AICompanion.Core.Configuration;

namespace AICompanion.Core.Asr;

/// <summary>
/// whisper.cpp 语音识别器。
///
/// 通过 <c>ai_asr.dll</c>（见 third_party/ai_asr/ai_asr_shim.h）访问 whisper.cpp，
/// 而**不是**直接 P/Invoke whisper.dll。这是本项目一个明确的设计决策：
///
///   whisper_full_params 有 40 多个字段且内嵌匿名结构体与函数指针，
///   在 C# 里手工声明它的布局一旦有偏差，不会编译报错，而是运行时破坏调用栈。
///   把这一层用 C 写、用真实头文件编译，ABI 风险就被 C 编译器彻底吸收，
///   C# 侧只需要面对下面这 5 个参数全是基本类型的函数。
///
/// 线程：<see cref="TranscribeAsync"/> 内部走 Task.Run，
/// 因为 whisper_full 是长时间 CPU/GPU 计算，绝不能在调用方线程（高优先级音频线程或 UI 线程）上同步执行。
/// </summary>
public sealed class WhisperCppRecognizer : ISpeechRecognizer
{
    /// <summary>输出文本缓冲区大小（UTF-8 字节）。一段语音的转写结果远小于这个值。</summary>
    private const int OutputCapacity = 8192;

    private readonly AppConfig _config;
    private readonly IAppLogger _logger;
    private readonly string _libraryPath;
    private readonly string _modelPath;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private IntPtr _library;
    private AiAsrInitDelegate? _init;
    private AiAsrTranscribeDelegate? _transcribe;
    private AiAsrResetDelegate? _reset;
    private AiAsrShutdownDelegate? _shutdown;
    private AiAsrSystemInfoDelegate? _systemInfo;

    private bool _ready;
    private string? _statusMessage;
    private bool _disposed;

    private WhisperCppRecognizer(AppConfig config, IAppLogger logger, string libraryPath, string modelPath)
    {
        _config = config;
        _logger = logger;
        _libraryPath = libraryPath;
        _modelPath = modelPath;
    }

    public bool IsReady => _ready;

    public string? StatusMessage => _statusMessage;

    /// <summary>创建并初始化识别器；失败时返回 null 并给出原因（调用方据此降级到文字输入）。</summary>
    public static WhisperCppRecognizer? TryCreate(
        AppConfig config,
        string libraryPath,
        string modelPath,
        IAppLogger logger,
        out string? error)
    {
        var recognizer = new WhisperCppRecognizer(config, logger, libraryPath, modelPath);

        if (!recognizer.Initialize(out error))
        {
            recognizer.Dispose();
            return null;
        }

        return recognizer;
    }

    private bool Initialize(out string? error)
    {
        error = null;

        try
        {
            _library = NativeLibrary.Load(_libraryPath);

            _init = GetExport<AiAsrInitDelegate>("ai_asr_init");
            _transcribe = GetExport<AiAsrTranscribeDelegate>("ai_asr_transcribe");
            _reset = GetExport<AiAsrResetDelegate>("ai_asr_reset");
            _shutdown = GetExport<AiAsrShutdownDelegate>("ai_asr_shutdown");
            _systemInfo = GetExport<AiAsrSystemInfoDelegate>("ai_asr_system_info");

            var info = Marshal.PtrToStringUTF8(_systemInfo()) ?? "（无系统信息）";
            _logger.Info($"whisper.cpp 已加载：{info}");

            // 线程数：0 表示交给 shim 自动决定（shim 默认 4）。
            var threads = _config.Asr.Threads > 0 ? _config.Asr.Threads : 0;

            var errorBuffer = new byte[512];
            var rc = _init(_modelPath, threads, useGpu: 0, errorBuffer, errorBuffer.Length);

            if (rc != 0)
            {
                var message = DecodeError(errorBuffer);
                error = $"ai_asr_init 返回 {rc}：{message}";
                _statusMessage = error;
                return false;
            }

            _ready = true;
            _statusMessage = "就绪";
            _logger.Info($"whisper 模型已加载：{_modelPath}（线程 {threads}，CPU 模式）");
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            _statusMessage = ex.Message;
            _logger.Error($"初始化 whisper.cpp 失败：{ex.Message}", ex);
            return false;
        }
    }

    private T GetExport<T>(string name) where T : Delegate
    {
        if (!NativeLibrary.TryGetExport(_library, name, out var address) || address == IntPtr.Zero)
        {
            throw new EntryPointNotFoundException(
                $"在 {_libraryPath} 中找不到导出函数 {name}。请确认 ai_asr.dll 是配套版本。");
        }

        return Marshal.GetDelegateForFunctionPointer<T>(address);
    }

    public async Task<string> TranscribeAsync(
        float[] samples,
        int sampleRate,
        CancellationToken cancellationToken = default)
    {
        if (!_ready || _transcribe is null)
        {
            throw new InvalidOperationException(_statusMessage ?? "语音识别器未就绪。");
        }

        if (samples.Length == 0)
        {
            return string.Empty;
        }

        if (sampleRate != 16000)
        {
            // whisper 只接受 16kHz。与其静默产出错误结果，不如明确报错。
            throw new NotSupportedException($"whisper 只支持 16000Hz 采样，当前为 {sampleRate}Hz。");
        }

        // 串行化：llama 的 whisper 上下文不是线程安全的，同时只允许一个识别在跑。
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await Task.Run(() => Transcribe(samples, cancellationToken), cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private string Transcribe(float[] samples, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var output = new byte[OutputCapacity];
        var language = string.IsNullOrWhiteSpace(_config.Asr.Language) ? null : _config.Asr.Language;

        var pinned = GCHandle.Alloc(samples, GCHandleType.Pinned);
        try
        {
            var rc = _transcribe!(
                pinned.AddrOfPinnedObject(),
                samples.Length,
                language,
                translate: 0,
                output,
                output.Length);

            if (rc < 0)
            {
                throw new InvalidOperationException($"ai_asr_transcribe 返回 {rc}（识别失败）。");
            }

            var text = DecodeText(output, rc);
            return text.Trim();
        }
        finally
        {
            pinned.Free();
        }
    }

    public void Reset()
    {
        try
        {
            _reset?.Invoke();
        }
        catch (Exception ex)
        {
            _logger.Warn($"重置 whisper 状态失败：{ex.Message}");
        }
    }

    private static string DecodeText(byte[] buffer, int length)
    {
        if (length <= 0)
        {
            return string.Empty;
        }

        var usable = Math.Min(length, buffer.Length);

        // 以第一个 0 结尾为准，避免把缓冲区尾部的脏数据读进来。
        var end = Array.IndexOf(buffer, (byte)0, 0, usable);
        if (end < 0)
        {
            end = usable;
        }

        return Encoding.UTF8.GetString(buffer, 0, end);
    }

    private static string DecodeError(byte[] buffer)
    {
        var end = Array.IndexOf(buffer, (byte)0);
        if (end < 0)
        {
            end = buffer.Length;
        }

        var text = Encoding.UTF8.GetString(buffer, 0, end).Trim();
        return string.IsNullOrEmpty(text) ? "（无错误详情）" : text;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _ready = false;

        try
        {
            _shutdown?.Invoke();
        }
        catch (Exception ex)
        {
            _logger.Warn($"释放 whisper 上下文失败：{ex.Message}");
        }

        /*
         * 故意**不**调用 NativeLibrary.Free。
         *
         * 实测证据（tools/ExportProbe）：
         *   调用 NativeLibrary.Free 后进程退出码为 0xC0000005（ACCESS_VIOLATION）；
         *   去掉这一行后退出码为 0。
         *
         * 原因是 ai_asr.dll 静态链接了 C 运行库与 ggml 的全局注册表，
         * 卸载动态库会触发这些静态对象的析构，而析构顺序不受控，于是踩到已释放内存。
         * 卸载原生库本来就不是退出流程的必需品：进程结束时操作系统会统一回收。
         * 因此这里只释放 whisper 上下文（ai_asr_shutdown），库句柄交给进程退出处理。
         */
        _library = IntPtr.Zero;

        _gate.Dispose();
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int AiAsrInitDelegate(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string modelPath,
        int nThreads,
        int useGpu,
        byte[] error,
        int errorCapacity);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int AiAsrTranscribeDelegate(
        IntPtr samples,
        int nSamples,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string? language,
        int translate,
        byte[] outText,
        int outCapacity);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void AiAsrResetDelegate();

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void AiAsrShutdownDelegate();

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate IntPtr AiAsrSystemInfoDelegate();
}
