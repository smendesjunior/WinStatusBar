using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WinStatusBar.Core.Ai;
using WinStatusBar.Services;
namespace WinStatusBar.Views;

public sealed partial class SettingsWindow
{
    /// <summary>Texto de ajuda solto dentro de um cartão: mesmo recuo horizontal das linhas.</summary>
    private static TextBlock Pad(TextBlock text) { text.Margin = new Thickness(16, 4, 16, 14); return text; }

    private void BuildAdditionalAiAccounts()
    {
        var accounts = new StackPanel { Spacing = 12 };
        AiPage.Children.Insert(Math.Min(3, AiPage.Children.Count), accounts);
        var monitoring = (StackPanel)((Border)AiPage.Children[2]).Child;
        monitoring.Children.Add(new Border { Height = 1, Background = ThemeBrushes.Get("DividerStrokeColorDefaultBrush") });
        monitoring.Children.Add(Toggle("Lenovo Qira", "Acompanha as conversas do Qira e o chat dele na barra (modo Híbrido).", s => s.QiraEnabled, (s, v) => s.QiraEnabled = v));
        monitoring.Children.Add(Toggle("Conversas por API nesta barra", "Inclui as conversas feitas aqui na lista de tarefas e na escolha Automático. As preferências por serviço ficam abaixo.", s => s.ApiActivityEnabled, (s, v) => s.ApiActivityEnabled = v));
        var providers = new StackPanel { Spacing = 6 };
        foreach (var provider in AdditionalAiAccounts.Providers)
            providers.Children.Add(Toggle(provider.Name, "Acompanha respostas, erros e cancelamentos quando você usa esta IA no chat da barra.",
                s => !s.DisabledApiActivityProviders.Contains(provider.Id), (s, on) => { s.DisabledApiActivityProviders.Remove(provider.Id); if (!on) s.DisabledApiActivityProviders.Add(provider.Id); }));
        providers.Children.Add(Pad(Help("Essas escolhas valem enquanto Conversas por API nesta barra estiver ativado. APIs personalizadas também são acompanhadas. Conversas feitas fora da barra não são observadas.")));
        // Lista direta (sem menu recolhido), como as demais opções da seção.
        foreach (var child in providers.Children.ToList()) { providers.Children.Remove(child); monitoring.Children.Add(child); }

        BuildAiOrder(accounts);
        BuildCliConnections(accounts);
        foreach (var provider in AdditionalAiAccounts.Providers.Where(p => p.Endpoint is not null && p.Id != "gemini"))
            AddApiOnlyCard(accounts, provider);
        AddCopilotCard(accounts);

        var local = NewAccountCard("IA local", "ollama-estatico.svg", null, "");
        local.Identity(null, null, "Modelos no seu PC");
        local.SetNote("Não usa conta nem chave. Funciona quando um servidor Ollama (ou compatível) está em execução; os modelos instalados aparecem no seletor do chat. O phi4-mini é o mesmo modelo base do modo local do Qira.");
        local.Action.Visibility = Visibility.Collapsed;
        AddLocalModelRows(local);
        accounts.Children.Add(local.Root);
        var qira = NewAccountCard("Lenovo Qira", "qira.svg", null, "");
        qira.Identity(null, null, QiraAppBridge.IsInstalled ? "Aplicativo Lenovo instalado" : "Não instalado neste PC");
        qira.SetNote("Chat na barra com o próprio Qira, aberto em segundo plano e sem janela, sempre no modo Híbrido (o Qira decide entre o modelo dele e a nuvem). Para um modelo só no PC, use a IA local com o phi4-mini (o mesmo Phi-4 mini, pelo Ollama): ele usa a GPU NVIDIA quando ligada e a CPU quando ela está desligada. Imagens geradas aparecem no chat; conectores ativados no Qira são usados por ele. Os créditos da nuvem são lidos do Qira.");
        // Créditos na linha de baixo (o nome grande fica curto, como nos outros cartões).
        if (QiraBridge.CurrentQuota is { } qq)
            qira.Identity(QiraAppBridge.IsInstalled ? "Aplicativo Lenovo instalado" : "Não instalado neste PC", "Créditos da nuvem: " + QiraBridge.QuotaText(qq), "");
        qira.Action.Visibility = Visibility.Collapsed;
        accounts.Children.Add(qira.Root);
        BuildConnectors(accounts);

        var restore = new Button { Content = "Voltar à API própria anterior" };
        var restored = Help("");
        restore.Click += (_, _) => { bool ok = false; Commit(s => ok = AdditionalAiAccounts.RestoreCustom(s)); restored.Text = ok ? "API própria anterior selecionada. Sua chave original foi preservada." : "Nenhuma configuração anterior guardada neste PC."; };
        var restorePanel = new StackPanel { Spacing = 6, Margin = new Thickness(16, 12, 16, 12) }; restorePanel.Children.Add(restore); restorePanel.Children.Add(restored);
        accounts.Children.Add(Group(restorePanel));
        accounts.Children.Add(GroupTitle("Outro serviço (API própria)"));
        accounts.Children.Add(Help("Qualquer serviço compatível com OpenAI ou Anthropic (OpenRouter, Groq, Mistral, LM Studio…). Aparece no seletor do chat com o nome escolhido."));
        accounts.Children.Add(_apiSettingsPage);
    }

