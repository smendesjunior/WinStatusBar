using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WinStatusBar.Core.Ai;
using WinStatusBar.Core.Settings;
using WinStatusBar.Services;
using WinStatusBar.Views.Panels;
namespace WinStatusBar.Views;

/// <summary>
/// Coluna "Claude API" no menu de cotas do Claude (2.0.0), no mesmo desenho da coluna "OpenAI API" do Codex: tokens e
/// gastos dos últimos 30 dias, pelos relatórios oficiais da Anthropic (chave administrativa do cartão Claude Code).
/// Aparece só com uma chave da Anthropic configurada (de conversa ou administrativa).
/// </summary>
internal static class ClaudeApiUsageView
{
    private static IReadOnlyList<ApiUsageDay>? _cached;
    private static DateTimeOffset _updated;
    private static string? _error;
    private static Task<IReadOnlyList<ApiUsageDay>>? _pending;

    public static bool Configured(AppSettings s) =>
        ProviderApiKeys.AdminKey(ProviderApiKeys.Anthropic) is not null || ProviderApiKeys.ChatKey(s, ProviderApiKeys.Anthropic) is not null;

    // Leitura do Cofre guardada por 1 minuto: o painel de tarefas pergunta a cada atualização.
    private static (bool Configured, bool Admin, DateTimeOffset At) _keys;
    /// <summary>Chave salva ou removida no cartão: relê o Cofre e os gastos na próxima atualização.</summary>
    public static void KeysChanged() { _keys = default; _updated = default; _cached = null; _error = null; }
    private static (bool Configured, bool Admin) Keys(AppSettings s)
    {
        if (DateTimeOffset.Now - _keys.At > TimeSpan.FromMinutes(1))
        {
            bool admin = ProviderApiKeys.AdminKey(ProviderApiKeys.Anthropic) is not null;
            _keys = (admin || ProviderApiKeys.ChatKey(s, ProviderApiKeys.Anthropic) is not null, admin, DateTimeOffset.Now);
        }
        return (_keys.Configured, _keys.Admin);
    }

    /// <summary>
    /// Texto da Claude API na linha do Claude no painel de tarefas ("API usado US$ 12,34"), como o da OpenAI API no
    /// Codex; null sem chave da Anthropic. Com a chave administrativa, atualiza os gastos em segundo plano.
    /// </summary>
    public static string? OverviewMeta(AppSettings s, int refreshMinutes)
    {
        var (configured, admin) = Keys(s);
        if (!configured) return null;
        if (!admin) return "API: chave configurada";
        if (_pending is null && DateTimeOffset.Now - _updated > TimeSpan.FromMinutes(Math.Max(1, refreshMinutes))
            && ProviderApiKeys.AdminKey(ProviderApiKeys.Anthropic) is { } key)
            _ = Task.Run(async () =>
            {
                try { _pending ??= AnthropicUsageClient.ReadAsync(key, CancellationToken.None); _cached = await _pending; _updated = DateTimeOffset.Now; _error = null; }
                catch (Exception ex) { _error = ex.Message; _updated = DateTimeOffset.Now; }
                finally { _pending = null; }
            });
        var pt = System.Globalization.CultureInfo.GetCultureInfo("pt-BR");
        return _cached is { Count: > 0 } days ? "API usado US$ " + days.Sum(d => d.Cost).ToString("N2", pt) : _error is null ? "API: lendo gastos…" : "API: gastos indisponíveis";
    }

    public static FrameworkElement Build()
    {
        var settings = ((App)Application.Current).Settings.Current;
        var root = new StackPanel { Width = 380, Spacing = 6 };
        root.Children.Add(new TextBlock { Text = "Claude API", FontSize = 18, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        var total = new TextBlock { FontSize = 20 };
        root.Children.Add(total);
        var charts = new Grid { RowSpacing = 6 };
        charts.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); charts.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var tokenChart = new ApiTokenChart(); var costChart = new ApiTokenChart("Costs");
        TextBlock Card(string name, ApiTokenChart chart, int row, bool visible)
        {
            var title = new TextBlock { Text = name, FontSize = 14, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold };
            var content = new StackPanel { Spacing = 4 }; content.Children.Add(title); content.Children.Add(chart);
            var card = new Border { Padding = new Thickness(10, 8, 10, 8), CornerRadius = new CornerRadius(8), Background = PanelKit.Res("CardBackgroundFillColorDefaultBrush"), Child = content };
            Grid.SetRow(card, row); card.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
            charts.Children.Add(card);
            return title;
        }
        var tokensTitle = Card("Tokens", tokenChart, 0, settings.ApiShowTokens);
        var costsTitle = Card("Gastos", costChart, 1, settings.ApiShowCosts);
        root.Children.Add(charts);
        var status = PanelKit.Caption("", 11); status.TextWrapping = TextWrapping.Wrap; root.Children.Add(status);
        var refresh = PanelKit.Small(new Button { Content = "Atualizar" });
        var console = PanelKit.Small(new HyperlinkButton { Content = "Console da Anthropic", NavigateUri = new Uri("https://console.anthropic.com/usage") });
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        actions.Children.Add(refresh); actions.Children.Add(console);
        root.Children.Add(actions);

        void Render()
        {
            bool admin = ProviderApiKeys.AdminKey(ProviderApiKeys.Anthropic) is not null;
            refresh.Visibility = admin ? Visibility.Visible : Visibility.Collapsed;
            var days = _cached;
            charts.Visibility = days is { Count: > 0 } ? Visibility.Visible : Visibility.Collapsed;
            if (!admin)
            {
                total.Text = "Uso e gastos não configurados";
                status.Text = "Para ver tokens e gastos, salve uma chave administrativa (sk-ant-admin…) em Configurações › IAs e contas › Claude Code. A chave de conversa não tem acesso aos relatórios da Anthropic.";
                return;
            }
            if (days is not { Count: > 0 }) { total.Text = "Gastos · —"; status.Text = _error ?? "Consultando a Anthropic…"; return; }
            decimal cost = days.Sum(d => d.Cost);
            total.Text = $"Gastos · US$ {cost:N2}";
            costsTitle.Text = $"Gastos · US$ {cost:N2}";
            tokensTitle.Text = $"Tokens · {days.Sum(d => d.Input + d.Output):N0}";
            tokenChart.Update(days); costChart.Update(days);
            status.Text = $"{days[0].Date:dd/MM} a {days[^1].Date:dd/MM} · atualizado em {_updated.ToLocalTime():dd/MM HH:mm} · relatórios da organização (Anthropic)"
                + (_error is null ? "" : " · última consulta: " + _error);
        }
        async Task Load()
        {
            if (ProviderApiKeys.AdminKey(ProviderApiKeys.Anthropic) is not { } key) { Render(); return; }
            refresh.IsEnabled = false; if (_cached is null) status.Text = "Consultando a Anthropic…";
            try { _pending ??= AnthropicUsageClient.ReadAsync(key, CancellationToken.None); _cached = await _pending; _updated = DateTimeOffset.Now; _error = null; }
            catch (Exception ex) { _error = ex.Message; }
            finally { _pending = null; refresh.IsEnabled = true; Render(); }
        }
        refresh.Click += async (_, _) => await Load();
        root.Loaded += async (_, _) => { Render(); if (DateTimeOffset.Now - _updated > TimeSpan.FromMinutes(Math.Max(1, settings.ApiUsageRefreshMinutes))) await Load(); };
        Render();
        return root;
    }
}
