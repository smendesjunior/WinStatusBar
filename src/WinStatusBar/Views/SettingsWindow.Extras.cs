using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WinStatusBar.Clip;
using WinStatusBar.Core.Settings;
using Localization = WinStatusBar.Services.Localization;

namespace WinStatusBar.Views;

/// <summary>
/// Configurações montadas em código, no mesmo padrão visual das páginas do XAML (cartões com linhas "título + ajuda | controle"):
/// • Área de transferência: uso, privacidade, o que guardar e limites do histórico.
/// • Barra e aparência › Botões de atalho e Efeitos.
/// • Hardware › Painéis de detalhes.
/// • Chat da barra (a página inteira) e o editor da API própria, mostrado em "IAs e contas".
/// Cada alteração é salva e aplicada na hora.
/// </summary>
public sealed partial class SettingsWindow
{
    private TextBlock? _clipStats;
    private readonly List<(Control Control, Func<AppSettings, object> Read)> _extras = new();
    private bool _extrasLoading;

    private void BuildExtraSettings()
    {
        _extrasLoading = true;
        BuildClipboardPage();
        BuildSensorSetup();

        // Barra e aparência: os botões de atalho ficam logo abaixo dos itens da barra (antes eram uma página à parte).
        ShortcutsHost.Children.Add(GroupTitle(global::WinStatusBar.Services.Localization.T("Botões de atalho")));
        ShortcutsHost.Children.Add(Help(Localization.T("Arraste a alça para ordenar os botões e use os interruptores para escolher quais aparecem à direita da barra.")));
        ShortcutsHost.Children.Add(BuildShortcutSettings());

        AppearancePage.Children.Add(GroupTitle(global::WinStatusBar.Services.Localization.T("Efeitos")));
        AppearancePage.Children.Add(Group(
            Toggle(global::WinStatusBar.Services.Localization.T("Brilho no agente em atividade"), global::WinStatusBar.Services.Localization.T("Um brilho arco-íris circula pelo contorno do módulo do agente e da barra de pergunta enquanto eles trabalham: linha fina na borda e halo suave por fora. Respeita a opção de animações do Windows."),
                s => s.AgentGlow, (s, v) => s.AgentGlow = v),
            Toggle(global::WinStatusBar.Services.Localization.T("Linha de separação no fundo adaptativo"), global::WinStatusBar.Services.Localization.T("Linha de 1 pixel, bem sutil, entre a barra e a janela maximizada. Some na área de trabalho e com janelas menores."),
                s => s.AdaptiveDivider, (s, v) => s.AdaptiveDivider = v)));

        // Hardware: os painéis de detalhes abrem a página (são o que mais se usa); drivers e frequências ficam em "Avançado".
        DetailPanelsHost.Children.Add(GroupTitle(global::WinStatusBar.Services.Localization.T("Painéis de detalhes")));
        DetailPanelsHost.Children.Add(Group(
            Toggle(global::WinStatusBar.Services.Localization.T("Abrir painel ao clicar"), global::WinStatusBar.Services.Localization.T("Clique em CPU, RAM, GPU ou rede para ver gráficos e detalhes. Desligado, a barra mostra só a dica ao passar o mouse."),
                s => s.DetailPanels, (s, v) => s.DetailPanels = v),
            Toggle(global::WinStatusBar.Services.Localization.T("Aplicativos no painel da CPU"), "Lista os aplicativos abertos, agrupados como no Gerenciador de Tarefas.", s => s.CpuPanelApps, (s, v) => s.CpuPanelApps = v),
            Toggle(global::WinStatusBar.Services.Localization.T("Permitir finalizar tarefas"), global::WinStatusBar.Services.Localization.T("Mostra o botão \"Finalizar tarefa\". O app recebe primeiro o pedido de fechar; o encerramento forçado só é oferecido se ele não responder."),
                s => s.AllowEndTask, (s, v) => s.AllowEndTask = v)));

        SystemExtras.Children.Add(GroupTitle(Localization.T("Cópia das preferências")));
        SystemExtras.Children.Add(Group(
            Toggle(global::WinStatusBar.Services.Localization.T("Guardar preferências no OneDrive"), global::WinStatusBar.Services.Localization.T("Copia as configurações para o seu OneDrive pessoal (WinStatusBar\\configuracoes.json). Numa instalação limpa com a mesma conta, tudo volta sozinho. A chave da API não vai junto."),
                s => s.SyncSettings, (s, v) => s.SyncSettings = v)));
        BuildOrganizedPages();
        BuildCommandSettings();
        BuildCustomApiEditor();
        // Cotas e tarefas › Tarefa na barra: progresso estimado e resumo, no mesmo cartão do título e da tarefa em destaque.
        foreach (var row in new[]
        {
            Toggle("Estimar progresso quando não houver plano", "Mostra ~% no círculo usando etapas observadas, como leitura, edição e testes. É uma aproximação de baixa confiança; não avança pelo tempo. Um plano explícito sempre tem prioridade.", s => s.EstimateProgressWithoutPlan, (s, v) => s.EstimateProgressWithoutPlan = v),
            Toggle("Resumo automático do andamento", "Gera uma síntese da tarefa selecionada em chamadas separadas à IA. Usa a autenticação e as cotas existentes; não altera a conversa original.",
                s => s.TaskBriefingEnabled, (s,v) => s.TaskBriefingEnabled = v),
            Choice("Intervalo mínimo entre resumos", "Só gera outro resumo se houver mudanças. Intervalos maiores reduzem chamadas à IA.",
                [("15 segundos",15), ("30 segundos",30), ("1 minuto",60), ("2 minutos",120), ("5 minutos",300)],
                s => s.TaskBriefingIntervalSeconds, (s,v) => s.TaskBriefingIntervalSeconds = v),
        })
        {
            TaskFollowHost.Children.Add(Divider());
            TaskFollowHost.Children.Add(row);
        }
        FinishOrganizedPages();
        BuildAdditionalAiAccounts();
        _extrasLoading = false;
    }