    /// <summary>
    /// IA local (Ollama): modelos carregados (como "ollama ps"), liberar agora (como "ollama stop") e por quanto tempo o
    /// modelo fica carregado depois do último uso (keep_alive enviado ao Ollama em cada pedido da barra).
    /// </summary>
    private void AddLocalModelRows(AccountCard local)
    {
        var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(3) };
        var state = new TextBlock { FontSize = 13, Foreground = ThemeBrushes.Get("TextFillColorSecondaryBrush"), TextWrapping = TextWrapping.Wrap, MaxWidth = 280, TextAlignment = TextAlignment.Right };
        IReadOnlyList<WinStatusBar.Core.Ai.Local.OllamaModels.Loaded>? loaded = null;
        async void Refresh()
        {
            loaded = await WinStatusBar.Core.Ai.Local.OllamaModels.LoadedAsync(http, CancellationToken.None);
            state.Text = WinStatusBar.Core.Ai.Local.OllamaModels.StatusText(loaded, DateTimeOffset.Now);
        }
        var release = SmallButton("Liberar agora", async () =>
        {
            foreach (var m in loaded ?? []) try { await WinStatusBar.Core.Ai.Local.OllamaModels.UnloadAsync(http, m.Name, CancellationToken.None); } catch { }
            Refresh();
        });
        local.Rows.Children.Add(AccountRow("", "Modelo carregado", state,
            Help("O Ollama escolhe sozinho onde rodar: GPU NVIDIA quando ligada; sem ela (modo de energia), CPU. Carrega no primeiro pedido e libera depois do tempo abaixo."), release));

