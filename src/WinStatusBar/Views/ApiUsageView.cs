using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WinStatusBar.Core.Ai;
using WinStatusBar.Services;
using WinStatusBar.Views.Panels;
namespace WinStatusBar.Views;

internal static class ApiUsageView
{
    private const string Vault = "WinStatusBar.OpenAiBilling";
    private static IReadOnlyList<ApiUsageDay>? _cached;
    private static DateTimeOffset _updated;
    private static Task<IReadOnlyList<ApiUsageDay>>? _pending;
    public static FrameworkElement Build()
    {
        var settings = ((App)Application.Current).Settings.Current;
        var root = new StackPanel { Width = 380, Spacing = 6 };
        root.Children.Add(new TextBlock { Text = "OpenAI API", FontSize = 18, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        var balance = new TextBlock { FontSize = 20, TextWrapping = TextWrapping.Wrap };
        var validity = PanelKit.Caption("", 11);
        root.Children.Add(balance); root.Children.Add(validity);
        var charts = new Grid { RowSpacing = 6, ColumnSpacing = 6 };
        charts.ColumnDefinitions.Add(new ColumnDefinition()); charts.ColumnDefinitions.Add(new ColumnDefinition());
        charts.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); charts.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var tokenChart = new ApiTokenChart(); var costChart = new ApiTokenChart("Costs"); var requestsChart = new ApiTokenChart("Requests");
        TextBlock Card(string name, ApiTokenChart chart)
        {
            var title = new TextBlock { Text = name, FontSize = 14, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold };
            var content = new StackPanel { Spacing = 4 }; content.Children.Add(title); content.Children.Add(chart);
            var card = new Border { Padding = new Thickness(10, 8, 10, 8), CornerRadius = new CornerRadius(8), Background = PanelKit.Res("CardBackgroundFillColorDefaultBrush"), Child = content };
            if (name == "Tokens") { Grid.SetRow(card, 0); Grid.SetColumnSpan(card, 2); }
            else { Grid.SetRow(card, 1); Grid.SetColumn(card, name == "Gastos" ? 0 : 1); }
            card.Visibility = (name == "Tokens" ? settings.ApiShowTokens : name == "Gastos" ? settings.ApiShowCosts : settings.ApiShowRequests) ? Visibility.Visible : Visibility.Collapsed;
            charts.Children.Add(card);
            return title;
        }
        var costsTitle = Card("Gastos", costChart);
        var tokensTitle = Card("Tokens", tokenChart);
        var requestsTitle = Card("Requisições", requestsChart);
        root.Children.Add(charts);
        var status = PanelKit.Caption("", 11); root.Children.Add(status);
        var actions = (StackPanel)OpenAiDashboardView.Section();
        root.Children.Add(actions);
        var key = new PasswordBox { PlaceholderText = "Chave administrativa", PasswordRevealMode = PasswordRevealMode.Hidden };
        var save = WinStatusBar.Views.Panels.PanelKit.Small(new Button { Content = "Salvar e consultar", IsEnabled = false, FontSize = 12 });
        key.PasswordChanged += (_, _) => save.IsEnabled = !string.IsNullOrWhiteSpace(key.Password);
        var config = new StackPanel { Spacing = 6 };
        config.Children.Add(PanelKit.Caption("Alternativa ao login: chave administrativa no Cofre do Windows. A consulta direta fornece uso e custos; o saldo vem da conta conectada.", 12));
        config.Children.Add(key); config.Children.Add(save);
        var expander = new Expander { Header = "Consulta direta por chave (opcional)", Content = config, HorizontalAlignment = HorizontalAlignment.Stretch };
        var configure = WinStatusBar.Views.Panels.PanelKit.Small(new Button { Content = "Configurar", FontSize = 12 });
        configure.Click += async (_, _) => { var dialog = new ContentDialog { Title = "Consulta direta por chave", Content = config, CloseButtonText = "Fechar", XamlRoot = root.XamlRoot, RequestedTheme = root.ActualTheme }; await dialog.ShowAsync(); dialog.Content = null; };
        ToolTipService.SetToolTip(configure, "Configurar consulta direta por chave administrativa");
        var row = actions.Children.OfType<StackPanel>().First(p => Equals(p.Tag, "actions"));
        row.Children.Insert(row.Children.Count - 1, configure); // antes de "Sair…"
        void Render()
        {
            var session = OpenAiDashboardSession.Current;
            var days = session?.Days.Count > 0 ? session.Days : _cached;
            balance.Text = session?.Balance is { } available ? $"Saldo disponível · US$ {available:N2}" : "Saldo disponível · não informado";
            validity.Text = session?.Expiration is { } expires ? $"Próxima expiração: {expires.ToLocalTime():dd/MM/yyyy}" : "";
            validity.Visibility = validity.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
            charts.Visibility = days is { Count: > 0 } ? Visibility.Visible : Visibility.Collapsed;
            if (days is not { Count: > 0 }) { status.Text = "Conecte sua conta OpenAI para sincronizar saldo e gráficos."; return; }
            costsTitle.Text = $"Gastos · US$ {days.Sum(d => d.Cost):N2}";
            tokensTitle.Text = $"Tokens · {days.Sum(d => d.Input + d.Output):N0}";
            requestsTitle.Text = $"Requisições · {days.Sum(d => d.Requests):N0}";
            tokenChart.Update(days); costChart.Update(days); requestsChart.Update(days);
            var updated = session?.Updated ?? _updated;
            status.Text = $"{days[0].Date:dd/MM} a {days[^1].Date:dd/MM} · atualizado em {updated.ToLocalTime():dd/MM HH:mm}" + (session is null ? " · API da organização" : " · " + (session.Account ?? "conta conectada") + " · filtros do painel OpenAI");
        }
        async Task Load(bool direct = false)
        {
            if (!direct && OpenAiDashboardView.Enabled) { status.Text = "Sincronizando com a conta OpenAI…"; OpenAiDashboardView.Refresh(); return; }
            var credential = ApiKeyStore.Get(Vault);
            if (string.IsNullOrEmpty(credential)) { Render(); return; }
            save.IsEnabled = false; status.Text = "Consultando a API…";
            try { _pending ??= ApiUsageClient.ReadAsync(credential, CancellationToken.None); _cached = await _pending; _updated = DateTimeOffset.Now; Render(); }
            catch (Exception ex) { status.Text = ex.Message; }
            finally { _pending = null; save.IsEnabled = !string.IsNullOrWhiteSpace(key.Password); }
        }
        save.Click += async (_, _) => { ApiKeyStore.Set(key.Password, Vault); key.Password = ""; expander.IsExpanded = false; await Load(true); };
        void SessionChanged() => root.DispatcherQueue.TryEnqueue(Render);
        var timer = root.DispatcherQueue.CreateTimer(); timer.Interval = TimeSpan.FromMinutes(settings.ApiUsageRefreshMinutes);
        timer.Tick += async (_, _) => await Load();
        root.Loaded += async (_, _) => { OpenAiDashboardSession.Changed += SessionChanged; timer.Start(); Render(); if (DateTimeOffset.Now - (OpenAiDashboardSession.Current?.Updated ?? _updated) > TimeSpan.FromMinutes(settings.ApiUsageRefreshMinutes)) await Load(); };
        root.Unloaded += (_, _) => { timer.Stop(); OpenAiDashboardSession.Changed -= SessionChanged; };
        Render(); return root;
    }
}
