using System.Runtime.InteropServices;

namespace WinStatusBar.Services;

/// <summary>Executa somente o atalho de voz que o usuário configurou no aplicativo oficial.</summary>
internal static class VoiceShortcut
{
    public static bool Send(string shortcut)
    {
        string[] parts = shortcut.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2) return false;
        var modifiers = new List<byte>();
        foreach (string part in parts[..^1])
        {
            byte key = part.ToLowerInvariant() switch { "ctrl" or "control" => 0x11, "alt" => 0x12, "shift" => 0x10, _ => 0 };
            if (key == 0 || modifiers.Contains(key)) return false;
            modifiers.Add(key);
        }
        string last = parts[^1].ToUpperInvariant();
        byte vk = last == "SPACE" ? (byte)0x20 : last.Length == 1 && char.IsAsciiLetterOrDigit(last[0]) ? (byte)last[0]
            : last.StartsWith('F') && int.TryParse(last[1..], out int f) && f is >= 1 and <= 24 ? (byte)(0x70 + f - 1) : (byte)0;
        if (vk == 0) return false;
        foreach (byte key in modifiers) keybd_event(key, 0, 0, 0);
        keybd_event(vk, 0, 0, 0); keybd_event(vk, 0, 2, 0);
        foreach (byte key in modifiers.AsEnumerable().Reverse()) keybd_event(key, 0, 2, 0);
        return true;
    }
    /// <summary>
    /// Texto do atalho a partir das teclas pressionadas no campo das configurações ("Ctrl+Shift+F9"), no formato que
    /// <see cref="Send"/> aceita: pelo menos um modificador e uma letra, número, tecla de função ou Espaço. null se não servir.
    /// </summary>
    public static string? Format(bool ctrl, bool alt, bool shift, int virtualKey)
    {
        string? key = virtualKey == 0x20 ? "Space"
            : virtualKey is >= 0x30 and <= 0x39 or >= 0x41 and <= 0x5A ? ((char)virtualKey).ToString()
            : virtualKey is >= 0x70 and <= 0x87 ? "F" + (virtualKey - 0x70 + 1)
            : null;
        if (key is null || !(ctrl || alt || shift)) return null;
        return (ctrl ? "Ctrl+" : "") + (alt ? "Alt+" : "") + (shift ? "Shift+" : "") + key;
    }

    [DllImport("user32.dll")] private static extern void keybd_event(byte key, byte scan, uint flags, nuint extra);
}