        static string Label(string v) => v switch
        {
            "0" => "Liberar logo após responder",
            "1m" => "1 minuto",
            "5m" => "5 minutos (padrão do Ollama)",
            "15m" => "15 minutos",
            "30m" => "30 minutos",
            "1h" => "1 hora",
            "-1" => "Sempre carregado",
            _ => LocalKeepAlive.Describe(v),
        };
        var keep = new ComboBox { MinWidth = 240 };
        var values = LocalKeepAlive.Choices.ToList();
        if (!values.Contains(S.LocalModelKeepAlive)) values.Add(S.LocalModelKeepAlive);
        foreach (var v in values) keep.Items.Add(new ComboBoxItem { Content = Label(v), Tag = v });
        keep.SelectedIndex = values.IndexOf(S.LocalModelKeepAlive);
        keep.SelectionChanged += (_, _) => { if (keep.SelectedItem is ComboBoxItem { Tag: string v } && v != S.LocalModelKeepAlive) Commit(s => s.LocalModelKeepAlive = v); };
        local.Rows.Children.Add(AccountRow("", "Manter carregado", keep,
            Help("Enviado ao Ollama em cada pedido (keep_alive). A contagem começa quando a resposta termina e recomeça a cada uso.")));
        Closed += (_, _) => http.Dispose();
        Refresh(); _accountRefreshers.Add(Refresh);
    }

    /// <summary>DeepSeek e Grok: só chave de API (os serviços não oferecem login de conta para apps de terceiros).</summary>
    private void AddApiOnlyCard(StackPanel accounts, AdditionalAiAccounts.Provider provider)
    {
        var card = NewAccountCard(provider.Name, provider.Id + ".svg", provider.KeysUrl, "Console da API");
        var (keyBadge, setKey) = Badge();
        var keyHelp = Help("");
        var configure = SmallButton("Configurar chave…", () => { });
        var remove = SmallButton("Remover chave", () => { });
        var use = SmallButton("Usar no chat", () => { });
        var usage = new TextBlock { FontSize = 13, Foreground = ThemeBrushes.Get("TextFillColorSecondaryBrush"), TextWrapping = TextWrapping.Wrap, MaxWidth = 260, TextAlignment = TextAlignment.Right };
        card.Rows.Children.Add(AccountRow("", "Chave de API", keyBadge, keyHelp, configure, remove, use));
        card.Rows.Children.Add(AccountRow("", "Cota e saldo", usage));
        void Refresh()
        {
            bool key = ApiKeyStore.Get(provider.Resource) is not null;
            string? model = AdditionalAiAccounts.Models().GetValueOrDefault(provider.Id);
            card.Identity(null, null, key ? "Conectado por chave de API" : "Não conectado");
            card.Action.Content = key ? "Sair" : "Entrar";
            setKey(key, key ? "Configurada" : "Não configurada");
            keyHelp.Text = key ? "Guardada no Cofre do Windows deste PC" + (model is null ? "." : " · modelo " + model + ".") + " A chave não informa o e-mail da conta."
                : "Crie uma chave no console oficial e cole aqui. Fica só neste PC, no Cofre do Windows.";
            remove.IsEnabled = use.IsEnabled = key;
            usage.Text = provider.Id == "deepseek" ? "Saldo exibido no menu de agentes da barra" : "Não informados pela chave; consulte o console oficial";
        }
        async Task Configure()
        {
            if (!await ConfigureApiKeyAsync(provider, card.Note)) return;
            Commit(s => AdditionalAiAccounts.Activate(s, provider)); Refresh();
            card.SetNote("Chave verificada e salva; " + provider.Name + " selecionado para novas conversas. Isso não confirma saldo de créditos.");
        }
        async Task Remove()
        {
            if (!await ConfirmAsync("Remover a chave do " + provider.Name + "?", "A chave é apagada do Cofre do Windows deste PC. A conta no site não é alterada.", "Remover")) return;
            try { ApiKeyStore.Set(null, provider.Resource); Refresh(); card.SetNote("Chave removida deste PC."); } catch { card.SetNote("Não foi possível remover a chave."); }
        }
        configure.Click += async (_, _) => await Configure();
        remove.Click += async (_, _) => await Remove();
        use.Click += (_, _) => { Commit(s => AdditionalAiAccounts.Activate(s, provider)); card.SetNote(provider.Name + " selecionado para novas conversas."); };
        card.Action.Click += async (_, _) => { if (ApiKeyStore.Get(provider.Resource) is null) await Configure(); else await Remove(); };
        Refresh(); _accountRefreshers.Add(Refresh);
        accounts.Children.Add(card.Root);
    }

    /// <summary>Copilot: conta Microsoft no Copilot embutido da barra (página oficial, sem teclado nem janela externa).</summary>
    private void AddCopilotCard(StackPanel accounts)
    {
        var card = NewAccountCard("Microsoft Copilot", "copilot.svg", "https://account.microsoft.com/", "Minha conta Microsoft");
        var (badge, setBadge) = Badge();
        card.Rows.Children.Add(AccountRow("", "Login no Copilot da barra", badge,
            Help("Você entra uma vez na página oficial do Copilot, numa janela da barra. Depois as perguntas são respondidas no próprio chat da barra, sem abrir o app nem digitar em outras janelas. Não há API pública do Copilot pessoal.")));
        void Refresh()
        {
            bool signed = CopilotWebSession.SignedIn;
            card.Identity(CopilotWebSession.AccountName, CopilotWebSession.AccountEmail, signed ? "Conta Microsoft conectada" : "Não conectado");
            card.Action.Content = signed ? "Sair" : "Entrar";
            setBadge(signed, signed ? "Conectado" : "Não conectado");
        }
        card.Action.Click += async (_, _) =>
        {
            if (!CopilotWebSession.SignedIn) { CopilotWebSession.ShowSignIn(); card.SetNote("Entre na janela do Copilot que foi aberta. O cartão se atualiza quando você voltar."); return; }
            if (!await ConfirmAsync("Sair do Copilot da barra?", "Apaga a sessão do Copilot guardada pela barra neste PC. O app Copilot do Windows e sua conta Microsoft não são alterados.", "Sair")) return;
            await CopilotWebSession.SignOutAsync(); Refresh(); card.SetNote("Sessão do Copilot da barra encerrada.");
        };
        Refresh(); _accountRefreshers.Add(Refresh);
        accounts.Children.Add(card.Root);
    }

    /// <summary>Diálogo de chave: verifica listando os modelos (não gera texto) e salva no Cofre do Windows.</summary>
    private async Task<bool> ConfigureApiKeyAsync(AdditionalAiAccounts.Provider provider, TextBlock feedbackOut)
    {
        var password = new PasswordBox { PlaceholderText = "Cole a chave da API (vazio mantém a salva)" };
        var models = new ComboBox { PlaceholderText = "Verifique a chave para listar modelos", HorizontalAlignment = HorizontalAlignment.Stretch, DisplayMemberPath = "Label" };
        var feedback = Help("A verificação consulta os modelos da conta; não gera texto nem envia o contexto da tela.");
        var test = new Button { Content = "Verificar chave e listar modelos" };
        var keys = new HyperlinkButton { Content = "Obter chave no site oficial ↗", NavigateUri = new Uri(provider.KeysUrl!) };
        var content = new StackPanel { Spacing = 10 }; content.Children.Add(password); content.Children.Add(keys); content.Children.Add(test); content.Children.Add(models); content.Children.Add(feedback);
        var dialog = new ContentDialog { XamlRoot = AiPage.XamlRoot, RequestedTheme = AiPage.ActualTheme, Title = "Chave de API · " + provider.Name, Content = content, PrimaryButtonText = "Salvar", IsPrimaryButtonEnabled = false, CloseButtonText = "Cancelar" };
        string? verified = null;
        password.PasswordChanged += (_, _) => { verified = null; dialog.IsPrimaryButtonEnabled = false; models.ItemsSource = null; };
        test.Click += async (_, _) =>
        {
            string? value = string.IsNullOrWhiteSpace(password.Password) ? ApiKeyStore.Get(provider.Resource) : password.Password.Trim();
            if (string.IsNullOrWhiteSpace(value)) { feedback.Text = "Informe a chave fornecida pelo serviço."; return; }
            test.IsEnabled = false; password.IsEnabled = false; feedback.Text = "Consultando modelos…";
            try
            {
                var catalog = await AgentPrompt.CatalogAsync(AgentPrompt.Target.Api, new("openai", provider.Endpoint!, value), CancellationToken.None);
                var textModels = catalog.Models.Where(m => !new[] { "image", "embedding", "tts", "transcrib", "audio", "video" }.Any(x => m.Id.Contains(x, StringComparison.OrdinalIgnoreCase))).ToList();
                models.ItemsSource = textModels; models.SelectedItem = textModels.FirstOrDefault(m => m.Id == AdditionalAiAccounts.Models().GetValueOrDefault(provider.Id)) ?? textModels.FirstOrDefault();
                verified = value; dialog.IsPrimaryButtonEnabled = textModels.Count > 0;
                feedback.Text = (catalog.Error is { } keyError ? AiErrorExplainer.Explain(AgentPrompt.Target.Api, keyError).Message : null) ?? (textModels.Count > 0 ? "Chave aceita. Escolha o modelo de conversa." : "O serviço não retornou modelos de conversa para esta conta.");
            }
            catch (Exception ex) { feedback.Text = AiErrorExplainer.Explain(AgentPrompt.Target.Api, ex.Message).Message; }
            finally { test.IsEnabled = true; password.IsEnabled = true; }
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary || verified is null || models.SelectedItem is not AgentPrompt.ModelOption model) return false;
        try { ApiKeyStore.Set(verified, provider.Resource); AdditionalAiAccounts.Save(provider, model.Id); return true; }
        catch { feedbackOut.Text = "Não foi possível salvar a chave neste PC."; feedbackOut.Visibility = Visibility.Visible; return false; }
        finally { password.Password = ""; }
    }
}
