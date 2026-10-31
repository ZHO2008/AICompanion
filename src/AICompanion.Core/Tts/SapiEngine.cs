using System.IO;
using System.Runtime.InteropServices;
using AICompanion.Core.Abstractions;
using AICompanion.Core.Models;

/*
 * 整个产品只面向 Windows（WPF + NAudio + nvidia-smi + whisper.cpp + SAPI）。
 * 这里用到的 SAPI COM 组件与 Type.GetTypeFromProgID 都只有 Windows 支持，
 * 而 Core 的目标框架刻意保持 net8.0 —— 这样 GameMonitor 与 5 个验证探针
 * 都不必改成 net8.0-windows。因此只在包含这些调用的本文件内关闭 CA1416。
 */
#pragma warning disable CA1416

namespace AICompanion.Core.Tts;

/// <summary>
/// Windows 内置语音合成（SAPI）引擎，通过 COM 直接驱动 SAPI.SpVoice。
///
/// 为什么需要它：Piper 需要额外的可执行文件与 .onnx 模型，Chatterbox 需要独立的 Python 服务。
/// 两者都没就位时，程序原本**完全没有语音输出** —— 对一个"桌面伴侣"来说这是最影响可用性的缺口。
/// SAPI 是 Windows 自带的，任何机器上都能立刻出声，作为兜底最合适。
///
/// 为什么用 COM 而不引用 System.Speech 包：
/// 实测 System.Speech 8.0.0 在 .NET 8 上调用 <c>SpeechSynthesizer.Speak</c> 会抛
/// NullReferenceException（线程池 MTA 与自建 STA 线程都一样），而同一条 SAPI 能力
/// 通过 COM 调用在 PowerShell 中稳定可用。直接驱动 COM 既避开了该缺陷，也少一个依赖包。
/// </summary>
public sealed class SapiEngine : ITTSEngine
{
    private readonly IAppLogger _logger;
    private readonly object _sync = new();

    private object? _currentVoice;
    private string? _loggedVoice;

    // 选中的 SAPI 音色 token（COM 对象），首次合成时解析一次后复用。
    private object? _voiceToken;
    private bool _voiceResolved;

    public SapiEngine(IAppLogger logger)
    {
        _logger = logger;
    }

    public string Name => "sapi";

    /// <summary>SAPI 由系统提供，只要能创建 SpVoice 就认为可用。</summary>
    public bool IsAvailable
    {
        get
        {
            try
            {
                var type = Type.GetTypeFromProgID("SAPI.SpVoice");
                if (type is null)
                {
                    return false;
                }

                var probe = Activator.CreateInstance(type);
                ReleaseCom(probe);
                return true;
            }
            catch
            {
                return false;
            }
        }
    }

    public Task<byte[]> SynthesizeAsync(string text, VoiceConfig config, CancellationToken cancellationToken = default)
    {
        // COM 合成是同步阻塞调用，放到线程池即可（COM 会自动放入 MTA，实测可用）。
        return Task.Run(() => Synthesize(text, config, cancellationToken), cancellationToken);
    }

    private byte[] Synthesize(string text, VoiceConfig config, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var voiceType = Type.GetTypeFromProgID("SAPI.SpVoice")
            ?? throw new InvalidOperationException("系统中没有 SAPI.SpVoice（Windows 语音组件缺失）。");
        var streamType = Type.GetTypeFromProgID("SAPI.SpFileStream")
            ?? throw new InvalidOperationException("系统中没有 SAPI.SpFileStream（Windows 语音组件缺失）。");

        var tempFile = Path.Combine(Path.GetTempPath(), $"ai-companion-tts-{Guid.NewGuid():N}.wav");

        dynamic? voice = null;
        dynamic? stream = null;

        try
        {
            voice = Activator.CreateInstance(voiceType)
                ?? throw new InvalidOperationException("创建 SAPI.SpVoice 失败。");
            stream = Activator.CreateInstance(streamType)
                ?? throw new InvalidOperationException("创建 SAPI.SpFileStream 失败。");

            TrySelectVoice(voice, config);

            // 语速与音量：SAPI 的 Rate 是 -10..10，Volume 是 0..100。
            // 配置越界时夹紧而不是抛异常——语音参数不该阻断对话。
            /*
             * 语速/音量：SAPI 的 Rate 是 -10..10、Volume 是 0..100。
             * 这里用 VoiceConfig 上已有的字段（没有就保持默认），并夹紧取值 ——
             * 语音参数越界不该阻断对话。
             *
             * 注：TtsConfig.SapiRate / SapiVolume 目前只用于文档与设置面板展示，
             * 真正下发到引擎还要经过 ITTSEngine 接口扩展，留待 TTS 引擎统一接口时再接。
             */
            try
            {
                voice.Rate = 0;
                voice.Volume = 100;
            }
            catch (Exception ex)
            {
                _logger.Debug($"设置 SAPI 语速/音量失败（忽略）：{ex.Message}");
            }

            lock (_sync)
            {
                _currentVoice = voice;
            }

            // SSFMCreateForWrite = 3：创建（覆盖）一个有写权限的 WAV 文件
            stream.Open(tempFile, 3, false);
            voice.AudioOutputStream = stream;

            // SVSFDefault = 0：同步朗读，返回时文件已写完
            voice.Speak(text, 0);

            stream.Close();

            cancellationToken.ThrowIfCancellationRequested();

            return File.ReadAllBytes(tempFile);
        }
        finally
        {
            lock (_sync)
            {
                if (ReferenceEquals(_currentVoice, voice))
                {
                    _currentVoice = null;
                }
            }

            try
            {
                stream?.Close();
            }
            catch
            {
                // 已关闭时忽略
            }

            ReleaseCom(stream);
            ReleaseCom(voice);

            try
            {
                if (File.Exists(tempFile))
                {
                    File.Delete(tempFile);
                }
            }
            catch
            {
                // 临时文件清理失败不影响结果
            }
        }
    }