    /// <summary>Pasta do histórico: padrão no OneDrive pessoal (sincroniza e sobrevive à formatação), ou outra escolhida.</summary>
    private Grid HistoryFolderRow()
    {
        string Current() => string.IsNullOrWhiteSpace(S.CommandHistoryFolder) ? WinStatusBar.Services.ConversationStore.DefaultFolder : S.CommandHistoryFolder;
        var path = new TextBlock { Text = Current(), TextWrapping = TextWrapping.Wrap, FontSize = 12, Foreground = WinStatusBar.Services.ThemeBrushes.Get("TextFillColorSecondaryBrush") };
        var open = new Button { Content = global::WinStatusBar.Services.Localization.T("Abrir") };
        var change = new Button { Content = global::WinStatusBar.Services.Localization.T("Alterar…") };
        var reset = new Button { Content = global::WinStatusBar.Services.Localization.T("Padrão") };
        open.Click += (_, _) =>
        {
            try { Directory.CreateDirectory(Current()); System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"\"{Current()}\"") { UseShellExecute = true })?.Dispose(); }
            catch (Exception ex) { App.Log("Pasta do histórico: " + ex.Message); }
        };
        change.Click += async (_, _) =>
        {
            var picker = new Windows.Storage.Pickers.FolderPicker();
            WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
            picker.FileTypeFilter.Add("*");
            var folder = await picker.PickSingleFolderAsync();
            if (folder is null) return;
            Commit(s => s.CommandHistoryFolder = folder.Path);
            path.Text = Current();
        };
        reset.Click += (_, _) => { Commit(s => s.CommandHistoryFolder = string.Empty); path.Text = Current(); };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        buttons.Children.Add(open); buttons.Children.Add(change); buttons.Children.Add(reset);
        var panel = new StackPanel { Spacing = 6 };
        panel.Children.Add(path); panel.Children.Add(buttons);
        return Row(global::WinStatusBar.Services.Localization.T("Pasta do histórico"), global::WinStatusBar.Services.Localization.T("Por padrão no seu OneDrive pessoal: sincroniza entre PCs e volta depois de formatar. Nada é enviado para outro lugar."), panel);
    }

    /// <summary>Atalho global da barra de comando: "Gravar" captura a próxima combinação pressionada (Esc cancela).</summary>
    private Grid HotkeyRow()
    {
        string Label(string combo) => string.IsNullOrWhiteSpace(combo) ? global::WinStatusBar.Services.Localization.T("Desativado") : WinStatusBar.Services.GlobalHotkey.Format(combo).Replace("Space", global::WinStatusBar.Services.Localization.T("Espaço"));
        var current = new TextBlock { Text = Label(S.CommandHotkey), VerticalAlignment = VerticalAlignment.Center, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold };
        var record = new Button { Content = global::WinStatusBar.Services.Localization.T("Gravar") };
        var off = new Button { Content = global::WinStatusBar.Services.Localization.T("Desativar") };
        record.Click += (_, _) =>
        {
            var hotkey = _app.CommandHotkey;
            if (hotkey is null) return;
            current.Text = global::WinStatusBar.Services.Localization.T("Pressione o atalho… (Esc cancela)");
            record.IsEnabled = false;
            hotkey.Record(combo => DispatcherQueue.TryEnqueue(() =>
            {
                record.IsEnabled = true;
                if (combo is not null) Commit(s => s.CommandHotkey = combo);
                current.Text = Label(S.CommandHotkey);
            }));
        };
        off.Click += (_, _) => { _app.CommandHotkey?.CancelRecording(); record.IsEnabled = true; Commit(s => s.CommandHotkey = string.Empty); current.Text = Label(S.CommandHotkey); };
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
        panel.Children.Add(current); panel.Children.Add(record); panel.Children.Add(off);
        return Row(global::WinStatusBar.Services.Localization.T("Atalho para abrir"), global::WinStatusBar.Services.Localization.T("Abre a barra de comando com o cursor pronto para digitar, de qualquer lugar. Padrão: Alt+Espaço (no lugar do menu da janela do Windows)."), panel);
    }

    /// <summary>
    /// Chat da barra: o chat e o atalho, como cada conversa nova começa, as permissões (todas num grupo só: edição de
    /// arquivos, acesso ao PC e conectores), o histórico e a voz dos aplicativos oficiais.
    /// </summary>
    private void BuildCommandSettings()
    {
        var header = new StackPanel { Spacing = 4 };
        header.Children.Add(new TextBlock { Text = Localization.T("Chat da barra"), Style = (Style)Root.Resources["PageTitle"] });
        header.Children.Add(Help(global::WinStatusBar.Services.Localization.T("A caixa no centro da barra: pergunte ao Claude, Codex, Gemini, Copilot, Lenovo Qira, IA local ou à sua API, com histórico sincronizado no OneDrive.")));
        CommandPage.Children.Add(header);
        var previewChat = new Button { Content = Localization.T("Abrir o chat agora"), HorizontalAlignment = HorizontalAlignment.Left };
        previewChat.Click += (_, _) => (_app.Bars.FirstOrDefault(b => b.ContainsCursor()) ?? _app.Bars.FirstOrDefault())?.FocusCommandBar();
        CommandPage.Children.Add(previewChat);

        CommandPage.Children.Add(GroupTitle(Localization.T("Chat")));
        CommandPage.Children.Add(Group(
            Toggle(Localization.T("Mostrar o chat na barra"), global::WinStatusBar.Services.Localization.T("Caixa no centro da barra. Ao clicar, escolha Claude Code, Codex, IA local ou a sua API, além do modelo e do esforço. Enter envia e a resposta aparece no balão."),
                s => s.CommandBar, (s, v) => s.CommandBar = v),
            HotkeyRow()));

        CommandPage.Children.Add(GroupTitle(Localization.T("Cada conversa nova começa com")));
        CommandPage.Children.Add(Group(
            Toggle(global::WinStatusBar.Services.Localization.T("Pesquisa na web ligada por padrão"), global::WinStatusBar.Services.Localization.T("Cada conversa nova começa com o 🌐 ativo (Claude Code e Codex). Dá para desligar na própria conversa."),
                s => s.CommandWebDefault, (s, v) => s.CommandWebDefault = v),
            Toggle("Usar o contexto da tela por padrão", Localization.T("Ao abrir o chat, leva junto a janela em uso: o texto selecionado, o documento ou a planilha abertos (mesmo sem salvar) e, no navegador, o endereço e o texto da página. Tudo aparece em etiquetas removíveis; o botão Contexto do chat liga ou desliga em cada conversa."),
                s => s.CommandDetectActiveFile, (s, v) => s.CommandDetectActiveFile = v),
            Toggle("Respostas organizadas", "Pede à IA, na primeira mensagem de cada conversa, título quando a resposta é longa, passos numerados, termos-chave em negrito e uma nota final quando houver ressalva. Desligado, a IA responde no formato dela.",
                s => s.CommandFormatHint, (s, v) => s.CommandFormatHint = v)));

        CommandPage.Children.Add(GroupTitle(Localization.T("Permissões")));
        CommandPage.Children.Add(Group(
            Toggle("Permitir edição de arquivos por padrão", "Novas conversas começam com edição habilitada para Claude Code e Codex. Você pode mudar no botão de edição do chat.",
                s => s.CommandAllowEditsDefault, (s, v) => s.CommandAllowEditsDefault = v),
            Toggle(Localization.T("Permitir acesso amplo ao PC e à rede"), Localization.T("Deixa o chat agir no PC a seu pedido: aplicativos, janelas, arquivos e pastas (locais e de rede), comandos, scripts e dispositivos acessíveis. Vale para todas as IAs, com o lápis de edição ligado."),
                s => s.ContextActionsEnabled, (s, v) => s.ContextActionsEnabled = v),
            Help(Localization.T("O conteúdo usado na tarefa pode ser enviado à IA escolhida. As permissões da sua conta do Windows e da rede continuam valendo, e o acesso de administrador pede sua autorização (UAC). Desligue para impedir novas ações; use Parar para interromper a tarefa atual.")),
            ConnectorsAccessRow()));
        CommandPage.Children.Add(AllowedConnectorsGroup());

        CommandPage.Children.Add(GroupTitle(global::WinStatusBar.Services.Localization.T("Histórico")));
        CommandPage.Children.Add(Group(
            Toggle(global::WinStatusBar.Services.Localization.T("Guardar histórico de conversas"), global::WinStatusBar.Services.Localization.T("Todas as IAs num histórico só, aberto pelo botão de relógio do painel. Cada conversa pode ser reaberta e continuada."),
                s => s.CommandSaveHistory, (s, v) => s.CommandSaveHistory = v),
            HistoryFolderRow(),
            Toggle(global::WinStatusBar.Services.Localization.T("Registrar no histórico do app Claude"), global::WinStatusBar.Services.Localization.T("Ao terminar, a conversa do Claude Code é importada pelo app Claude e aparece no histórico dele."),
                s => s.CommandClaudeHistory, (s, v) => s.CommandClaudeHistory = v)));

        CommandPage.Children.Add(GroupTitle(Localization.T("Voz nos aplicativos oficiais")));
        CommandPage.Children.Add(Help("O botão de ondas abre ChatGPT ou Claude. Para iniciar voz pelo atalho, informe abaixo a mesma combinação configurada no aplicativo oficial (Ctrl, Alt, Shift e tecla). Sem atalho, o aplicativo é aberto e você inicia a voz nele. Não é o ditado do Windows."));
        var chatGptVoice = new TextBox { Text = S.CommandChatGptVoiceHotkey, PlaceholderText = Localization.T("ex.: Ctrl+Shift+F9") };
        chatGptVoice.TextChanged += (_, _) => Commit(s => s.CommandChatGptVoiceHotkey = chatGptVoice.Text.Trim());
        var claudeVoice = new TextBox { Text = S.CommandClaudeVoiceHotkey, PlaceholderText = Localization.T("ex.: Ctrl+Shift+F9") };
        claudeVoice.TextChanged += (_, _) => Commit(s => s.CommandClaudeVoiceHotkey = claudeVoice.Text.Trim());
        CommandPage.Children.Add(Group(
            Row(Localization.T("Atalho de voz do ChatGPT"), null, chatGptVoice),
            Row(Localization.T("Atalho de voz do Claude"), Localization.T("Se o aplicativo do Claude oferecer um atalho de voz."), claudeVoice)));
    }

    /// <summary>
    /// APIs próprias (várias): seletor + Adicionar/Remover; os campos editam a API escolhida, e a chave vai para o Cofre de
    /// Credenciais do Windows. O cartão é mostrado em "IAs e contas".
    /// </summary>
    private void BuildCustomApiEditor()
    {
        WinStatusBar.Services.AdditionalAiAccounts.MigrateCustom(S);
        var profiles = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch, PlaceholderText = "Nenhuma API cadastrada" };
        var addApi = WinStatusBar.Views.Panels.PanelKit.Small(new Button { Content = "Adicionar" });
        var removeApi = WinStatusBar.Views.Panels.PanelKit.Small(new Button { Content = "Remover" });
        bool loadingProfiles = false;
        var kind = new ComboBox { Style = (Style)Root.Resources["SettingChoice"] };
        kind.Items.Add(new ComboBoxItem { Content = global::WinStatusBar.Services.Localization.T("Compatível com OpenAI"), Tag = "openai" });
        kind.Items.Add(new ComboBoxItem { Content = "Anthropic", Tag = "anthropic" });
        kind.SelectedIndex = S.ApiKind == "anthropic" ? 1 : 0;
        kind.SelectionChanged += (_, _) => { if (!loadingProfiles && kind.SelectedItem is ComboBoxItem { Tag: string k }) Commit(s => { s.ApiKind = k; WinStatusBar.Services.AdditionalAiAccounts.SaveCustom(s); }); };

        var key = new PasswordBox { HorizontalAlignment = HorizontalAlignment.Stretch };
        var keyStatus = Help("");
        void RefreshKeyPlaceholder() => key.PlaceholderText = WinStatusBar.Services.AdditionalAiAccounts.ActiveKey(S) is not null ? global::WinStatusBar.Services.Localization.T("Chave salva (digite para trocar)") : "sk-…";
        void RefreshApiState() => keyStatus.Text = "Endpoint: " + (string.IsNullOrWhiteSpace(S.ApiBaseUrl) ? "não configurado" : S.ApiBaseUrl)
            + " · Chave: " + (WinStatusBar.Services.AdditionalAiAccounts.ActiveKey(S) is not null ? "salva (validade não verificada)" : "ausente")
            + " · Modelo: " + (string.IsNullOrWhiteSpace(S.ApiDefaultModel) ? "automático (primeiro do catálogo)" : S.ApiDefaultModel);
        void LoadProfiles()
        {
            loadingProfiles = true;
            profiles.Items.Clear();
            foreach (var api in S.CustomApis)
            {
                var item = new ComboBoxItem { Content = string.IsNullOrWhiteSpace(api.Name) ? "API sem nome" : api.Name, Tag = api };
                profiles.Items.Add(item);
                if (WinStatusBar.Services.AdditionalAiAccounts.CustomActive(S) && api.Resource == S.ApiCredentialResource) profiles.SelectedItem = item;
            }
            removeApi.IsEnabled = profiles.SelectedItem is not null;
            kind.SelectedIndex = S.ApiKind == "anthropic" ? 1 : 0;
            loadingProfiles = false;
            RefreshKeyPlaceholder(); RefreshApiState();
        }
        profiles.SelectionChanged += (_, _) =>
        {
            if (loadingProfiles || profiles.SelectedItem is not ComboBoxItem { Tag: WinStatusBar.Core.Settings.CustomApi api }) return;
            Commit(s => WinStatusBar.Services.AdditionalAiAccounts.ActivateCustom(s, api));
            LoadExtraValues(); LoadProfiles();
        };
        addApi.Click += (_, _) =>
        {
            Commit(s =>
            {
                var api = new WinStatusBar.Core.Settings.CustomApi { Name = "Nova API", Resource = "WinStatusBar.CommandApi." + Guid.NewGuid().ToString("N")[..8] };
                WinStatusBar.Services.AdditionalAiAccounts.SaveCustom(s);
                s.CustomApis.Add(api);
                WinStatusBar.Services.AdditionalAiAccounts.ActivateCustom(s, api);
            });
            LoadExtraValues(); LoadProfiles();
        };
        removeApi.Click += async (_, _) =>
        {
            if (profiles.SelectedItem is not ComboBoxItem { Tag: WinStatusBar.Core.Settings.CustomApi api }) return;
            var confirm = new ContentDialog { XamlRoot = Root.XamlRoot, RequestedTheme = Root.ActualTheme, Title = "Remover API",
                Content = $"Remover \"{(string.IsNullOrWhiteSpace(api.Name) ? "API sem nome" : api.Name)}\" e apagar a chave dela do Cofre do Windows?",
                PrimaryButtonText = "Remover", CloseButtonText = "Cancelar", DefaultButton = ContentDialogButton.Close };
            if (await confirm.ShowAsync() != ContentDialogResult.Primary) return;
            try { WinStatusBar.Services.ApiKeyStore.Set(null, api.Resource); } catch (Exception ex) { App.Log("API própria: " + ex.Message); }
            Commit(s =>
            {
                s.CustomApis.RemoveAll(a => a.Resource == api.Resource);
                if (s.CustomApis.FirstOrDefault() is { } next) WinStatusBar.Services.AdditionalAiAccounts.ActivateCustom(s, next);
                else { s.ApiName = s.ApiBaseUrl = s.ApiDefaultModel = string.Empty; s.ApiKind = "openai"; s.ApiCredentialResource = "WinStatusBar.CommandApi"; s.ApiCredentialEndpoint = string.Empty; }
            });
            LoadExtraValues(); LoadProfiles();
        };
        var profilePanel = new StackPanel { Spacing = 8 };
        profilePanel.Children.Add(profiles);
        var profileButtons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
        profileButtons.Children.Add(addApi); profileButtons.Children.Add(removeApi);
        profilePanel.Children.Add(profileButtons);

        var save = WinStatusBar.Views.Panels.PanelKit.Small(new Button { Content = "Salvar chave" });
        var remove = WinStatusBar.Views.Panels.PanelKit.Small(new Button { Content = Localization.T("Remover chave") });
        var verify = WinStatusBar.Views.Panels.PanelKit.Small(new Button { Content = "Verificar e listar modelos" });
        var modelChoices = new ComboBox { PlaceholderText = "Modelos retornados pela API", DisplayMemberPath = "Label", MinWidth = 240, Visibility = Visibility.Collapsed };
        save.Click += (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(key.Password)) return;
            try { WinStatusBar.Services.ApiKeyStore.Set(key.Password, S.ApiCredentialResource); S.ApiCredentialEndpoint = S.ApiBaseUrl; key.Password = string.Empty; Commit(s => WinStatusBar.Services.AdditionalAiAccounts.SaveCustom(s)); RefreshKeyPlaceholder(); RefreshApiState(); }
            catch (Exception ex) { keyStatus.Text = global::WinStatusBar.Services.Localization.T("Não foi possível salvar a chave."); App.Log("API própria: " + ex.Message); }
        };
        remove.Click += (_, _) =>
        {
            try { WinStatusBar.Services.ApiKeyStore.Set(null, S.ApiCredentialResource); Commit(_ => { }); RefreshKeyPlaceholder(); RefreshApiState(); }
            catch (Exception ex) { App.Log("API própria: " + ex.Message); }
        };
        verify.Click += async (_, _) =>
        {
            RefreshApiState();
            if (!Uri.TryCreate(S.ApiBaseUrl, UriKind.Absolute, out var endpoint) || endpoint.Scheme is not ("http" or "https")) { keyStatus.Text += "\nInforme um endereço base HTTP/HTTPS válido."; return; }
            verify.IsEnabled = false; modelChoices.ItemsSource = null;
            try
            {
                var catalog = await WinStatusBar.Core.Ai.AgentPrompt.CatalogAsync(WinStatusBar.Core.Ai.AgentPrompt.Target.Api,
                    new(S.ApiKind, S.ApiBaseUrl, WinStatusBar.Services.AdditionalAiAccounts.ActiveKey(S)), CancellationToken.None);
                modelChoices.ItemsSource = catalog.Models;
                modelChoices.Visibility = catalog.Models.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
                modelChoices.SelectedItem = catalog.Models.FirstOrDefault(m => m.Id == S.ApiDefaultModel);
                keyStatus.Text += "\n" + ((catalog.Error is { } apiError ? WinStatusBar.Core.Ai.AiErrorExplainer.Explain(WinStatusBar.Core.Ai.AgentPrompt.Target.Api, apiError).Message : null) ?? (catalog.Models.Count > 0 ? "Conexão aceita: escolha o modelo padrão na lista. Isso não confirma créditos ou permissão de geração." : "Nenhum modelo retornado pelo serviço."));
            }
            catch (Exception ex) { keyStatus.Text += "\n" + WinStatusBar.Core.Ai.AiErrorExplainer.Explain(WinStatusBar.Core.Ai.AgentPrompt.Target.Api, ex.Message).Message; }
            finally { verify.IsEnabled = true; }
        };
        modelChoices.SelectionChanged += (_, _) => { if (modelChoices.SelectedItem is WinStatusBar.Core.Ai.AgentPrompt.ModelOption m) { Commit(s => { s.ApiDefaultModel = m.Id; WinStatusBar.Services.AdditionalAiAccounts.SaveCustom(s); }); LoadExtraValues(); RefreshApiState(); } };
        // Chave: a caixa na coluna da direita; ações, modelos e estado ocupam a largura toda logo abaixo (antes ficava tudo espremido à direita).
        var keyActions = new StackPanel { Spacing = 8, Margin = new Thickness(16, 0, 16, 14) };
        var keyButtons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        keyButtons.Children.Add(save); keyButtons.Children.Add(remove); keyButtons.Children.Add(verify);
        keyActions.Children.Add(keyButtons); keyActions.Children.Add(modelChoices); keyActions.Children.Add(keyStatus);
        var keyBlock = new StackPanel();
        keyBlock.Children.Add(Row(global::WinStatusBar.Services.Localization.T("Chave da API"), global::WinStatusBar.Services.Localization.T("Fica no Cofre de Credenciais do Windows, nunca nas configurações nem nos logs."), key));
        keyBlock.Children.Add(keyActions);
        LoadProfiles();

        _apiSettingsPage.Children.Add(Group(
            Row("API", "Cada API cadastrada aparece no seletor do chat com o próprio nome e guarda a própria chave. Os campos abaixo editam a API escolhida.", profilePanel),
            Text(global::WinStatusBar.Services.Localization.T("Nome"), global::WinStatusBar.Services.Localization.T("Como a API aparece no balão de seleção."), "OpenRouter, Groq, Azure…", s => s.ApiName, (s, v) => { s.ApiName = v; WinStatusBar.Services.AdditionalAiAccounts.SaveCustom(s); }),
            Row(Localization.T("Tipo de API"), global::WinStatusBar.Services.Localization.T("OpenAI cobre a maioria dos serviços (OpenAI, OpenRouter, Groq, Mistral, LM Studio…)."), kind),
            Text(Localization.T("Endereço do serviço (URL)"), global::WinStatusBar.Services.Localization.T("Ex.: https://api.openai.com/v1 · https://openrouter.ai/api/v1 · https://api.anthropic.com"), "https://…/v1", s => s.ApiBaseUrl, (s, v) => { s.ApiBaseUrl = v; WinStatusBar.Services.AdditionalAiAccounts.SaveCustom(s); }),
            keyBlock,
            Text(global::WinStatusBar.Services.Localization.T("Modelo padrão"), global::WinStatusBar.Services.Localization.T("Opcional. Em branco, o balão usa o primeiro modelo da lista do serviço."), "gpt-…", s => s.ApiDefaultModel, (s, v) => { s.ApiDefaultModel = v; WinStatusBar.Services.AdditionalAiAccounts.SaveCustom(s); })));

        // Voltar à API própria anterior: um link discreto no fim do cartão (antes era um botão solto num cartão sem título).
        var restore = new HyperlinkButton { Content = Localization.T("Voltar à API própria anterior"), Padding = new Thickness(0) };
        var restored = Help("");
        restored.Visibility = Visibility.Collapsed;
        restore.Click += (_, _) =>
        {
            bool ok = false;
            Commit(s => ok = WinStatusBar.Services.AdditionalAiAccounts.RestoreCustom(s));
            restored.Text = ok ? "API própria anterior selecionada. Sua chave original foi preservada." : "Nenhuma configuração anterior guardada neste PC.";
            restored.Visibility = Visibility.Visible;
            if (ok) { LoadExtraValues(); LoadProfiles(); }
        };
        var restorePanel = new StackPanel { Spacing = 4, Margin = new Thickness(16, 0, 16, 0) };
        restorePanel.Children.Add(restore); restorePanel.Children.Add(restored);
        _apiSettingsPage.Children.Add(restorePanel);
    }

    private void BuildClipboardPage()
    {
        var header = new StackPanel { Spacing = 4 };
        header.Children.Add(new TextBlock { Text = global::WinStatusBar.Services.Localization.T("Área de transferência"), Style = (Style)Root.Resources["PageTitle"] });
        header.Children.Add(Help(global::WinStatusBar.Services.Localization.T("Histórico ilimitado e persistente, emojis e atalhos do Windows, integrados à barra. Itens fixados nunca são apagados pelos limites.")));
        ClipPage.Children.Add(header);

        ClipPage.Children.Add(GroupTitle(global::WinStatusBar.Services.Localization.T("Uso")));
        ClipPage.Children.Add(Group(
            Toggle(global::WinStatusBar.Services.Localization.T("Histórico e emojis"), global::WinStatusBar.Services.Localization.T("Desligado, nada é capturado e Win+V / Win+. voltam a abrir os painéis do Windows. O histórico salvo é mantido."),
                s => s.ClipboardEnabled, (s, v) => s.ClipboardEnabled = v),
            Toggle(global::WinStatusBar.Services.Localization.T("Colar ao escolher um item"), global::WinStatusBar.Services.Localization.T("Cola direto no aplicativo que estava em uso. Desligado, o item só vai para a área de transferência (cole com Ctrl+V)."),
                s => s.ClipboardAutoPaste, (s, v) => s.ClipboardAutoPaste = v),
            Toggle(global::WinStatusBar.Services.Localization.T("Win+V abre este histórico"), global::WinStatusBar.Services.Localization.T("No lugar do histórico nativo do Windows. O painel abre junto ao cursor de texto ou ao mouse."),
                s => s.ClipboardWinV, (s, v) => s.ClipboardWinV = v),
            Toggle("Win+. e Win+; abrem os emojis", global::WinStatusBar.Services.Localization.T("No lugar do painel de emojis do Windows, com nomes em português e tons de pele."),
                s => s.ClipboardEmojiKeys, (s, v) => s.ClipboardEmojiKeys = v)));

        ClipPage.Children.Add(GroupTitle(Localization.T("Privacidade")));
        ClipPage.Children.Add(Group(
            Toggle(global::WinStatusBar.Services.Localization.T("Pausar captura"), global::WinStatusBar.Services.Localization.T("Enquanto pausado, nada do que você copiar entra no histórico."), s => s.ClipboardPaused, (s, v) => s.ClipboardPaused = v),
            Text(global::WinStatusBar.Services.Localization.T("Aplicativos ignorados"), global::WinStatusBar.Services.Localization.T("Nomes dos processos, separados por vírgula. Nada copiado neles entra no histórico. Conteúdo marcado como sensível pelo próprio app já é ignorado sempre."),
                "ex.: KeePass, Bitwarden", s => s.ClipboardIgnoredApps, (s, v) => s.ClipboardIgnoredApps = v)));

        ClipPage.Children.Add(GroupTitle(global::WinStatusBar.Services.Localization.T("O que guardar")));
        ClipPage.Children.Add(Group(
            Toggle(global::WinStatusBar.Services.Localization.T("Imagens"), global::WinStatusBar.Services.Localization.T("Capturas de tela e imagens copiadas."), s => s.ClipboardCaptureImages, (s, v) => s.ClipboardCaptureImages = v),
            Choice(global::WinStatusBar.Services.Localization.T("Tamanho máximo de uma imagem"), global::WinStatusBar.Services.Localization.T("Imagens maiores não entram no histórico."),
                [(global::WinStatusBar.Services.Localization.T("Sem limite"), 0), ("5 MB", 5), ("10 MB", 10), ("20 MB", 20), ("50 MB", 50)], s => s.ClipboardMaxImageMb, (s, v) => s.ClipboardMaxImageMb = v),
            Toggle(global::WinStatusBar.Services.Localization.T("Arquivos e pastas"), global::WinStatusBar.Services.Localization.T("Itens copiados no Explorador de Arquivos (o histórico guarda o caminho)."), s => s.ClipboardCaptureFiles, (s, v) => s.ClipboardCaptureFiles = v),
            Choice(global::WinStatusBar.Services.Localization.T("Cópia de segurança dos arquivos"), global::WinStatusBar.Services.Localization.T("Guarda uma cópia para você colar mesmo se o original for movido ou apagado."),
                [(global::WinStatusBar.Services.Localization.T("Desligada"), 0), (global::WinStatusBar.Services.Localization.T("Até 10 MB"), 10), (global::WinStatusBar.Services.Localization.T("Até 25 MB"), 25), (global::WinStatusBar.Services.Localization.T("Até 100 MB"), 100), (global::WinStatusBar.Services.Localization.T("Até 500 MB"), 500)], s => s.ClipboardBackupLimitMb, (s, v) => s.ClipboardBackupLimitMb = v)));

        ClipPage.Children.Add(GroupTitle(global::WinStatusBar.Services.Localization.T("Limites do histórico")));
        _clipStats = Help(string.Empty);
        var open = new HyperlinkButton { Content = global::WinStatusBar.Services.Localization.T("Abrir pasta do histórico"), Padding = new Thickness(0) };
        open.Click += (_, _) => ClipHost.OpenDataFolder();
        var clear = new Button { Content = global::WinStatusBar.Services.Localization.T("Limpar histórico…") };
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 16 };
        actions.Children.Add(clear); actions.Children.Add(open);
        var summary = new StackPanel { Padding = new Thickness(16, 12, 16, 12), Spacing = 10 };
        summary.Children.Add(_clipStats); summary.Children.Add(actions);
        clear.Click += (_, _) => ConfirmClear(summary, clear);
        ClipPage.Children.Add(Group(
            Choice(global::WinStatusBar.Services.Localization.T("Quantidade máxima de itens"), global::WinStatusBar.Services.Localization.T("Ao passar do limite, sai o item usado há mais tempo."),
                [(global::WinStatusBar.Services.Localization.T("Ilimitada"), 0), ("100", 100), ("250", 250), ("500", 500), ("1.000", 1000), ("5.000", 5000)], s => s.ClipboardMaxItems, (s, v) => s.ClipboardMaxItems = v),
            Choice(global::WinStatusBar.Services.Localization.T("Apagar itens sem uso há mais de"), global::WinStatusBar.Services.Localization.T("Conta a partir da última vez que o item foi copiado ou colado."),
                [(global::WinStatusBar.Services.Localization.T("Nunca"), 0), (global::WinStatusBar.Services.Localization.T("1 dia"), 1), (global::WinStatusBar.Services.Localization.T("7 dias"), 7), (global::WinStatusBar.Services.Localization.T("30 dias"), 30), (global::WinStatusBar.Services.Localization.T("90 dias"), 90), ("1 ano", 365)], s => s.ClipboardMaxAgeDays, (s, v) => s.ClipboardMaxAgeDays = v),
            Choice(global::WinStatusBar.Services.Localization.T("Espaço máximo em disco"), global::WinStatusBar.Services.Localization.T("Imagens e cópias de arquivos. Ao passar do limite, saem as mais antigas."),
                [(global::WinStatusBar.Services.Localization.T("Ilimitado"), 0), ("100 MB", 100), ("250 MB", 250), ("500 MB", 500), ("1 GB", 1024), ("5 GB", 5120)], s => s.ClipboardMaxStorageMb, (s, v) => s.ClipboardMaxStorageMb = v),
            summary));
    }

    /// <summary>Confirmação dentro da própria página antes de apagar (fixados são mantidos).</summary>
    private void ConfirmClear(StackPanel host, Button clear)
    {
        var (items, pinned, _) = ClipHost.Stats();
        int n = items - pinned;
        if (n <= 0) { _clipStats!.Text = global::WinStatusBar.Services.Localization.T("Não há itens não fixados para apagar."); return; }
        var bar = new InfoBar
        {
            IsOpen = true, Severity = InfoBarSeverity.Warning, IsClosable = false,
            Title = $"Apagar {n:N0} itens?", Message = global::WinStatusBar.Services.Localization.T("Os itens fixados serão mantidos. Esta ação não pode ser desfeita."),
        };
        var yes = new Button { Content = global::WinStatusBar.Services.Localization.T("Apagar") };
        if (Application.Current.Resources.TryGetValue("AccentButtonStyle", out var st)) yes.Style = (Style)st;
        var no = new Button { Content = global::WinStatusBar.Services.Localization.T("Cancelar") };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        buttons.Children.Add(yes); buttons.Children.Add(no);
        bar.ActionButton = null;
        bar.Content = buttons;
        clear.IsEnabled = false;
        host.Children.Add(bar);
        void Close() { host.Children.Remove(bar); clear.IsEnabled = true; UpdateClipStats(); }
        no.Click += (_, _) => Close();
        yes.Click += (_, _) => { int removed = ClipHost.ClearUnpinned(); Close(); _clipStats!.Text = $"{removed:N0} itens apagados. " + _clipStats.Text; };
    }

    private void UpdateClipStats()
    {
        if (_clipStats is null) return;
        if (!ClipHost.Started) { _clipStats.Text = global::WinStatusBar.Services.Localization.T("A área de transferência está desligada."); return; }
        var (items, pinned, bytes) = ClipHost.Stats();
        string size = bytes >= 1073741824 ? $"{bytes / 1073741824.0:0.0} GB" : $"{bytes / 1048576.0:0.0} MB";
        _clipStats.Text = $"{items:N0} itens · {pinned:N0} fixados · {size} em disco" + (ClipHost.Paused ? " · captura pausada" : string.Empty);
    }

    /// <summary>Recarrega os controles extras quando a janela é reaberta (os valores podem ter mudado pela bandeja).</summary>
    private void LoadExtraValues()
    {
        _extrasLoading = true;
        foreach (var (control, read) in _extras)
        {
            object value = read(S);
            switch (control)
            {
                case ToggleSwitch t: t.IsOn = (bool)value; break;
                case ComboBox c: SelectValue(c, (int)value); break;
                case TextBox x: if (x.FocusState == FocusState.Unfocused) x.Text = (string)value; break;
            }
        }
        RefreshShortcutOrder();
        _extrasLoading = false;
        UpdateClipStats();
    }

    // ------------------------------------------------------------------ peças (mesmo estilo das linhas em XAML)
    private void Commit(Action<AppSettings> change)
    {
        if (_extrasLoading || _loading) return;
        change(S);
        try
        {
            _app.Settings.Save();
            _app.ApplySettings();
            SaveStatusIcon.Glyph = "";
            SaveStatusText.Text = global::WinStatusBar.Services.Localization.F("Alterações salvas · {0:HH:mm}", new object?[] {DateTime.Now});
        }
        catch (Exception ex)
        {
            SaveStatusIcon.Glyph = "";
            SaveStatusText.Text = global::WinStatusBar.Services.Localization.T("Não foi possível salvar a alteração. Tente novamente.");
            App.Log(global::WinStatusBar.Services.Localization.T("Configurações (extras): ") + ex.Message);
        }
        UpdateClipStats();
    }

    private Grid Row(string title, string? help, FrameworkElement control)
    {
        var row = new Grid { Padding = new Thickness(16, 12, 16, 12), ColumnSpacing = 20, RowSpacing = 8 };
        row.ColumnDefinitions.Add(new ColumnDefinition());
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(240) });
        row.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        row.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var text = new StackPanel { Spacing = 3, VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock { Text = title, Style = (Style)Root.Resources["SettingTitle"] });
        if (help is not null) text.Children.Add(Help(help));
        var host = new Grid { VerticalAlignment = VerticalAlignment.Center };
        if (control is Button) control.HorizontalAlignment = HorizontalAlignment.Right;
        host.Children.Add(control);
        Grid.SetColumn(host, 1);
        row.Children.Add(text); row.Children.Add(host);
        row.SizeChanged += OnSettingRowSizeChanged; // mesma adaptação a janelas estreitas das demais linhas
        return row;
    }

    private Grid Toggle(string title, string? help, Func<AppSettings, bool> get, Action<AppSettings, bool> set)
    {
        var t = new ToggleSwitch { OnContent = global::WinStatusBar.Services.Localization.T("Ativado"), OffContent = global::WinStatusBar.Services.Localization.T("Desativado"), HorizontalAlignment = HorizontalAlignment.Right, IsOn = get(S) };
        t.Toggled += (_, _) => Commit(s => set(s, t.IsOn));
        _extras.Add((t, s => get(s)));
        return Row(title, help, t);
    }

    private Grid Choice(string title, string? help, (string Label, int Value)[] options, Func<AppSettings, int> get, Action<AppSettings, int> set)
    {
        var box = new ComboBox { Style = (Style)Root.Resources["SettingChoice"] };
        foreach (var (label, value) in options) box.Items.Add(new ComboBoxItem { Content = label, Tag = value });
        SelectValue(box, get(S));
        box.SelectionChanged += (_, _) => { if (box.SelectedItem is ComboBoxItem { Tag: int v }) Commit(s => set(s, v)); };
        _extras.Add((box, s => get(s)));
        return Row(title, help, box);
    }

    private Grid Text(string title, string? help, string placeholder, Func<AppSettings, string> get, Action<AppSettings, string> set)
    {
        var box = new TextBox { PlaceholderText = placeholder, Text = get(S), TextWrapping = TextWrapping.Wrap };
        box.LostFocus += (_, _) => { if (box.Text != get(S)) Commit(s => set(s, box.Text.Trim())); };
        _extras.Add((box, s => get(s)));
        return Row(title, help, box);
    }

    /// <summary>Seleciona a opção do valor salvo; um valor fora da lista ganha uma opção própria.</summary>
    private static void SelectValue(ComboBox box, int value)
    {
        var item = box.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (int)i.Tag == value);
        if (item is null) { item = new ComboBoxItem { Content = value.ToString("N0"), Tag = value }; box.Items.Add(item); }
        box.SelectedItem = item;
    }

    private Border Group(params FrameworkElement[] rows)
    {
        var stack = new StackPanel();
        for (int i = 0; i < rows.Length; i++)
        {
            if (i > 0 && rows[i] is not TextBlock) stack.Children.Add(new Border { Height = 1, Background = WinStatusBar.Services.ThemeBrushes.Get("DividerStrokeColorDefaultBrush") });
            // Texto de ajuda solto no cartão: mesmo recuo das linhas (16), para alinhar com os títulos e as descrições.
            if (rows[i] is TextBlock loose && loose.Margin == default) loose.Margin = new Thickness(16, 0, 16, 12);
            stack.Children.Add(rows[i]);
        }
        return new Border { Style = (Style)Root.Resources["SettingsGroup"], Child = stack };
    }

    private TextBlock GroupTitle(string text) => new() { Text = text, Style = (Style)Root.Resources["GroupTitle"] };
    private TextBlock Help(string text) => new() { Text = text, Style = (Style)Root.Resources["SettingHelp"] };
}
