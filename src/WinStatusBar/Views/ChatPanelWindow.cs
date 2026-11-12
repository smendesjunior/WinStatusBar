using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;
using WinRT.Interop;
using static WinStatusBar.Interop.Win32;

namespace WinStatusBar.Views;

/// <summary>Janela compacta da conversa: independente, arrastável e com acrílico ativo.</summary>
internal sealed class ChatPanelWindow : Window
{
    private readonly FrameworkElement _body;
    private readonly FrameworkElement _composer;
    private readonly Grid _root;
    private readonly double _width;
    private MonitorInfo? _monitor;
    private bool _positioned, _resizing, _disposing;
    private bool _hasConversation;
    private bool _resizeQueued;
    private bool _preparingLayout;
    private int _lastWidth;
    public bool IsOpen { get; private set; }
    public bool HasInputFocus => IsOpen && !_cloaked && GetForegroundWindow() == _hwnd;
    public event Action? Opened;
    public event Action? Hidden;

    private readonly Func<double> _desiredHeight;
    private int _lastHeight;
    private readonly Border _frame;
    private readonly IntPtr _hwnd;
    /// <summary>Margem do conteúdo até a borda da janela.</summary>
    private const double FramePadding = 12;
    private const double FrameExtra = FramePadding * 2;

    /// <summary>
    /// Tema atual a cada abertura (o tema do Windows pode ter mudado desde que a janela foi criada): conteúdo, fundo
    /// e contorno no mesmo tema. Fundo nas cores dos menus do Windows 11, quase opaco para manter a leitura.
    /// </summary>
    private void ApplyTheme()
    {
        bool dark = App.IsDarkTheme;
        _root.RequestedTheme = dark ? ElementTheme.Dark : ElementTheme.Light;
        _frame.Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent); // o acrílico do sistema aparece por trás
        ThemeApplied?.Invoke();
    }

    /// <summary>Depois de cada aplicação de tema: o conteúdo do chat atualiza as cores próprias sem ser recriado.</summary>
    public event Action? ThemeApplied;

    /// <summary>Tema do Windows mudou com o chat vivo (aberto ou oculto): reaplica o visual.</summary>
    public void RefreshTheme() { if (!_disposing) ApplyTheme(); }

    /// <param name="desiredHeight">
    /// Altura natural do conteúdo (DIPs), somada pelas partes. Não usar body.DesiredSize: o WinUI limita esse valor
    /// à altura atual da janela, e o painel nunca cresceria além do tamanho em que abriu (o campo ficava cortado).
    /// </param>
    public ChatPanelWindow(FrameworkElement body, FrameworkElement dragHandle, FrameworkElement composer, double width, Func<double> desiredHeight)
    {
        Title = "Conversa · WinStatusBar";
        _body = body; _composer = composer; _width = width; _desiredHeight = desiredHeight;
        body.VerticalAlignment = VerticalAlignment.Stretch;
        // Janela do sistema: acrílico oficial, canto de 8 px, contorno e sombra do Windows 11.
        // Caixas internas usam o mesmo raio da janela (8, OverlayCornerRadius do WinUI 3): iguais dentro e fora.
        _frame = new Border { Padding = new Thickness(FramePadding), Child = body };
        _root = new Grid { Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent) };
        _root.Children.Add(_frame); Content = _root;
        ApplyTheme();
        var presenter = OverlappedPresenter.Create();
        presenter.SetBorderAndTitleBar(true, false);
        presenter.IsMaximizable = false; presenter.IsMinimizable = false;
        presenter.IsResizable = false; presenter.IsAlwaysOnTop = true;
        AppWindow.SetPresenter(presenter);
#if DEBUG
        AppWindow.IsShownInSwitchers = true;
        AppWindow.Title = "Conversa · WinStatusBar (diagnóstico)";
#else
        AppWindow.IsShownInSwitchers = false;
#endif
        if (File.Exists(App.IconPath)) AppWindow.SetIcon(App.IconPath);
        var hwnd = WindowNative.GetWindowHandle(this);
        long ex = GetWindowLongPtr(hwnd, GWL_EXSTYLE);
#if DEBUG
        SetWindowLongPtr(hwnd, GWL_EXSTYLE, (ex & ~WS_EX_TOOLWINDOW) | WS_EX_APPWINDOW);
#else
        SetWindowLongPtr(hwnd, GWL_EXSTYLE, (ex & ~WS_EX_APPWINDOW) | WS_EX_TOOLWINDOW);
