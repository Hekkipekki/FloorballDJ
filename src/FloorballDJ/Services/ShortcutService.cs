using System.Windows.Input;

namespace FloorballDJ.Services;

public static class ShortcutService
{
    public static string? Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var trimmed = value.Trim();
        return trimmed.Equals("None", StringComparison.OrdinalIgnoreCase) ||
               trimmed.Equals("N/A", StringComparison.OrdinalIgnoreCase)
            ? null
            : trimmed;
    }

    public static bool IsModifierKey(Key key) => key is
        Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift or
        Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin;

    public static string? FromKeyEvent(KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key == Key.None || IsModifierKey(key)) return null;

        var parts = new List<string>();
        var modifiers = Keyboard.Modifiers;
        if (modifiers.HasFlag(ModifierKeys.Control)) parts.Add("Ctrl");
        if (modifiers.HasFlag(ModifierKeys.Alt)) parts.Add("Alt");
        if (modifiers.HasFlag(ModifierKeys.Shift)) parts.Add("Shift");
        if (modifiers.HasFlag(ModifierKeys.Windows)) parts.Add("Win");
        parts.Add(DisplayKey(key));
        return string.Join(" + ", parts);
    }

    public static bool Matches(string? shortcut, KeyEventArgs e)
    {
        var stored = Normalize(shortcut);
        var pressed = FromKeyEvent(e);
        return stored is not null && pressed is not null &&
               Canonical(stored).Equals(Canonical(pressed), StringComparison.OrdinalIgnoreCase);
    }

    public static string ModifierPrompt()
    {
        var parts = new List<string>();
        var modifiers = Keyboard.Modifiers;
        if (modifiers.HasFlag(ModifierKeys.Control)) parts.Add("Ctrl");
        if (modifiers.HasFlag(ModifierKeys.Alt)) parts.Add("Alt");
        if (modifiers.HasFlag(ModifierKeys.Shift)) parts.Add("Shift");
        if (modifiers.HasFlag(ModifierKeys.Windows)) parts.Add("Win");
        return parts.Count == 0 ? "Väntar på tangent …" : $"{string.Join(" + ", parts)} + …";
    }

    private static string Canonical(string value) =>
        value.Replace(" ", "", StringComparison.Ordinal).Replace("Control", "Ctrl", StringComparison.OrdinalIgnoreCase);

    private static string DisplayKey(Key key)
    {
        if (key is >= Key.D0 and <= Key.D9) return ((int)key - (int)Key.D0).ToString();
        if (key is >= Key.NumPad0 and <= Key.NumPad9) return $"Num {((int)key - (int)Key.NumPad0)}";
        return key switch
        {
            Key.OemPlus => "+",
            Key.OemMinus => "-",
            Key.OemComma => ",",
            Key.OemPeriod => ".",
            Key.Return => "Enter",
            Key.Back => "Backspace",
            Key.Next => "Page Down",
            Key.Prior => "Page Up",
            _ => key.ToString()
        };
    }
}
