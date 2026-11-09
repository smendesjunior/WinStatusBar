using System.Runtime.InteropServices;
using static WinStatusBar.Interop.Win32;

namespace WinStatusBar.Services;

/// <summary>Ícone na área de notificação (Shell_NotifyIcon) com janela de mensagens própria e menu nativo.</summary>
public sealed class TrayIconService : IDisposable
{
    public enum Command { Settings = 1, Refresh, RestartIntegration, Diagnostics, Exit, Clipboard, Emoji, ClipPause, ClipData }

    private static readonly uint CallbackMsg = WM_USER + 77;
    private static readonly uint TaskbarCreated = RegisterWindowMessageW("TaskbarCreated");
    private readonly WNDPROC _proc;
    private readonly IntPtr _hwnd;
    private IntPtr _icon;
    private bool _added;

    public event Action<Command>? CommandInvoked;
    public event Action<int, int>? MenuRequested;
    public event Action? HardwareChanged;

    public TrayIconService(string iconPath)
    {
        _proc = WndProc;
        var wc = new WNDCLASSEX
        {
            cbSize = (uint)Marshal.SizeOf<WNDCLASSEX>(),
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_proc),
            hInstance = GetModuleHandleW(null),
            lpszClassName = "WinStatusBar.TrayWindow",
        };
        RegisterClassExW(ref wc);
        // Janela oculta comum (não message-only) para receber a mensagem TaskbarCreated em broadcast.
        _hwnd = CreateWindowExW(WS_EX_TOOLWINDOW_U, wc.lpszClassName, "WinStatusBar Tray", 0, 0, 0, 0, 0, IntPtr.Zero, IntPtr.Zero, wc.hInstance, IntPtr.Zero);
        _iconPath = iconPath;
        LoadIcon();
        Add();
    }

    private readonly string _iconPath;

    /// <summary>
    /// Carrega o quadro do .ico no tamanho que a bandeja usa na escala atual (16 px a 100%, 24 px a 150%…).
    /// Um ícone de 16 px esticado pelo Shell, ou um quadro PNG pequeno, pode aparecer em branco no Windows 11.
    /// </summary>
    private void LoadIcon()
    {
        if (_icon != IntPtr.Zero) { DestroyIcon(_icon); _icon = IntPtr.Zero; }
        if (!File.Exists(_iconPath)) return;
        try
        {
            int size = GetSystemMetricsForDpi(SM_CXSMICON, GetDpiForSystem());
            _icon = LoadImageW(IntPtr.Zero, _iconPath, IMAGE_ICON, size, size, LR_LOADFROMFILE);
        }
        catch { _icon = IntPtr.Zero; }
        if (_icon == IntPtr.Zero) ExtractIconExW(_iconPath, 0, out _, out _icon, 1);
        if (_icon == IntPtr.Zero) App.Log(global::WinStatusBar.Services.Localization.T("Bandeja: não foi possível carregar o ícone ") + _iconPath);
    }

    private const int SM_CXSMICON = 49;
    private const uint IMAGE_ICON = 1, LR_LOADFROMFILE = 0x10;
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr LoadImageW(IntPtr hInst, string name, uint type, int cx, int cy, uint fuLoad);
    [DllImport("user32.dll")] private static extern uint GetDpiForSystem();
    [DllImport("user32.dll")] private static extern int GetSystemMetricsForDpi(int index, uint dpi);

    private const uint WS_EX_TOOLWINDOW_U = 0x80, WM_SETTINGCHANGE_U = 0x001A, WM_DISPLAYCHANGE_U = 0x007E;

    private NOTIFYICONDATA Data(string tip = "WinStatusBar") => new()
    {
        cbSize = (uint)Marshal.SizeOf<NOTIFYICONDATA>(),
        hWnd = _hwnd,
        uID = 1,
        uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP | NIF_SHOWTIP,
        uCallbackMessage = CallbackMsg,
        hIcon = _icon,
        szTip = tip,
        szInfo = string.Empty,
        szInfoTitle = string.Empty,
    };

    private void Add()
    {
        var d = Data();
        _added = Shell_NotifyIconW(NIM_ADD, ref d);
        d.uVersion = NOTIFYICON_VERSION_4;
        Shell_NotifyIconW(NIM_SETVERSION, ref d);
    }

    public void SetTooltip(string text)
    {
        if (!_added) return;
        var d = Data(text.Length > 120 ? text[..120] : text);
        Shell_NotifyIconW(NIM_MODIFY, ref d);
    }

    public void ShowBalloon(string title, string message)
    {
        if (!_added) return;
        var d = Data();
        d.uFlags = NIF_INFO;
        d.szInfoTitle = title.Length > 60 ? title[..60] : title;
        d.szInfo = message.Length > 250 ? message[..250] : message;
        d.dwInfoFlags = NIIF_INFO | NIIF_NOSOUND;
        Shell_NotifyIconW(NIM_MODIFY, ref d);
    }

    private IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        // Broadcast de topologia, tela e energia. O destinatário só sinaliza o coletor.
        if (msg is 0x0219 or 0x0218 || msg == WM_DISPLAYCHANGE_U)
            HardwareChanged?.Invoke();
        if (msg == CallbackMsg)
        {
            uint ev = (uint)((long)lParam & 0xFFFF);
            if (ev is WM_RBUTTONUP or WM_CONTEXTMENU) ShowMenu();
            else if (ev is WM_LBUTTONUP or WM_LBUTTONDBLCLK) CommandInvoked?.Invoke(Command.Settings);
            return IntPtr.Zero;
        }
        // Explorer reiniciado ou escala alterada: recarrega no novo tamanho e registra de novo.
        if (msg == TaskbarCreated && TaskbarCreated != 0) { _added = false; LoadIcon(); Add(); return IntPtr.Zero; }
        if (msg == WM_SETTINGCHANGE_U || msg == WM_DISPLAYCHANGE_U)
        {
            LoadIcon();
            if (_added) { var d = Data(); Shell_NotifyIconW(NIM_MODIFY, ref d); }
        }
        return DefWindowProcW(hWnd, msg, wParam, lParam);
    }

    private void ShowMenu()
    {
        GetCursorPos(out var pt);
        MenuRequested?.Invoke(pt.X, pt.Y);
    }

    public void Dispose()
    {
        if (_added) { var d = Data(); Shell_NotifyIconW(NIM_DELETE, ref d); _added = false; }
        if (_icon != IntPtr.Zero) { DestroyIcon(_icon); _icon = IntPtr.Zero; }
        if (_hwnd != IntPtr.Zero) DestroyWindow(_hwnd);
    }
}
