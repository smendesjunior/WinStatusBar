using System.Diagnostics;
using System.Runtime.CompilerServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using WinStatusBar.Services;

namespace WinStatusBar.Views;

/// <summary>
/// Contas das IAs no estilo do cartão de conta do Windows (Iniciar › conta Microsoft): avatar, nome, e-mail,
/// "Sair"/"Entrar" no canto e linhas de plano, chave de API e uso. Tudo é lido localmente; nada confirma validade online.
/// </summary>
public sealed partial class SettingsWindow
{
    private readonly List<Action> _accountRefreshers = new();

    /// <summary>
    /// Expansor dentro de um cartão, como nas Configurações do Windows 11: sem "cartão dentro do cartão" (fundo e borda
    /// transparentes), com a mesma divisória e a mesma grade das linhas (ícone de 24 px · 12 · rótulo) e o conteúdo
    /// recuado na coluna do rótulo. O recuo do cabeçalho vem de um StaticResource do modelo (16 px + borda), que o
    /// estilo leve não substitui: por isso é zerado na parte "ExpanderHeader" quando o modelo é aplicado.
    /// </summary>
    private static Border NestedExpander(string glyph, string header, UIElement content)
    {
        var title = new Grid { ColumnSpacing = 12 };
        title.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(24) });
        title.ColumnDefinitions.Add(new ColumnDefinition());
        title.Children.Add(new FontIcon { Glyph = glyph, FontSize = 16, VerticalAlignment = VerticalAlignment.Center });
        var label = new TextBlock { Text = header, FontSize = 14, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap };
        Grid.SetColumn(label, 1); title.Children.Add(label);
        var expander = new Expander
        {
            Header = title, Content = content,
            HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch,
            MinHeight = 40, BorderThickness = new Thickness(0), Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent),
        };
        var clear = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent);
        foreach (string key in new[] { "ExpanderHeaderBackground", "ExpanderHeaderBorderBrush", "ExpanderContentBackground", "ExpanderContentBorderBrush" })
            expander.Resources[key] = clear;
        expander.Resources["ExpanderHeaderBorderThickness"] = new Thickness(0);
        expander.Resources["ExpanderChevronMargin"] = new Thickness(12, 0, 0, 0);
        expander.Resources["ExpanderContentPadding"] = new Thickness(36, 4, 0, 10);
        // O Loaded do Expander pode vir antes do modelo; o do cabeçalho só vem depois, já dentro do botão "ExpanderHeader".
        title.Loaded += (_, _) =>
        {
            for (DependencyObject? node = title; node is not null && node != expander; node = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(node))
                if (node is Microsoft.UI.Xaml.Controls.Primitives.ToggleButton { Name: "ExpanderHeader" } part) { part.Padding = new Thickness(0); part.BorderThickness = new Thickness(0); break; }
        };
        // Divisória igual à das linhas (AccountRow); a borda do próprio Expander não é desenhada no cabeçalho.
        return new Border { BorderThickness = new Thickness(0, 1, 0, 0), BorderBrush = ThemeBrushes.Get("DividerStrokeColorDefaultBrush"), Child = expander };
    }

    private sealed class AccountCard
    {
        public required Border Root { get; init; }
        public required PersonPicture Picture { get; init; }
        public required TextBlock Name { get; init; }
        public required TextBlock Email { get; init; }
        public required TextBlock Note { get; init; }
        public required HyperlinkButton Action { get; init; }
        public required StackPanel Rows { get; init; }
        public required Image Logo { get; init; }
        public void Identity(string? name, string? email, string fallback)
        {
            Name.Text = name ?? email ?? fallback;
            Email.Text = name is not null ? email ?? "" : email is not null ? "" : "";
            Email.Visibility = string.IsNullOrEmpty(Email.Text) ? Visibility.Collapsed : Visibility.Visible;
            Picture.DisplayName = name ?? email ?? "";
        }
        public void SetNote(string text) { Note.Text = text; Note.Visibility = text.Length == 0 ? Visibility.Collapsed : Visibility.Visible; }
    }

    private static async Task SetClaudeLogoAsync(Image target)
    {
        try { target.Source = await IconService.ClaudeIconAsync() ?? AccountLogo("estrela-vetorial-animada.svg"); } catch { }
    }

    private static ImageSource? AccountLogo(string file)
    {
        string path = Path.Combine(AppContext.BaseDirectory, "Assets", "AgentLogos", file);
        if (!File.Exists(path)) return null;
        // O SVG do ChatGPT é branco e tem margem no viewBox (feito para a barra): aqui sairia menor que os outros
        // logotipos e sumiria no tema claro. Usa só a grade 24×24 do símbolo, na cor do texto do tema.
        if (file == "chatgpt-estatico.svg")
        {
            string color = ThemeBrushes.Get("TextFillColorPrimaryBrush") is SolidColorBrush text ? $"#{text.Color.R:X2}{text.Color.G:X2}{text.Color.B:X2}" : "white";
            return SvgFromText(File.ReadAllText(path).Replace("viewBox=\"-6 -6 36 36\"", "viewBox=\"0 0 24 24\"").Replace("fill=\"white\"", $"fill=\"{color}\""));
        }
        return IconService.LogoSvg(AccountLogoSize, new Uri(path));
    }

    /// <summary>Maior tamanho em que os logotipos das contas aparecem (cartões: 24; ordem das IAs: 20).</summary>
    private const double AccountLogoSize = 24;

    private static ImageSource SvgFromText(string svg)
    {
        var source = IconService.LogoSvg(AccountLogoSize);
        _ = Load();
        return source;
        async Task Load()
        {
            try
            {
                using var stream = new Windows.Storage.Streams.InMemoryRandomAccessStream();
                using (var writer = new Windows.Storage.Streams.DataWriter(stream))
                {
                    writer.WriteString(svg);
                    await writer.StoreAsync();
                    writer.DetachStream();
                }
                stream.Seek(0);
                await source.SetSourceAsync(stream);
            }
            catch (Exception ex) { App.Log("Configurações: logotipo SVG não carregou: " + ex.Message); }
        }
    }

    private AccountCard NewAccountCard(string service, string logoFile, string? manageUrl, string manageLabel)
    {
        // Mesma grade das linhas abaixo (24 px · 12 · texto): o nome do serviço fica na coluna dos rótulos.
        var header = new Grid { ColumnSpacing = 12 };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(24) });
        header.ColumnDefinitions.Add(new ColumnDefinition());
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var logo = new Image { Source = AccountLogo(logoFile), Width = 24, Height = 24, VerticalAlignment = VerticalAlignment.Center };
        header.Children.Add(logo);
        var title = new TextBlock { Text = service, FontSize = 15, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(title, 1); header.Children.Add(title);
        // Margem negativa: o texto de "Sair"/"Entrar" termina na mesma borda dos valores das linhas, sem perder a área de foco.
        var action = new HyperlinkButton { FontSize = 14, Padding = new Thickness(8, 4, 8, 4), Margin = new Thickness(0, 0, -8, 0), VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(action, 2); header.Children.Add(action);

        var picture = new PersonPicture { Width = 64, Height = 64, VerticalAlignment = VerticalAlignment.Top };
        var name = new TextBlock { FontSize = 20, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis };
        var email = new TextBlock { FontSize = 13, Foreground = ThemeBrushes.Get("TextFillColorSecondaryBrush"), TextTrimming = TextTrimming.CharacterEllipsis, IsTextSelectionEnabled = true };
        var who = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        who.Children.Add(name); who.Children.Add(email);
        if (manageUrl is not null)
            who.Children.Add(new HyperlinkButton { Content = manageLabel, NavigateUri = new Uri(manageUrl), Padding = new Thickness(0, 2, 0, 0), FontSize = 13 });
        var identity = new Grid { ColumnSpacing = 16, Margin = new Thickness(0, 14, 0, 6) };
        identity.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        identity.ColumnDefinitions.Add(new ColumnDefinition());
        identity.Children.Add(picture); Grid.SetColumn(who, 1); identity.Children.Add(who);

        var note = Help(""); note.Visibility = Visibility.Collapsed;
        var rows = new StackPanel();
        var body = new StackPanel();
        body.Children.Add(header); body.Children.Add(identity); body.Children.Add(note); body.Children.Add(rows);
        var root = new Border { Style = (Style)Root.Resources["SettingsGroup"], Padding = new Thickness(16, 14, 16, 8), Child = body };
        return new AccountCard { Logo = logo, Root = root, Picture = picture, Name = name, Email = email, Note = note, Action = action, Rows = rows };
    }

    /// <summary>Linha do cartão: ícone · rótulo (e ajuda) · valor à direita; ações opcionais embaixo, lado a lado.</summary>
    private Grid AccountRow(string glyph, string label, FrameworkElement value, TextBlock? help = null, params Button[] actions)
    {
        var row = new Grid { ColumnSpacing = 12, RowSpacing = 6, Padding = new Thickness(0, 10, 0, 10), BorderThickness = new Thickness(0, 1, 0, 0), BorderBrush = ThemeBrushes.Get("DividerStrokeColorDefaultBrush") };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(24) });
        row.ColumnDefinitions.Add(new ColumnDefinition());
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        row.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        row.Children.Add(new FontIcon { Glyph = glyph, FontSize = 16, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 2, 0, 0) });
        var text = new StackPanel { Spacing = 2 };
        text.Children.Add(new TextBlock { Text = label, FontSize = 14 });
        if (help is not null) text.Children.Add(help);
        Grid.SetColumn(text, 1); row.Children.Add(text);
        value.VerticalAlignment = VerticalAlignment.Top;
        Grid.SetColumn(value, 2); row.Children.Add(value);
        if (actions.Length > 0)
        {
            var bar = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            foreach (var a in actions) bar.Children.Add(a);
            Grid.SetRow(bar, 1); Grid.SetColumn(bar, 1); Grid.SetColumnSpan(bar, 2); row.Children.Add(bar);
        }
        return row;
    }

    /// <summary>Selo explícito de estado ("Configurada" em verde / "Não configurada" neutro).</summary>
    private static (Border Root, Action<bool, string> Set) Badge()
    {
        var text = new TextBlock { FontSize = 12 };
        var icon = new FontIcon { FontSize = 11 };
        var stack = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5 };
        stack.Children.Add(icon); stack.Children.Add(text);
        var root = new Border { CornerRadius = new CornerRadius(10), Padding = new Thickness(9, 3, 10, 3), Child = stack };
        void Set(bool ok, string label)
        {
            text.Text = label; icon.Glyph = ok ? "" : "";
            root.Background = ThemeBrushes.Get(ok ? "SystemFillColorSuccessBackgroundBrush" : "SystemFillColorNeutralBackgroundBrush");
            text.Foreground = icon.Foreground = ThemeBrushes.Get(ok ? "SystemFillColorSuccessBrush" : "TextFillColorSecondaryBrush");
        }
        Set(false, "");
        return (root, Set);
    }

    /// <summary>
    /// Linha de uma chave (conversa ou administrativa) no cartão da conta: selo de estado, caixa de senha com a chave
    /// salva em bolinhas (nunca exibida em texto), Salvar/Atualizar (confere a chave antes de gravar) e Remover.
    /// </summary>
    private (Grid Root, Action Refresh) KeyEditorRow(ProviderApiKeys.Provider p, bool admin, string envKey)
    {
        var (badge, setBadge) = Badge();
        var help = Help("");
        var box = new PasswordBox { Width = 320, PasswordRevealMode = PasswordRevealMode.Hidden, FontSize = 13,
            PlaceholderText = admin ? (p.Id == "anthropic" ? "Cole a chave administrativa (sk-ant-admin…)" : "Cole a chave administrativa (sk-admin-…)")
                                    : (p.Id == "anthropic" ? "Cole a chave da API (sk-ant-…)" : "Cole a chave da API (sk-…)") };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(box, admin ? "Chave administrativa" : "Chave de API");
        var save = new Button { FontSize = 12, Padding = new Thickness(10, 4, 10, 5), IsEnabled = false };
        var remove = new Button { Content = "Remover", FontSize = 12, Padding = new Thickness(10, 4, 10, 5) };
        var get = new HyperlinkButton { Content = admin ? "Criar chave administrativa" : "Criar chave", FontSize = 12, NavigateUri = new Uri(admin ? p.AdminKeysUrl : p.KeysUrl) };
        string label = admin ? "Chave administrativa (uso e gastos)" : "Chave de API (conversa)";
        var row = AccountRow("", label, badge, help, save, remove);
        var bar = row.Children.OfType<StackPanel>().First(b => Grid.GetRow(b) == 1);
        bar.Children.Insert(0, box); bar.Children.Add(get);
        box.VerticalAlignment = save.VerticalAlignment = remove.VerticalAlignment = get.VerticalAlignment = VerticalAlignment.Center;
        string? Stored() => admin ? ProviderApiKeys.AdminKey(p) : ProviderApiKeys.ChatKey(S, p);
        bool busy = false;
        void Refresh()
        {
            if (busy) return;
            string? stored = Stored();
            bool has = stored is not null;
            // A chave salva aparece só como bolinhas; digitar por cima troca a chave.
            box.Password = stored ?? "";
            save.Content = has ? "Atualizar" : "Salvar";
            save.IsEnabled = false; remove.IsEnabled = has;
            bool env = !admin && Environment.GetEnvironmentVariable(envKey) is { Length: > 0 };
            setBadge(has || env, has ? "Configurada" : env ? "Pela variável" : "Não configurada");
            help.Text = admin
                ? (has ? (p.Id == "anthropic" ? "Usada só para ler uso e gastos no menu Claude API das cotas." : "Usada só para ler uso e gastos no menu OpenAI API das cotas.")
                       : (p.Id == "anthropic" ? "Opcional: para ver tokens e gastos da Claude API no menu de cotas do Claude. É diferente da chave de conversa."
                                               : "Opcional: alternativa ao login da OpenAI para ver uso e gastos. É diferente da chave de conversa."))
                : (has ? $"Em uso no seletor da barra como \"{ProviderApiKeys.Entry(S, p)?.Name ?? p.Name}\" (modelo {ProviderApiKeys.Entry(S, p)?.DefaultModel})."
                       : env ? $"{envKey} definida neste PC (usada pelo CLI). Salve uma chave aqui para conversar pela barra."
                             : $"Salve uma chave para conversar com \"{p.Name}\" pela barra.");
        }
        box.PasswordChanged += (_, _) => save.IsEnabled = !busy && !string.IsNullOrWhiteSpace(box.Password) && box.Password.Trim() != Stored();
        save.Click += async (_, _) =>
        {
            string key = box.Password.Trim();
            if (key.Length == 0) return;
            busy = true; save.IsEnabled = remove.IsEnabled = box.IsEnabled = false;
            help.Text = "Conferindo a chave…";
            string? error;
            try
            {
                if (admin)
                {
                    error = await ProviderApiKeys.VerifyAdminKeyAsync(p, key, CancellationToken.None);
                    if (error is null) ProviderApiKeys.SaveAdminKey(p, key);
                }
                else
                {
                    var (catalog, err) = await ProviderApiKeys.VerifyChatKeyAsync(p, key, CancellationToken.None);
                    error = err;
                    if (catalog is not null) { ProviderApiKeys.SaveChatKey(S, p, key, catalog); _app.Settings.Save(); _app.ApplySettings(); }
                }
            }
            catch (Exception ex) { error = ex.Message; }
            busy = false; box.IsEnabled = true;
            if (p.Id == "anthropic") ClaudeApiUsageView.KeysChanged();
            Refresh();
            if (error is not null) { box.Password = key; save.IsEnabled = true; help.Text = "Chave não salva: " + error; }
            else help.Text = "Chave conferida e salva neste PC (Cofre do Windows). " + help.Text;
        };
        remove.Click += async (_, _) =>
        {
            if (!await ConfirmAsync(admin ? "Remover a chave administrativa?" : $"Remover a chave de \"{p.Name}\"?",
                admin ? "A barra deixa de ler uso e gastos por esta chave. A chave continua válida no site até você revogá-la lá."
                      : $"\"{p.Name}\" sai do seletor de IA da barra. A chave continua válida no site até você revogá-la lá.", "Remover")) return;
            try
            {
                if (admin) ProviderApiKeys.RemoveAdminKey(p);
                else { ProviderApiKeys.RemoveChatKey(S, p); _app.Settings.Save(); _app.ApplySettings(); }
                if (p.Id == "anthropic") ClaudeApiUsageView.KeysChanged();
                Refresh(); help.Text = "Chave removida deste PC. " + help.Text;
            }
            catch (Exception ex) { help.Text = "Não foi possível remover: " + ex.Message; }
        };
        return (row, Refresh);
    }

    private static Button SmallButton(string label, Action click)
    {
        var b = new Button { Content = label, FontSize = 12, Padding = new Thickness(10, 4, 10, 5) };
        b.Click += (_, _) => click(); return b;
    }

    private void BuildCliConnections(StackPanel accounts)
    {
        accounts.Children.Add(GroupTitle(Localization.T("Suas IAs")));
        accounts.Children.Add(Help("Conta, plano e chave de cada IA, lidos neste PC. \"Sair\" e \"Entrar\" usam os comandos oficiais de cada serviço; o estado é atualizado ao voltar a esta janela."));
        AddClaudeOrCodexCard(accounts, "claude", "Claude Code", "", "https://claude.ai/settings/account", "Conta Anthropic", "ANTHROPIC_API_KEY");
        AddClaudeOrCodexCard(accounts, "codex", "Codex", "chatgpt-estatico.svg", "https://chatgpt.com/#settings/Account", "Conta OpenAI", "OPENAI_API_KEY");
        AddGeminiCard(accounts);
        Activated += (_, e) => { if (e.WindowActivationState != WindowActivationState.Deactivated) foreach (var r in _accountRefreshers) r(); };
    }

    private void AddClaudeOrCodexCard(StackPanel accounts, string id, string service, string logo, string manageUrl, string manageLabel, string envKey)
    {
        var card = NewAccountCard(service, logo, manageUrl, manageLabel);
        // Logo do Claude: o ícone oficial do app instalado (o SVG animado aparecia como um ponto).
        if (id == "claude") _ = SetClaudeLogoAsync(card.Logo);
        var plan = new TextBlock { FontSize = 13, Foreground = ThemeBrushes.Get("TextFillColorSecondaryBrush") };
        card.Rows.Children.Add(AccountRow("\uE8C7", "Assinatura", plan));
        // 2.0.0: as duas chaves do servi\u00E7o no pr\u00F3prio cart\u00E3o, com caixa em bolinhas, Salvar/Atualizar e Remover.
        var provider = id == "codex" ? ProviderApiKeys.OpenAi : ProviderApiKeys.Anthropic;
        var chatRow = KeyEditorRow(provider, admin: false, envKey);
        var adminRow = KeyEditorRow(provider, admin: true, envKey);
        card.Rows.Children.Add(chatRow.Root); card.Rows.Children.Add(adminRow.Root);
        if (id == "codex")
        {
            // Saldo, gráficos e faturamento da API OpenAI (antes na página "API e faturamento").
            card.Rows.Children.Add(NestedExpander("", Localization.T("Uso e gastos da API OpenAI"), _openAiBillingPanel));
        }
        bool signedIn = false;
        void Refresh()
        {
            var state = AiAccountReader.Read(id);
            signedIn = state.HasSession;
            if (signedIn) card.Identity(state.Name, state.Email ?? "e-mail não informado pelo serviço", service);
            else card.Identity(null, null, "Não conectado");
            plan.Text = signedIn ? state.Plan ?? "Plano não informado" : "—";
            card.Action.Content = signedIn ? "Sair" : "Entrar";
            chatRow.Refresh(); adminRow.Refresh();
            card.SetNote(signedIn ? "" : "Nenhuma sessão encontrada neste PC. Clique em Entrar para fazer login pelo CLI oficial.");
        }
        card.Action.Click += async (_, _) =>
        {
            if (!signedIn) { OpenAccountTool(id, "login", card.Note); card.Note.Visibility = Visibility.Visible; return; }
            var dialog = new ContentDialog { XamlRoot = AiPage.XamlRoot, RequestedTheme = AiPage.ActualTheme,
                Title = "Sair de " + service + "?", Content = "O comando oficial remove a autenticação do CLI neste PC. Outros aplicativos que usam a mesma sessão podem pedir login de novo. Chaves de API e sessões dos sites não são alteradas.",
                PrimaryButtonText = "Sair", CloseButtonText = "Cancelar", DefaultButton = ContentDialogButton.Close };
            if (await dialog.ShowAsync() == ContentDialogResult.Primary) { OpenAccountTool(id, "logout", card.Note); card.Note.Visibility = Visibility.Visible; }
        };
        Refresh(); _accountRefreshers.Add(Refresh);
        accounts.Children.Add(card.Root);
    }

    /// <summary>
    /// Gemini com um login só: a conta Google da barra (gemini.google.com) serve para conversar e para ler as cotas do
    /// plano. A chave de API do AI Studio é o outro método. O login do Gemini CLI fica em "Avançado".
    /// </summary>
    private void AddGeminiCard(StackPanel accounts)
    {
        var provider = AdditionalAiAccounts.Gemini;
        var card = NewAccountCard("Gemini", "gemini.svg", "https://myaccount.google.com/", "Conta Google");

        // Conta Google: conversa + cotas do plano.
        var (googleBadge, setGoogle) = Badge();
        var googleHelp = Help("");
        // Um botão de login só: "Entrar"/"Sair" no alto do cartão (a linha tinha um segundo, "Entrar no Google…", igual).
        var googleRefresh = SmallButton("Atualizar cotas", () => GoogleUsageSession.Refresh());
        card.Rows.Children.Add(AccountRow("\uE77B", "Conta Google · conversa e cotas do plano", googleBadge, googleHelp, googleRefresh));

        // Chave de API.
        var (keyBadge, setKey) = Badge();
        var keyHelp = Help("");
        var configure = SmallButton("Configurar chave…", () => { });
        var remove = SmallButton("Remover chave", () => { });
        var keys = new Button { Content = "Obter chave grátis ↗", FontSize = 12, Padding = new Thickness(10, 4, 10, 5) };
        keys.Click += async (_, _) => await Windows.System.Launcher.LaunchUriAsync(new Uri(provider.KeysUrl!));
        card.Rows.Children.Add(AccountRow("\uE192", "Chave de API (Google AI Studio)", keyBadge, keyHelp, configure, remove, keys));

        // Método padrão nas conversas.
        var useLogin = new RadioButton { Content = "Conta Google", GroupName = "geminiAuth", MinWidth = 0 };
        var useKey = new RadioButton { Content = "Chave de API", GroupName = "geminiAuth", MinWidth = 0 };
        var method = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        method.Children.Add(useLogin); method.Children.Add(useKey);
        card.Rows.Children.Add(AccountRow("\uE8AB", "Usar nas conversas da barra", method, Help("Os dois aparecem no seletor do chat; esta é a opção padrão.")));

        // Avançado: login do Gemini CLI (contas Code Assist Standard/Enterprise) e projeto do Google Cloud.
        var cliState = Help("");
        var project = new TextBox { PlaceholderText = "id-do-projeto", Text = S.GeminiCloudProject, Width = 220, HorizontalAlignment = HorizontalAlignment.Left };
        project.LostFocus += (_, _) => { if (project.Text.Trim() != S.GeminiCloudProject) Commit(s => s.GeminiCloudProject = project.Text.Trim()); };
        var cliManage = SmallButton("Gerenciar login do CLI…", () => OpenAccountTool("gemini", "manage", cliState));
        var advanced = new StackPanel { Spacing = 8 };
        advanced.Children.Add(cliState);
        advanced.Children.Add(Help("O login do Gemini CLI só é aceito pelo Google com Gemini Code Assist Standard ou Enterprise. Nesse caso, informe o projeto do Google Cloud ligado à assinatura; a conversa pela conta Google acima continua sendo o padrão."));
        advanced.Children.Add(new TextBlock { Text = Localization.T("Projeto do Google Cloud"), FontSize = 14 });
        advanced.Children.Add(project);
        advanced.Children.Add(cliManage);
        card.Rows.Children.Add(NestedExpander("", "Avançado · login do Gemini CLI", advanced));

        bool refreshing = false;
        void Refresh()
        {
            refreshing = true;
            var g = GoogleUsageSession.Current;
            bool google = GeminiWebSession.SignedIn, key = ApiKeyStore.Get(provider.Resource) is not null;
            card.Identity(google ? g?.Name : null, google ? g?.Email ?? "conta Google conectada" : null, key ? "Conectado por chave de API" : "Não conectado");
            card.Action.Content = google ? "Sair" : "Entrar";
            setGoogle(google, google ? "Conectado" : "Não conectado");
            googleHelp.Text = google
                ? (g is { CurrentPercent: not null } ? $"Plano {g.Plan ?? "Gemini"} · uso atual {g.CurrentPercent:0} % (renova {g.CurrentReset ?? "—"}) · semanal {g.WeeklyPercent:0} % (renova {g.WeeklyReset ?? "—"}) · lido às {g.Updated.LocalDateTime:HH:mm}."
                    : "Conectado. As cotas do plano aparecem após a primeira leitura (a cada 5 min).") + " Conversas pela conta usam o seu plano do Gemini."
                : "Um login só: entre na conta Google numa janela da barra para conversar com o Gemini do seu plano e ver as cotas (Uso atual e Limite semanal).";
            googleRefresh.IsEnabled = google;
            setKey(key, key ? "Configurada" : "Não configurada");
            string? model = AdditionalAiAccounts.Models().GetValueOrDefault(provider.Id);
            keyHelp.Text = key ? "Guardada no Cofre do Windows deste PC" + (model is null ? "." : " · modelo " + model.Replace("models/", "") + ".") + " Nível gratuito do AI Studio; a chave não informa o e-mail."
                : "Opcional. Crie uma chave gratuita no Google AI Studio e cole aqui. Fica só neste PC, no Cofre do Windows.";
            remove.IsEnabled = key;
            useLogin.IsEnabled = true; useKey.IsEnabled = key;
            useLogin.IsChecked = S.GeminiAuth != "api"; useKey.IsChecked = S.GeminiAuth == "api";
            bool cli = WinStatusBar.Core.Ai.Gemini.GeminiCli.Find() is not null;
            cliState.Text = !cli ? "Gemini CLI não instalado neste PC." : WinStatusBar.Core.Ai.Gemini.GeminiCli.HasLogin() ? "Gemini CLI com login gravado." : "Gemini CLI sem login.";
            card.SetNote("");
            refreshing = false;
        }
        useLogin.Checked += (_, _) => { if (!refreshing) Commit(s => s.GeminiAuth = "login"); };
        useKey.Checked += (_, _) => { if (!refreshing) Commit(s => s.GeminiAuth = "api"); };
        configure.Click += async (_, _) =>
        {
            if (await ConfigureApiKeyAsync(provider, card.Note))
            {
                if (WinStatusBar.Core.Ai.Gemini.GeminiCli.Find() is not null) Commit(s => { s.GeminiAuth = "api"; s.CommandTarget = "gemini"; });
                else Commit(s => AdditionalAiAccounts.Activate(s, provider));
                Refresh(); card.SetNote("Chave verificada e salva. Gemini · chave de API selecionado para novas conversas.");
            }
        };
        remove.Click += async (_, _) =>
        {
            if (!await ConfirmAsync("Remover a chave do Gemini?", "A chave é apagada do Cofre do Windows deste PC. A conta Google não é alterada.", "Remover")) return;
            try { ApiKeyStore.Set(null, provider.Resource); if (S.GeminiAuth == "api") Commit(s => s.GeminiAuth = "login"); Refresh(); card.SetNote("Chave removida deste PC."); }
            catch { card.SetNote("Não foi possível remover a chave."); }
        };
        card.Action.Click += async (_, _) =>
        {
            if (!GeminiWebSession.SignedIn) { GoogleUsageSession.ShowSignIn(); card.SetNote("Entre na conta Google na janela aberta e feche-a ao terminar."); return; }
            if (!await ConfirmAsync("Sair da conta Google na barra?", "Apaga a sessão Google guardada pela barra neste PC (conversa e cotas). Sua conta Google, o navegador e a chave de API não são alterados.", "Sair")) return;
            GoogleUsageSession.SignOut(); await GeminiWebSession.SignOutAsync(); Refresh(); card.SetNote("Sessão Google da barra encerrada.");
        };
        Refresh(); _accountRefreshers.Add(Refresh);
        // Evento estático: sem cancelar a inscrição ao fechar, a janela ficava presa na memória e continuava se atualizando a
        // cada leitura de cota. Escondida (ver HideForReuse), não se atualiza: o estado é relido ao reabrir.
        Action changed = () => DispatcherQueue.TryEnqueue(() => { if (AppWindow.IsVisible) Refresh(); });
        GoogleUsageSession.Changed += changed;
        Closed += (_, _) => GoogleUsageSession.Changed -= changed;
        accounts.Children.Add(card.Root);
    }

    private async Task<bool> ConfirmAsync(string title, string content, string primary)
    {
        var dialog = new ContentDialog { XamlRoot = AiPage.XamlRoot, RequestedTheme = AiPage.ActualTheme, Title = title, Content = content,
            PrimaryButtonText = primary, CloseButtonText = "Cancelar", DefaultButton = ContentDialogButton.Close };
        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    private static void OpenAccountTool(string provider, string action, TextBlock feedback, [CallerFilePath] string source = "")
    {
        try
        {
            string name = provider + "-" + action + ".cmd";
            string? project = Directory.GetParent(Path.GetDirectoryName(source)!)?.Parent?.Parent?.FullName;
            string file = Path.Combine(project ?? "", "tools", "accounts", name);
            if (!File.Exists(file)) file = Path.Combine(AppContext.BaseDirectory, "AccountTools", name);
            if (!File.Exists(file)) { feedback.Text = "Script de conexão ausente. Repare a instalação da barra."; return; }
            Process.Start(new ProcessStartInfo("explorer.exe", "\"" + file + "\"") { UseShellExecute = true })?.Dispose();
            feedback.Text = "Janela do CLI aberta. Conclua lá; o cartão se atualiza quando você voltar a esta janela.";
        }
        catch { feedback.Text = "Não foi possível abrir o script de autenticação."; }
    }
}
