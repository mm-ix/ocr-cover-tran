using System.Windows.Forms;

namespace ShotTrans.Input;

/// <summary>RegisterHotKey 的修饰键 + 虚拟键组合。</summary>
public readonly record struct Hotkey(uint Modifiers, uint VirtualKey)
{
    public bool IsEmpty => VirtualKey == 0;

    public static bool TryParse(string? text, out Hotkey hotkey, out string error)
    {
        hotkey = default;
        error = "";
        if (string.IsNullOrWhiteSpace(text))
        {
            error = "快捷键不能为空";
            return false;
        }

        uint mods = 0;
        uint vk = 0;
        foreach (var rawPart in text.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            switch (rawPart.ToLowerInvariant())
            {
                case "ctrl":
                case "control":
                    mods |= NativeHotkeyValues.ModControl;
                    continue;
                case "alt":
                    mods |= NativeHotkeyValues.ModAlt;
                    continue;
                case "shift":
                    mods |= NativeHotkeyValues.ModShift;
                    continue;
                case "win":
                case "windows":
                case "meta":
                    mods |= NativeHotkeyValues.ModWin;
                    continue;
            }

            if (vk != 0)
            {
                error = "只能包含一个主键";
                return false;
            }
            if (!TryParseKey(rawPart, out vk))
            {
                error = $"无法识别按键 “{rawPart}”";
                return false;
            }
        }

        if (vk == 0)
        {
            error = "缺少主键（例如 X、F8）";
            return false;
        }

        var parsed = new Hotkey(mods, vk);
        if (!Validate(parsed, out error)) return false;
        hotkey = parsed;
        return true;
    }

    public static bool Validate(Hotkey hotkey, out string error)
    {
        error = "";
        uint vk = hotkey.VirtualKey;
        if (hotkey.IsEmpty)
        {
            error = "缺少主键（例如 X、F8）";
            return false;
        }
        bool functionKey = (vk >= 0x70 && vk <= 0x87) || vk == 0x2C || vk == 0x13 || vk == 0x91;
        if (hotkey.Modifiers == 0 && !functionKey)
        {
            error = "除 F1~F24 / PrintScreen 外，快捷键需要至少一个修饰键（Ctrl/Alt/Shift/Win）";
            return false;
        }
        if (vk is 0x1B or 0x20 or 0x09 or 0x0D or 0x08)
        {
            error = "Esc / Space / Tab / Enter / Backspace 不能作为主键";
            return false;
        }
        if (vk is 0x10 or 0x11 or 0x12 or 0x5B or 0x5C)
        {
            error = "主键不能只是修饰键";
            return false;
        }
        return true;
    }

    public static Hotkey FromKeys(Keys keyData)
    {
        var key = keyData & Keys.KeyCode;
        uint mods = 0;
        if ((keyData & Keys.Control) != 0) mods |= NativeHotkeyValues.ModControl;
        if ((keyData & Keys.Alt) != 0) mods |= NativeHotkeyValues.ModAlt;
        if ((keyData & Keys.Shift) != 0) mods |= NativeHotkeyValues.ModShift;
        uint vk = key switch
        {
            Keys.ControlKey or Keys.ShiftKey or Keys.Menu or Keys.LWin or Keys.RWin => 0,
            _ => (uint)key
        };
        return new Hotkey(mods, vk);
    }

    public string ToDisplayString()
    {
        if (IsEmpty) return "";
        var parts = new List<string>(5);
        if ((Modifiers & NativeHotkeyValues.ModControl) != 0) parts.Add("Ctrl");
        if ((Modifiers & NativeHotkeyValues.ModAlt) != 0) parts.Add("Alt");
        if ((Modifiers & NativeHotkeyValues.ModShift) != 0) parts.Add("Shift");
        if ((Modifiers & NativeHotkeyValues.ModWin) != 0) parts.Add("Win");
        parts.Add(KeyDisplay(VirtualKey));
        return string.Join("+", parts);
    }

    public static string KeyDisplay(uint vk)
    {
        if (vk >= 'A' && vk <= 'Z') return ((char)vk).ToString();
        if (vk >= '0' && vk <= '9') return ((char)vk).ToString();
        if (vk >= 0x70 && vk <= 0x87) return "F" + (vk - 0x70 + 1);
        if (vk >= 0x60 && vk <= 0x69) return "Num" + (vk - 0x60);
        var map = new Dictionary<uint, string>
        {
            [0x2C] = "PrintScreen", [0x20] = "Space", [0x09] = "Tab", [0x0D] = "Enter",
            [0x1B] = "Esc", [0x08] = "Backspace", [0x2D] = "Insert", [0x2E] = "Delete",
            [0x24] = "Home", [0x23] = "End", [0x21] = "PageUp", [0x22] = "PageDown",
            [0x26] = "Up", [0x28] = "Down", [0x25] = "Left", [0x27] = "Right",
            [0x13] = "Pause", [0x91] = "ScrollLock", [0x90] = "NumLock", [0x14] = "CapsLock",
            [0xC0] = "`", [0xBD] = "-", [0xBB] = "=", [0xBC] = ",", [0xBE] = ".",
            [0xBF] = "/", [0xBA] = ";", [0xDE] = "'", [0xDB] = "[", [0xDD] = "]",
            [0xDC] = "\\"
        };
        return map.TryGetValue(vk, out var s) ? s : "0x" + vk.ToString("X2");
    }

    private static bool TryParseKey(string raw, out uint vk)
    {
        vk = 0;
        var s = raw.Trim();
        if (s.Length == 1)
        {
            char c = char.ToUpperInvariant(s[0]);
            if (c >= 'A' && c <= 'Z') { vk = c; return true; }
            if (c >= '0' && c <= '9') { vk = (uint)('0' + (c - '0')); return true; }
        }

        var lower = s.ToLowerInvariant();
        var map = new Dictionary<string, uint>
        {
            ["printscreen"] = 0x2C, ["prtsc"] = 0x2C, ["snapshot"] = 0x2C,
            ["space"] = 0x20, ["tab"] = 0x09, ["enter"] = 0x0D, ["return"] = 0x0D,
            ["esc"] = 0x1B, ["escape"] = 0x1B, ["backspace"] = 0x08, ["back"] = 0x08,
            ["insert"] = 0x2D, ["delete"] = 0x2E, ["del"] = 0x2E,
            ["home"] = 0x24, ["end"] = 0x23, ["pageup"] = 0x21, ["pgup"] = 0x21,
            ["pagedown"] = 0x22, ["pgdn"] = 0x22, ["up"] = 0x26, ["down"] = 0x28,
            ["left"] = 0x25, ["right"] = 0x27, ["pause"] = 0x13, ["scrolllock"] = 0x91,
            ["numlock"] = 0x90, ["capslock"] = 0x14,
            ["`"] = 0xC0, ["-"] = 0xBD, ["="] = 0xBB, [","] = 0xBC, ["."] = 0xBE,
            ["/"] = 0xBF, [";"] = 0xBA, ["'"] = 0xDE, ["["] = 0xDB, ["]"] = 0xDD, ["\\"] = 0xDC
        };
        if (map.TryGetValue(lower, out vk)) return true;

        if (lower.Length >= 2 && lower[0] == 'f' && int.TryParse(lower[1..], out int fn) && fn >= 1 && fn <= 24)
        {
            vk = (uint)(0x70 + fn - 1);
            return true;
        }
        if (lower.StartsWith("num") && int.TryParse(lower[3..], out int num) && num >= 0 && num <= 9)
        {
            vk = (uint)(0x60 + num);
            return true;
        }
        if (Enum.TryParse<Keys>(s, true, out var key) && key != Keys.None)
        {
            var code = (int)(key & Keys.KeyCode);
            if (code is >= 1 and <= 254)
            {
                vk = (uint)code;
                return true;
            }
        }
        return false;
    }
}

internal static class NativeHotkeyValues
{
    public const uint ModAlt = 0x0001;
    public const uint ModControl = 0x0002;
    public const uint ModShift = 0x0004;
    public const uint ModWin = 0x0008;
    public const uint ModNoRepeat = 0x4000;
}
