using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WinStatusBar.Core.Settings;
using WinStatusBar.Services;
using WinStatusBar.Views.Panels;
namespace WinStatusBar.Views;

/// <summary>
/// Páginas das Configurações montadas em código e a ordem do menu (SettingsNavigation). Organização de 11/11/2026:
/// Geral · Barra e aparência · IAs e contas · Cotas e tarefas · Chat da barra · Codex e modelos · Hardware ·
/// Rede e bateria · Área de transferência e, no rodapé, Sobre e suporte.
/// Cada grupo montado em código entra num painel com nome declarado no XAML (ShortcutsHost, AiOrderHost…) ou numa
/// página criada por AddPage; nada é localizado por posição nem pelo texto do título.
/// </summary>
public sealed partial class SettingsWindow
{
    private readonly Dictionary<string, StackPanel> _organizedPages = new();
    // A API própria é montada junto do chat (Extras) e mostrada em "IAs e contas"; o saldo/faturamento da OpenAI fica
    // dentro do cartão do Codex.
    private readonly StackPanel _apiSettingsPage = new() { Spacing = 12 };
    private readonly StackPanel _openAiBillingPanel = new() { Spacing = 12 };
    /// <summary>Título do grupo "Bateria" (destino do nome antigo "battery").</summary>
    private FrameworkElement? _batterySection;
    /// <summary>Relê os modos de energia do Windows ao entrar em "Rede e bateria".</summary>
    private Action? _refreshPower;

    private StackPanel AddPage(string tag, string title, string description, string glyph, bool footer = false)
    {
        var page = new StackPanel { Spacing = 12, Visibility = Visibility.Collapsed };
        var header = new StackPanel { Spacing = 4 };
        header.Children.Add(new TextBlock { Text = title, Style = (Style)Root.Resources["PageTitle"] });
        header.Children.Add(Help(description));
        page.Children.Add(header);
        SettingsPages.Children.Add(page);
        var item = new NavigationViewItem { Content = title, Tag = tag, Icon = new FontIcon { Glyph = glyph } };
        if (footer) Nav.FooterMenuItems.Add(item); else Nav.MenuItems.Add(item);
        _organizedPages[tag] = page;
        return page;
    }
    private Button ActionButton(string label, Action action)
    {
        var button = new Button { Content = label, HorizontalAlignment = HorizontalAlignment.Left };
        button.Click += (_, _) => action(); return button;
    }
    private static Border Divider() => new() { Height = 1, Background = ThemeBrushes.Get("DividerStrokeColorDefaultBrush") };

