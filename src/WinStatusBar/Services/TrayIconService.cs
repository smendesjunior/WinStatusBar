using System.Runtime.InteropServices;
using static WinStatusBar.Interop.Win32;

namespace WinStatusBar.Services;

/// <summary>Ícone na área de notificação (Shell_NotifyIcon) com janela de mensagens própria e menu nativo.</summary>
public sealed class TrayIconService : IDisposable
{
    public enum Command { Settings = 1, Refresh, RestartIntegration, Diagnostics, Exit, Clipboard, Emoji, ClipPause, ClipData }

    private static readonly uint CallbackMsg = WM_USER + 77;
    /// <summary>Clique no balão (0x405), entregue no LOWORD do lParam com NOTIFYICON_VERSION_4.</summary>
    private const uint NIN_BALLOONUSERCLICK = WM_USER + 5;
    private static readonly uint TaskbarCreated = RegisterWindowMessageW("TaskbarCreated");
    private readonly WNDPROC _proc;
    private readonly IntPtr _hwnd;
    private IntPtr _icon;
    private bool _added;

    public event Action<Command>? CommandInvoked;
    public event Action<int, int>? MenuRequested;
    public event Action? HardwareChanged;
    /// <summary>O usuário clicou no último balão mostrado.</summary>
    public event Action? BalloonClicked;

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
        WatchPresence();
    }

    // ------------------------------------------------------------------ Presença (tela da sessão e bloqueio)
    // Tela desta sessão (vale também em área de trabalho remota, ao contrário de GUID_CONSOLE_DISPLAY_STATE).
    private static readonly Guid SessionDisplayStatus = new("2b84c20e-ad23-4ddf-93db-05ffbd7efca5");
    private const int PBT_POWERSETTINGCHANGE = 0x8013, WM_WTSSESSION_CHANGE = 0x02B1, WTS_SESSION_LOCK = 0x7, WTS_SESSION_UNLOCK = 0x8;
    private IntPtr _displayNotification;

    /// <summary>
    /// Avisos do Windows para <see cref="WinStatusBar.Core.Presence"/>: tela da sessão ligada/desligada (o Windows manda
    /// o estado atual logo no registro) e sessão bloqueada/desbloqueada (o estado inicial vem de WTSQuerySessionInformation).
    /// </summary>
    private void WatchPresence()
    {
        if (_hwnd == IntPtr.Zero) return;
        var guid = SessionDisplayStatus;
        _displayNotification = RegisterPowerSettingNotification(_hwnd, ref guid, 0 /* DEVICE_NOTIFY_WINDOW_HANDLE */);
        WTSRegisterSessionNotification(_hwnd, 0 /* NOTIFY_FOR_THIS_SESSION */);
        if (SessionLocked() is bool locked) Core.Presence.SetLocked(locked);
    }

    /// <summary>
    /// Sessão atual bloqueada? WTSINFOEX: Level@0, WTSINFOEX_LEVEL1 alinhado a 8 (SessionId@8, SessionState@12,
    /// SessionFlags@16: 0 = bloqueada, 1 = desbloqueada; conferido em 10/11/2026).
    /// </summary>
    private static bool? SessionLocked()
    {
        try
        {
            if (!WTSQuerySessionInformationW(IntPtr.Zero, -1 /* WTS_CURRENT_SESSION */, 25 /* WTSSessionInfoEx */, out var buffer, out int bytes) || buffer == IntPtr.Zero)
                return null;
            try { return bytes >= 20 && Marshal.ReadInt32(buffer, 0) == 1 ? Marshal.ReadInt32(buffer, 16) switch { 0 => true, 1 => false, _ => null } : null; }
            finally { WTSFreeMemory(buffer); }
        }
        catch { return null; }
    }

    private static bool HandlePresence(uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == 0x0218 && (int)wParam == PBT_POWERSETTINGCHANGE && lParam != IntPtr.Zero)
        {
            // POWERBROADCAST_SETTING: GUID PowerSetting; DWORD DataLength; UCHAR Data[] (DWORD: 0 desligada, 1 ligada, 2 esmaecida).
            if (Marshal.PtrToStructure<Guid>(lParam) == SessionDisplayStatus && Marshal.ReadInt32(lParam, 16) >= 4)
                Core.Presence.SetDisplay(Marshal.ReadInt32(lParam, 20) != 0);
            return true;
        }
        if (msg == WM_WTSSESSION_CHANGE)
        {
            if ((int)wParam == WTS_SESSION_LOCK) Core.Presence.SetLocked(true);
            else if ((int)wParam == WTS_SESSION_UNLOCK) Core.Presence.SetLocked(false);
            return true;
        }
        return false;
    }

    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr RegisterPowerSettingNotification(IntPtr recipient, ref Guid setting, int flags);
    [DllImport("user32.dll")] private static extern bool UnregisterPowerSettingNotification(IntPtr handle);
    [DllImport("wtsapi32.dll")] private static extern bool WTSRegisterSessionNotification(IntPtr hwnd, int flags);
    [DllImport("wtsapi32.dll")] private static extern bool WTSUnRegisterSessionNotification(IntPtr hwnd);
    [DllImport("wtsapi32.dll", CharSet = CharSet.Unicode)] private static extern bool WTSQuerySessionInformationW(IntPtr server, int session, int infoClass, out IntPtr buffer, out int bytes);
    [DllImport("wtsapi32.dll")] private static extern void WTSFreeMemory(IntPtr memory);

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

    /// <summary>Dica que o ícone mostra agora (última enviada com sucesso); null depois de qualquer envio que a troque
    /// pela dica padrão (ícone registrado de novo, ícone recarregado), para a próxima ser enviada de novo.</summary>
    private string? _shownTip;

    private void Add()
    {
        var d = Data();
        _shownTip = null;
        _added = Shell_NotifyIconW(NIM_ADD, ref d);
        d.uVersion = NOTIFYICON_VERSION_4;
        Shell_NotifyIconW(NIM_SETVERSION, ref d);
    }

    public void SetTooltip(string text)
    {
        if (!_added) return;
        string tip = text.Length > 120 ? text[..120] : text;
        if (tip == _shownTip) return; // a barra pede a cada atualização dos agentes; o Shell só precisa saber quando muda
        var d = Data(tip);
        _shownTip = Shell_NotifyIconW(NIM_MODIFY, ref d) ? tip : null;
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
        // Tela ligada/desligada e bloqueio: não são mudança de hardware (não recarregam adaptadores nem sensores).
        if (HandlePresence(msg, wParam, lParam)) return msg == 0x0218 ? new IntPtr(1) : IntPtr.Zero;
        // Broadcast de topologia, tela e energia. O destinatário só sinaliza o coletor.
        if (msg is 0x0219 or 0x0218 || msg == WM_DISPLAYCHANGE_U)
            HardwareChanged?.Invoke();
        if (msg == CallbackMsg)
        {
            uint ev = (uint)((long)lParam & 0xFFFF);
            if (ev is WM_RBUTTONUP or WM_CONTEXTMENU) ShowMenu();
            else if (ev is WM_LBUTTONUP or WM_LBUTTONDBLCLK) CommandInvoked?.Invoke(Command.Settings);
            else if (ev == NIN_BALLOONUSERCLICK) BalloonClicked?.Invoke();
            return IntPtr.Zero;
        }
        // Explorer reiniciado ou escala alterada: recarrega no novo tamanho e registra de novo.
        if (msg == TaskbarCreated && TaskbarCreated != 0) { _added = false; LoadIcon(); Add(); return IntPtr.Zero; }
        if (msg == WM_SETTINGCHANGE_U || msg == WM_DISPLAYCHANGE_U)
        {
            LoadIcon();
            if (_added) { var d = Data(); _shownTip = null; Shell_NotifyIconW(NIM_MODIFY, ref d); }
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
        if (_displayNotification != IntPtr.Zero) { UnregisterPowerSettingNotification(_displayNotification); _displayNotification = IntPtr.Zero; }
        if (_hwnd != IntPtr.Zero) { WTSUnRegisterSessionNotification(_hwnd); DestroyWindow(_hwnd); }
    }
}
