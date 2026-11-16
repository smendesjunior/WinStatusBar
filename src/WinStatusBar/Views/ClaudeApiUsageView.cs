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
