using System.Text.Json.Serialization;

namespace RouterSpeed;

/// <summary>A single key and its modifiers, suitable for Windows RegisterHotKey.</summary>
public sealed record ShortcutDefinition(Keys Key, bool Control, bool Alt, bool Shift, bool Win)
{
    public static ShortcutDefinition Default { get; } = new(Keys.N, Control: true, Alt: true, Shift: false, Win: false);

    [JsonIgnore]
    public string DisplayText
    {
        get
        {
            var parts = new List<string>(5);
            if (Control) parts.Add("Ctrl");
            if (Alt) parts.Add("Alt");
            if (Shift) parts.Add("Shift");
            if (Win) parts.Add("Win");
            parts.Add(KeyText(Key));
            return string.Join(" + ", parts);
        }
    }

    /// <returns>A user-facing error, or null when the shortcut is valid.</returns>
    public string? Validate()
    {
        if (Key != (Key & Keys.KeyCode) || !AvailableKeys.Contains(Key))
            return "请选择一个有效的主按键。F12 为系统保留按键，不能使用。";
        if (!Control && !Alt && !Win)
            return "请至少选择 Ctrl、Alt 或 Win，避免影响日常输入。";
        return null;
    }

    internal static IReadOnlyList<Keys> AvailableKeys { get; } = CreateAvailableKeys();

    internal static string KeyText(Keys key)
    {
        if (key >= Keys.A && key <= Keys.Z) return key.ToString();
        if (key >= Keys.D0 && key <= Keys.D9) return ((int)key - (int)Keys.D0).ToString();
        if (key >= Keys.NumPad0 && key <= Keys.NumPad9) return "小键盘 " + ((int)key - (int)Keys.NumPad0);
        if (key >= Keys.F1 && key <= Keys.F24) return key.ToString();
        return key switch
        {
            Keys.Space => "Space", Keys.Tab => "Tab", Keys.Enter => "Enter", Keys.Escape => "Esc",
            Keys.Back => "Backspace", Keys.Insert => "Insert", Keys.Delete => "Delete",
            Keys.Home => "Home", Keys.End => "End", Keys.PageUp => "Page Up", Keys.PageDown => "Page Down",
            Keys.Up => "↑", Keys.Down => "↓", Keys.Left => "←", Keys.Right => "→", Keys.Pause => "Pause",
            _ => "未选择"
        };
    }

    private static IReadOnlyList<Keys> CreateAvailableKeys()
    {
        var keys = new List<Keys>();
        for (int key = (int)Keys.A; key <= (int)Keys.Z; key++) keys.Add((Keys)key);
        for (int key = (int)Keys.D0; key <= (int)Keys.D9; key++) keys.Add((Keys)key);
        for (int key = (int)Keys.F1; key <= (int)Keys.F24; key++)
            if ((Keys)key != Keys.F12) keys.Add((Keys)key);
        keys.AddRange([Keys.Space, Keys.Tab, Keys.Enter, Keys.Escape, Keys.Back, Keys.Insert, Keys.Delete,
            Keys.Home, Keys.End, Keys.PageUp, Keys.PageDown, Keys.Up, Keys.Down, Keys.Left, Keys.Right, Keys.Pause]);
        for (int key = (int)Keys.NumPad0; key <= (int)Keys.NumPad9; key++) keys.Add((Keys)key);
        return keys.AsReadOnly();
    }
}