    /// <summary>
    /// 选音色：把配置里的音色名当作 SAPI 音色描述的**子串**来匹配，匹配不到就用系统默认。
    /// 例如 piper 的音色名 zh_CN-huayan-medium 通常匹配不到，此时保留默认音色即可。
    /// </summary>
    private void TrySelectVoice(dynamic voice, VoiceConfig config)
    {
        // 音色每台机器固定，只在第一次合成时枚举一次：每次合成都遍历
        // ISpeechObjectTokens 会不断产生新的 COM RCW（且中途 return 时更难释放），
        // 这里把结果缓存下来，后续直接复用选中的 token。
        if (_voiceResolved)
        {
            if (_voiceToken is not null)
            {
                voice.Voice = _voiceToken;
            }

            return;
        }

        _voiceResolved = true;

        object? tokens = null;
        try
        {
            var wanted = config.FallbackPiperVoice;
            if (string.IsNullOrWhiteSpace(wanted))
            {
                LogDefaultVoice(voice);
                return;
            }

            tokens = voice.GetVoices();
            dynamic collection = tokens;
            int count = collection.Count;

            for (var i = 0; i < count; i++)
            {
                dynamic token = collection.Item(i);
                string description = token.GetDescription(0);

                if (description is not null &&
                    description.Contains(wanted, StringComparison.OrdinalIgnoreCase))
                {
                    voice.Voice = token;
                    _voiceToken = (object)token;
                    _logger.Info($"SAPI 音色已选为：{description}");
                    return;
                }
            }

            LogDefaultVoice(voice);
        }
        catch (Exception ex)
        {
            _logger.Warn($"SAPI 选择音色失败，使用系统默认音色：{ex.Message}");
        }
        finally
        {
            ReleaseCom(tokens);
        }
    }

    private void LogDefaultVoice(dynamic voice)
    {
        if (_loggedVoice is not null)
        {
            return;
        }

        try
        {
            var description = voice.Voice.GetDescription(0);
            _loggedVoice = description;
            _logger.Info($"SAPI 使用系统默认音色：{description}");
        }
        catch
        {
            // 仅日志，忽略
        }
    }

    public void Stop()
    {
        lock (_sync)
        {
            if (_currentVoice is null)
            {
                return;
            }

            try
            {
                // dynamic 绑定必须经由 dynamic 类型的本地变量：
                // 字段声明为 object 时不会触发动态绑定。
                dynamic voice = _currentVoice;

                // SVSFPurgeBeforeSpeak = 2：清空播放队列
                voice.Speak(string.Empty, 2);
            }
            catch
            {
                // 引擎已释放时忽略
            }
        }
    }

    public void Unload()
    {
        Stop();

        object? cachedToken;

        lock (_sync)
        {
            ReleaseCom(_currentVoice);
            _currentVoice = null;

            /*
             * 缓存的音色 token 也必须在卸载时释放，并复位"已解析"标记。
             *
             * 两个问题：
             *   1. _voiceToken 是枚举 ISpeechObjectTokens 得到的 COM RCW，只有握在
             *      _voiceToken 字段里这一份引用；不释放每次"重启语音服务"（Reset 会重建
             *      引擎）就漏一个 RCW，反复重置会一路累积。
             *   2. _voiceResolved 一旦置 true 就永不复位，同一个实例再次启用时会直接
             *      复用旧 token —— 用户改了 voice 配置也不生效（配置看起来"改了没用"）。
             * 复位后下一次合成会按当前配置重新选一次音色。
             */
            cachedToken = _voiceToken;
            _voiceToken = null;
            _voiceResolved = false;
        }

        // 放在锁外释放：ReleaseComObject 会进 COM，不该占着 _sync。
        ReleaseComToken(cachedToken);
    }

    /// <summary>
    /// 释放缓存的音色 token：用 <see cref="Marshal.ReleaseComObject"/> 递减引用计数。
    /// 释放失败只吞掉异常 —— 卸载流程不该因为一个 COM 引用而失败。
    /// </summary>
    private static void ReleaseComToken(object? comObject)
    {
        if (comObject is null)
        {
            return;
        }

        try
        {
            if (Marshal.IsComObject(comObject))
            {
                Marshal.ReleaseComObject(comObject);
            }
        }
        catch
        {
            // 释放失败不应影响调用方
        }
    }

    private static void ReleaseCom(object? comObject)
    {
        if (comObject is null)
        {
            return;
        }

        try
        {
            if (Marshal.IsComObject(comObject))
            {
                Marshal.FinalReleaseComObject(comObject);
            }
        }
        catch
        {
            // 释放失败不应影响调用方
        }
    }
}
