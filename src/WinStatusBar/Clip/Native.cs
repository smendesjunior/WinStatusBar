#nullable disable
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace WinStatusBar.Clip;

/// <summary>Chamadas Win32 usadas pelo app (atalhos globais, foco, colar, bandeja, fonte de emoji).</summary>
internal static class Native
{
    // ------------------------------------------------------------------ janelas e foco
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern bool BringWindowToTop(IntPtr h);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] public static extern bool AttachThreadInput(uint a, uint b, bool attach);
    [DllImport("kernel32.dll")] public static extern uint GetCurrentThreadId();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetClassName(IntPtr h, StringBuilder sb, int max);
    [DllImport("dwmapi.dll")] public static extern int DwmSetWindowAttribute(IntPtr h, int attr, ref int val, int size);
    [DllImport("user32.dll")] public static extern uint GetDpiForWindow(IntPtr h);

    public static void ForceForeground(IntPtr hwnd)
    {
        IntPtr fg = GetForegroundWindow();
        uint fgThread = GetWindowThreadProcessId(fg, out _);
        uint me = GetCurrentThreadId();
        if (fgThread != 0 && fgThread != me)
        {
            AttachThreadInput(fgThread, me, true);
            SetForegroundWindow(hwnd);
            BringWindowToTop(hwnd);
            AttachThreadInput(fgThread, me, false);
        }
        else SetForegroundWindow(hwnd);
    }

    public static bool IsShellWindow(IntPtr h)
    {
        var sb = new StringBuilder(64);
        GetClassName(h, sb, 64);
        string c = sb.ToString();
        return c is "Shell_TrayWnd" or "Shell_SecondaryTrayWnd" or "NotifyIconOverflowWindow" or "TopLevelWindowForOverflowXamlIsland" or "Progman" or "WorkerW";
    }

    // ------------------------------------------------------------------ posicao (cursor de texto / monitor)
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)]
    public struct GUITHREADINFO
    {
        public int cbSize, flags;
        public IntPtr hwndActive, hwndFocus, hwndCapture, hwndMenuOwner, hwndMoveSize, hwndCaret;
        public RECT rcCaret;
    }
    [StructLayout(LayoutKind.Sequential)]
    public struct MONITORINFO { public int cbSize; public RECT rcMonitor, rcWork; public uint dwFlags; }
    [DllImport("user32.dll")] public static extern bool GetGUIThreadInfo(uint tid, ref GUITHREADINFO info);
    [DllImport("user32.dll")] public static extern bool ClientToScreen(IntPtr h, ref POINT p);
    [DllImport("user32.dll")] public static extern bool GetCursorPos(out POINT p);
    [DllImport("user32.dll")] public static extern IntPtr MonitorFromPoint(POINT pt, uint flags);
    [DllImport("user32.dll")] public static extern bool GetMonitorInfo(IntPtr mon, ref MONITORINFO mi);
    [DllImport("shcore.dll")] public static extern int GetDpiForMonitor(IntPtr mon, int type, out uint dpiX, out uint dpiY);

    /// <summary>Ponto (em pixels fisicos) onde abrir o painel: abaixo do cursor de texto, ou no ponteiro do mouse.</summary>
    public static POINT CaretOrCursor(IntPtr fg)
    {
        GetCursorPos(out var pt);
        if (fg != IntPtr.Zero)
        {
            uint tid = GetWindowThreadProcessId(fg, out _);
            var gi = new GUITHREADINFO { cbSize = Marshal.SizeOf<GUITHREADINFO>() };
            if (GetGUIThreadInfo(tid, ref gi) && gi.hwndCaret != IntPtr.Zero)
            {
                var p = new POINT { X = gi.rcCaret.Left, Y = gi.rcCaret.Bottom };
                ClientToScreen(gi.hwndCaret, ref p);
                pt = new POINT { X = p.X, Y = p.Y + 6 };
            }
        }
        return pt;
    }

    // ------------------------------------------------------------------ teclado (colar, digitar emoji)
    [StructLayout(LayoutKind.Sequential)] struct MOUSEINPUT { public int dx, dy; public uint mouseData, dwFlags, time; public IntPtr dwExtraInfo; }
    [StructLayout(LayoutKind.Sequential)] struct KEYBDINPUT { public ushort wVk, wScan; public uint dwFlags, time; public IntPtr dwExtraInfo; }
    [StructLayout(LayoutKind.Explicit)] struct INPUTUNION { [FieldOffset(0)] public MOUSEINPUT mi; [FieldOffset(0)] public KEYBDINPUT ki; }
    [StructLayout(LayoutKind.Sequential)] struct INPUT { public uint type; public INPUTUNION u; }
    [DllImport("user32.dll", SetLastError = true)] static extern uint SendInput(uint n, INPUT[] inputs, int size);
    [DllImport("user32.dll")] public static extern short GetAsyncKeyState(int vk);
    const uint KEYEVENTF_KEYUP = 0x2, KEYEVENTF_UNICODE = 0x4;

    static INPUT Key(ushort vk, bool up) => new() { type = 1, u = new INPUTUNION { ki = new KEYBDINPUT { wVk = vk, dwFlags = up ? KEYEVENTF_KEYUP : 0 } } };

    /// <summary>Envia Ctrl+V; devolve quantas teclas o Windows aceitou (0 = recusado).</summary>
    public static uint SendCtrlV()
    {
        var list = new List<INPUT>();
        foreach (int mod in new[] { 0x10, 0x12, 0x5B, 0x5C }) // solta Shift/Alt/Win se ainda estiverem pressionadas
            if (GetAsyncKeyState(mod) < 0) list.Add(Key((ushort)mod, true));
        list.Add(Key(0x11, false)); list.Add(Key(0x56, false)); list.Add(Key(0x56, true)); list.Add(Key(0x11, true));
        return SendInput((uint)list.Count, list.ToArray(), Marshal.SizeOf<INPUT>());
    }

    [DllImport("user32.dll")] public static extern bool IsWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern IntPtr GetAncestor(IntPtr h, uint flags);
    [DllImport("kernel32.dll", SetLastError = true)] static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);
    [DllImport("advapi32.dll", SetLastError = true)] static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);
    [DllImport("advapi32.dll", SetLastError = true)] static extern bool GetTokenInformation(IntPtr token, int cls, out int info, int size, out int ret);

    /// <summary>A janela está em primeiro plano (ela ou a janela principal dela).</summary>
    public static bool IsForeground(IntPtr h)
    {
        IntPtr fg = GetForegroundWindow();
        return fg != IntPtr.Zero && (fg == h || GetAncestor(fg, 2 /* GA_ROOT */) == GetAncestor(h, 2));
    }

    /// <summary>
    /// O app da janela roda como administrador e a barra não: o Windows descarta em silêncio as teclas que a barra
    /// envia para ele (UIPI). Nesse caso o item fica na área de transferência para o Ctrl+V da própria pessoa.
    /// </summary>
    public static bool IsElevatedAboveUs(IntPtr h)
    {
        GetWindowThreadProcessId(h, out uint pid);
        return pid != 0 && IsElevated(pid) && !IsElevated((uint)Environment.ProcessId);
    }

    static bool IsElevated(uint pid)
    {
        IntPtr p = OpenProcess(0x1000 /* PROCESS_QUERY_LIMITED_INFORMATION */, false, pid);
        if (p == IntPtr.Zero) return false;
        try
        {
            if (!OpenProcessToken(p, 0x0008 /* TOKEN_QUERY */, out IntPtr t)) return false;
            try { return GetTokenInformation(t, 20 /* TokenElevation */, out int e, 4, out _) && e != 0; }
            finally { CloseHandle(t); }
        }
        finally { CloseHandle(p); }
    }

    /// <summary>Nome da classe e do processo da janela, para o log.</summary>
    public static string Describe(IntPtr h)
    {
        if (h == IntPtr.Zero) return "(nenhuma)";
        var sb = new StringBuilder(64);
        GetClassName(h, sb, 64);
        GetWindowThreadProcessId(h, out uint pid);
        string name = "?";
        try { using var p = Process.GetProcessById((int)pid); name = p.ProcessName; } catch { }
        return $"{name}/{sb}";
    }

    /// <summary>Digita o texto no app em foco (como o painel de emojis nativo, sem usar a area de transferencia).</summary>
    public static void SendText(string s)
    {
        var list = new List<INPUT>();
        foreach (char ch in s)
        {
            list.Add(new INPUT { type = 1, u = new INPUTUNION { ki = new KEYBDINPUT { wScan = ch, dwFlags = KEYEVENTF_UNICODE } } });
            list.Add(new INPUT { type = 1, u = new INPUTUNION { ki = new KEYBDINPUT { wScan = ch, dwFlags = KEYEVENTF_UNICODE | KEYEVENTF_KEYUP } } });
        }
        SendInput((uint)list.Count, list.ToArray(), Marshal.SizeOf<INPUT>());
    }

    // ------------------------------------------------------------------ area de transferencia
    [DllImport("user32.dll", SetLastError = true)] public static extern bool AddClipboardFormatListener(IntPtr h);
    [DllImport("user32.dll")] public static extern IntPtr GetClipboardOwner();
    public delegate IntPtr SUBCLASSPROC(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam, UIntPtr id, UIntPtr refData);
    [DllImport("comctl32.dll")] public static extern bool SetWindowSubclass(IntPtr h, SUBCLASSPROC proc, UIntPtr id, UIntPtr refData);
    [DllImport("comctl32.dll")] public static extern IntPtr DefSubclassProc(IntPtr h, uint msg, IntPtr w, IntPtr l);

    /// <summary>Nome amigavel do app que copiou (ou null). isSelf = foi este proprio app.</summary>
    public static string ClipboardSourceApp(out bool isSelf) => ClipboardSourceApp(out isSelf, out _);

    /// <summary>Também devolve o nome do processo (ex.: "KeePass"), usado na lista de apps ignorados.</summary>
    public static string ClipboardSourceApp(out bool isSelf, out string processName)
    {
        isSelf = false; processName = null;
        IntPtr h = GetClipboardOwner();
        if (h == IntPtr.Zero) h = GetForegroundWindow();
        if (h == IntPtr.Zero) return null;
        GetWindowThreadProcessId(h, out uint pid);
        if (pid == (uint)Environment.ProcessId) { isSelf = true; return null; }
        try
        {
            using var p = Process.GetProcessById((int)pid);
            processName = p.ProcessName;
            try
            {
                string d = p.MainModule?.FileVersionInfo.FileDescription;
                if (!string.IsNullOrWhiteSpace(d)) return d.Trim();
            }
            catch { }
            return p.ProcessName;
        }
        catch { return null; }
    }

    // ------------------------------------------------------------------ hook de teclado e loop de mensagens
    [StructLayout(LayoutKind.Sequential)] public struct KBDLLHOOKSTRUCT { public uint vkCode, scanCode, flags, time; public IntPtr extra; }
    public delegate IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll", SetLastError = true)] public static extern IntPtr SetWindowsHookEx(int id, HookProc proc, IntPtr hMod, uint threadId);
    [DllImport("user32.dll")] public static extern IntPtr CallNextHookEx(IntPtr h, int n, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] public static extern bool UnhookWindowsHookEx(IntPtr h);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr GetModuleHandle(string name);
    [StructLayout(LayoutKind.Sequential)] public struct MSG { public IntPtr hwnd; public uint message; public IntPtr wParam, lParam; public uint time; public POINT pt; }
    [DllImport("user32.dll")] public static extern int GetMessage(out MSG m, IntPtr h, uint min, uint max);
    [DllImport("user32.dll")] public static extern bool TranslateMessage(ref MSG m);
    [DllImport("user32.dll")] public static extern IntPtr DispatchMessage(ref MSG m);
    [DllImport("user32.dll")] public static extern UIntPtr SetTimer(IntPtr h, UIntPtr id, uint ms, IntPtr proc);
    [DllImport("user32.dll")] public static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);

    // ------------------------------------------------------------------ bandeja (Shell_NotifyIcon + menu nativo)
    public delegate IntPtr WNDPROC(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct WNDCLASSEX
    {
        public uint cbSize, style; public IntPtr lpfnWndProc; public int cbClsExtra, cbWndExtra;
        public IntPtr hInstance, hIcon, hCursor, hbrBackground; public string lpszMenuName, lpszClassName; public IntPtr hIconSm;
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct NOTIFYICONDATA
    {
        public uint cbSize; public IntPtr hWnd; public uint uID, uFlags, uCallbackMessage; public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
        public uint dwState, dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
        public uint uVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
        public uint dwInfoFlags; public Guid guidItem; public IntPtr hBalloonIcon;
    }
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern ushort RegisterClassEx(ref WNDCLASSEX wc);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr CreateWindowEx(uint exStyle, string cls, string name, uint style, int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr inst, IntPtr param);
    [DllImport("user32.dll")] public static extern IntPtr DefWindowProc(IntPtr h, uint msg, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] public static extern bool DestroyWindow(IntPtr h);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern uint RegisterWindowMessage(string s);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] public static extern bool Shell_NotifyIcon(uint msg, ref NOTIFYICONDATA d);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] public static extern uint ExtractIconEx(string file, int index, out IntPtr large, out IntPtr small, uint n);
    [DllImport("user32.dll")] public static extern bool DestroyIcon(IntPtr h);
    [DllImport("user32.dll")] public static extern IntPtr CreatePopupMenu();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern bool AppendMenu(IntPtr menu, uint flags, UIntPtr id, string text);
    [DllImport("user32.dll")] public static extern int TrackPopupMenuEx(IntPtr menu, uint flags, int x, int y, IntPtr h, IntPtr tpm);
    [DllImport("user32.dll")] public static extern bool DestroyMenu(IntPtr menu);

    // ------------------------------------------------------------------ cobertura da fonte de emoji
    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)]
    static extern IntPtr CreateFont(int h, int w, int esc, int orient, int weight, uint italic, uint underline, uint strike, uint charset, uint outPrec, uint clipPrec, uint quality, uint pitch, string face);
    [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleDC(IntPtr hdc);
    [DllImport("gdi32.dll")] static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);
    [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr obj);
    [DllImport("gdi32.dll")] static extern bool DeleteDC(IntPtr hdc);
    [DllImport("gdi32.dll")] static extern uint GetFontData(IntPtr hdc, uint table, uint offset, byte[] buffer, uint length);

    /// <summary>Le a tabela 'cmap' da fonte (quais caracteres ela realmente desenha).</summary>
    public static byte[] ReadCmap(string face)
    {
        IntPtr dc = CreateCompatibleDC(IntPtr.Zero);
        IntPtr font = CreateFont(-24, 0, 0, 0, 400, 0, 0, 0, 1, 0, 0, 0, 0, face);
        IntPtr old = SelectObject(dc, font);
        try
        {
            const uint CMAP = 0x70616D63; // 'cmap' em little-endian
            uint size = GetFontData(dc, CMAP, 0, null, 0);
            if (size == 0 || size == 0xFFFFFFFF) return null;
            var buf = new byte[size];
            return GetFontData(dc, CMAP, 0, buf, size) == size ? buf : null;
        }
        finally { SelectObject(dc, old); DeleteObject(font); DeleteDC(dc); }
    }
}
