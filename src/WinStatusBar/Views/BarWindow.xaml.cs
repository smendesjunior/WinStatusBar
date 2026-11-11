using Microsoft.UI;
using Microsoft.UI.Composition;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using WinRT;
using WinRT.Interop;
using WinStatusBar.Core.Settings;
using WinStatusBar.Services;
using WinStatusBar.ViewModels;
using WinStatusBar.Views.Panels;
using static WinStatusBar.Interop.Win32;

namespace WinStatusBar.Views;

/// <summary>
/// Barra superior: janela WinUI 3 transformada em AppBar do Shell. Sem moldura, sem sombra, sem cantos
/// arredondados, 100% da largura do monitor, fora do Alt+Tab e da taskbar.
/// </summary>
public sealed partial class BarWindow : Window
{
    private readonly MonitoringHub _hub;
    private readonly SettingsService _settings;
    private readonly IntPtr _hwnd;
    private ISystemBackdropControllerWithTargets? _backdrop;
    private SystemBackdropConfiguration? _backdropConfig;
    private readonly DispatcherQueueTimer _layoutTimer;
    private readonly Dictionary<string, FrameworkElement> _modules;
    private readonly HashSet<string> _overflowed = new();
    private bool _closing;
    private double _moduleGap = 8;
    private readonly List<(string Key, Button Button)> _shortcutOverflow = new();
    private readonly Dictionary<TextBlock, (double Size, double Height)> _nativeTextSizes = new();

    public BarViewModel VM => _hub.ViewModel;
    public string MonitorDevice { get; }
    public AppBarService? AppBar { get; private set; }
    public event EventHandler? DisplayChanged;

    public BarWindow(MonitoringHub hub, SettingsService settings, string monitorDevice)
    {
        _hub = hub;
        _settings = settings;
        MonitorDevice = monitorDevice;
        InitializeComponent();
        Title = global::WinStatusBar.Services.Localization.T("WinStatusBar");
        _hwnd = WindowNative.GetWindowHandle(this);
        EnableFolderDrop(ExplorerShortcutButton, OnExplorerShortcut);
        EnableFolderDrop(DocumentsShortcutButton, OnDocumentsShortcut);
        EnableFolderDrop(DownloadsShortcutButton, OnDownloadsShortcut);
        EnableFolderDrop(PersonalShortcutButton, OnPersonalShortcut);
        _modules = new()
        {
            ["agent"] = AgentModule, ["task"] = TaskArea, ["network"] = NetworkModule, ["cpu"] = CpuModule,
            ["battery"] = BatteryModule, ["ram"] = RamModule, ["gpu"] = GpuModule, ["quotas"] = QuotasModule, ["shortcuts"] = ShortcutsModule,
        };

        VM.PropertyChanged += OnBatteryAvailabilityChanged;
        Closed += (_, _) => VM.PropertyChanged -= OnBatteryAvailabilityChanged;
        ConfigureWindow();
        ApplyAppearance();

        _layoutTimer = DispatcherQueue.CreateTimer();
        _layoutTimer.Interval = TimeSpan.FromSeconds(3);
        // Tela apagada ou sessão bloqueada: ninguém vê a barra, não há o que medir.
        _layoutTimer.Tick += (_, _) => { if (!Core.Presence.Away) UpdateOverflow(); };
        SetupQuotaFlyouts();
        SetupQuotaModeSwitch();
        SetupExtraIconAnimation();
        SetupActivityFlyout();
        SetupDetailPanels();
        StabilizeTextWidths();
        SetupCommandBar();
        ApplyCommandBarOptions();
        PopupMaterial.Apply(MoreMenu);
        SetupAgentGlow();
        // Todos os menus da barra (inclusive os criados depois) seguem o tema do Windows com camada de contraste.
        Root.Loaded += (_, _) => FollowAppThemeEverywhere(Root);
        PopupMaterial.FollowAppTheme((Flyout)Root.Resources["SessionsFlyout"]);
        Root.SizeChanged += (_, _) => UpdateLayoutSize();
        // Fechamento vindo de fora (Alt+F4, WM_CLOSE de outro programa): recusado antes do Closed, senão os demais
        // tratadores de Closed desmontavam painéis, chat e fundo adaptativo com a barra ainda na tela.
        AppWindow.Closing += (_, e) => { if (!_closing) e.Cancel = true; };
        Closed += (_, e) => { if (!_closing) { e.Handled = true; } };
    }

    private void OnBatteryAvailabilityChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(VM.BatteryAvailable)) { BuildColumns(); UpdateOverflow(); }
    }

    private void ConfigureWindow()
    {
        var appWindow = AppWindow;
        appWindow.IsShownInSwitchers = false; // fora do Alt+Tab e da taskbar
        appWindow.Title = "WinStatusBar";
        if (File.Exists(App.IconPath)) appWindow.SetIcon(App.IconPath);
        var presenter = OverlappedPresenter.Create();
        presenter.SetBorderAndTitleBar(false, false);
        presenter.IsResizable = false;
        presenter.IsMaximizable = false;
        presenter.IsMinimizable = false;
        presenter.IsAlwaysOnTop = true;
        appWindow.SetPresenter(presenter);

        // Estilo Win32 de popup puro (sem WS_CAPTION/WS_DLGFRAME/WS_THICKFRAME → sem borda e sem sombra do DWM)
        // e janela de ferramenta. O subclass do AppBarService bloqueia a reintrodução desses estilos (WM_STYLECHANGING).
        AppBar = new AppBarService(_hwnd, CurrentMonitor);
        EnforceChromeless(_hwnd);
        SetWindowPos(_hwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_FRAMECHANGED);

        AppBar.Positioned += (_, _) => DispatcherQueue.TryEnqueue(UpdateLayoutSize);
        AppBar.DisplayChanged += (_, _) => DisplayChanged?.Invoke(this, EventArgs.Empty);
        AppBar.FullscreenChanged += (_, full) => App.Log($"Tela cheia {(full ? "detectada" : "encerrada")} em {MonitorDevice}");
    }

    private MonitorInfo? CurrentMonitor()
    {
        var all = GetMonitors();
        return all.FirstOrDefault(m => m.Device == MonitorDevice) ?? all.FirstOrDefault(m => m.IsPrimary) ?? all.FirstOrDefault();
    }

    public void ShowBar()
    {
        AppWindow.Show(false); // sem ativar
        EnforceChromeless(_hwnd);
        Bindings.Update(); // a barra nunca é "ativada": inicializa os x:Bind explicitamente
        AppBar!.HeightEpx = 48 * _settings.Current.Scale;
        AppBar.HideBehindFullscreen = _settings.Current.Fullscreen == FullscreenBehavior.HideBehind;
        AppBar.Register();
        _layoutTimer.Start();
        UpdateLayoutSize();
    }

    public void ReRegister()
    {
        AppBar?.Unregister();
        AppBar?.Register();
    }

    public void CloseBar()
    {
        _closing = true;
        _layoutTimer.Stop();
        TeardownAgentGlow();
        AppBar?.Dispose();
        AppBar = null;
        _backdrop?.Dispose();
        _backdrop = null;
        // O modelo da barra vive mais que a janela (uma barra por monitor, recriada a cada troca de monitor): sem isto,
        // os x:Bind continuavam inscritos nele, atualizando e prendendo na memória cada barra já fechada.
        try { Bindings.StopTracking(); } catch (Exception ex) { App.Log("Barra: vínculos não liberados ao fechar: " + ex.Message); }
        Close();
    }

    // ------------------------------------------------------------------ aparência
    public void ApplyAppearance()
    {
        var s = _settings.Current;
        var theme = App.IsDarkTheme ? ElementTheme.Dark : ElementTheme.Light;
        Root.RequestedTheme = theme;
        ScaleHost.RequestedTheme = ElementTheme.Default; // o modo adaptativo reaplica o contraste com o fundo
        int dark = App.IsDarkTheme ? 1 : 0;
        DwmSetWindowAttribute(_hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, sizeof(int));
        SetupBackdrop(s);
        ApplyQuotaOrder();
        BuildColumns();
        UpdateGlowColors();
        UpdateAgentGlow(restart: true);
        if (_panels.Count > 0) ApplyBarOptions();
        if (AppBar is not null)
        {
            double h = 48 * s.Scale;
            AppBar.HideBehindFullscreen = s.Fullscreen == FullscreenBehavior.HideBehind;
            if (Math.Abs(AppBar.HeightEpx - h) > 0.1) { AppBar.HeightEpx = h; AppBar.Reposition(); }
        }
        UpdateLayoutSize();
    }

    private void SetupBackdrop(AppSettings s)
    {
        _backdrop?.Dispose();
        _backdrop = null;
        if (SystemBackdrop is not null) SystemBackdrop = null;
        _tint = null;
        Root.BackgroundTransition = null; // o modo adaptativo liga a transição da camada de cor
        Root.Background = new SolidColorBrush(Colors.Transparent);
        _backdropConfig ??= new SystemBackdropConfiguration();
        // A barra raramente está "ativa": força o estado ativo para o material não cair na cor sólida de fallback.
        _backdropConfig.IsInputActive = true;
        _backdropConfig.Theme = App.IsDarkTheme ? SystemBackdropTheme.Dark : SystemBackdropTheme.Light;

        if (s.Backdrop == BackdropKind.Adaptive) { StartAdaptive(); return; }
        StopAdaptive();
        ApplyMaterial(s.Backdrop, s.TintOpacity);
    }

    /// <summary>Aplica Mica, Mica Alt, Acrílico ou cor sólida (também usado pelo modo adaptativo com janela em tamanho menor).</summary>
    private void ApplyMaterial(BackdropKind kind, double tintOpacity)
    {
        if (kind is BackdropKind.Mica or BackdropKind.MicaAlt && MicaController.IsSupported())
        {
            var mica = new MicaController { Kind = kind == BackdropKind.MicaAlt ? MicaKind.BaseAlt : MicaKind.Base };
            if (tintOpacity > 0) { mica.TintOpacity = (float)tintOpacity; mica.LuminosityOpacity = (float)Math.Min(1, tintOpacity + 0.2); mica.TintColor = App.IsDarkTheme ? ColorFromHex(0x202020) : ColorFromHex(0xF3F3F3); }
            mica.AddSystemBackdropTarget(this.As<ICompositionSupportsSystemBackdrop>());
            mica.SetSystemBackdropConfiguration(_backdropConfig!);
            _backdrop = mica;
        }
        else if (kind == BackdropKind.Acrylic && DesktopAcrylicController.IsSupported())
        {
            var acrylic = new DesktopAcrylicController { Kind = DesktopAcrylicKind.Base };
            if (tintOpacity > 0) { acrylic.TintOpacity = (float)tintOpacity; acrylic.LuminosityOpacity = (float)Math.Min(1, tintOpacity + 0.2); acrylic.TintColor = App.IsDarkTheme ? ColorFromHex(0x1C1C1C) : ColorFromHex(0xEEEEEE); }
            acrylic.AddSystemBackdropTarget(this.As<ICompositionSupportsSystemBackdrop>());
            acrylic.SetSystemBackdropConfiguration(_backdropConfig!);
            _backdrop = acrylic;
        }
        else
        {
            Root.Background = WinStatusBar.Services.ThemeBrushes.Get("SolidBackgroundFillColorBaseBrush");
        }
    }

    private static Windows.UI.Color ColorFromHex(int rgb) => Windows.UI.Color.FromArgb(255, (byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);

    // ------------------------------------------------------------------ layout
    private void UpdateLayoutSize()
    {
        double scale = _settings.Current.Scale;
        double width = Root.ActualWidth;
        if (width <= 0) return;
        ScaleHost.Width = width / scale;
        ScaleHost.Height = 48;
        ScaleHost.RenderTransform = Math.Abs(scale - 1) < 0.01 ? null : new ScaleTransform { ScaleX = scale, ScaleY = scale };
        // Acionador central: largura exata do texto e do ícone da IA.
        UpdateCommandWidth();
        if (_commandLabel is null) UpdateOverflow();
    }

    /// <summary>
    /// Monta os dois lados da barra: os módulos até a tarefa (inclusive) à esquerda da barra de comando e os demais à direita.
    /// As duas laterais têm a mesma largura, então a barra de comando fica sempre no centro exato da tela.
    /// </summary>
    private void BuildColumns()
    {
        var s = _settings.Current;
        int split = s.ModuleOrder.IndexOf("task");
        var left = new List<string>();
        var right = new List<string>();
        for (int i = 0; i < s.ModuleOrder.Count; i++)
        {
            string key = s.ModuleOrder[i];
            if (!_modules.TryGetValue(key, out var el)) continue;
            bool isLeft = split < 0 ? key != "shortcuts" : i <= split;
            var target = isLeft ? ModulesGrid : RightModulesGrid;
            // Confere pelas listas de cada lado (na abertura da janela, el.Parent ainda vem nulo e o módulo não mudava de lado).
            var other = isLeft ? RightModulesGrid : ModulesGrid;
            if (other.Children.Contains(el)) other.Children.Remove(el);
            if (!target.Children.Contains(el)) target.Children.Add(el);
            (isLeft ? left : right).Add(key);
        }
        LayoutSide(ModulesGrid, left, isLeft: true);
        LayoutSide(RightModulesGrid, right, isLeft: false);
        RebuildMoreMenu();
    }

    /// <summary>Colunas de um lado, com separadores discretos. Esquerda: a tarefa ocupa o espaço livre.
    /// Direita: espaço livre antes dos módulos, mantendo todo o grupo junto à borda direita.</summary>
    private void LayoutSide(Grid grid, List<string> keys, bool isLeft)
    {
        var s = _settings.Current;
        foreach (var sep in grid.Children.OfType<Rectangle>().ToList()) grid.Children.Remove(sep);
        grid.ColumnDefinitions.Clear();
        int col = 0;
        bool first = true, elastic = false;
        int shownCount = 0;
        if (!isLeft)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            col++; elastic = true;
        }
        foreach (var key in keys)
        {
            var el = _modules[key];
            bool visible = !s.HiddenModules.Contains(key) && !_overflowed.Contains(key) && (key != "battery" || VM.BatteryAvailable);
            el.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
            if (!visible) { Grid.SetColumn(el, 0); continue; }
            // Sem separador logo depois do primeiro módulo da esquerda: a caixa da IA já tem contorno próprio.
            if (!first && !(key == "shortcuts" && !isLeft) && !(isLeft && shownCount == 1))
            {
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                var sep = new Rectangle { Style = (Style)Application.Current.Resources["BarSeparator"], Margin = new Thickness(_moduleGap / 2, 0, _moduleGap / 2, 0) };
                Grid.SetColumn(sep, col++);
                grid.Children.Add(sep);
            }
            bool star = isLeft && key == "task";
            elastic |= star;
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = star ? new GridLength(1, GridUnitType.Star) : GridLength.Auto });
            Grid.SetColumn(el, col++);
            first = false;
            shownCount++;
        }
        if (!elastic) grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        if (grid.ColumnDefinitions.Count == 0) grid.ColumnDefinitions.Add(new ColumnDefinition());
    }

    /// <summary>Todos os módulos habilitados permanecem visíveis. Compacta antes de reduzir cada lateral.</summary>
    private void UpdateOverflow()
    {
        if (double.IsNaN(ScaleHost.Width) || ScaleHost.Width <= 0) return;
        var s = _settings.Current;
        double box = CommandHost.Visibility == Visibility.Visible ? CommandHost.Width + 16 : 0;
        double side = Math.Max(1, (ScaleHost.Width - box) / 2);
        if (_overflowed.Count > 0) { _overflowed.Clear(); BuildColumns(); }
        foreach (var (key, button) in _shortcutOverflow) button.Visibility = ShortcutEnabled(key) ? Visibility.Visible : Visibility.Collapsed;
        _shortcutOverflow.Clear();
        int split = s.ModuleOrder.IndexOf("task");
        bool IsLeft(string key) => split < 0 ? key != "shortcuts" : s.ModuleOrder.IndexOf(key) <= split;
        foreach (var (key, module) in _modules) ApplyNativeTextScale(module, 1);
        // A largura mínima dos textos (StabilizeTextWidths) fica como está: zerá-la aqui, a cada 3 s, desfazia a estabilização.
        double left = 0, right = 0;
        // Reduce empty spacing before reducing readability. All sizes remain native to the monitor DPI.
        foreach (var (gap, padding, innerGap, shortcutWidth) in new[] { (8d, 8d, 10d, 36d), (6d, 6d, 8d, 30d), (4d, 4d, 6d, 26d) })
        {
            _moduleGap = gap;
            foreach (var grid in new[] { ModulesGrid, RightModulesGrid })
                foreach (var separator in grid.Children.OfType<Rectangle>()) separator.Margin = new Thickness(gap / 2, 0, gap / 2, 0);
            foreach (var module in new[] { NetworkModule, CpuModule, RamModule, GpuModule, BatteryModule })
            {
                module.Padding = new Thickness(padding, 0, padding, 0);
                if (module.Content is StackPanel content) content.Spacing = innerGap;
            }
            foreach (var button in new[] { ClipboardButton, TaskManagerButton, SettingsShortcutButton, StoreButton,
                ExplorerShortcutButton, DocumentsShortcutButton, DownloadsShortcutButton, PersonalShortcutButton, MoreButton }) button.Width = shortcutWidth;
            left = right = 8;
            foreach (var key in s.ModuleOrder)
            {
                if (!_modules.TryGetValue(key, out var el) || s.HiddenModules.Contains(key) || key == "battery" && !VM.BatteryAvailable) continue;
                double width;
                if (key == "task") width = 120;
                else { el.Measure(new Windows.Foundation.Size(double.PositiveInfinity, 48)); width = el.DesiredSize.Width; }
                if (IsLeft(key)) left += width + gap + 1; else right += width + gap + 1;
            }
            if (left <= side && right <= side) break;
        }
        // Resize the actual text, never a bitmap of the group. Icons retain the common 17-DIP footprint.
        if (right > side)
        {
            for (double ratio = 0.95; ratio >= 0.69 && right > side; ratio -= 0.05)
            {
                foreach (var (key, module) in _modules) if (!IsLeft(key)) ApplyNativeTextScale(module, ratio);
                foreach (var panel in new[] { NetTextPanel, CpuTextPanel, RamTextPanel, GpuTextPanel, BatteryTextPanel }) panel.MinWidth = 0;
                right = 8;
                foreach (var key in s.ModuleOrder)
                {
                    if (IsLeft(key) || !_modules.TryGetValue(key, out var module) || s.HiddenModules.Contains(key) || key == "battery" && !VM.BatteryAvailable) continue;
                    module.Measure(new Windows.Foundation.Size(double.PositiveInfinity, 48));
                    right += module.DesiredSize.Width + _moduleGap + 1;
                }
            }
        }
        double MeasureRight()
        {
            double total = 8;
            foreach (string key in s.ModuleOrder)
            {
                if (IsLeft(key) || !_modules.TryGetValue(key, out var module) || s.HiddenModules.Contains(key) || _overflowed.Contains(key) || key == "battery" && !VM.BatteryAvailable) continue;
                module.Measure(new Windows.Foundation.Size(double.PositiveInfinity, 48));
                total += module.DesiredSize.Width + _moduleGap + 1;
            }
            return total;
        }
        // Preserve Settings and More on the bar. Additional shortcuts remain usable in More on narrow screens.
        if (right > side)
        {
            var buttons = ShortcutButtons();
            foreach (string key in s.ShortcutOrder.AsEnumerable().Reverse().Where(k => k != "settings"))
            {
                if (right <= side) break;
                if (!buttons.TryGetValue(key, out var button) || button.Visibility != Visibility.Visible) continue;
                button.Visibility = Visibility.Collapsed;
                _shortcutOverflow.Add((key, button));
                right = MeasureRight();
            }
        }
        // Extremely narrow screens use the same More menu for details rather than painting outside the screen.
        if (right > side)
            foreach (string key in new[] { "gpu", "battery", "network", "cpu", "quotas", "agent" })
            {
                if (right <= side) break;
                if (IsLeft(key) || s.HiddenModules.Contains(key) || !_modules.TryGetValue(key, out var module)) continue;
                _overflowed.Add(key); module.Visibility = Visibility.Collapsed;
                right = MeasureRight();
            }
        // O menu "…" é remontado ao abrir (OnMoreOpening) e pelo BuildColumns: não precisa ser refeito a cada medição.
        if (_overflowed.Count > 0) BuildColumns();
        var space = BarSpace.Allocate(ScaleHost.Width, box, left, right);
        BarLayout.ColumnDefinitions[0].Width = new GridLength(space.Left);
        BarLayout.ColumnDefinitions[2].Width = new GridLength(space.Right);
        // Scroll only if a very small screen cannot fit the chosen modules even after spacing is compacted.
        ModulesGrid.Width = Math.Max(space.Left, left);
        RightModulesGrid.Width = Math.Max(space.Right, right);
    }

    private Dictionary<string, Button> ShortcutButtons() => new()
    {
        ["clipboard"] = ClipboardButton, ["taskmanager"] = TaskManagerButton, ["settings"] = SettingsShortcutButton,
        ["store"] = StoreButton, ["explorer"] = ExplorerShortcutButton, ["documents"] = DocumentsShortcutButton,
        ["downloads"] = DownloadsShortcutButton, ["personal"] = PersonalShortcutButton,
    };

    private bool ShortcutEnabled(string key)
    {
        var s = _settings.Current;
        return key switch { "clipboard" => s.ShortcutClipboard && Clip.ClipHost.Active, "taskmanager" => s.ShortcutTaskManager,
            "settings" => s.ShortcutSettings, "store" => s.ShortcutStore, "explorer" => s.ShortcutExplorer,
            "documents" => s.ShortcutDocuments, "downloads" => s.ShortcutDownloads, "personal" => s.ShortcutPersonal, _ => false };
    }

    private void ApplyNativeTextScale(DependencyObject element, double ratio)
    {
        if (element is FontIcon or Image or BatteryIcon) return;
        if (element is TextBlock text)
        {
            if (!_nativeTextSizes.TryGetValue(text, out var original))
                _nativeTextSizes[text] = original = (text.FontSize, text.LineHeight);
            text.FontSize = Math.Max(Math.Min(original.Size, 9), original.Size * ratio);
            if (original.Height > 0) text.LineHeight = Math.Max(text.FontSize + 2, original.Height * ratio);
        }
        int count = VisualTreeHelper.GetChildrenCount(element);
        for (int i = 0; i < count; i++) ApplyNativeTextScale(VisualTreeHelper.GetChild(element, i), ratio);
    }

    private void RebuildMoreMenu()
    {
        MoreMenu.Items.Clear();
        foreach (var key in _overflowed)
        {
            string text = key switch
            {
                "network" => global::WinStatusBar.Services.Localization.F("Rede: ↑ {0}  ↓ {1}", new object?[] {VM.NetUp,VM.NetDown}),
                "battery" => $"Bateria: {VM.BatteryText} · {VM.BatteryTime}",
                "gpu" => global::WinStatusBar.Services.Localization.F("GPU: {0}", new object?[] {VM.GpuText}),
                "ram" => global::WinStatusBar.Services.Localization.F("RAM: {0}", new object?[] {VM.RamText}),
                "cpu" => global::WinStatusBar.Services.Localization.F("CPU: {0}", new object?[] {VM.CpuText}),
                "quotas" => global::WinStatusBar.Services.Localization.F("Codex 5h {0} · 7d {1}  |  Claude 5h {2} · 7d {3}", new object?[] {VM.Codex5h,VM.Codex7d,VM.Claude5h,VM.Claude7d}),
                "agent" => $"{VM.AgentName}: {VM.AgentState}",
                _ => key,
            };
            var overflowItem = new MenuFlyoutItem { Text = text };
            overflowItem.Click += (_, _) =>
            {
                if (_modules.TryGetValue(key, out var module) && module is Button button && _panels.TryGetValue(button, out var panel)) panel.Flyout.ShowAt(MoreButton);
            };
            MoreMenu.Items.Add(overflowItem);
        }
        foreach (var (key, button) in _shortcutOverflow.AsEnumerable().Reverse())
        {
            var item = new MenuFlyoutItem { Text = Microsoft.UI.Xaml.Automation.AutomationProperties.GetName(button) };
            item.Click += (_, _) =>
            {
                var e = new RoutedEventArgs();
                switch (key)
                {
                    case "clipboard": OnClipboard(MoreButton, e); break;
                    case "taskmanager": OnTaskManager(MoreButton, e); break;
                    case "settings": OnSettings(MoreButton, e); break;
                    case "store": OnStore(MoreButton, e); break;
                    case "explorer": OnExplorerShortcut(MoreButton, e); break;
                    case "documents": OnDocumentsShortcut(MoreButton, e); break;
                    case "downloads": OnDownloadsShortcut(MoreButton, e); break;
                    case "personal": OnPersonalShortcut(MoreButton, e); break;
                }
            };
            MoreMenu.Items.Add(item);
        }
        if (_overflowed.Count > 0 || _shortcutOverflow.Count > 0) MoreMenu.Items.Add(new MenuFlyoutSeparator());

        var sessions = new MenuFlyoutItem { Text = global::WinStatusBar.Services.Localization.T("Sessões de IA"), Icon = new FontIcon { Glyph = "" } };
        sessions.Click += (_, _) => FlyoutShow();
        var refresh = new MenuFlyoutItem { Text = global::WinStatusBar.Services.Localization.T("Atualizar dados"), Icon = new FontIcon { Glyph = "" } };
        refresh.Click += async (_, _) => await _hub.RefreshNowAsync();
        var settings = new MenuFlyoutItem { Text = global::WinStatusBar.Services.Localization.T("Configurações do WinStatusBar"), Icon = new FontIcon { Glyph = "" } };
        settings.Click += (_, _) => App.Current.ShowSettings();
        var diag = new MenuFlyoutItem { Text = global::WinStatusBar.Services.Localization.T("Diagnóstico"), Icon = new FontIcon { Glyph = "" } };
        diag.Click += (_, _) => App.Current.ShowSettings(diagnostics: true);
        // Confere e conserta sozinho o que as IAs da barra e do Codex precisam (Configurações › Modelos).
        var fix = new MenuFlyoutItem { Text = global::WinStatusBar.Services.Localization.T("Corrigir problemas das IAs"), Icon = new FontIcon { Glyph = "\uE90F" } };
        fix.Click += (_, _) => App.Current.ShowSettings(repair: true);
        var exit = new MenuFlyoutItem { Text = global::WinStatusBar.Services.Localization.T("Fechar WinStatusBar"), Icon = new FontIcon { Glyph = "" } };
        exit.Click += (_, _) => App.Current.ExitApp();
        MoreMenu.Items.Add(sessions);
        MoreMenu.Items.Add(refresh);
        MoreMenu.Items.Add(settings);
        MoreMenu.Items.Add(diag);
        MoreMenu.Items.Add(fix);
        MoreMenu.Items.Add(new MenuFlyoutSeparator());
        MoreMenu.Items.Add(exit);
        MoreMenu.Opening -= OnMoreOpening;
        MoreMenu.Opening += OnMoreOpening;
    }

    private void OnMoreOpening(object? sender, object e)
    {
        MoreMenu.Opening -= OnMoreOpening;
        RebuildMoreMenu(); // valores atualizados no momento da abertura
    }

    private void FlyoutShow()
    {
        if (Root.Resources["SessionsFlyout"] is Flyout f)
            f.ShowAt(TaskArea.Visibility == Visibility.Visible ? TaskModule : MoreButton);
    }

    /// <summary>"Configurar" na coluna da direita do menu de agentes: abre as Configurações (contas das IAs).</summary>
    /// <summary>
    /// Logo das IAs adicionais animado enquanto trabalham, sem deformar o desenho (AgentMotion): o Gemini dá um quarto
    /// de volta suave; Copilot, Qira, DeepSeek, Grok e APIs respiram na opacidade. Respeita a opção de animações do Windows.
    /// </summary>
    private void SetupExtraIconAnimation()
    {
        void Apply()
        {
            string id = VM.ExtraWorking;
            Services.AgentMotion.SetKind(AgentIconImage, Services.AgentMotion.KindFor(id));
            Services.AgentMotion.SetActive(AgentIconImage, id.Length > 0);
        }
        System.ComponentModel.PropertyChangedEventHandler onExtraWorking = (_, e) => { if (e.PropertyName == nameof(VM.ExtraWorking)) DispatcherQueue.TryEnqueue(Apply); };
        VM.PropertyChanged += onExtraWorking;
        Closed += (_, _) => VM.PropertyChanged -= onExtraWorking; // o modelo vive mais que a janela
        Apply();
    }

    /// <summary>Seletor "Restante | Usado" no cabeçalho do menu de agentes (mesmo ajuste das Configurações).</summary>
    private void SetupQuotaModeSwitch()
    {
        var rest = new ToggleButton { Content = "Restante", FontSize = 11, Padding = new Thickness(10, 2, 10, 3), MinHeight = 0, CornerRadius = new CornerRadius(11, 0, 0, 11) };
        var used = new ToggleButton { Content = "Usado", FontSize = 11, Padding = new Thickness(10, 2, 10, 3), MinHeight = 0, CornerRadius = new CornerRadius(0, 11, 11, 0) };
        void Sync() { bool r = _settings.Current.QuotaMode == Core.Settings.QuotaDisplay.Remaining; rest.IsChecked = r; used.IsChecked = !r; }
        void Set(Core.Settings.QuotaDisplay mode)
        {
            _settings.Current.QuotaMode = mode;
            try { _settings.Save(); } catch { }
            App.Current.ApplySettings(); _hub.QueueAgentRefresh(); Sync();
        }
        rest.Click += (_, _) => Set(Core.Settings.QuotaDisplay.Remaining);
        used.Click += (_, _) => Set(Core.Settings.QuotaDisplay.Used);
        Sync();
        QuotaModeSwitch.Children.Add(rest); QuotaModeSwitch.Children.Add(used);
        if (Root.Resources["SessionsFlyout"] is Flyout flyout)
        {
            flyout.Opening += (_, _) => { Sync(); GoogleUsageSession.BeginLive(); };
            flyout.Closed += (_, _) => GoogleUsageSession.EndLive();
        }
    }

    private void OnAgentConfigureClick(object sender, RoutedEventArgs e)
    {
        if (Root.Resources["SessionsFlyout"] is Flyout flyout) flyout.Hide();
        App.Current.ShowSettings();
    }

    private void OnAgentOptionClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is not AgentOptionSlot slot) return;
        _hub.SelectAgent(slot.Option.Key);
        if (Root.Resources["SessionsFlyout"] is Flyout flyout) flyout.Hide();
    }

    /// <summary>
    /// Espaçamento uniforme entre módulos: o texto ocupa só a largura real (sem MinWidth fixo, que sobrava à direita),
    /// e a largura só cresce enquanto os valores mudam, para a barra não "tremer"; a cada minuto volta ao tamanho atual.
    /// </summary>
    private void StabilizeTextWidths()
    {
        StackPanel[] panels = [NetTextPanel, CpuTextPanel, RamTextPanel, GpuTextPanel, BatteryTextPanel];
        foreach (var panel in panels)
            panel.SizeChanged += (_, e) => { if (e.NewSize.Width > panel.MinWidth) panel.MinWidth = Math.Ceiling(e.NewSize.Width); };
        var reset = DispatcherQueue.CreateTimer();
        reset.Interval = TimeSpan.FromMinutes(1);
        reset.Tick += (_, _) => { foreach (var p in panels) p.MinWidth = 0; };
        reset.Start();
        Closed += (_, _) => reset.Stop();
    }

    // ------------------------------------------------------------------ área de transferência

    /// <summary>Abre o painel logo abaixo do botão, com a borda direita alinhada a ele (como os demais painéis da barra).</summary>
    private void OnClipboard(object sender, RoutedEventArgs e)
    {
        var anchor = ClipboardButton.Visibility == Visibility.Visible ? ClipboardButton : MoreButton;
        var p = anchor.TransformToVisual(null).TransformPoint(new Windows.Foundation.Point(anchor.ActualWidth, anchor.ActualHeight));
        double scale = Root.XamlRoot?.RasterizationScale ?? 1;
        var pos = AppWindow.Position;
        Clip.ClipHost.Toggle("clip", new Windows.Graphics.PointInt32(pos.X + (int)Math.Round(p.X * scale), pos.Y + (int)Math.Round((p.Y + 6) * scale)));
    }

    /// <summary>Painéis de detalhes (CPU, memória, GPU, rede) abertos pelo clique no módulo da barra.</summary>
    private void SetupDetailPanels()
    {
        Attach(CpuModule, new CpuDetailsPanel(_hub, _settings), nameof(VM.CpuTooltip));
        Attach(BatteryModule, new BatteryDetailsPanel(_hub), nameof(VM.BatteryTooltip));
        Attach(RamModule, new MemoryDetailsPanel(_hub), nameof(VM.RamTooltip));
        Attach(GpuModule, new GpuDetailsPanel(_hub, _settings), nameof(VM.GpuTooltip));
        Attach(NetworkModule, new NetworkDetailsPanel(_hub, _settings), nameof(VM.NetTooltip));
        ApplyBarOptions();
    }

    private readonly Dictionary<Button, (Flyout Flyout, string TipPath)> _panels = new();

    private void Attach(Button module, IDetailsPanel panel, string tipPath)
    {
        var flyout = PanelKit.CreateFlyout(panel.Content, panel.Start, panel.Stop);
        _panels[module] = (flyout, tipPath);
        Closed += (_, _) => { flyout.Hide(); panel.Dispose(); };
    }

    /// <summary>Opções da barra: painéis ao clicar (ou só a dica) e quais botões de atalho aparecem.</summary>
    private void ApplyBarOptions()
    {
        var s = _settings.Current;
        foreach (var (button, (flyout, tip)) in _panels)
        {
            button.Flyout = s.DetailPanels ? flyout : null;
            if (s.DetailPanels) ToolTipService.SetToolTip(button, null); // o painel substitui a dica
            else button.SetBinding(ToolTipService.ToolTipProperty, new Microsoft.UI.Xaml.Data.Binding { Source = VM, Path = new PropertyPath(tip), Mode = Microsoft.UI.Xaml.Data.BindingMode.OneWay });
        }
        ClipboardButton.Visibility = s.ShortcutClipboard && Clip.ClipHost.Active ? Visibility.Visible : Visibility.Collapsed;
        TaskManagerButton.Visibility = s.ShortcutTaskManager ? Visibility.Visible : Visibility.Collapsed;
        SettingsShortcutButton.Visibility = s.ShortcutSettings ? Visibility.Visible : Visibility.Collapsed;
        StoreButton.Visibility = s.ShortcutStore ? Visibility.Visible : Visibility.Collapsed;
        ExplorerShortcutButton.Visibility = s.ShortcutExplorer ? Visibility.Visible : Visibility.Collapsed;
        DocumentsShortcutButton.Visibility = s.ShortcutDocuments ? Visibility.Visible : Visibility.Collapsed;
        DownloadsShortcutButton.Visibility = s.ShortcutDownloads ? Visibility.Visible : Visibility.Collapsed;
        PersonalShortcutButton.Visibility = s.ShortcutPersonal ? Visibility.Visible : Visibility.Collapsed;
        var shortcuts = new Dictionary<string, UIElement>
        {
            ["clipboard"] = ClipboardButton, ["taskmanager"] = TaskManagerButton, ["settings"] = SettingsShortcutButton,
            ["store"] = StoreButton, ["explorer"] = ExplorerShortcutButton, ["documents"] = DocumentsShortcutButton,
            ["downloads"] = DownloadsShortcutButton, ["personal"] = PersonalShortcutButton,
        };
        var ordered = s.ShortcutOrder.Where(shortcuts.ContainsKey).Select(key => shortcuts[key]).Append(MoreButton).ToList();
        for (int i = 0; i < ordered.Count; i++)
            if (ShortcutsModule.Children.IndexOf(ordered[i]) != i)
            {
                ShortcutsModule.Children.Remove(ordered[i]);
                ShortcutsModule.Children.Insert(i, ordered[i]);
            }
        ApplyCommandBarOptions();
    }

    public void ShowTrayMenu(int screenX, int screenY, Action<TrayIconService.Command> invoke)
    {
        var menu = new MenuFlyout();
        PopupMaterial.Apply(menu);
        if (Clip.ClipHost.Started)
        {
            Add(global::WinStatusBar.Services.Localization.T("Área de transferência  (Win+V)"), TrayIconService.Command.Clipboard, "\uE77F");
            Add(global::WinStatusBar.Services.Localization.T("Emojis  (Win+.)"), TrayIconService.Command.Emoji, "\uE76E");
            var pause = new ToggleMenuFlyoutItem { Text = global::WinStatusBar.Services.Localization.T("Pausar captura da área de transferência"), IsChecked = Clip.ClipHost.Paused, Icon = new FontIcon { Glyph = "\uE769" } };
            pause.Click += (_, _) => invoke(TrayIconService.Command.ClipPause);
            menu.Items.Add(pause);
            Add(global::WinStatusBar.Services.Localization.T("Abrir pasta do histórico"), TrayIconService.Command.ClipData, "\uE838");
            menu.Items.Add(new MenuFlyoutSeparator());
        }
        Add(global::WinStatusBar.Services.Localization.T("Abrir configurações"), TrayIconService.Command.Settings, "\uE713");
        Add(global::WinStatusBar.Services.Localization.T("Atualizar dados"), TrayIconService.Command.Refresh, "\uE72C");
        Add(global::WinStatusBar.Services.Localization.T("Reiniciar integração"), TrayIconService.Command.RestartIntegration, "\uE777");
        Add(global::WinStatusBar.Services.Localization.T("Diagnóstico"), TrayIconService.Command.Diagnostics, "\uE9D9");
        menu.Items.Add(new MenuFlyoutSeparator());
        Add(global::WinStatusBar.Services.Localization.T("Fechar WinStatusBar"), TrayIconService.Command.Exit, "\uE711");
        var origin = new POINT();
        ClientToScreen(_hwnd, ref origin);
        double scale = Root.XamlRoot.RasterizationScale;
        SetForegroundWindow(_hwnd);
        menu.ShowAt(Root, new Microsoft.UI.Xaml.Controls.Primitives.FlyoutShowOptions
        {
            Position = new Windows.Foundation.Point((screenX - origin.X) / scale, (screenY - origin.Y) / scale),
            Placement = Microsoft.UI.Xaml.Controls.Primitives.FlyoutPlacementMode.TopEdgeAlignedRight,
        });

        void Add(string label, TrayIconService.Command command, string glyph)
        {
            var item = new MenuFlyoutItem { Text = label, Icon = new FontIcon { Glyph = glyph } };
            item.Click += (_, _) => invoke(command);
            menu.Items.Add(item);
        }
    }

    // ------------------------------------------------------------------ menus de detalhes das cotas
    private void SetupQuotaFlyouts()
    {
        CodexQuotaButton.Flyout = CreateQuotaFlyout(Core.Ai.AgentKind.Codex);
        ClaudeQuotaButton.Flyout = CreateQuotaFlyout(Core.Ai.AgentKind.Claude);
        // Ícones das cotas em uma cor só (ThemeIcon.MonoLogo no XAML), como os do lado direito; colorido só no módulo do agente.
        // Gemini e Copilot: menu de detalhes como Codex e Claude (o site oficial fica como botão dentro dele).
        GeminiQuotaButton.Flyout = CreateExtraQuotaFlyout("gemini");
        CopilotQuotaButton.Flyout = CreateExtraQuotaFlyout("copilot");
        QiraQuotaButton.Flyout = CreateExtraQuotaFlyout("qira");
        ApplyQuotaOrder();
    }

    private readonly HashSet<Flyout> _themedFlyouts = new();
    /// <summary>Percorre a barra e aplica o tema do Windows a cada Flyout de botão ainda não tratado.</summary>
    private void FollowAppThemeEverywhere(DependencyObject node)
    {
        if (node is Button { Flyout: Flyout f } && _themedFlyouts.Add(f)) PopupMaterial.FollowAppTheme(f);
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++) FollowAppThemeEverywhere(VisualTreeHelper.GetChild(node, i));
    }

    /// <summary>Detalhes das cotas do Gemini (plano Google) ou dos créditos do Copilot (assinatura Microsoft).</summary>
    private Flyout CreateExtraQuotaFlyout(string id)
    {
        var host = new StackPanel { Spacing = 10, Width = Views.Panels.PanelKit.Width };
        StackPanel Row(string title, double used, string reset)
        {
            bool remaining = _settings.Current.QuotaMode == Core.Settings.QuotaDisplay.Remaining;
            double shown = remaining ? 100 - used : used;
            var row = new StackPanel { Spacing = 4 };
            var head = new Grid();
            head.Children.Add(new TextBlock { Text = title, FontSize = 13, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
            head.Children.Add(new TextBlock { Text = $"{Math.Round(shown):0} % {(remaining ? "restante" : "usado")}", FontSize = 13, HorizontalAlignment = HorizontalAlignment.Right });
            row.Children.Add(head);
            row.Children.Add(new ProgressBar { Minimum = 0, Maximum = 100, Value = shown, Height = 4 });
            if (reset.Length > 0) row.Children.Add(Views.Panels.PanelKit.Caption(reset, 12));
            return row;
        }
        void Build()
        {
            host.Children.Clear();
            host.RequestedTheme = App.IsDarkTheme ? ElementTheme.Dark : ElementTheme.Light;
            string site;
            if (id == "gemini")
            {
                var g = GoogleUsageSession.Current;
                site = "https://gemini.google.com/usage?hl=pt-BR";
                host.Children.Add(new TextBlock { Text = "Gemini" + (g?.Plan is { } p ? " · plano " + char.ToUpper(p[0]) + p[1..].ToLowerInvariant() : ""), FontSize = 18, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
                if (g is { SignedIn: true })
                {
                    if (g.CurrentPercent is { } cur) host.Children.Add(Row("Uso atual", cur, g.CurrentReset is { } r1 ? "Renova " + r1 : ""));
                    if (g.WeeklyPercent is { } wk) host.Children.Add(Row("Limite semanal", wk, g.WeeklyReset is { } r2 ? "Renova " + r2 : ""));
                    host.Children.Add(Views.Panels.PanelKit.Caption($"Conta Google: {g.Email ?? "conectada"} · lido às {g.Updated.LocalDateTime:HH:mm} em gemini.google.com/usage. Atualiza a cada 5 min.", 12));
                }
                else host.Children.Add(Views.Panels.PanelKit.Caption("Entre na conta Google em Configurações › IA › Gemini para ver as cotas do plano.", 12));
                // Chave de API (Google AI Studio): pedidos do dia por modelo, com os limites por minuto.
                if (ApiKeyStore.Get(AdditionalAiAccounts.Gemini.Resource) is not null)
                {
                    var api = GoogleUsageSession.Api;
                    host.Children.Add(new TextBlock { Text = "API · chave do AI Studio" + (api?.Tier is { } tier ? " · " + tier : ""), FontSize = 15,
                        FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Margin = new Thickness(0, 6, 0, 0) });
                    var models = GoogleUsageSession.ApiModelsToShow();
                    string Show(long? n) => Core.Ai.AiStudioUsageText.Show(n);
                    // A página informa o MÁXIMO por modelo no intervalo: com intervalo de 1 dia vale como "hoje"; senão é pico.
                    bool today = api?.Daily == true;
                    string peak = today ? "" : "pico ";
                    foreach (var m in models)
                    {
                        string perMinute = (m.RpmLimit is not null ? $"{peak}{Show(m.RpmUsed ?? 0)}/{Show(m.RpmLimit)} por minuto" : "")
                            + (m.TpmLimit is not null ? $" · {Show(m.TpmUsed ?? 0)}/{Show(m.TpmLimit)} tokens/min" : "");
                        if (m.DailyPercent is { } daily)
                            host.Children.Add(Row(m.Model + (today ? " · pedidos hoje" : " · maior uso diário"), daily,
                                $"{Show(m.RpdUsed ?? 0)} de {Show(m.RpdLimit)} por dia · {GoogleUsageSession.ApiDailyReset()}" + (perMinute.Length > 0 ? " · " + perMinute : "")));
                        else host.Children.Add(Views.Panels.PanelKit.Caption(m.Model + (perMinute.Length > 0 ? ": " + perMinute : ": sem limite diário informado"), 12));
                    }
                    host.Children.Add(Views.Panels.PanelKit.Caption(api is not null && models.Count > 0
                        ? $"Lido às {api.Updated.LocalDateTime:HH:mm} em aistudio.google.com (limites da sua chave)"
                            + (today ? "." : $"; o AI Studio informa o pico de uso no intervalo de {api.Period ?? "28 dias"}, não o uso de agora.")
                            + " Atualiza a cada minuto com este menu aberto."
                        : GoogleUsageSession.ApiError ?? "Lendo os limites da chave no Google AI Studio…", 12));
                }
            }
            else if (id == "qira")
            {
                var q = Core.Ai.QiraBridge.CurrentQuota;
                site = "";
                host.Children.Add(new TextBlock { Text = "Lenovo Qira · modo Híbrido", FontSize = 18, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
                if (q is { Limit: > 0 })
                {
                    var pt = System.Globalization.CultureInfo.GetCultureInfo("pt-BR");
                    if (!q.Unlimited) host.Children.Add(Row("Créditos da nuvem no ciclo", 100.0 * q.Used / q.Limit,
                        $"{q.Limit - q.Used} de {q.Limit} créditos restantes" + (q.Renews is { } r ? " · renova " + r.ToLocalTime().ToString("dd/MM", pt) : "")));
                    else host.Children.Add(Views.Panels.PanelKit.Caption("Créditos da nuvem ilimitados nesta conta.", 12));
                    host.Children.Add(Views.Panels.PanelKit.Caption($"Lido às {q.Updated.LocalDateTime:HH:mm} no próprio Qira. A Lenovo desconta conforme o uso da nuvem no modo Híbrido; não é um crédito por mensagem.", 12));
                }
                else host.Children.Add(Views.Panels.PanelKit.Caption("Créditos ainda não lidos. Converse com o Qira pela barra uma vez.", 12));
            }
            else
            {
                var c = MicrosoftCreditsSession.Current;
                site = "https://account.microsoft.com/services";
                host.Children.Add(new TextBlock { Text = "Microsoft Copilot" + (c?.Plan is { } p ? " · " + p : ""), FontSize = 18, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
                if (c is { SignedIn: true, Total: > 0 })
                {
                    host.Children.Add(Row("Créditos de IA", 100.0 * (c.Total - c.Remaining) / c.Total, $"{c.Remaining} de {c.Total} créditos restantes · recarregados no início de cada mês"));
                    host.Children.Add(Views.Panels.PanelKit.Caption($"Lido às {c.Updated.LocalDateTime:HH:mm} em account.microsoft.com, com o login do Copilot da barra. Atualiza a cada 5 min.", 12));
                }
                else host.Children.Add(Views.Panels.PanelKit.Caption("Créditos ainda não lidos. Entre no Copilot em Configurações › IA › Microsoft Copilot.", 12));
            }
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            var refresh = WinStatusBar.Views.Panels.PanelKit.Small(new Button { Content = "Atualizar agora", FontSize = 12 });
            refresh.Click += (_, _) => { if (id == "gemini") { GoogleUsageSession.ForceApi(); GoogleUsageSession.Refresh(); } else if (id == "qira") _ = QiraHubSession.RefreshQuotaAsync(); else MicrosoftCreditsSession.Refresh(); };
            var open = WinStatusBar.Views.Panels.PanelKit.Small(new Button { Content = "Página oficial ↗", FontSize = 12 });
            open.Click += async (_, _) => await Windows.System.Launcher.LaunchUriAsync(new Uri(site));
            buttons.Children.Add(refresh); if (site.Length > 0) buttons.Children.Add(open);
            if (id == "gemini" && ApiKeyStore.Get(AdditionalAiAccounts.Gemini.Resource) is not null)
            {
                var limits = WinStatusBar.Views.Panels.PanelKit.Small(new Button { Content = "Limites da API ↗", FontSize = 12 });
                limits.Click += async (_, _) => await Windows.System.Launcher.LaunchUriAsync(new Uri(GoogleUsageSession.ApiLimitsPage));
                buttons.Children.Add(limits);
            }
            host.Children.Add(buttons);
        }
        void Changed() => DispatcherQueue.TryEnqueue(Build);
        return Views.Panels.PanelKit.CreateFlyout(host,
            () => { Build(); if (id == "gemini") { GoogleUsageSession.Changed += Changed; GoogleUsageSession.BeginLive(); } else if (id == "qira") Core.Ai.QiraBridge.QuotaChanged += Changed; else MicrosoftCreditsSession.Changed += Changed; },
            () => { if (id == "gemini") { GoogleUsageSession.Changed -= Changed; GoogleUsageSession.EndLive(); } else if (id == "qira") Core.Ai.QiraBridge.QuotaChanged -= Changed; else MicrosoftCreditsSession.Changed -= Changed; });
    }

    /// <summary>Módulos de cota na barra: só os escolhidos nas Configurações, na ordem da lista "Ordem das IAs".</summary>
    private void ApplyQuotaOrder()
    {
        var s = _settings.Current;
        var buttons = new Dictionary<string, Button> { ["codex"] = CodexQuotaButton, ["claude"] = ClaudeQuotaButton, ["gemini"] = GeminiQuotaButton, ["copilot"] = CopilotQuotaButton, ["qira"] = QiraQuotaButton };
        var wanted = s.AiOrder.Where(k => s.BarQuotaServices.Contains(k) && buttons.ContainsKey(k)).Select(k => buttons[k]).ToList();
        if (QuotasModule.Children.OfType<Button>().SequenceEqual(wanted)) return;
        QuotasModule.Children.Clear();
        foreach (var b in wanted) QuotasModule.Children.Add(b);
    }

    private Flyout CreateQuotaFlyout(Core.Ai.AgentKind kind)
    {
        var flyout = new Flyout
        {
            Placement = Microsoft.UI.Xaml.Controls.Primitives.FlyoutPlacementMode.BottomEdgeAlignedRight,
            ShouldConstrainToRootBounds = false,
            SystemBackdrop = new DesktopAcrylicBackdrop(),
        };
        var presenter = new Style(typeof(FlyoutPresenter));
        presenter.Setters.Add(new Setter(FlyoutPresenter.BackgroundProperty, new SolidColorBrush(Colors.Transparent)));
        presenter.Setters.Add(new Setter(FlyoutPresenter.PaddingProperty, new Thickness(16, 14, 16, 14)));
        presenter.Setters.Add(new Setter(FlyoutPresenter.MinWidthProperty, 360d));
        presenter.Setters.Add(new Setter(FlyoutPresenter.MaxWidthProperty, 840d));
        // Altura até o fim da área de trabalho deste monitor (o padrão do WinUI corta em ~760 pontos e põe barra de rolagem
        // mesmo com espaço sobrando); só rola se o conteúdo não couber na tela.
        var screen = CurrentMonitor();
        double maxHeight = screen is null ? 1000 : (screen.Work.Bottom - screen.Work.Top) * 96.0 / screen.Dpi - 16;
        presenter.Setters.Add(new Setter(FlyoutPresenter.MaxHeightProperty, Math.Max(600, maxHeight)));
        presenter.Setters.Add(new Setter(FlyoutPresenter.CornerRadiusProperty, new CornerRadius(8)));
        flyout.FlyoutPresenterStyle = presenter;
        // Conteúdo montado na abertura (dados atuais) e atualizado enquanto o menu estiver aberto.
        var timer = DispatcherQueue.CreateTimer();
        timer.Interval = TimeSpan.FromSeconds(5);
        ContentControl? quota = null, resume = null;
        double resumeWidth = 0;
        const double columnSpacing = 20;
        // Rodapé (só no Codex): o aviso de bloqueio da conta, as conversas paradas pela cota e o Continuar pela API, num
        // cartão próprio, separado das cotas, na largura das duas colunas quando a coluna da OpenAI API está ao lado.
        UIElement? Resume()
        {
            if (kind != Core.Ai.AgentKind.Codex) return null;
            double inner = resumeWidth > 0 ? resumeWidth : QuotaDetailsView.ContentWidth;
            var block = QuotaDetailsView.BlockFooter(_hub, Root.RequestedTheme, inner);
            var section = CodexApiResumeView.Section(App.Current.ApiResume, Root.RequestedTheme, resumeWidth, columnSpacing, divider: false);
            if (block is null && section is null) return null;
            var stack = new StackPanel { Spacing = 12 };
            if (block is not null) stack.Children.Add(block);
            if (section is not null) stack.Children.Add(section);
            const double pad = 14; // o fundo do cartão avança pela margem do menu; o conteúdo fica alinhado com as colunas
            return new Border
            {
                Child = stack,
                Padding = new Thickness(pad, 12, pad, 12),
                Margin = new Thickness(-pad, 6, -pad, 0),
                CornerRadius = new CornerRadius(8),
                BorderThickness = new Thickness(1),
                Background = Services.ThemeBrushes.ForTheme("CardBackgroundFillColorSecondaryBrush", Root.RequestedTheme),
                BorderBrush = Services.ThemeBrushes.ForTheme("CardStrokeColorDefaultBrush", Root.RequestedTheme),
            };
        }
        timer.Tick += (_, _) =>
        {
            if (QuotaDetailsView.ResetPending) return;
            if (quota is not null) quota.Content = QuotaDetailsView.Build(_hub, _settings.Current, kind, Root.RequestedTheme);
            if (resume is not null) resume.Content = Resume();
        };
        flyout.Opening += (_, _) =>
        {
            quota = new ContentControl { Content = QuotaDetailsView.Build(_hub, _settings.Current, kind, Root.RequestedTheme) };
            resumeWidth = 0;
            var monitor = CurrentMonitor();
            double width = monitor is null ? 900 : (monitor.Work.Right - monitor.Work.Left) * 96.0 / monitor.Dpi;
            UIElement body;
            if (kind == Core.Ai.AgentKind.Codex && _settings.Current.ApiUsageVisible)
            {
                var api = ApiUsageView.Build();
                if (width >= 850)
                {
                    var columns = new Grid { ColumnSpacing = columnSpacing, RequestedTheme = Root.RequestedTheme };
                    columns.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                    columns.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                    quota.VerticalAlignment = VerticalAlignment.Top;
                    columns.Children.Add(quota); Grid.SetColumn(api, 1); columns.Children.Add(api);
                    resumeWidth = QuotaDetailsView.ContentWidth + columnSpacing + api.Width;
                    body = columns;
                }
                else
                {
                    // Compact screens switch sections instead of shrinking text or clipping content.
                    var sections = new Pivot { Width = 400, RequestedTheme = Root.RequestedTheme };
                    resume = new ContentControl { Content = Resume() };
                    var codexPage = new StackPanel { Spacing = 12 };
                    codexPage.Children.Add(quota); codexPage.Children.Add(resume);
                    sections.Items.Add(new PivotItem { Header = "Codex", Content = codexPage });
                    sections.Items.Add(new PivotItem { Header = "OpenAI API", Content = api });
                    flyout.Content = sections;
                    timer.Start();
                    return;
                }
            }
            else body = quota;
            resume = new ContentControl { Content = Resume(), RequestedTheme = Root.RequestedTheme };
            var page = new StackPanel { Spacing = 12, RequestedTheme = Root.RequestedTheme };
            page.Children.Add(body); page.Children.Add(resume);
            flyout.Content = page;
            timer.Start();
        };
        flyout.Closed += (_, _) => timer.Stop();
        return flyout;
    }

    // ------------------------------------------------------------------ atalhos
    private void OnTaskManager(object sender, RoutedEventArgs e) => WindowsLauncherService.TaskManager();
    private Flyout? _settingsFlyout;
    private void OnSettings(object sender, RoutedEventArgs e)
    {
        if (_settingsFlyout is null)
        {
            var panel = new SettingsDetailsPanel(uri =>
            {
                _settingsFlyout?.Hide();
                WindowsLauncherService.Shell(uri);
            });
            _settingsFlyout = PanelKit.CreateFlyout(panel.Content, panel.Start, panel.Stop);
            Closed += (_, _) => { _settingsFlyout?.Hide(); panel.Dispose(); };
        }
        _settingsFlyout.ShowAt(sender as FrameworkElement ?? SettingsShortcutButton);
    }
    private void OnStore(object sender, RoutedEventArgs e) => WindowsLauncherService.Store();
    private static void EnableFolderDrop(Button button, RoutedEventHandler open)
    {
        button.AllowDrop = true;
        button.DragEnter += (_, e) =>
        {
            if (e.DataView.Contains(Windows.ApplicationModel.DataTransfer.StandardDataFormats.StorageItems)) open(button, new RoutedEventArgs());
        };
    }
    private readonly Dictionary<string, FolderPopupWindow> _folderFlyouts = new();
    private void OpenRecentFolder(object sender, string key, string title, Func<string> path)
    {
        if (!_folderFlyouts.TryGetValue(key, out var flyout))
        {
            flyout = RecentFolderFlyout.Create(title, path);
            _folderFlyouts[key] = flyout;
            Closed += (_, _) => flyout.Dispose();
        }
        if (!flyout.IsOpen) flyout.ShowAt(sender as FrameworkElement ?? MoreButton, _hwnd);
    }
    private void OnExplorerShortcut(object sender, RoutedEventArgs e) => OpenRecentFolder(sender, "explorer", "Pasta pessoal", () => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
    private void OnDocumentsShortcut(object sender, RoutedEventArgs e) => OpenRecentFolder(sender, "documents", "Documentos", () => Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments));
    private void OnDownloadsShortcut(object sender, RoutedEventArgs e) => OpenRecentFolder(sender, "downloads", "Downloads", Core.Metrics.RecentFolderItems.DownloadsPath);
    private void OnPersonalShortcut(object sender, RoutedEventArgs e) => OpenRecentFolder(sender, "personal", "Pasta pessoal", () => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
}
