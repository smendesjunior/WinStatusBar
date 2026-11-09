using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WinStatusBar.Services;
using WinStatusBar.Views.Panels;
namespace WinStatusBar.Views;

public sealed partial class SettingsWindow
{
    private readonly Dictionary<string, StackPanel> _organizedPages = new();
    // A antiga página "API e faturamento" foi absorvida (07/11/2026): a API própria vira um bloco no fim das contas
    // das IAs e o saldo/faturamento da OpenAI fica dentro do cartão do Codex.
    private readonly StackPanel _apiSettingsPage = new() { Spacing = 12 };
    private readonly StackPanel _openAiBillingPanel = new() { Spacing = 12 };
    private StackPanel AddPage(string tag, string title, string description, string glyph)
    {
        var page = new StackPanel { Spacing = 12, Visibility = Visibility.Collapsed };
        page.Children.Add(new TextBlock { Text = title, Style = (Style)Root.Resources["PageTitle"] });
        page.Children.Add(Help(description));
        SettingsPages.Children.Add(page);
        Nav.MenuItems.Insert(Nav.MenuItems.Count - 2, new NavigationViewItem { Content = title, Tag = tag, Icon = new FontIcon { Glyph = glyph } });
        _organizedPages[tag] = page;
        return page;
    }
    private Button ActionButton(string label, Action action)
    {
        var button = new Button { Content = label, HorizontalAlignment = HorizontalAlignment.Left };
        button.Click += (_, _) => action(); return button;
    }
    private void BuildOrganizedPages()
    {
        var clear = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent);
        Root.Resources["ExpanderChevronBackground"] = clear;
        Root.Resources["ExpanderChevronBorderBrush"] = clear;
        var network = AddPage("connections", "Conexões", "Escolha a conexão monitorada e escolha redes e dispositivos nos menus dos botões. O Windows realiza a conexão e o pareamento.", "\uE701");
        int networkHeading = GpuPage.Children.ToList().FindIndex(e => e is TextBlock t && t.Text == Localization.T("Rede"));
        if (networkHeading >= 0)
            for (int i = 0; i < 2; i++) { var element = GpuPage.Children[networkHeading]; GpuPage.Children.RemoveAt(networkHeading); network.Children.Add(element); }
        network.Children.Add(Group(Toggle("Seletores no menu de rede", "Mostra Wi-Fi, Bluetooth e Hotspot móvel no painel da barra. Wi-Fi e Bluetooth abrem listas junto aos botões, para conectar ou parear; o hotspot é configurado no Windows.", s => s.NetworkConnectionsVisible, (s,v) => s.NetworkConnectionsVisible = v)));
        network.Children.Add(Group(Toggle("Teste de velocidade", "Mostra o teste no painel da rede. Só consome dados quando você inicia; usa servidores da Cloudflare.", s => s.SpeedTestEnabled, (s,v) => s.SpeedTestEnabled = v)));
        foreach (string name in new[] { "Wi-Fi", "Bluetooth", "Hotspot móvel" })
        {
            var button = new Button { Content = "Abrir " + name };
            var feedback = Help("");
            feedback.Visibility = Visibility.Collapsed;
            feedback.Margin = new Thickness(16, 0, 16, 12);
            button.Click += async (_, _) => { button.IsEnabled = false; try { await ConnectionPicker.ShowAsync(button, name, message => { feedback.Text = message; feedback.Visibility = string.IsNullOrWhiteSpace(message) ? Visibility.Collapsed : Visibility.Visible; }); } finally { button.IsEnabled = true; } };
            network.Children.Add(Group(Row(name, name == "Bluetooth" ? "O Windows cuida do pareamento. Dispositivos já pareados podem exigir conexão no aplicativo correspondente." : name == "Wi-Fi" ? "Escolha a rede na lista junto ao botão. Perfis salvos são reutilizados; redes pessoais novas podem pedir senha. A conexão é realizada pelo Windows." : "Compartilhe sua conexão; nome da rede, senha e ativação são controlados pelo Windows.", button), feedback));
        }
        var battery = AddPage("battery", "Bateria e energia", "Leituras e histórico vêm do Windows. Os modos abaixo alteram as preferências de energia do computador.", "\uE83F");
        battery.Children.Add(Group(
            Toggle("Widget de bateria", "Mostra carga, estado de alimentação e estimativa de tempo. O ícone muda de acordo com os dados informados pelo Windows.", s => !s.HiddenModules.Contains("battery"), (s,v) => { if (v) s.HiddenModules.Remove("battery"); else s.HiddenModules.Add("battery"); var module = _modules.FirstOrDefault(m => m.Key == "battery"); if (module is not null) module.IsShown = v; }),
            Choice("Período inicial do histórico", "Define o período ao abrir o painel. Você também pode alternar no próprio gráfico, com detalhes ao passar o mouse.", [("Últimas 24 horas",1),("Últimos 7 dias",7)], s => s.BatteryHistoryDays, (s,v) => s.BatteryHistoryDays = v),
            Toggle("Controles de energia no menu", "Mostra modo de energia, limite da economia e redução de brilho no painel da bateria. Eles continuam disponíveis aqui.", s => s.BatteryPowerControlsVisible, (s,v) => s.BatteryPowerControlsVisible = v)));
        battery.Children.Add(GroupTitle("Preferências do Windows"));
        battery.Children.Add(Help("Conectado e na bateria têm modos independentes. A economia pode começar num percentual escolhido e reduzir o brilho. Recursos não oferecidos pelo computador ficam indisponíveis."));
        var power = new BatteryPowerControls();
        // No mesmo cartão das demais configurações (antes os controles ficavam soltos na página).
        battery.Children.Add(new Border { Style = (Style)Root.Resources["SettingsGroup"], Child = power.Content, Padding = new Thickness(16, 12, 16, 14) });
        battery.Loaded += async (_, _) => await power.RefreshAsync();
        var shortcuts = AddPage("shortcuts", "Atalhos", "Arraste a alça para ordenar os botões e use os interruptores para escolher quais aparecem à direita da barra.", "\uE71D");
        // Move the existing shortcut editor, preserving its bindings and saved order.
        int shortcutHeading = AppearancePage.Children.ToList().FindIndex(e => e is TextBlock t && t.Text == Localization.T("Botões de atalho"));
        if (shortcutHeading >= 0)
            for (int i = 0; i < 2; i++) { var element = AppearancePage.Children[shortcutHeading]; AppearancePage.Children.RemoveAt(shortcutHeading); shortcuts.Children.Add(element); }
    }
    private void FinishOrganizedPages()
    {
        for (int i = 0; i < 2; i++) { var item = CommandPage.Children[CommandPage.Children.Count - (2 - i)]; CommandPage.Children.Remove(item); AiPage.Children.Add(item); }
        CommandPage.Children.Add(GroupTitle("Permissão de acesso ao PC e à rede"));
        CommandPage.Children.Add(Group(
            Toggle("Permitir acesso amplo ao PC e à rede", "Autoriza o executor da barra a atuar em aplicativos, janelas, arquivos e pastas locais, unidades mapeadas e compartilhamentos de rede (caminhos UNC), conforme seu pedido. Inclui ler, criar, editar, renomear, mover e converter arquivos, executar comandos e scripts e interagir com serviços e dispositivos acessíveis, como impressoras, celulares e NAS. Vale para todas as IAs usadas no chat da barra, com o lápis de edição ligado.", s => s.ContextActionsEnabled, (s,v) => s.ContextActionsEnabled = v),
            Help("O conteúdo e os resultados usados na tarefa podem ser enviados à IA selecionada. Desligue esta permissão para impedir novas ações do executor; use Parar para interromper a tarefa atual. As permissões da sua conta do Windows e da rede continuam valendo. Acesso elevado exige sua autorização no UAC. Serviços e dispositivos dependem das interfaces de controle, conexão, credenciais e autorizações disponíveis; estar na mesma rede não libera acesso a um celular ou outro equipamento.")));
        _openAiBillingPanel.Children.Add(Group(
            Toggle("Mostrar no menu de cotas", "Exibe o saldo e os gráficos nativos ao abrir as cotas do Codex. Cotas da assinatura e créditos da API são informações diferentes.", s => s.ApiUsageVisible, (s,v) => s.ApiUsageVisible = v),
            Choice("Atualizar automaticamente a cada", "Em segundo plano, mesmo com o menu fechado, usando a conta conectada ou a chave administrativa. O último conjunto completo fica visível durante a atualização; não realiza chamadas de geração de texto.", [("1 minuto",1),("5 minutos",5),("15 minutos",15),("30 minutos",30),("1 hora",60)], s => s.ApiUsageRefreshMinutes, (s,v) => s.ApiUsageRefreshMinutes = v),
            Toggle("Gráfico de tokens", "Separa tokens de entrada e saída. Passe o mouse sobre o gráfico para ver os valores por dia.", s => s.ApiShowTokens, (s,v) => s.ApiShowTokens = v),
            Toggle("Gráfico de gastos", "Valores em dólares, no período e na organização consultados. Custo zero não significa ausência de uso.", s => s.ApiShowCosts, (s,v) => s.ApiShowCosts = v),
            Toggle("Gráfico de requisições", "Quantidade diária de solicitações registradas pela conta.", s => s.ApiShowRequests, (s,v) => s.ApiShowRequests = v)));
        _openAiBillingPanel.Children.Add(Group(Row("Conta conectada", "Entre no site oficial para autorizar a leitura nesta máquina. Organização e período seguem os filtros do painel; saldo e validade vêm do faturamento. Use Conta OpenAI para trocar a organização ou sair pelo próprio site.", OpenAiDashboardView.Section()),
            Row("Pausar sincronização da conta", "Interrompe novas consultas pelo login sem apagar sua sessão no site. Entrar novamente reativa a sincronização.", ActionButton("Pausar", () => OpenAiDashboardView.Pause()))));
        var key = new PasswordBox { PlaceholderText = "Chave administrativa (opcional)" };
        var feedback = Help(ApiKeyStore.Get("WinStatusBar.OpenAiBilling") is null ? "Nenhuma chave administrativa salva." : "Chave administrativa salva neste PC.");
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        buttons.Children.Add(ActionButton("Salvar", () => { if (string.IsNullOrWhiteSpace(key.Password)) return; try { ApiKeyStore.Set(key.Password, "WinStatusBar.OpenAiBilling"); key.Password = ""; feedback.Text = "Chave administrativa salva."; } catch { feedback.Text = "Não foi possível salvar a chave."; } }));
        buttons.Children.Add(ActionButton("Remover", () => { ApiKeyStore.Set(null, "WinStatusBar.OpenAiBilling"); feedback.Text = "Chave administrativa removida."; }));
        var credentials = new StackPanel { Spacing = 6 }; credentials.Children.Add(key); credentials.Children.Add(buttons); credentials.Children.Add(feedback);
        _openAiBillingPanel.Children.Add(Group(Row("Consulta direta de uso e custos", "Alternativa ao login: requer chave administrativa da organização. É diferente da chave de conversa e não informa saldo de créditos. Fica no Cofre do Windows; não é sincronizada.", credentials)));
        BuildModelsPage();
        BuildUpdatesPage();
        BuildAboutPage();
        string[] order = ["system", "appearance", "shortcuts", "ai", "models", "command", "gpu", "connections", "battery", "clip", "diagnostics", "updates", "about"];
        var items = Nav.MenuItems.OfType<NavigationViewItem>().ToDictionary(i => (string)i.Tag);
        Nav.MenuItems.Clear(); foreach (string tag in order) if (items.TryGetValue(tag, out var item)) Nav.MenuItems.Add(item);
        if (items.TryGetValue("gpu", out var hardware)) hardware.Content = "Hardware";
        // O título da página acompanha o menu (a parte de rede foi para "Conexões").
        if (GpuPage.Children.FirstOrDefault() is StackPanel head && head.Children.FirstOrDefault() is TextBlock pageTitle) pageTitle.Text = "Hardware";
        if (items.TryGetValue("command", out var chat)) chat.Content = "Chat e contexto";
        AppearancePage.Children.Add(Help("A caixa de IA permanece centralizada. Widgets ficam à direita e se adaptam ao espaço e ao DPI; atalhos excedentes entram no menu de mais opções. Os gráficos usam desenho nativo para preservar a nitidez."));
    }
}
