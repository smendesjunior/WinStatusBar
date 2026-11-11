using System.Runtime.InteropServices;
using static WinStatusBar.Interop.Win32;

namespace WinStatusBar.Services;

/// <summary>
/// Registro da janela como AppBar do Shell (SHAppBarMessage) ancorada no topo de um monitor.
/// Reserva a área de trabalho (janelas maximizadas começam abaixo da barra), reage a ABN_POSCHANGED,
/// ABN_FULLSCREENAPP, mudança de resolução/DPI, retomada da suspensão e reinício do Explorer (TaskbarCreated).
/// </summary>
public sealed class AppBarService : IDisposable
{
    private static readonly uint CallbackMessage = RegisterWindowMessageW("WinStatusBar.AppBarCallback");
    private static readonly uint TaskbarCreated = RegisterWindowMessageW("TaskbarCreated");

    private readonly IntPtr _hwnd;
    private readonly SUBCLASSPROC _proc; // referência mantida para o GC não coletar o delegate
    private bool _registered;
    private bool _fullscreen;
    private bool _positioning;

    public Func<MonitorInfo?> MonitorProvider { get; set; }
    public double HeightEpx { get; set; } = 48;
    public bool HideBehindFullscreen { get; set; } = true;
    public RECT CurrentRect { get; private set; }
    public uint CurrentDpi { get; private set; } = 96;
    public string? LastError { get; private set; }
    public int ExplorerRestarts { get; private set; }

    public event EventHandler? Positioned;
    public event EventHandler? DisplayChanged;
    public event EventHandler? WallpaperChanged;
    public event EventHandler<bool>? FullscreenChanged;

    public AppBarService(IntPtr hwnd, Func<MonitorInfo?> monitorProvider)
    {
        _hwnd = hwnd;
        MonitorProvider = monitorProvider;
        _proc = SubclassProc;
        SetWindowSubclass(_hwnd, _proc, 0x5742, IntPtr.Zero);
    }

    public bool IsRegistered => _registered;

    public void Register()
    {
        if (_registered) return;
        var abd = NewData();
        abd.uCallbackMessage = CallbackMessage;
        _registered = SHAppBarMessage(ABM_NEW, ref abd) != UIntPtr.Zero;
        if (!_registered) LastError = $"ABM_NEW falhou ({Marshal.GetLastWin32Error()})";
        Reposition();
    }

    public void Unregister()
    {
        if (!_registered) return;
        var abd = NewData();
        SHAppBarMessage(ABM_REMOVE, ref abd);
        _registered = false;
    }

    /// <summary>ABM_QUERYPOS → ajusta a altura → ABM_SETPOS → move a janela exatamente para o retângulo aprovado.</summary>
    public void Reposition()
    {
        if (_positioning) return;
        var monitor = MonitorProvider();
        if (monitor is null) return;
        _positioning = true;
        try
        {
            CurrentDpi = monitor.Dpi;
            int height = (int)Math.Round(HeightEpx * monitor.Dpi / 96.0);
            var abd = NewData();
            abd.uEdge = ABE_TOP;
            abd.rc = new RECT { Left = monitor.Bounds.Left, Right = monitor.Bounds.Right, Top = monitor.Bounds.Top, Bottom = monitor.Bounds.Top + height };
            if (_registered)
            {
                SHAppBarMessage(ABM_QUERYPOS, ref abd);
                // O Shell pode deslocar o topo (ex.: outra AppBar). Mantém a altura e 100% da largura do monitor.
                abd.rc.Bottom = abd.rc.Top + height;
                abd.rc.Left = monitor.Bounds.Left;
                abd.rc.Right = monitor.Bounds.Right;
                SHAppBarMessage(ABM_SETPOS, ref abd);
            }
            CurrentRect = abd.rc;
            var z = _fullscreen && HideBehindFullscreen ? HWND_BOTTOM : HWND_TOPMOST;
            SetWindowPos(_hwnd, z, abd.rc.Left, abd.rc.Top, abd.rc.Width, abd.rc.Height, SWP_NOACTIVATE | SWP_SHOWWINDOW | SWP_FRAMECHANGED);
            Positioned?.Invoke(this, EventArgs.Empty);
        }
        finally { _positioning = false; }
    }

    private APPBARDATA NewData() => new() { cbSize = (uint)Marshal.SizeOf<APPBARDATA>(), hWnd = _hwnd };