#endif
        int round = 2; DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref round, sizeof(int)); // DWMWCP_ROUND: 8 px
        // Acrílico do Windows: a Microsoft recomenda acrílico para superfícies passageiras que fecham ao clicar fora
        // (menus, flyouts, popups) e Mica para janelas principais. Ele acompanha o tema claro/escuro do conteúdo.
        SystemBackdrop = new DesktopAcrylicBackdrop();
        _hwnd = hwnd;
        _root.Loaded += (_, _) => App.Log($"Chat: conteúdo carregado {_root.ActualWidth:0}x{_root.ActualHeight:0}");
        dragHandle.PointerPressed += (_, e) =>
        {
            var source = e.OriginalSource as DependencyObject;
            while (source is not null && source != dragHandle)
            {
                if (source is Microsoft.UI.Xaml.Controls.Primitives.ButtonBase) return;
                source = VisualTreeHelper.GetParent(source);
            }
            if (source != dragHandle) return;
            ReleaseCapture(); SendMessageW(hwnd, 0x00A1, (IntPtr)2, IntPtr.Zero);
        };
        AppWindow.Closing += (_, e) => { if (!_disposing) { e.Cancel = true; Hide(); } };
        Activated += (_, e) =>
        {
            if (e.WindowActivationState != WindowActivationState.Deactivated || !IsOpen || _disposing) return;
            // Aguarda a troca de foco para identificar a nova janela. Menus e diálogos
            // pertencentes ao chat continuam funcionando sem fechar o painel por engano.
            DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
            {
                if (!IsOpen || _disposing) return;
                var foreground = GetForegroundWindow();
                if (foreground == hwnd || (foreground != IntPtr.Zero && GetAncestor(foreground, 3 /* GA_ROOTOWNER */) == hwnd)) return;
                Hide();
            });
        };
        body.LayoutUpdated += (_, _) =>
        {
            if (_resizeQueued || _monitor is null || _disposing || !IsOpen || _preparingLayout) return;
            _resizeQueued = true;
            DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
            {
                _resizeQueued = false;
                if (!_disposing && IsOpen) ResizeToContent();
            });
        };
        // A janela existe desde já, oculta do compositor (DWM cloak), e o WinUI a desenha em segundo plano.
        // Esconder a janela (Hide) descartava o desenho; ao reabrir o Windows mostrava 1 quadro preto e o fundo
        // vazio por ~30–100 ms antes do conteúdo. Oculta assim, ela não recebe cliques nem aparece no Alt+Tab.
        Cloak(true);
        AppWindow.Show(false);
        // Não medir/redimensionar dentro de SizeChanged: Measure com altura infinita
        // invalida o layout que acabou de ser calculado e pode impedir o primeiro frame.
    }

    public void ShowAt(FrameworkElement anchor, MonitorInfo monitor)
    {
        var elapsed = System.Diagnostics.Stopwatch.StartNew();
        bool firstTime = _monitor is null || !_positioned;
        _monitor = monitor;
        ApplyTheme();
        bool resized = PrepareLayout();
        if (!_positioned)
        {
            var work = monitor.Work;
            AppWindow.Move(new PointInt32(work.Left + Math.Max(8, (work.Width - AppWindow.Size.Width) / 2), work.Top + 12));
            _positioned = true;
        }
        var fg = GetForegroundWindow();
        if (fg != _hwnd && fg != IntPtr.Zero) _returnFocusTo = fg;
        IsOpen = true; Activate();
        int token = ++_showToken;
        if (!firstTime && !resized && ContentReady())
        {
            // Já desenhado no tamanho certo: aparece no mesmo quadro.
            Reveal(token, elapsed);
        }
        else
        {
            // Primeira abertura ou tamanho novo: espera o WinUI desenhar o novo layout (2 quadros + 60 ms) antes de mostrar.
            int frames = 0;
            void OnRendering(object? sender, object e)
            {
                if (token != _showToken || !IsOpen) { Microsoft.UI.Xaml.Media.CompositionTarget.Rendering -= OnRendering; return; }
                if (!ContentReady()) { frames = 0; return; }
                if (++frames < 2) return;
                Microsoft.UI.Xaml.Media.CompositionTarget.Rendering -= OnRendering;
                Reveal(token, elapsed);
            }
            Microsoft.UI.Xaml.Media.CompositionTarget.Rendering += OnRendering;
            var fallback = DispatcherQueue.CreateTimer();
            fallback.Interval = TimeSpan.FromMilliseconds(100); fallback.IsRepeating = true;
            fallback.Tick += (_, _) =>
            {
                if (token != _showToken || !IsOpen || !_cloaked) { fallback.Stop(); return; }
                if (!ContentReady()) PrepareLayout();
                if (ContentReady()) { fallback.Stop(); Microsoft.UI.Xaml.Media.CompositionTarget.Rendering -= OnRendering; Reveal(token, elapsed); }
            };
            fallback.Start();
        }
    }

    private IntPtr _returnFocusTo;

    private int _showToken;
    private bool _cloaked;

    private bool PrepareLayout()
    {
        if (_monitor is null || _preparingLayout) return false;
        _preparingLayout = true;
        try
        {
            // Mede a altura natural antes de consultar DesiredSize. O tamanho antigo da janela
            // deixava a linha Star ocupar o espaço e empurrava o compositor para fora do viewport.
            _body.Measure(new Windows.Foundation.Size(_width, double.PositiveInfinity));
            bool resized = ResizeToContent();
            double scale = _root.XamlRoot?.RasterizationScale ?? _monitor.Dpi / 96.0;
            var size = AppWindow.ClientSize;
            var logical = new Windows.Foundation.Size(size.Width / scale, size.Height / scale);
            _root.Measure(logical);
            _root.Arrange(new Windows.Foundation.Rect(0, 0, logical.Width, logical.Height));
            _root.UpdateLayout();
            return resized;
        }
        finally { _preparingLayout = false; }
    }

    private bool ContentReady()
    {
        if (_composer.ActualHeight < 1 || _composer.ActualWidth < 1 || _root.ActualHeight < 1) return false;
        var position = _composer.TransformToVisual(_root).TransformPoint(new Windows.Foundation.Point());
        return position.Y >= 0 && position.Y + _composer.ActualHeight <= _root.ActualHeight + 2;
    }

    private void Reveal(int token, System.Diagnostics.Stopwatch elapsed)
    {
        if (token != _showToken || !IsOpen || !_cloaked || !ContentReady()) return;
        Cloak(false);
        Activate();
        WinStatusBar.Clip.Native.ForceForeground(_hwnd);
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            if (IsOpen && !_cloaked && GetForegroundWindow() == _hwnd) Opened?.Invoke();
        });
        App.Log($"Chat: abertura pronta em {elapsed.ElapsedMilliseconds} ms · compositor {_composer.ActualWidth:0}x{_composer.ActualHeight:0} · janela {_root.ActualWidth:0}x{_root.ActualHeight:0}");
    }

    public bool FocusInput(TextBox input)
    {
        // A logical XAML focus while cloaked/inactive does not start the text caret.
        if (!IsOpen || _cloaked || GetForegroundWindow() != _hwnd) return false;
        var island = _root.XamlRoot.ContentIsland;
        var controller = Microsoft.UI.Input.InputFocusController.GetForIsland(island);
        bool hadNativeFocus = controller.HasFocus;
        bool nativeFocus = controller.TrySetFocus() && controller.HasFocus;
        bool focused = input.Focus(FocusState.Keyboard);
        bool actual = ReferenceEquals(FocusManager.GetFocusedElement(_root.XamlRoot), input);
        App.Log($"Chat: foco de entrada confirmado={nativeFocus && focused && actual}, ilhaAntes={hadNativeFocus}, ilhaDepois={controller.HasFocus}, estado={input.FocusState}");
        return nativeFocus && focused && actual;
    }

    private void Cloak(bool on)
    {
        if (_cloaked == on) return;
        int value = on ? 1 : 0;
        DwmSetWindowAttribute(_hwnd, 13 /* DWMWA_CLOAK */, ref value, sizeof(int));
        _cloaked = on;
    }

    public void SetConversationVisible(bool visible)
    {
        if (_hasConversation == visible) return;
        _hasConversation = visible;
        ResizeToContent();
    }

    /// <summary>Ajusta a janela ao conteúdo; devolve true quando o tamanho mudou.</summary>
    private bool ResizeToContent()
    {
        if (_resizing || _monitor is null) return false;
        _resizing = true;
        try
        {
            double scale = _monitor.Dpi / 96.0;
            int w = (int)Math.Ceiling((_width + FrameExtra) * scale);
            double contentHeight = _desiredHeight();
            if (contentHeight < 1) contentHeight = 160;
            // Cresce com a conversa até 2/3 da área de trabalho; daí em diante só a conversa rola e o campo continua inteiro.
            int max = (int)(_monitor.Work.Height * 2 / 3.0);
            int h = Math.Clamp((int)Math.Ceiling((contentHeight + FrameExtra) * scale), (int)(120 * scale), max);
            // Tolerância de 2 px: arredondamentos não podem gerar um ciclo de redimensionamento.
            bool sameHeight = Math.Abs(h - _lastHeight) <= 2;
            if (_lastWidth != w || !sameHeight)
            {
                _lastWidth = w;
                _lastHeight = h;
                AppWindow.ResizeClient(new SizeInt32(w, h));
                return true;
            }
            return false;
        }
        finally { _resizing = false; }
    }

    public void Hide()
    {
        if (!IsOpen) return;
        IsOpen = false;
        _showToken++;
        // Fica oculto do compositor, mas desenhado: reabre instantaneamente, sem quadro preto nem fundo vazio.
        Cloak(true);
        // Devolve o foco para onde estava (fechar com Esc/X); ao clicar fora, o foco já está na outra janela.
        if (GetForegroundWindow() == _hwnd && _returnFocusTo != IntPtr.Zero && IsWindow(_returnFocusTo))
            SetForegroundWindow(_returnFocusTo);
        Hidden?.Invoke();
    }
    public void DisposePanel() { _disposing = true; Close(); }
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool IsWindow(IntPtr hwnd);
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool ReleaseCapture();
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern IntPtr SendMessageW(IntPtr hwnd, uint msg, IntPtr wparam, IntPtr lparam);
}
