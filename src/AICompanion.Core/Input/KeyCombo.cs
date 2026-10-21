namespace AICompanion.Core.Input;

/// <summary>
/// 按键组合解析（"Home"、"F2"、"Alt+Q"、"Ctrl+Alt+Q"）。
///
/// 为什么放在 Core 而不是 UI 层：
///   解析逻辑是纯函数，放在 Core 才能被探针直接测；
///   而且它和"用哪个平台 API 读键状态"无关，Windows 只是其中一种实现。
/// </summary>
public static class KeyCombo
{
    public const int VkShift = 0x10;
    public const int VkControl = 0x11;
    public const int VkAlt = 0x12;
    public const int VkLWin = 0x5B;

    /// <summary>修饰键（也含 Win 的主判定键），按需全按下方算触发。</summary>
    public static IReadOnlyList<int> Modules(int[] modifiers) => modifiers;

    /// <summary>
    /// 解析按键描述。成功时 <paramref name="modifiers"/> 为修饰键虚拟码数组，
    /// <paramref name="mainKey"/> 为主键虚拟码。
    /// </summary>
    public static bool TryParse(string? spec, out int[] modifiers, out int mainKey)
    {
        modifiers = Array.Empty<int>();
        mainKey = 0;

        if (string.IsNullOrWhiteSpace(spec))
        {
            return false;
        }

        var parts = spec.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0)
        {
            return false;
        }

        var mods = new List<int>();

        for (var i = 0; i < parts.Length - 1; i++)
        {
            if (!TryParseModifier(parts[i], out var vk))
            {
                return false;
            }

            mods.Add(vk);
        }

        if (!TryParseMainKey(parts[^1], out mainKey))
        {
            return false;
        }

        modifiers = mods.ToArray();
        return true;
    }

    /// <summary>主键是否被支持（用于配置校验时的友好提示）。</summary>
    public static bool IsValid(string? spec) => TryParse(spec, out _, out _);

    private static bool TryParseModifier(string text, out int vk)
    {
        switch (text.ToLowerInvariant())
        {
            case "ctrl":
            case "control":
                vk = VkControl;
                return true;

            case "alt":
                vk = VkAlt;
                return true;

            case "shift":
                vk = VkShift;
                return true;

            case "win":
            case "meta":
                vk = VkLWin;
                return true;

            default:
                vk = 0;
                return false;
        }
    }

    private static bool TryParseMainKey(string text, out int vk)
    {
        vk = 0;

        if (text.Length == 1)
        {
            var ch = char.ToUpperInvariant(text[0]);

            if (ch is >= 'A' and <= 'Z')
            {
                vk = ch;
                return true;
            }

            if (ch is >= '0' and <= '9')
            {
                vk = ch;
                return true;
            }

            switch (ch)
            {
                case ' ': vk = 0x20; return true;   // Space
                case '`': vk = 0xC0; return true;
                case '-': vk = 0xBD; return true;
                case '=': vk = 0xBB; return true;
                case ',': vk = 0xBC; return true;
                case '.': vk = 0xBE; return true;
                case '/': vk = 0xBF; return true;
                case ';': vk = 0xBA; return true;
                case '\'': vk = 0xDE; return true;
                case '[': vk = 0xDB; return true;
                case ']': vk = 0xDD; return true;
                case '\\': vk = 0xDC; return true;
            }

            return false;
        }

        switch (text.ToLowerInvariant())
        {
            case "space": vk = 0x20; return true;
            case "enter":
            case "return": vk = 0x0D; return true;
            case "tab": vk = 0x09; return true;
            case "esc":
            case "escape": vk = 0x1B; return true;
            case "backspace": vk = 0x08; return true;
            case "capslock": vk = 0x14; return true;
            case "insert": vk = 0x2D; return true;
            case "delete":
            case "del": vk = 0x2E; return true;
            case "home": vk = 0x24; return true;
            case "end": vk = 0x23; return true;
            case "pageup": vk = 0x21; return true;
            case "pagedown": vk = 0x22; return true;
            case "up": vk = 0x26; return true;
            case "down": vk = 0x28; return true;
            case "left": vk = 0x25; return true;
            case "right": vk = 0x27; return true;
            case "printscreen": vk = 0x2C; return true;
            case "pause": vk = 0x13; return true;
        }

        // F1..F24
        if (text.Length >= 2 && (text[0] is 'f' or 'F') && int.TryParse(text[1..], out var fn) && fn is >= 1 and <= 24)
        {
            vk = 0x70 + fn - 1;
            return true;
        }

        return false;
    }
}