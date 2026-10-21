using AICompanion.Core.Abstractions;
using AICompanion.Core.Configuration;

namespace AICompanion.Core.Asr;

/// <summary>
/// 语音识别器工厂：按"文件是否存在"决定用真实引擎还是降级实现。
///
/// 这样安排的用意与手册 §6 的兜底原则一致：
/// ASR 属于"可降级缺失"，缺组件时程序必须照常启动，只是把语音输入换成文字输入。
/// </summary>
public static class SpeechRecognizerFactory
{
    public static ISpeechRecognizer Create(AppConfig config, IAppLogger logger)
    {
        var log = logger.ForCategory("ASR");

        var libraryPath = AppPaths.Resolve(config.Asr.LibraryPath);
        var modelPath = AppPaths.Resolve(config.Asr.ModelPath);

        if (!File.Exists(libraryPath))
        {
            var reason = $"whisper.cpp 动态库不存在：{libraryPath}";
            log.Warn(reason + " —— 语音输入不可用，将只支持文字输入。");
            return new UnavailableSpeechRecognizer(reason);
        }

        if (!File.Exists(modelPath))
        {
            var reason = $"whisper 模型文件不存在：{modelPath}";
            log.Warn(reason + " —— 语音输入不可用，将只支持文字输入。");
            return new UnavailableSpeechRecognizer(reason);
        }

        try
        {
            var recognizer = WhisperCppRecognizer.TryCreate(config, libraryPath, modelPath, log, out var error);
            if (recognizer is not null)
            {
                return recognizer;
            }

            log.Warn($"初始化 whisper.cpp 失败：{error} —— 语音输入不可用，将只支持文字输入。");
            return new UnavailableSpeechRecognizer(error ?? "whisper.cpp 初始化失败。");
        }
        catch (Exception ex)
        {
            log.Error($"创建语音识别器异常：{ex.Message}", ex);
            return new UnavailableSpeechRecognizer(ex.Message);
        }
    }
}
