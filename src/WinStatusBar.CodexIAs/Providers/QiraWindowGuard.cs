using System.Diagnostics;
using System.Runtime.InteropServices;

namespace CodexIAs.Providers;

/// <summary>
/// Esconde, no instante em que aparecem, as janelas pequenas do Hub do Qira aberto por este app (barrinha de comando,
/// bolha flutuante). A janela grande do app (configurações), quando aberta de propósito, continua aparecendo.
/// Mesmo mecanismo da WinStatusBar: gancho de eventos do Windows numa thread própria com fila de mensagens.
/// </summary>
internal static class QiraWindowGuard
{
    private static readonly object Sync = new();
    private static int _pid;
    private static uint _threadId;
    private static WinEventProc? _proc;
    private static DateTime _hideAllUntil;

    public static void Watch(int pid, TimeSpan hideAllFor = default)
    {
        lock (Sync)
        {
            if (hideAllFor > TimeSpan.Zero) _hideAllUntil = DateTime.UtcNow + hideAllFor;
            if (_pid == pid && _threadId != 0) return;
            if (_threadId != 0) PostThreadMessage(_threadId, WM_QUIT, IntPtr.Zero, IntPtr.Zero);
            _threadId = 0;
            _pid = pid;
            var ready = new ManualResetEventSlim();
            var thread = new Thread(() => Run(pid, ready)) { IsBackground = true, Name = "QiraWindowGuard" };
            thread.Start();
            ready.Wait(2000);
        }
    }

    /// <summary>Janela pequena (barrinha, bolha). Minimizada, a janela grande informa 160×28: não é "pequena"; escondê-la a tiraria da barra de tarefas.</summary>
    internal static bool ShouldHide(int width, int height, bool minimized) => !minimized && (height < 360 || width < 420);

    private static void HideIfSmall(IntPtr h)
    {
        if (!IsWindowVisible(h) || GetAncestor(h, GA_ROOT) != h) return;
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
        EnumWindows((h, _) => { GetWindowThreadProcessId(h, out uint owner); if (owner == pid) HideIfSmall(h); return true; }, IntPtr.Zero);
        ready.Set();
        Process? hub = null;
        try { hub = Process.GetProcessById(pid); } catch { }
        uint me = _threadId;
        using var timer = new System.Threading.Timer(_ => { try { if (hub is null || hub.HasExited) PostThreadMessage(me, WM_QUIT, IntPtr.Zero, IntPtr.Zero); } catch { } }, null, 2000, 2000);
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
