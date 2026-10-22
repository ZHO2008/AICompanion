using System.Diagnostics;
using System.Text;
using AICompanion.Core.Abstractions;
using AICompanion.Core.Configuration;
using AICompanion.Core.Models;

namespace AICompanion.Core.Tts;

/// <summary>
/// Piper 引擎（手册 §3.4 的 PiperEngine）。
///
/// IsAvailable 按手册要求"通过检查模型文件是否存在来判断"。
///
/// 合成路径按决策 Q9 分两级：
///   1. 【本轮可用】进程外调用 piper.exe：文本从 stdin 输入，WAV 写到临时文件再读回；
///   2. 【待补】piper.dll 编译完成后改为 P/Invoke（见 <see cref="PiperNativeApi"/> 的说明）。
/// 三级音色兜底的第三级 "zh_CN-default" 也在这里落地：
/// 解析音色时若角色与全局默认都拿不到可用的模型文件，会用配置里的 piper.model_path 兜底。
/// </summary>
public sealed class PiperEngine : ITTSEngine
{
    private readonly AppConfig _config;
    private readonly IAppLogger _logger;
    private readonly IBubbleNotifier _bubble;

    private readonly string _configuredModelPath;
    private readonly string _libraryPath;
    private readonly string _executablePath;
    private readonly string _espeakDataPath;

    public PiperEngine(AppConfig config, IAppLogger logger, IBubbleNotifier bubble)
    {
        _config = config;
        _logger = logger.ForCategory("TTS.Piper");
        _bubble = bubble;

        _configuredModelPath = AppPaths.Resolve(config.Tts.Piper.ModelPath);
        _libraryPath = AppPaths.Resolve(config.Tts.Piper.LibraryPath);
        _executablePath = AppPaths.Resolve(config.Tts.Piper.ExecutablePath);
        _espeakDataPath = AppPaths.Resolve(config.Tts.Piper.EspeakDataPath);

        var probe = PiperNativeApi.Probe(_libraryPath, out var message);
        NativeLibraryStatus = probe;
        NativeLibraryMessage = message;

        if (probe == PiperNativeProbe.Loaded)
        {
            _logger.Info(message);
        }
        else
        {
            _logger.Info($"{message} 将使用进程外 piper.exe 兜底。");
        }
    }

    public string Name => "piper";

    public PiperNativeProbe NativeLibraryStatus { get; }

    public string NativeLibraryMessage { get; }

    /// <summary>手册要求：通过检查模型文件是否存在来判断可用性。</summary>
    public bool IsAvailable => File.Exists(_configuredModelPath);

    /// <summary>进程外 piper.exe 是否就绪。</summary>
    public bool IsExecutableAvailable => File.Exists(_executablePath);

    public string ConfiguredModelPath => _configuredModelPath;

    public async Task<byte[]> SynthesizeAsync(string text, VoiceConfig config, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return Array.Empty<byte>();
        }

        // 手册：合成时使用 config.FallbackPiperVoice 作为音色标识。
        var modelPath = ResolveModelPath(config.FallbackPiperVoice);

        if (modelPath is null)
        {
            throw new FileNotFoundException(
                $"找不到可用的 Piper 模型文件。音色 \"{config.FallbackPiperVoice}\" 无法解析，" +
                $"配置里的 tts.piper.model_path 也不存在（{_configuredModelPath}）。" +
                "请放置模型文件或修改 config.yaml 的 tts.piper.model_path。");
        }

        if (!IsExecutableAvailable)
        {
            throw new FileNotFoundException(
                $"Piper 动态库尚未编译（{NativeLibraryMessage}），且未找到进程外兜底的 piper.exe（{_executablePath}）。" +
                "请编译 Piper C 动态库，或把官方 piper.exe 放到该路径。");
        }

