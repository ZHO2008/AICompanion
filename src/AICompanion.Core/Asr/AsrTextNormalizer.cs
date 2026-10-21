using System.Runtime.InteropServices;
using System.Text;

namespace AICompanion.Core.Asr;

/// <summary>
/// ASR 文本后处理。
///
/// 解决的问题：whisper 在中文场景经常输出**繁体**（"天氣不錯"），界面上看着别扭。
///
/// 为什么不用 <c>Microsoft.VisualBasic.Strings.StrConv</c>：
/// 那条路在 .NET 8 上会抛
/// <c>ArgumentException: This system does not contain support for the Traditional Chinese locale</c>
/// （.NET Framework 下能跑，.NET 8 + ICU 下不行）。
/// 改为直接调用 Windows 的 <c>LCMapStringEx</c> + <c>LCMAP_SIMPLIFIED_CHINESE</c>，
/// 这就是系统自带的简繁映射，完整且零维护。
///
/// 任何一步失败都**原样返回**：宁可显示繁体，也不能因为后处理把识别结果弄丢。
/// </summary>
public static class AsrTextNormalizer
{
    private const uint LcmapSimplifiedChinese = 0x02000000;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int LCMapStringEx(
        string locale,
        uint flags,
        string source,
        int sourceLength,
        [Out] char[] destination,
        int destinationLength,
        IntPtr version,
        IntPtr reserved,
        IntPtr handle);

    /// <summary>把可能出现的繁体字转成简体；已经是简体时原样返回。</summary>
    public static string ToSimplified(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return text ?? string.Empty;
        }

        try
        {
            // 输出长度按 2 倍预留：个别映射可能变长
            var buffer = new char[text.Length * 2 + 2];

            var length = LCMapStringEx(
                "zh-CN", LcmapSimplifiedChinese,
                text, text.Length,
                buffer, buffer.Length,
                IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);

            return length > 0 ? new string(buffer, 0, length) : text;
        }
        catch
        {
            return text;
        }
    }

    /// <summary>去掉中文之间的多余空格（ASR 偶尔会插入），英文单词之间的空格保留。</summary>
    public static string NormalizeSpaces(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return text ?? string.Empty;
        }

        var builder = new StringBuilder(text.Length);
        var previous = '\0';

        foreach (var ch in text)
        {
            if (ch == ' ' && IsCjk(previous))
            {
                continue;
            }

            builder.Append(ch);
            previous = ch;
        }

        return builder.ToString().Trim();
    }

    /// <summary>把 ASR 原始输出整理成可直接展示/送进管线的文本。</summary>
    public static string Normalize(string? text) => NormalizeSpaces(ToSimplified(text));

    private static bool IsCjk(char ch)
        => (ch >= 0x4E00 && ch <= 0x9FFF) ||     // 基本汉字
           (ch >= 0x3400 && ch <= 0x4DBF) ||     // 扩展 A
           (ch >= 0xF900 && ch <= 0xFAFF);       // 兼容汉字
}
