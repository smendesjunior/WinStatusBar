#nullable disable
using System.Runtime.InteropServices;

namespace WinStatusBar.Clip;

/// <summary>
/// Win+V (area de transferencia) e Win+. / Win+; (emojis), no lugar do painel nativo.
/// Hook de teclado de baixo nivel numa thread propria. O Windows remove esses hooks sem avisar
/// (apos suspender, bloquear a tela ou se o sistema travar por um instante), por isso ele e
/// reinstalado a cada 30 segundos.
/// </summary>
public sealed class KeyboardHook
{
    public event Action<string> Pressed;   // "clip" ou "emoji"
    Native.HookProc proc;
    IntPtr hook;

    public void Start()
    {
        var t = new Thread(() =>
        {
            proc = Callback;
            Install();
            Native.SetTimer(IntPtr.Zero, UIntPtr.Zero, 30000, IntPtr.Zero);
            while (Native.GetMessage(out var m, IntPtr.Zero, 0, 0) > 0)
            {
                if (m.message == 0x0113) { Install(); continue; } // WM_TIMER
                Native.TranslateMessage(ref m);
                Native.DispatchMessage(ref m);
            }
        }) { IsBackground = true, Name = "WinStatusBar.ClipHook" };
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
    }

    void Install()
    {
        IntPtr novo = Native.SetWindowsHookEx(13, proc, Native.GetModuleHandle("user32"), 0);
        if (novo == IntPtr.Zero) { Log.Write("SetWindowsHookEx falhou: " + Marshal.GetLastWin32Error()); return; }
        IntPtr antigo = hook;
        hook = novo;
        if (antigo != IntPtr.Zero) Native.UnhookWindowsHookEx(antigo);
    }

    IntPtr Callback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            var kb = Marshal.PtrToStructure<Native.KBDLLHOOKSTRUCT>(lParam);
            bool injected = (kb.flags & 0x10) != 0;
            string target = kb.vkCode switch { 0x56 => "clip", 0xBE or 0xBA or 0xBF => "emoji", _ => null };
            // Atalho desligado nas configurações (ou recurso desativado): deixa o Windows abrir o painel nativo.
            if (target == "clip" && !ClipHost.HandlesWinV || target == "emoji" && !ClipHost.HandlesEmojiKeys) target = null;
            if (!injected && target != null)
            {
                bool win = Native.GetAsyncKeyState(0x5B) < 0 || Native.GetAsyncKeyState(0x5C) < 0;
                bool other = Native.GetAsyncKeyState(0x11) < 0 || Native.GetAsyncKeyState(0x12) < 0 || Native.GetAsyncKeyState(0x10) < 0;
                // Win+; no teclado ABNT2 fica na tecla 0xBF; nos teclados americanos, 0xBA
                if (win && !other)
                {
                    int msg = wParam.ToInt32();
                    if (msg is 0x100 or 0x104)
                    {
                        // tecla "mascara": impede o menu Iniciar de abrir ao soltar o Win
                        Native.keybd_event(0xE8, 0, 0, UIntPtr.Zero);
                        Native.keybd_event(0xE8, 0, 2, UIntPtr.Zero);
                        Pressed?.Invoke(target);
                    }
                    return (IntPtr)1;
                }
            }
        }
        return Native.CallNextHookEx(hook, nCode, wParam, lParam);
    }
}