        return await RunPiperProcessAsync(text, modelPath, config, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 音色 → 模型文件的解析。支持三种写法，逐级尝试，最后回落到全局配置：
    ///   1. 直接是路径（含分隔符或以 .onnx 结尾）；
    ///   2. models/piper/&lt;voice&gt;.onnx；
    ///   3. models/piper/&lt;voice&gt;/model.onnx；
    ///   4. 回落到 config.yaml 的 tts.piper.model_path。
    /// </summary>
    public string? ResolveModelPath(string? voiceId)
    {
        if (!string.IsNullOrWhiteSpace(voiceId))
        {
            var candidate = AppPaths.Resolve(voiceId);
            if (File.Exists(candidate))
            {
                return candidate;
            }

            var onnxDirect = AppPaths.Resolve(Path.Combine("models", "piper", voiceId + ".onnx"));
            if (File.Exists(onnxDirect))
            {
                return onnxDirect;
            }

            var inFolder = AppPaths.Resolve(Path.Combine("models", "piper", voiceId, "model.onnx"));
            if (File.Exists(inFolder))
            {
                return inFolder;
            }

            _logger.Warn($"音色 \"{voiceId}\" 未解析到模型文件，回退使用 {_configuredModelPath}。");
        }

        return File.Exists(_configuredModelPath) ? _configuredModelPath : null;
    }

    private async Task<byte[]> RunPiperProcessAsync(
        string text,
        string modelPath,
        VoiceConfig config,
        CancellationToken cancellationToken)
    {
        var tempOutput = Path.Combine(Path.GetTempPath(), $"piper_{Guid.NewGuid():N}.wav");

        var psi = new ProcessStartInfo(_executablePath)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        psi.ArgumentList.Add("--model");
        psi.ArgumentList.Add(modelPath);
        psi.ArgumentList.Add("--output_file");
        psi.ArgumentList.Add(tempOutput);

        // Piper 的 length_scale 与语速成反比。
        if (config.Speed > 0 && Math.Abs(config.Speed - 1.0) > 0.001)
        {
            psi.ArgumentList.Add("--length_scale");
            psi.ArgumentList.Add((1.0 / config.Speed).ToString("0.###", System.Globalization.CultureInfo.InvariantCulture));
        }

        if (Directory.Exists(_espeakDataPath))
        {
            psi.ArgumentList.Add("--espeak_data");
            psi.ArgumentList.Add(_espeakDataPath);
        }

        using var process = new Process { StartInfo = psi };

        // 读流任务在 try 外声明：异常路径也要把它们收掉，否则
        // （a）它们会在 Process 被 Dispose 后抛 IOException 且无人观察，
        // （b）piper 卡在写 stderr 时会一直挂着。
        Task<string>? stderrTask = null;
        Task<string>? stdoutTask = null;

        try
        {
            if (!process.Start())
            {
                throw new InvalidOperationException("piper.exe 启动失败。");
            }

            // 文本经 stdin 传入（piper 的标准用法）。
            await process.StandardInput.WriteLineAsync(text.AsMemory(), cancellationToken).ConfigureAwait(false);
            await process.StandardInput.FlushAsync(cancellationToken).ConfigureAwait(false);
            process.StandardInput.Close();

            stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
            stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(60));

            try
            {
                await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw new TimeoutException("piper.exe 合成超时（60 秒）。");
            }

            var stderr = await stderrTask.ConfigureAwait(false);
            _ = await stdoutTask.ConfigureAwait(false);

            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException($"piper.exe 退出码 {process.ExitCode}：{stderr.Trim()}");
            }

            if (!File.Exists(tempOutput))
            {
                throw new InvalidOperationException($"piper.exe 未生成输出文件。stderr：{stderr.Trim()}");
            }

            var bytes = await File.ReadAllBytesAsync(tempOutput, cancellationToken).ConfigureAwait(false);
            _logger.Info($"Piper 合成完成：{text.Length} 字 → {bytes.Length} 字节 WAV（模型 {Path.GetFileName(modelPath)}）。");
            return bytes;
        }
        finally
        {
            // 任何异常路径都必须把子进程收掉：残留的 piper.exe 会占着 tempOutput，
            // 下面的删除就会失败，%TEMP% 里按 GUID 一路累积 wav。
            TryKill(process);

            var pending = new List<Task>(2);
            if (stderrTask is not null)
            {
                pending.Add(stderrTask);
            }

            if (stdoutTask is not null)
            {
                pending.Add(stdoutTask);
            }

            if (pending.Count > 0)
            {
                try
                {
                    // 进程已被杀，管道会立刻见了 EOF；给半秒收尾，异常本身不重要（下面统一观察）。
                    await Task.WhenAny(Task.WhenAll(pending), Task.Delay(500)).ConfigureAwait(false);
                    GC.KeepAlive(pending);
                }
                catch (Exception ex)
                {
                    _logger.Debug($"收尾 piper 输出流时异常（忽略）：{ex.Message}");
                }
            }

            try
            {
                if (File.Exists(tempOutput))
                {
                    File.Delete(tempOutput);
                }
            }
            catch (Exception ex)
            {
                // 删不掉通常意味着还有进程占着这个文件，记一条便于排查。
                _logger.Debug($"临时文件删除失败（{Path.GetFileName(tempOutput)}）：{ex.Message}");
            }
        }
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            // 忽略。
        }
    }

    public void Stop()
    {
        // 进程外模式每次调用都是独立进程，没有常驻播放状态需要停止。
    }

    public void Unload()
    {
        // 进程外模式无常驻资源。待切换到 P/Invoke 后，这里需要释放 piper_synthesizer。
    }
}
