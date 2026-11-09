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
    [DllImport("user32.dll")] private static extern void keybd_event(byte key, byte scan, uint flags, nuint extra);
}
