#nullable disable
using System.Runtime.InteropServices;

namespace WinStatusBar.Clip;

/// <summary>
/// Win+V (area de transferencia) e Win+. / Win+; (emojis), no lugar do painel nativo.
/// Hook de teclado de baixo nivel numa thread propria. O Windows remove esses hooks sem avisar
/// (apos suspender, bloquear a tela, ou quando o processo demora a responder, por exemplo numa coleta de lixo
/// do .NET), por isso ele e reinstalado a cada 5 segundos (antes 30: o Win+V ficava ate meio minuto sem funcionar).
/// </summary>
public sealed class KeyboardHook
{
    public event Action<string> Pressed;   // "clip" ou "emoji"
    Native.HookProc proc;
    IntPtr hook;

    // Diagnóstico: quanto tempo o Windows esperou pelo gancho (do momento da tecla até a chamada).
    int lateKeys, maxLag;
    long lastPauseTicks;

    public void Start()
    {
        var t = new Thread(() =>
        {
            proc = Callback;
            Install();
            lastPauseTicks = GC.GetTotalPauseDuration().Ticks;
            Native.SetTimer(IntPtr.Zero, UIntPtr.Zero, 5000, IntPtr.Zero);
            while (Native.GetMessage(out var m, IntPtr.Zero, 0, 0) > 0)
            {
                if (m.message == 0x0113) { Install(); Report(); continue; } // WM_TIMER
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

    void Report()
    {
        int late = Interlocked.Exchange(ref lateKeys, 0), lag = Interlocked.Exchange(ref maxLag, 0);
        long pause = GC.GetTotalPauseDuration().Ticks;
        double pausedMs = TimeSpan.FromTicks(pause - lastPauseTicks).TotalMilliseconds;
        lastPauseTicks = pause;
        var info = GC.GetGCMemoryInfo(GCKind.Any);
        double lastPause = info.PauseDurations.Length > 0 ? info.PauseDurations[0].TotalMilliseconds + (info.PauseDurations.Length > 1 ? info.PauseDurations[1].TotalMilliseconds : 0) : 0;
        if (late > 0 || pausedMs > 150)
            Log.Write($"Diagnóstico do gancho: {late} teclas atrasadas (maior atraso {lag} ms); coletor de lixo parou o processo {pausedMs:N0} ms nos últimos 5 s (última coleta: geração {info.Generation}, {lastPause:N0} ms, {(info.Compacted ? "compactou" : "sem compactar")}, heap {info.HeapSizeBytes / 1048576} MB).");
    }

    IntPtr Callback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            var kb = Marshal.PtrToStructure<Native.KBDLLHOOKSTRUCT>(lParam);
            int lag = Environment.TickCount - (int)kb.time;
            if (lag > 150 && lag < 60000)
            {
                Interlocked.Increment(ref lateKeys);
                if (lag > maxLag) maxLag = lag;
            }
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
                        int l = lag;
                        ThreadPool.QueueUserWorkItem(_ => Log.Write($"Atalho {target}: tecla recebida pelo gancho {l} ms depois de pressionada."));
                        Pressed?.Invoke(target);
                    }
                    return (IntPtr)1;
                }
            }
        }
        return Native.CallNextHookEx(hook, nCode, wParam, lParam);
    }
}
