using System.Runtime.InteropServices;

namespace WinStatusBar.Services;

/// <summary>
/// Atalho global configurável (ex.: "Alt+Space") por hook de teclado de baixo nível, numa thread própria.
/// Funciona também com combinações que o Windows reserva (Alt+Espaço, Win+…), que RegisterHotKey recusa.
/// Também grava o próximo atalho pressionado (tela de configurações). Reinstalado a cada 30 s, como o hook do Win+V.
/// </summary>
internal sealed class GlobalHotkey
{
    [Flags] private enum Mods { None = 0, Ctrl = 1, Alt = 2, Shift = 4, Win = 8 }

    public event Action? Pressed;
    private delegate IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam);
    private HookProc? _proc;
    private IntPtr _hook;
    private volatile int _vk;
    private volatile Mods _mods;
    private volatile bool _swallowUp;
    private Action<string?>? _recording;

    public void Start()
    {
        var t = new Thread(() =>
        {
            _proc = Callback;
            Install();
            SetTimer(IntPtr.Zero, UIntPtr.Zero, 30000, IntPtr.Zero);
            while (GetMessage(out var m, IntPtr.Zero, 0, 0) > 0)
            {
                if (m.message == 0x0113) { Install(); continue; } // WM_TIMER
                TranslateMessage(ref m);
                DispatchMessage(ref m);
            }
        }) { IsBackground = true, Name = "WinStatusBar.CommandHotkey" };
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
    }

    /// <summary>Define o atalho ("Ctrl+Alt+Shift+Win+Tecla"); vazio ou inválido desliga.</summary>
    public void Set(string? combo)
    {
        if (TryParse(combo, out var mods, out int vk)) { _mods = mods; _vk = vk; }
        else _vk = 0;
    }

    /// <summary>Grava o próximo atalho pressionado (Esc cancela → null). O atalho gravado não chega aos outros apps.</summary>
    public void Record(Action<string?> done) => _recording = done;
    public void CancelRecording() => _recording = null;

    private void Install()
    {
        IntPtr fresh = SetWindowsHookEx(13, _proc!, GetModuleHandle("user32"), 0);
        if (fresh == IntPtr.Zero) { App.Log("Atalho da barra de comando: SetWindowsHookEx falhou: " + Marshal.GetLastWin32Error()); return; }
        IntPtr old = _hook;
        _hook = fresh;
        if (old != IntPtr.Zero) UnhookWindowsHookEx(old);
    }

    private IntPtr Callback(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0)
        {
            var kb = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
            int msg = wParam.ToInt32();
            bool down = msg is 0x100 or 0x104, up = msg is 0x101 or 0x105;
            bool injected = (kb.flags & 0x10) != 0;
            int vk = (int)kb.vkCode;
            if (!injected && !IsModifier(vk))
            {
                if (_recording is { } rec && down)
                {
                    _recording = null;
                    var m = CurrentMods();
                    string? combo = vk == 0x1B && m == Mods.None ? null : Format(m, vk);
                    MaskIfNeeded(m);
                    _swallowUp = true;
                    rec(combo);
                    return 1;
                }
                if (_vk != 0 && vk == _vk)
                {
                    if (down && CurrentMods() == _mods)
                    {
                        MaskIfNeeded(_mods);
                        _swallowUp = true;
                        Pressed?.Invoke();
                        return 1;
                    }
                    if (up && _swallowUp) { _swallowUp = false; return 1; }
                }
                else if (up && _swallowUp && _recording is null) _swallowUp = false;
            }
        }
        return CallNextHookEx(_hook, code, wParam, lParam);
    }

    /// <summary>Tecla "máscara": impede que soltar Win abra o Iniciar e que soltar Alt ative a barra de menus.</summary>
    private static void MaskIfNeeded(Mods m)
    {
        if ((m & (Mods.Win | Mods.Alt)) == 0) return;
        keybd_event(0xE8, 0, 0, UIntPtr.Zero);
        keybd_event(0xE8, 0, 2, UIntPtr.Zero);
    }

    private static bool IsModifier(int vk) => vk is 0x10 or 0x11 or 0x12 or 0x5B or 0x5C or 0xA0 or 0xA1 or 0xA2 or 0xA3 or 0xA4 or 0xA5;

    private static Mods CurrentMods()
    {
        var m = Mods.None;
        if (GetAsyncKeyState(0x11) < 0) m |= Mods.Ctrl;
        if (GetAsyncKeyState(0x12) < 0) m |= Mods.Alt;
        if (GetAsyncKeyState(0x10) < 0) m |= Mods.Shift;
        if (GetAsyncKeyState(0x5B) < 0 || GetAsyncKeyState(0x5C) < 0) m |= Mods.Win;
        return m;
    }

    public static string Format(string? combo) => TryParse(combo, out var m, out int vk) ? Format(m, vk) : string.Empty;

    private static string Format(Mods m, int vk)
    {
        var parts = new List<string>();
        if (m.HasFlag(Mods.Ctrl)) parts.Add("Ctrl");
        if (m.HasFlag(Mods.Alt)) parts.Add("Alt");
        if (m.HasFlag(Mods.Shift)) parts.Add("Shift");
        if (m.HasFlag(Mods.Win)) parts.Add("Win");
        parts.Add(((Windows.System.VirtualKey)vk).ToString());
        return string.Join("+", parts);
    }

    private static bool TryParse(string? combo, out Mods mods, out int vk)
    {
        mods = Mods.None; vk = 0;
        if (string.IsNullOrWhiteSpace(combo)) return false;
        foreach (var raw in combo.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            switch (raw.ToLowerInvariant())
            {
                case "ctrl" or "control": mods |= Mods.Ctrl; break;
                case "alt": mods |= Mods.Alt; break;
                case "shift": mods |= Mods.Shift; break;
                case "win" or "windows": mods |= Mods.Win; break;
                default:
                    if (Enum.TryParse<Windows.System.VirtualKey>(raw, true, out var key)) vk = (int)key;
                    else return false;
                    break;
            }
        }
        return vk != 0 && !IsModifier(vk);
    }

    [StructLayout(LayoutKind.Sequential)] private struct KBDLLHOOKSTRUCT { public uint vkCode, scanCode, flags, time; public UIntPtr extra; }
    [StructLayout(LayoutKind.Sequential)] private struct MSG { public IntPtr hwnd; public uint message; public UIntPtr wParam; public IntPtr lParam; public uint time; public int x, y; }
    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr SetWindowsHookEx(int id, HookProc proc, IntPtr mod, uint thread);
    [DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32.dll")] private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string name);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int vk);
    [DllImport("user32.dll")] private static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);
    [DllImport("user32.dll")] private static extern UIntPtr SetTimer(IntPtr hwnd, UIntPtr id, uint ms, IntPtr proc);
    [DllImport("user32.dll")] private static extern int GetMessage(out MSG msg, IntPtr hwnd, uint min, uint max);
    [DllImport("user32.dll")] private static extern bool TranslateMessage(ref MSG msg);
    [DllImport("user32.dll")] private static extern IntPtr DispatchMessage(ref MSG msg);
}