    private void BuildOrganizedPages()
    {
        var clear = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent);
        Root.Resources["ExpanderChevronBackground"] = clear;
        Root.Resources["ExpanderChevronBorderBrush"] = clear;
        AddPage("accounts", Localization.T("IAs e contas"), Localization.T("Entre nas suas IAs, guarde as chaves de API e veja os conectores e plugins de cada uma."), "");
        BuildNetworkAndBatteryPage();
    }

    private void BuildNetworkAndBatteryPage()
    {
        var page = AddPage("connections", Localization.T("Rede e bateria"), Localization.T("A conexão acompanhada, os painéis de rede e de bateria e as preferências de energia do Windows."), "");

        // Rede: o grupo com a conexão acompanhada vem do XAML (NetworkBox) e ganha aqui as opções do painel de rede.
        GpuPage.Children.Remove(NetworkGroup);
        page.Children.Add(NetworkGroup);
        NetworkRows.Children.Add(Divider());
        NetworkRows.Children.Add(Toggle(Localization.T("Wi-Fi, Bluetooth e hotspot no painel de rede"), "Mostra Wi-Fi, Bluetooth e Hotspot móvel no painel da barra. Wi-Fi e Bluetooth abrem listas junto aos botões, para conectar ou parear; o hotspot é configurado no Windows.", s => s.NetworkConnectionsVisible, (s,v) => s.NetworkConnectionsVisible = v));
        NetworkRows.Children.Add(Divider());
        NetworkRows.Children.Add(Toggle("Teste de velocidade", "Mostra o teste no painel da rede e mede sozinho a cada 10 minutos (teste leve, cerca de 16 MB; pausa em rede limitada, como celular ou plano de dados). Usa servidores da Cloudflare.", s => s.SpeedTestEnabled, (s,v) => s.SpeedTestEnabled = v));

        // Conectar agora: os mesmos seletores do painel de rede, num cartão só.
        page.Children.Add(GroupTitle(Localization.T("Conectar agora")));
        page.Children.Add(Help(Localization.T("Escolha redes e dispositivos nos menus dos botões. O Windows realiza a conexão e o pareamento.")));
        var connect = new List<FrameworkElement>();
        foreach (string name in new[] { "Wi-Fi", "Bluetooth", "Hotspot móvel" })
        {
            var button = new Button { Content = "Abrir " + name };
            var feedback = Help("");
            feedback.Visibility = Visibility.Collapsed;
            feedback.Margin = new Thickness(16, 0, 16, 12);
            button.Click += async (_, _) => { button.IsEnabled = false; try { await ConnectionPicker.ShowAsync(button, name, message => { feedback.Text = message; feedback.Visibility = string.IsNullOrWhiteSpace(message) ? Visibility.Collapsed : Visibility.Visible; }); } finally { button.IsEnabled = true; } };
            connect.Add(Row(name, name == "Bluetooth" ? "O Windows cuida do pareamento. Dispositivos já pareados podem exigir conexão no aplicativo correspondente." : name == "Wi-Fi" ? "Escolha a rede na lista junto ao botão. Perfis salvos são reutilizados; redes pessoais novas podem pedir senha. A conexão é realizada pelo Windows." : "Compartilhe sua conexão; nome da rede, senha e ativação são controlados pelo Windows.", button));
            connect.Add(feedback);
        }
        page.Children.Add(Group(connect.ToArray()));

        // Bateria. Mostrar ou esconder o módulo na barra fica em "Barra e aparência › Itens da barra" (um lugar só).
        var batteryTitle = GroupTitle(Localization.T("Bateria"));
        _batterySection = batteryTitle;
        page.Children.Add(batteryTitle);
        page.Children.Add(Group(
            Choice("Período inicial do histórico", "Define o período ao abrir o painel. Você também pode alternar no próprio gráfico, com detalhes ao passar o mouse.", [("Últimas 24 horas",1),("Últimos 7 dias",7)], s => s.BatteryHistoryDays, (s,v) => s.BatteryHistoryDays = v),
            Toggle(Localization.T("Mostrar controles de energia no painel da bateria"), Localization.T("Mostra modo de energia, limite da economia e redução de brilho no painel da bateria."), s => s.BatteryPowerControlsVisible, (s,v) => s.BatteryPowerControlsVisible = v)));
        page.Children.Add(GroupTitle(Localization.T("Energia do Windows")));
        page.Children.Add(Help("Conectado e na bateria têm modos independentes. A economia pode começar num percentual escolhido e reduzir o brilho. Recursos não oferecidos pelo computador ficam indisponíveis."));
        var power = new BatteryPowerControls();
        // No mesmo cartão das demais configurações (antes os controles ficavam soltos na página).
        page.Children.Add(new Border { Style = (Style)Root.Resources["SettingsGroup"], Child = power.Content, Padding = new Thickness(16, 12, 16, 14) });
        page.Loaded += async (_, _) => await power.RefreshAsync();
        _refreshPower = () => _ = power.RefreshAsync();
    }

    private void FinishOrganizedPages()
    {
        _openAiBillingPanel.Children.Add(Group(Row("Conta conectada", "Entre no site oficial para autorizar a leitura nesta máquina. Organização e período seguem os filtros do painel; saldo e validade vêm do faturamento. Use Conta OpenAI para trocar a organização ou sair pelo próprio site.", OpenAiDashboardView.Section()),
            Row("Pausar sincronização da conta", "Interrompe novas consultas pelo login sem apagar sua sessão no site. Entrar novamente reativa a sincronização.", ActionButton("Pausar", () => OpenAiDashboardView.Pause()))));
        _openAiBillingPanel.Children.Add(Group(
            Toggle("Mostrar no menu de cotas", "Exibe o saldo e os gráficos nativos ao abrir as cotas do Codex. Cotas da assinatura e créditos da API são informações diferentes.", s => s.ApiUsageVisible, (s,v) => s.ApiUsageVisible = v),
            Choice("Atualizar automaticamente a cada", "Em segundo plano, mesmo com o menu fechado, usando a conta conectada ou a chave administrativa. O último conjunto completo fica visível durante a atualização; não realiza chamadas de geração de texto.", [("1 minuto",1),("5 minutos",5),("15 minutos",15),("30 minutos",30),("1 hora",60)], s => s.ApiUsageRefreshMinutes, (s,v) => s.ApiUsageRefreshMinutes = v),
            Toggle("Gráfico de tokens", "Separa tokens de entrada e saída. Passe o mouse sobre o gráfico para ver os valores por dia.", s => s.ApiShowTokens, (s,v) => s.ApiShowTokens = v),
            Toggle("Gráfico de gastos", "Valores em dólares, no período e na organização consultados. Custo zero não significa ausência de uso.", s => s.ApiShowCosts, (s,v) => s.ApiShowCosts = v),
            Toggle("Gráfico de requisições", "Quantidade diária de solicitações registradas pela conta.", s => s.ApiShowRequests, (s,v) => s.ApiShowRequests = v)));
        var key = new PasswordBox { PlaceholderText = "Chave administrativa (opcional)" };
        var feedback = Help(ApiKeyStore.Get("WinStatusBar.OpenAiBilling") is null ? "Nenhuma chave administrativa salva." : "Chave administrativa salva neste PC.");
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        buttons.Children.Add(ActionButton("Salvar", () => { if (string.IsNullOrWhiteSpace(key.Password)) return; try { ApiKeyStore.Set(key.Password, "WinStatusBar.OpenAiBilling"); key.Password = ""; feedback.Text = "Chave administrativa salva."; } catch { feedback.Text = "Não foi possível salvar a chave."; } }));
        buttons.Children.Add(ActionButton("Remover", () => { ApiKeyStore.Set(null, "WinStatusBar.OpenAiBilling"); feedback.Text = "Chave administrativa removida."; }));
        var credentials = new StackPanel { Spacing = 6 }; credentials.Children.Add(key); credentials.Children.Add(buttons); credentials.Children.Add(feedback);
        _openAiBillingPanel.Children.Add(Group(Row(Localization.T("Chave de administrador da OpenAI (opcional)"), "Alternativa ao login: requer chave administrativa da organização. É diferente da chave de conversa e não informa saldo de créditos. Fica no Cofre do Windows; não é sincronizada.", credentials)));
        BuildModelsPage();
        BuildAboutPage();

        // Menu na ordem de SettingsNavigation: as páginas do XAML e as criadas por AddPage; "Sobre e suporte" já está no rodapé.
        var items = Nav.MenuItems.OfType<NavigationViewItem>().ToDictionary(i => (string)i.Tag);
        Nav.MenuItems.Clear();
        foreach (string tag in SettingsNavigation.Menu) if (items.TryGetValue(tag, out var item)) Nav.MenuItems.Add(item);
    }
}
