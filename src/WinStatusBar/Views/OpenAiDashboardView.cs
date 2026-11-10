using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.Web.WebView2.Core;
using WinStatusBar.Views.Panels;
using WinStatusBar.Services;
namespace WinStatusBar.Views;

/// <summary>Official pages rendered unchanged. Authentication belongs to the site;
/// only numeric usage/billing responses already requested by the page are observed. No credentials are read.</summary>
internal sealed class OpenAiDashboardView : Grid
{
    private const string Usage = "https://platform.openai.com/usage";
    private const string Billing = "https://platform.openai.com/settings/organization/billing/overview";
    private static readonly string Folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WinStatusBar", "OpenAiDashboard");
    private static readonly string EnabledFile = Path.Combine(Folder, "enabled");
    private static Task<CoreWebView2Environment>? _environment;
    private static Window? _window;
    private static OpenAiDashboardView? _windowView;
    private static Windows.Graphics.RectInt32 _visibleBounds = new(100, 100, 1100, 800);
    private static bool _interactive;
    private bool _syncing, _gotUsage, _gotCosts, _billingRequested;
    private readonly WebView2 _web = new() { MinHeight = 300 };
    private readonly TextBlock _status = PanelKit.Caption("Painel oficial · platform.openai.com", 11);
    private bool _initialized, _closed;
    private string _page = Usage;
    public OpenAiDashboardView(bool expanded = false)
    {
        RowSpacing = 6;
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition());
        var toolbar = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        void Add(string label, Action action) { var b = new Button { Content = label, FontSize = 12, Padding = new Thickness(8, 4, 8, 4) }; b.Click += (_, _) => action(); toolbar.Children.Add(b); }
        Add("Uso", () => Navigate(Usage));
        Add("Saldo", () => Navigate(Billing));
        Add("↻", () => _web.CoreWebView2?.Reload());
        if (!expanded) Add("Entrar / ampliar ↗", () => ShowWindow(_page));
        Children.Add(toolbar);
        Grid.SetRow(_status, 1); Children.Add(_status);
        Grid.SetRow(_web, 2); Children.Add(_web);
        if (!expanded) _web.Height = 420;
        Loaded += async (_, _) => await InitializeAsync();
        Unloaded += (_, _) => { _closed = true; _web.Close(); };
    }
    private static async Task<CoreWebView2Environment> EnvironmentAsync()
    {
        Directory.CreateDirectory(Folder);
        return await CoreWebView2Environment.CreateWithOptionsAsync(null, Path.Combine(Folder, "Profile"), null);
    }
    private async Task InitializeAsync()
    {
        if (_initialized || _closed) return;
        _initialized = true;
        try
        {
            _environment ??= EnvironmentAsync();
            var environment = await _environment;
            if (_closed) return;
            await _web.EnsureCoreWebView2Async(environment);
            if (_closed) return;
            _web.CoreWebView2.Settings.IsPasswordAutosaveEnabled = false;
            _web.CoreWebView2.WebResourceResponseReceived += async (_, e) =>
            {
                try
                {
                    var uri = new Uri(e.Request.Uri);
                    if (e.Request.Method != "GET" || !OpenAiDashboardSession.Accepts(uri) || e.Response.StatusCode != 200) return;
                    using var content = await e.Response.GetContentAsync();
                    using var reader = new StreamReader(content.AsStreamForRead());
                    OpenAiDashboardSession.Observe(uri, await reader.ReadToEndAsync());
                    if (uri.AbsolutePath.EndsWith("completions")) _gotUsage = true;
                    if (uri.AbsolutePath.EndsWith("costs")) _gotCosts = true;
                    if (_syncing && _gotUsage && _gotCosts && !_billingRequested)
                    {
                        _billingRequested = true;
                        await Task.Delay(500);
                        if (!_closed) Navigate(Billing);
                    }
                    if (_syncing && uri.AbsolutePath.EndsWith("credit_grants"))
                    {
                        _syncing = false; _status.Text = "Saldo e gráficos sincronizados com o menu.";
                        CloseBackgroundWindow();
                    }
                }
                catch (Exception ex) { App.Log("Métricas do painel OpenAI: " + ex.Message); }
            };

            _web.CoreWebView2.NavigationStarting += (_, e) =>
            {
                if (!Uri.TryCreate(e.Uri, UriKind.Absolute, out var uri) || uri.Scheme != "https") { e.Cancel = true; return; }
                _status.Text = "Página oficial / login · " + uri.Host;
            };
            _web.CoreWebView2.NavigationCompleted += (_, e) =>
            {
#if DEBUG
                App.Log("Painel OpenAI: navegação " + (e.IsSuccess ? "concluída" : e.WebErrorStatus.ToString()));
#endif
                if (!e.IsSuccess) _status.Text = "Não foi possível carregar a página. Use Atualizar ou tente novamente mais tarde.";
            };
            _web.CoreWebView2.NewWindowRequested += (_, e) =>
            {
                e.Handled = true;
                if (Uri.TryCreate(e.Uri, UriKind.Absolute, out var uri) && uri.Scheme == "https") _web.CoreWebView2.Navigate(uri.AbsoluteUri);
            };
            _web.CoreWebView2.Navigate(_page);
        }
        catch (Exception ex) { _environment = null; _status.Text = "Painel web indisponível: " + ex.Message; }
    }
    private void Navigate(string page) { _page = page; _web.CoreWebView2?.Navigate(page); }
    public static bool Enabled => File.Exists(EnabledFile);
    public static void Refresh()
    {
        if (!Enabled || _interactive || _windowView?._syncing == true) return;
        ShowWindow(Usage, true);
    }
    internal static void ShowWindow(string page = Usage, bool background = false)
    {
        if (_window is not null)
        {
            if (!background)
            {
                _interactive = true;
                _window.AppWindow.MoveAndResize(_visibleBounds);
                _window.AppWindow.IsShownInSwitchers = true;
                _window.Activate();
            }
            _windowView?.BeginSync(); return;
        }
        var view = new OpenAiDashboardView(true) { _page = page, Padding = new Thickness(12) };
        _windowView = view;
        var window = _window = new Window { Title = "OpenAI · uso e saldo da API", Content = view, SystemBackdrop = new MicaBackdrop() };
        var monitor = WinStatusBar.Interop.Win32.GetMonitors().FirstOrDefault(m => m.IsPrimary);
        if (monitor is not null)
        {
            int width = Math.Min((int)(1100 * monitor.Dpi / 96.0), monitor.Work.Width - 32);
            int height = Math.Min((int)(800 * monitor.Dpi / 96.0), monitor.Work.Height - 32);
            _visibleBounds = new Windows.Graphics.RectInt32(monitor.Work.Left + (monitor.Work.Width - width) / 2, monitor.Work.Top + (monitor.Work.Height - height) / 2, width, height);
        }
        _interactive = !background;
        window.AppWindow.IsShownInSwitchers = !background;
        // WebView2 needs a loaded XAML host. Place it outside every monitor BEFORE
        // showing it, without activation or a taskbar entry; Activate-then-Hide flashes.
        var bounds = _visibleBounds;
        if (background)
        {
            bounds.X = WinStatusBar.Interop.Win32.GetMonitors().Select(m => m.Work.Left).DefaultIfEmpty(0).Min() - bounds.Width - 1024;
            bounds.Y = WinStatusBar.Interop.Win32.GetMonitors().Select(m => m.Work.Top).DefaultIfEmpty(0).Min() - bounds.Height - 1024;
        }
        window.AppWindow.MoveAndResize(bounds);
        if (File.Exists(App.IconPath)) window.AppWindow.SetIcon(App.IconPath);
        // Fechar a janela fecha de verdade (antes só escondia e o navegador dela ficava aberto); a posição é lembrada.
        window.AppWindow.Closing += (_, _) =>
        {
            if (_interactive) _visibleBounds = new(window.AppWindow.Position.X, window.AppWindow.Position.Y, window.AppWindow.Size.Width, window.AppWindow.Size.Height);
            _interactive = false;
        };
        window.Closed += (_, _) => { view._closed = true; view._web.Close(); _window = null; _windowView = null; };
        view.BeginSync();
        if (background) window.AppWindow.Show(false);
        else window.Activate();
    }
    private async void BeginSync()
    {
        if (_syncing) return;
        _syncing = true; _gotUsage = _gotCosts = _billingRequested = false;
        OpenAiDashboardSession.Begin();
        Navigate(Usage);
        await Task.Delay(TimeSpan.FromSeconds(45));
        if (_syncing)
        {
            _syncing = false;
            _status.Text = "Entre na conta para sincronizar o saldo e os gráficos.";
            CloseBackgroundWindow();
        }
    }

    /// <summary>
    /// Sincronização em segundo plano terminada (ou sem login): fecha a janela em vez de só escondê-la. O navegador do
    /// WebView2 dela ocupava centenas de MB o tempo todo; a próxima sincronização abre outra, com o mesmo login (os
    /// cookies ficam gravados no perfil). Fora do evento da página, pela fila da interface.
    /// </summary>
    private void CloseBackgroundWindow()
    {
        if (_interactive || _closed || !ReferenceEquals(_windowView, this) || _window is not { } window) return;
        window.DispatcherQueue.TryEnqueue(() => { if (!_interactive && ReferenceEquals(_windowView, this)) window.Close(); });
    }
    public static void Pause()
    {
        if (File.Exists(EnabledFile)) File.Delete(EnabledFile);
        if (_windowView is { } view) { view._syncing = false; view._web.CoreWebView2?.Stop(); }
    }
    public static FrameworkElement Section()
    {
        var root = new StackPanel { Spacing = 8 };
        var state = PanelKit.Caption("", 12);
        void RefreshState() => state.Text = "Faturamento: " + (Enabled ? "sincronização habilitada; isso não confirma login válido." : "sincronização pausada.")
            + "\nConta: e-mail não exposto pela integração. Confira no painel oficial."
            + (OpenAiDashboardSession.Current is { } snapshot ? "\nOrganização do último conjunto: " + (snapshot.Account ?? "não informada") + " · atualizado em " + snapshot.Updated.LocalDateTime.ToString("dd/MM HH:mm") : "\nSem dados de faturamento sincronizados.");
        RefreshState(); root.Children.Add(state);
        var connect = WinStatusBar.Views.Panels.PanelKit.Small(new Button { Content = Enabled ? "Conta OpenAI" : "Entrar na OpenAI", FontSize = 12 });
        connect.Click += (_, _) =>
        {
            Directory.CreateDirectory(Folder); File.WriteAllText(EnabledFile, "1");
            ShowWindow();
        };
        var refresh = WinStatusBar.Views.Panels.PanelKit.Small(new Button { Content = "Sincronizar", FontSize = 12 });
        refresh.Click += (_, _) => { if (Enabled) Refresh(); else ShowWindow(); };
        var logout = new Button { Content = "Sair…", FontSize = 12 };
        ToolTipService.SetToolTip(logout, "Sair do painel de faturamento");
        logout.Click += async (_, _) =>
        {
            var confirm = new ContentDialog { XamlRoot = root.XamlRoot, Title = "Sair da conta de faturamento?",
                Content = "Remove a sessão do painel OpenAI desta barra e os dados de faturamento em cache. As chaves de API de conversa, a chave administrativa e o login do Codex são conexões separadas.",
                PrimaryButtonText = "Sair deste painel", CloseButtonText = "Cancelar", DefaultButton = ContentDialogButton.Close };
            if (await confirm.ShowAsync() != ContentDialogResult.Primary) return;
            logout.IsEnabled = false;
            try
            {
                Pause();
                if (_windowView is null) ShowWindow();
                var view = _windowView ?? throw new InvalidOperationException();
                _environment ??= EnvironmentAsync();
                await view._web.EnsureCoreWebView2Async(await _environment);
                view._web.CoreWebView2.Stop();
                await view._web.CoreWebView2.Profile.ClearBrowsingDataAsync(CoreWebView2BrowsingDataKinds.AllProfile);
                view._web.CoreWebView2.Navigate("about:blank");
                OpenAiDashboardSession.Clear();
                RefreshState();
                state.Text += "\nSessão local do painel encerrada. Chaves de API mantidas; remova-as nos controles específicos se desejar.";
            }
            catch { state.Text = "Não foi possível confirmar a saída do painel. Abra Conta OpenAI e saia pelo site."; }
            finally { logout.IsEnabled = true; }
        };
        // Ações lado a lado, como antes (07/11/2026); quem chama pode inserir mais botões nesta linha (Tag "actions").
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Tag = "actions" };
        actions.Children.Add(connect); actions.Children.Add(refresh); actions.Children.Add(logout);
        root.Children.Add(actions);
        root.Loaded += (_, _) => { RefreshState(); OpenAiDashboardSession.Changed += RefreshState; };
        root.Unloaded += (_, _) => OpenAiDashboardSession.Changed -= RefreshState;
        return root;
    }
}
