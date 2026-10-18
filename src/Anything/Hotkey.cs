using Anything.Interop;

namespace Anything;

/// <summary>Global hotkey text format: "Ctrl+Alt+Shift+Win+F" (modifiers in any order, key last).</summary>
internal static class Hotkey
{
    public static bool TryParse(string? text, out uint modifiers, out Keys key)
    {
        modifiers = 0;
        key = Keys.None;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var parts = text.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        foreach (var p in parts)
        {
            switch (p.ToLowerInvariant())
            {
                case "ctrl": case "control": modifiers |= Native.MOD_CONTROL; break;
                case "alt": modifiers |= Native.MOD_ALT; break;
                case "shift": modifiers |= Native.MOD_SHIFT; break;
                case "win": case "windows": modifiers |= Native.MOD_WIN; break;
                default:
                    if (Enum.TryParse(p, true, out Keys k)) key = k;
                    else if (p.Length == 1 && char.IsDigit(p[0])) key = Keys.D0 + (p[0] - '0');
                    else return false;
                    break;
            }
        }
        return key != Keys.None && !IsModifierKey(key);
    }

    public static string Format(uint modifiers, Keys key)
    {
        if (key == Keys.None) return "";
        var parts = new List<string>();
        if ((modifiers & Native.MOD_CONTROL) != 0) parts.Add("Ctrl");
        if ((modifiers & Native.MOD_ALT) != 0) parts.Add("Alt");
        if ((modifiers & Native.MOD_SHIFT) != 0) parts.Add("Shift");
        if ((modifiers & Native.MOD_WIN) != 0) parts.Add("Win");
        parts.Add(key is >= Keys.D0 and <= Keys.D9 ? ((char)('0' + (key - Keys.D0))).ToString() : key.ToString());
        return string.Join('+', parts);
    }

    public static bool IsModifierKey(Keys k) => k is Keys.ControlKey or Keys.ShiftKey or Keys.Menu or Keys.LWin or Keys.RWin
        or Keys.LControlKey or Keys.RControlKey or Keys.LShiftKey or Keys.RShiftKey or Keys.LMenu or Keys.RMenu;
}
