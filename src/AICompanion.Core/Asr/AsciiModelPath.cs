using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using AICompanion.Core.Abstractions;

namespace AICompanion.Core.Asr;

/// <summary>
/// 把含中文的模型路径"翻译"成纯 ASCII 路径，供 whisper.cpp 使用。
///
/// 为什么需要：whisper 的原生接口收的是 <c>const char*</c>，Windows 上它内部用
/// <c>fopen()</c> / <c>std::ifstream</c> 打开文件，这两个 API 按**系统 ANSI 代码页**
/// 解释字节（简体中文机器是 GBK）。C# 侧按 UTF-8 传过去的中文路径，在原生那边
/// 就被拼成了乱码，结果是 <c>fopen</c> 返回 NULL、<c>ai_asr_init</c> 返回 -2，
/// 表现为"安装目录带中文 → 语音输入不可用"（英文目录一切正常，所以很容易漏测）。
///
/// 处理方式：在 <c>%LOCALAPPDATA%\AICompanion\cache\asr</c> 下用内容的 SHA256 前缀
/// 造一个 ASCII 名字的硬链接（同盘即时完成、不占额外空间；跨盘或权限不足时退回复制），
/// 把它交给原生层。模型文件本身不动，配置里的路径也不改（准则 9）。
/// </summary>
internal static class AsciiModelPath
{
    /// <summary>返回一个可安全传给原生层的路径；原路径已是 ASCII 时原样返回。</summary>
    public static string Ensure(string modelPath, IAppLogger logger)
    {
        if (string.IsNullOrWhiteSpace(modelPath) || IsAscii(modelPath))
        {
            return modelPath;
        }

        try
        {
            var source = new FileInfo(modelPath);
            if (!source.Exists)
            {
                // 文件不存在时不要自作主张，交给原生层报原始错误（信息更有用）。
                return modelPath;
            }

            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "AICompanion",
                "cache",
                "asr");

            Directory.CreateDirectory(dir);

            var hash = ShortHash(modelPath);
            var ext = Path.GetExtension(modelPath);
            var target = Path.Combine(dir, $"model-{hash}{ext}");

            if (File.Exists(target) && new FileInfo(target).Length == source.Length)
            {
                return target;
            }

            try
            {
                TryDelete(target);

                if (!CreateHardLink(target, source.FullName, IntPtr.Zero))
                {
                    File.Copy(source.FullName, target, overwrite: true);
                }

                logger.Info($"ASR 模型路径含非 ASCII 字符，已链接到 {target} 供原生层使用。");
            }
            catch (Exception ex)
            {
                logger.Warn($"为 ASR 模型创建 ASCII 别名失败（{ex.Message}），尝试直接使用原路径。");
                return modelPath;
            }

            return target;
        }
        catch (Exception ex)
        {
            logger.Warn($"处理 ASR 模型路径时出错：{ex.Message}");
            return modelPath;
        }
    }

    private static bool IsAscii(string text)
    {
        foreach (var c in text)
        {
            if (c > 127)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// 用"路径 + 文件大小 + 最后写入时间"算短哈希：内容同样时改名会换新文件，
    /// 避免沿用旧链接（模型换了但名字没变的情况很常见）。
    /// </summary>
    private static string ShortHash(string path)
    {
        try
        {
            var info = new FileInfo(path);
            var seed = $"{info.FullName}|{info.Length}|{info.LastWriteTimeUtc.Ticks}";
            var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(seed));
            return Convert.ToHexString(bytes)[..12].ToLowerInvariant();
        }
        catch
        {
            return "fallback";
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // 删不掉就让下面的 CreateHardLink / Copy 去报错。
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateHardLink(string fileName, string existingFileName, IntPtr securityAttributes);
}