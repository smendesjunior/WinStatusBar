using System.Diagnostics;
using System.Runtime.InteropServices;

namespace WinStatusBar.Services;

/// <summary>
/// Esconde, no instante em que aparecem, as janelas pequenas do Hub do Qira aberto pela barra (barrinha de comando,
/// bolha flutuante, janelas auxiliares). A pessoa usa o Qira só pela barra; a janela grande do app (configurações,
/// conectores), quando aberta de propósito, continua aparecendo.
/// Usa um gancho de eventos do Windows (sem varredura periódica) numa thread própria com fila de mensagens.
/// </summary>
internal static class QiraWindowGuard
{
    private static readonly object Sync = new();
    private static int _pid;
    private static uint _threadId;
    private static WinEventProc? _proc; // mantém o delegate vivo enquanto o gancho existir
    private static DateTime _pausedUntil, _hideAllUntil;

    /// <summary>Deixa o Qira aparecer por um tempo (quando a pessoa pede para abrir o app oficial pelo menu da barra).</summary>
    public static void Pause(TimeSpan duration) => _pausedUntil = DateTime.UtcNow + duration;

    /// <summary>Passa a vigiar o Hub indicado (troca o anterior, se houver).</summary>
    /// <param name="hideAllFor">Logo após abrir o Hub, esconde também as janelas grandes (tela de carregamento).</param>
    public static void Watch(int pid, TimeSpan hideAllFor = default)
    {
        lock (Sync)
        {
            if (hideAllFor > TimeSpan.Zero) _hideAllUntil = DateTime.UtcNow + hideAllFor;
            if (_pid == pid && _threadId != 0) return;
            Stop();
            _pid = pid;
            var ready = new ManualResetEventSlim();
            var thread = new Thread(() => Run(pid, ready)) { IsBackground = true, Name = "QiraWindowGuard" };
            thread.Start();
            ready.Wait(2000);
        }
    }

    private static void Stop()
    {
        if (_threadId != 0) PostThreadMessage(_threadId, WM_QUIT, IntPtr.Zero, IntPtr.Zero);
        _threadId = 0;
        _pid = 0;
    }

    /// <summary>Janela que deve ficar escondida: de nível superior e pequena (a janela principal do app é grande).</summary>
    /// <remarks>Minimizada, a janela grande informa um retângulo minúsculo (160×28): não é "pequena"; escondê-la a tiraria da barra de tarefas.</remarks>
    internal static bool ShouldHide(int width, int height, bool minimized) => !minimized && (height < 360 || width < 420);

    private static void HideIfSmall(IntPtr h)
    {
        if (DateTime.UtcNow < _pausedUntil || !IsWindowVisible(h) || GetAncestor(h, GA_ROOT) != h) return;
        if (!GetWindowRect(h, out var r)) return;
        if (DateTime.UtcNow >= _hideAllUntil && !ShouldHide(r.Right - r.Left, r.Bottom - r.Top, IsIconic(h))) return;
        ShowWindow(h, SW_HIDE);
    }
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr h);

    private static void Run(int pid, ManualResetEventSlim ready)
    {
        _threadId = GetCurrentThreadId();
        _proc = (hook, ev, h, idObject, idChild, thread, time) => { if (idObject == 0 && idChild == 0 && h != IntPtr.Zero) HideIfSmall(h); };
        IntPtr hook = SetWinEventHook(EVENT_OBJECT_SHOW, EVENT_OBJECT_LOCATIONCHANGE, IntPtr.Zero, _proc, (uint)pid, 0, WINEVENT_OUTOFCONTEXT);
        // Janelas já visíveis antes do gancho.
        EnumWindows((h, _) => { GetWindowThreadProcessId(h, out uint owner); if (owner == pid) HideIfSmall(h); return true; }, IntPtr.Zero);
        ready.Set();
        Process? hub = null;
        try { hub = Process.GetProcessById(pid); } catch { }
        uint me = _threadId;
        // Encerra quando o Hub fechar.
        using var timer = new Timer(_ => { try { if (hub is null || hub.HasExited) PostThreadMessage(me, WM_QUIT, IntPtr.Zero, IntPtr.Zero); } catch { } }, null, 2000, 2000);
        while (GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0) { TranslateMessage(ref msg); DispatchMessage(ref msg); }
        if (hook != IntPtr.Zero) UnhookWinEvent(hook);
        hub?.Dispose();
        lock (Sync) { if (_pid == pid) { _pid = 0; _threadId = 0; } }
    }

    private const uint EVENT_OBJECT_SHOW = 0x8002, EVENT_OBJECT_LOCATIONCHANGE = 0x800B, WINEVENT_OUTOFCONTEXT = 0, WM_QUIT = 0x0012, GA_ROOT = 2;
    private const int SW_HIDE = 0;
    private delegate void WinEventProc(IntPtr hook, uint ev, IntPtr hwnd, int idObject, int idChild, uint thread, uint time);
    private delegate bool EnumProc(IntPtr h, IntPtr p);
    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct MSG { public IntPtr Hwnd; public uint Message; public IntPtr WParam, LParam; public uint Time; public int X, Y; }
    [DllImport("user32.dll")] private static extern IntPtr SetWinEventHook(uint min, uint max, IntPtr mod, WinEventProc proc, uint pid, uint thread, uint flags);
    [DllImport("user32.dll")] private static extern bool UnhookWinEvent(IntPtr hook);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumProc proc, IntPtr p);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr h, uint flags);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr h, int cmd);
    [DllImport("user32.dll")] private static extern int GetMessage(out MSG msg, IntPtr h, uint min, uint max);
    [DllImport("user32.dll")] private static extern bool TranslateMessage(ref MSG msg);
    [DllImport("user32.dll")] private static extern IntPtr DispatchMessage(ref MSG msg);
    [DllImport("user32.dll")] private static extern bool PostThreadMessage(uint thread, uint msg, IntPtr w, IntPtr l);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
}