    private IntPtr SubclassProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam, UIntPtr id, IntPtr refData)
    {
        if (msg == CallbackMessage && CallbackMessage != 0)
        {
            switch ((uint)wParam)
            {
                case ABN_POSCHANGED:
                    Reposition();
                    break;
                case ABN_FULLSCREENAPP:
                    bool full = lParam != IntPtr.Zero;
                    if (full != _fullscreen)
                    {
                        _fullscreen = full;
                        var z = full && HideBehindFullscreen ? HWND_BOTTOM : HWND_TOPMOST;
                        SetWindowPos(_hwnd, z, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
                        FullscreenChanged?.Invoke(this, full);
                    }
                    break;
            }
            return IntPtr.Zero;
        }
        if (msg == TaskbarCreated && TaskbarCreated != 0)
        {
            // Explorer reiniciado: o registro anterior foi perdido. Registra novamente.
            ExplorerRestarts++;
            _registered = false;
            Register();
            return IntPtr.Zero;
        }
        switch (msg)
        {
            case WM_CLOSE:
                // Pedido de fechar vindo de fora (Alt+F4, outro programa): a barra só fecha pela própria barra, que
                // remove este subclass (Dispose) antes de chamar Close(); por isso o fechamento dela não passa aqui.
                return IntPtr.Zero;
            case WM_WINDOWPOSCHANGING:
                {
                    // Mantém a barra na faixa "topmost" (como a taskbar), exceto atrás de apps em tela cheia.
                    var result = DefSubclassProc(hWnd, msg, wParam, lParam);
                    unsafe
                    {
                        var wp = (WINDOWPOS*)lParam;
                        if ((wp->flags & SWP_NOZORDER) == 0)
                            wp->hwndInsertAfter = _fullscreen && HideBehindFullscreen ? HWND_BOTTOM : HWND_TOPMOST;
                    }
                    return result;
                }
            case WM_NCCALCSIZE when wParam != IntPtr.Zero:
                // Área cliente = janela inteira: nenhuma moldura não-cliente é desenhada (nem borda, nem sombra).
                return IntPtr.Zero;
            case WM_STYLECHANGING:
                // Impede que o WinUI/presenter recoloque moldura (WS_DLGFRAME/WS_BORDER/WS_THICKFRAME): sem borda e sem sombra.
                // O WinUI ajusta styleNew no seu próprio tratamento; por isso o filtro é aplicado DEPOIS dele.
                {
                    var result = DefSubclassProc(hWnd, msg, wParam, lParam);
                    unsafe
                    {
                        var ss = (STYLESTRUCT*)lParam;
                        if ((int)wParam == GWL_STYLE) ss->styleNew &= ~FrameStyles;
                        else if ((int)wParam == GWL_EXSTYLE) ss->styleNew = (ss->styleNew & ~FrameExStyles) | (uint)WS_EX_TOOLWINDOW;
                    }
                    return result;
                }
            case WM_ACTIVATE:
                if (_registered) { var abd = NewData(); SHAppBarMessage(ABM_ACTIVATE, ref abd); }
                break;
            case WM_WINDOWPOSCHANGED:
                if (_registered && !_positioning) { var abd = NewData(); SHAppBarMessage(ABM_WINDOWPOSCHANGED, ref abd); }
                break;
            case WM_DISPLAYCHANGE:
                DisplayChanged?.Invoke(this, EventArgs.Empty);
                Reposition();
                break;
            case WM_SETTINGCHANGE:
                // Papel de parede trocado (manual, apresentação de slides, Spotlight): o fundo adaptativo relê na hora.
                if ((int)wParam == 0x0014 /* SPI_SETDESKWALLPAPER */) WallpaperChanged?.Invoke(this, EventArgs.Empty);
                break;
            case WM_DPICHANGED:
                // Ignora o retângulo sugerido: a barra sempre ocupa o topo do seu monitor com 48 epx.
                var r = DefSubclassProc(hWnd, msg, wParam, lParam);
                Reposition();
                return r;
            case WM_POWERBROADCAST:
                if ((int)wParam is PBT_APMRESUMEAUTOMATIC or PBT_APMRESUMESUSPEND)
                    _ = Task.Delay(1500).ContinueWith(_ => PostMessageW(_hwnd, CallbackMessage, (IntPtr)ABN_POSCHANGED, IntPtr.Zero));
                break;
            case WM_QUERYENDSESSION:
            case WM_ENDSESSION:
            case WM_DESTROY:
                Unregister();
                break;
        }
        return DefSubclassProc(hWnd, msg, wParam, lParam);
    }

    public void Dispose()
    {
        Unregister();
        RemoveWindowSubclass(_hwnd, _proc, 0x5742);
    }
}
