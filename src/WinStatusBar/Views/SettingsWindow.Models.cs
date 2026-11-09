using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WinStatusBar.Core.Ai.Codex;
using WinStatusBar.Services;

namespace WinStatusBar.Views;

/// <summary>
/// Configurações › Modelos: a ligação das IAs da barra com o Codex (estado em selo colorido, ligar, desligar e corrigir),
/// a lista das IAs detectadas neste PC com o estado de cada uma no Codex e a verificação manual dos modelos (novos, que
/// saíram, IAs fora e por quê), que atualiza as listas da barra e o seletor do Codex.
/// O trabalho é do componente das IAs no Codex (Automation\WinStatusBar.CodexIAs.exe); esta página só o chama e mostra o
/// relatório que ele grava.
/// </summary>
public sealed partial class SettingsWindow
{
    private enum Tone { Success, Critical, Caution, Neutral, Accent }

    /// <summary>Roda "Corrigir problemas" (pedido pelo menu "…" da barra).</summary>
    private Action? _startRepair;

    /// <summary>Abre a página Modelos e roda a correção dos problemas das IAs.</summary>
    public void StartRepair()
    {
        if (Nav.MenuItems.OfType<NavigationViewItem>().FirstOrDefault(i => (string)i.Tag == "models") is { } item) Nav.SelectedItem = item;
        _startRepair?.Invoke();
    }

    /// <summary>Selo de estado em cor de tema ("ONLINE" verde, "FALHA" vermelho…), no padrão dos selos da barra.</summary>
    private static (Border Root, Action<Tone, string> Set) StatusBadge(bool large = false)
    {
        var text = new TextBlock { FontSize = large ? 14 : 11.5, FontWeight = FontWeights.SemiBold, CharacterSpacing = 40, VerticalAlignment = VerticalAlignment.Center };
        var icon = new FontIcon { FontSize = large ? 13 : 11, VerticalAlignment = VerticalAlignment.Center };
        var stack = new StackPanel { Orientation = Orientation.Horizontal, Spacing = large ? 7 : 5 };
        stack.Children.Add(icon); stack.Children.Add(text);
        var root = new Border
        {
            CornerRadius = new CornerRadius(large ? 13 : 10), Padding = large ? new Thickness(12, 5, 14, 6) : new Thickness(9, 3, 10, 4), Child = stack,
            HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center,
        };
        void Set(Tone tone, string label)
        {
            text.Text = label;
            (string fg, string bg, string glyph) = tone switch
            {
                Tone.Success => ("SystemFillColorSuccessBrush", "SystemFillColorSuccessBackgroundBrush", ""),
                Tone.Critical => ("SystemFillColorCriticalBrush", "SystemFillColorCriticalBackgroundBrush", ""),
                Tone.Caution => ("SystemFillColorCautionBrush", "SystemFillColorCautionBackgroundBrush", ""),
                Tone.Accent => ("AccentTextFillColorPrimaryBrush", "SubtleFillColorSecondaryBrush", ""),
                _ => ("TextFillColorSecondaryBrush", "SystemFillColorNeutralBackgroundBrush", ""),
            };
            icon.Glyph = glyph;
            root.Background = ThemeBrushes.Get(bg);
            text.Foreground = icon.Foreground = ThemeBrushes.Get(fg);
        }
        Set(Tone.Neutral, "");
        return (root, Set);
    }

    private static Button BigButton(string label, bool accent)
    {
        var b = new Button { Content = label, MinWidth = 210, MinHeight = 40, Padding = new Thickness(20, 9, 20, 10), FontSize = 15 };
        if (accent && Application.Current.Resources.TryGetValue("AccentButtonStyle", out var style)) b.Style = (Style)style;
        return b;
    }

    private void BuildModelsPage()
    {
        var page = AddPage("models", "Modelos",
            "Atualize as listas de modelos da barra e do Codex e ligue as IAs configuradas neste PC no seletor de modelos do Codex. Nada é verificado sozinho: só quando você clica.",
            "");

        // ------------------------------------------------------------ listas de modelos (barra e Codex)
        var check = BigButton("Verificar e atualizar modelos", accent: true);
        var ring = new ProgressRing { IsActive = false, Width = 22, Height = 22, Visibility = Visibility.Collapsed, VerticalAlignment = VerticalAlignment.Center };
        var checkRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        checkRow.Children.Add(check); checkRow.Children.Add(ring);
        var feedback = new TextBlock { Style = (Style)Root.Resources["SettingHelp"], TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed };
        var listsCard = new StackPanel { Spacing = 10, Padding = new Thickness(16, 14, 16, 16) };
        listsCard.Children.Add(new TextBlock { Text = "Listas de modelos da barra e do Codex", FontSize = 18, FontWeight = FontWeights.SemiBold });
        listsCard.Children.Add(new TextBlock
        {
            Style = (Style)Root.Resources["SettingHelp"], TextWrapping = TextWrapping.Wrap,
            Text = "Um clique atualiza as duas listas de uma vez: os seletores de modelo do chat da barra (Claude, Gemini e Copilot com os modos dos sites, níveis de pensamento dos modelos da OpenAI, Lenovo Qira, APIs com chave e modelos locais) e o seletor de modelos do Codex, quando a ligação abaixo está ligada (IAs da barra e modelos novos da OpenAI e do Ollama). Gemini e Copilot são lidos dos próprios sites; leva cerca de 1 minuto.",
        });
        listsCard.Children.Add(checkRow);
        listsCard.Children.Add(feedback);
        page.Children.Add(new Border { Style = (Style)Root.Resources["SettingsGroup"], Child = listsCard });

        // ------------------------------------------------------------ ligação com o Codex
        var (badge, setBadge) = StatusBadge(large: true);
        var title = new TextBlock { Text = "Ligação com o Codex", FontSize = 18, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
        var head = new Grid { ColumnSpacing = 12 };
        head.ColumnDefinitions.Add(new ColumnDefinition());
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        head.Children.Add(title);
        Grid.SetColumn(badge, 1); head.Children.Add(badge);
        var codexStatus = new TextBlock { Style = (Style)Root.Resources["SettingHelp"], TextWrapping = TextWrapping.Wrap };
        var connect = BigButton("Ligar no Codex", accent: false);
        var repair = BigButton("Corrigir ligação", accent: false);
        repair.Visibility = Visibility.Collapsed;
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        actions.Children.Add(connect); actions.Children.Add(repair);
        var actionsScroll = new ScrollViewer { Content = actions, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled, HorizontalScrollMode = ScrollMode.Auto };
        var codexFeedback = new TextBlock { Style = (Style)Root.Resources["SettingHelp"], TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed };
        var about = new TextBlock
        {
            Style = (Style)Root.Resources["SettingHelp"], TextWrapping = TextWrapping.Wrap,
            Text = "Ligado, o seletor de modelos do Codex passa a ter as IAs configuradas aqui (com login, chave da API ou modelo local), pelo encaminhador local da barra (porta 11435; só texto), e os modelos novos do Ollama entram nele na verificação. Os modelos da OpenAI continuam iguais, pela sua conta. Desligar devolve o config.toml do Codex de antes.",
        };
        var card = new StackPanel { Spacing = 10, Padding = new Thickness(16, 14, 16, 16) };
        card.Children.Add(head); card.Children.Add(codexStatus); card.Children.Add(actionsScroll); card.Children.Add(codexFeedback); card.Children.Add(about);
        page.Children.Add(new Border { Style = (Style)Root.Resources["SettingsGroup"], Child = card });

        // ------------------------------------------------------------ corrigir problemas
        var repairAll = BigButton("Corrigir problemas", accent: false);
        var redoAll = BigButton("Refazer todas as ligações", accent: false);
        var repairRing = new ProgressRing { IsActive = false, Width = 22, Height = 22, Visibility = Visibility.Collapsed, VerticalAlignment = VerticalAlignment.Center };
        var repairButtons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        repairButtons.Children.Add(repairAll); repairButtons.Children.Add(redoAll); repairButtons.Children.Add(repairRing);
        var repairStatus = new TextBlock { Style = (Style)Root.Resources["SettingHelp"], TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed };
        var repairResults = new StackPanel();
        var repairCard = new StackPanel { Spacing = 10, Padding = new Thickness(16, 14, 16, 16) };
        repairCard.Children.Add(new TextBlock { Text = "Corrigir problemas das IAs", FontSize = 18, FontWeight = FontWeights.SemiBold });
        repairCard.Children.Add(new TextBlock
        {
            Style = (Style)Root.Resources["SettingHelp"], TextWrapping = TextWrapping.Wrap,
            Text = "\"Corrigir problemas\" confere tudo de que as IAs da barra e do Codex dependem e conserta sozinho o que estiver errado: encaminhador do Codex parado ou travado, porta 11435 ocupada, config.toml alterado por outro programa, IAs fora do seletor do Codex, ponte antiga da barra, Ollama parado, Lenovo Qira travado ou com o serviço de modelos parado, ligação do Claude Code com a barra e integrações da barra; no fim, verifica os modelos de novo. O que depende de você (login, administrador) aparece com o botão certo.\n\"Refazer todas as ligações\" é a salvaguarda: refaz tudo mesmo sem erro detectado (encaminhador, config.toml, ponte, integrações e páginas do Gemini e do Copilot).",
        });
        var repairScroll = new ScrollViewer { Content = repairButtons, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled, HorizontalScrollMode = ScrollMode.Auto };
        repairCard.Children.Add(repairScroll);
        repairCard.Children.Add(repairStatus);
        repairCard.Children.Add(repairResults);
        var repairBorder = new Border { Style = (Style)Root.Resources["SettingsGroup"], Child = repairCard };
        page.Children.Add(repairBorder);

        // ------------------------------------------------------------ IAs detectadas e relatório
        var listTitle = GroupTitle("IAs detectadas neste PC");
        page.Children.Add(listTitle);
        var report = new StackPanel { Spacing = 12 };
        page.Children.Add(report);

        bool busy = false;
        TextBlock output = feedback; // onde aparece a mensagem do comando em andamento
        void SetBusy(bool on, string? message)
        {
            busy = on;
            check.IsEnabled = connect.IsEnabled = repair.IsEnabled = repairAll.IsEnabled = redoAll.IsEnabled = !on;
            ring.IsActive = on;
            ring.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
            if (message is not null) { output.Text = message; output.Visibility = Visibility.Visible; }
            RenderReport();
        }

        async Task RunAsync(string working, string[] args, bool refreshBarLists, TextBlock? target = null)
        {
            if (busy) return;
            output = target ?? feedback;
            SetBusy(true, working);
            string message;
            try
            {
                // Lista da barra: níveis de pensamento dos modelos da OpenAI (do Codex instalado).
                if (refreshBarLists) await Task.Run(() => CodexModelLevels.RefreshNowAsync(CancellationToken.None));
                var result = await Task.Run(() => CodexIasComponent.RunAsync(args, CancellationToken.None));
                CodexIasComponent.NotifyModelsChanged();
                message = result.Mensagem;
            }
            catch (Exception ex) { message = "Falhou: " + ex.Message; }
            SetBusy(false, message);
            await RenderCodexAsync();
        }

        async Task<bool> ConfirmAsync(string dialogTitle, string text, string action)
        {
            var dialog = new ContentDialog
            {
                Title = dialogTitle, Content = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap },
                PrimaryButtonText = action, CloseButtonText = "Cancelar", DefaultButton = ContentDialogButton.Close,
                XamlRoot = Root.XamlRoot, RequestedTheme = Root.ActualTheme,
            };
            return await dialog.ShowAsync() == ContentDialogResult.Primary;
        }

        static string List(IEnumerable<string> names) => string.Join(", ", names);
        // Listas longas (ex.: modelos que saíram de uma API) ficam nos primeiros nomes, sem repetir o nome do serviço.
        static string Short(string service, List<string> names)
        {
            var clean = names.Select(n => n.StartsWith(service + " — ", StringComparison.Ordinal) ? n[(service.Length + 3)..] : n).ToList();
            return clean.Count <= 8 ? string.Join(", ", clean) : string.Join(", ", clean.Take(8)) + $" e mais {clean.Count - 8}";
        }

        Grid AiRow(CodexIntegration.ServiceReport s, bool linked)
        {
            var row = new Grid { Padding = new Thickness(16, 11, 16, 11), ColumnSpacing = 14 };
            row.ColumnDefinitions.Add(new ColumnDefinition());
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var text = new StackPanel { Spacing = 3, VerticalAlignment = VerticalAlignment.Center };
            text.Children.Add(new TextBlock { Text = s.Nome, Style = (Style)Root.Resources["SettingTitle"] });
            string detail = s.Falhou ? $"Não foi possível ler agora: {s.Motivo}." + (s.Total > 0 ? $" As {s.Total} opções anteriores continuam." : "")
                : s.Total == 0 ? (s.Motivo is { Length: > 0 } why ? char.ToUpper(why[0]) + why[1..] + "." : "Nenhuma opção.")
                : $"{s.Total} {(s.Total == 1 ? "opção" : "opções")}{(linked ? " no seletor do Codex" : "; entram no Codex ao ligar")}.";
            if (s.Novos.Count > 0) detail += "\nNovos: " + Short(s.Nome, s.Novos) + ".";
            if (s.Sairam.Count > 0) detail += "\nSaíram: " + Short(s.Nome, s.Sairam) + ".";
            text.Children.Add(Help(detail));
            row.Children.Add(text);

            var right = new StackPanel { Spacing = 6, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
            var (rowBadge, setRow) = StatusBadge();
            if (s.Falhou) setRow(Tone.Critical, "FALHA");
            else if (s.Total == 0) setRow(Tone.Neutral, "FORA");
            else if (!linked) setRow(Tone.Accent, "PRONTA");
            else setRow(Tone.Success, "NO CODEX");
            right.Children.Add(rowBadge);
            // Corrigir só esta IA: relê só ela (rápido; não reabre as páginas das outras).
            if (s.Falhou && s.Id is { Length: > 0 } id)
            {
                var retry = new Button { Content = "Tentar de novo", FontSize = 12, Padding = new Thickness(10, 4, 10, 5), HorizontalAlignment = HorizontalAlignment.Right };
                retry.Click += async (_, _) => await RunAsync($"Verificando {s.Nome} de novo…", ["--verificar", "--ia", id], refreshBarLists: false);
                retry.IsEnabled = !busy;
                right.Children.Add(retry);
            }
            if (s.Login is "gemini" or "copilot")
            {
                var login = new Button { Content = "Entrar", FontSize = 12, Padding = new Thickness(10, 4, 10, 5), HorizontalAlignment = HorizontalAlignment.Right };
                login.Click += (_, _) =>
                {
                    if (s.Login == "gemini") GeminiWebSession.ShowSignIn(); else CopilotWebSession.ShowSignIn();
                    feedback.Text = $"Entre na conta na janela que abriu. Depois clique em \"Tentar de novo\" ou em \"Verificar e atualizar modelos\".";
                    feedback.Visibility = Visibility.Visible;
                };
                var retry = new Button { Content = "Tentar de novo", FontSize = 12, Padding = new Thickness(10, 4, 10, 5), HorizontalAlignment = HorizontalAlignment.Right, IsEnabled = !busy };
                retry.Click += async (_, _) => await RunAsync($"Verificando {s.Nome} de novo…", ["--verificar", "--ia", s.Login], refreshBarLists: false);
                right.Children.Add(login);
                right.Children.Add(retry);
            }
            // Reler só esta IA quando quiser: no menu do botão direito da linha (sem botão a mais na lista).
            if (s.Id is { Length: > 0 } rid && rid != "removidas")
            {
                var only = new MenuFlyoutItem { Text = "Verificar só esta IA de novo", Icon = new FontIcon { Glyph = "\uE72C" } };
                only.Click += async (_, _) => await RunAsync($"Verificando {s.Nome} de novo…", ["--verificar", "--ia", rid], refreshBarLists: false);
                var menu = new MenuFlyout();
                menu.Items.Add(only);
                row.ContextFlyout = menu;
                row.Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Transparent); // o clique direito vale na linha toda
            }
            Grid.SetColumn(right, 1);
            row.Children.Add(right);
            return row;
        }

        void RenderReport()
        {
            report.Children.Clear();
            var r = CodexIntegration.LoadReport();
            bool linked = CodexIntegration.LoadState().Ligado;
            if (r is null)
            {
                listTitle.Text = "IAs detectadas neste PC";
                report.Children.Add(Help("Nenhuma verificação feita ainda neste PC. Clique em \"Verificar e atualizar modelos\"."));
                return;
            }
            listTitle.Text = $"IAs detectadas neste PC · verificadas em {r.VerificadoEm:dd/MM/yyyy HH:mm}";
            // Primeiro as que estão no Codex, depois as com falha e por fim as que ficaram de fora.
            var ordered = r.Servicos.OrderBy(s => s.Falhou ? 1 : s.Total > 0 ? 0 : 2).ToList();
            report.Children.Add(Group(ordered.Select(s => (FrameworkElement)AiRow(s, linked && r.Codex.Ligado)).ToArray()));

            var c = r.Codex;
            if (!c.Ligado)
            {
                if (c.Aviso is { Length: > 0 } warning) report.Children.Add(Help(char.ToUpper(warning[0]) + warning[1..] + "."));
                return;
            }
            var codexRows = new List<FrameworkElement>();
            if (c.Aviso is { Length: > 0 } aviso)
            {
                var (b, set) = StatusBadge(); set(Tone.Caution, "ATENÇÃO");
                codexRows.Add(Row("Seletor do Codex", char.ToUpper(aviso[0]) + aviso[1..] + ".", b));
            }
            if (c.PossiveisObsoletos.Count > 0)
            {
                var remove = new Button { Content = "Remover do seletor" };
                remove.Click += async (_, _) =>
                {
                    if (await ConfirmAsync("Remover modelos obsoletos?",
                            "Estes modelos da sua conta não estão mais na lista do Codex instalado e devem ter sido aposentados pela OpenAI:\n\n"
                            + List(c.PossiveisObsoletos) + "\n\nEles saem do seletor do Codex. Se algum voltar à lista do Codex, ele entra de novo na próxima verificação.", "Remover"))
                        await RunAsync("Removendo os modelos obsoletos e verificando…", ["--verificar", "--remover-obsoletos"], refreshBarLists: true);
                };
                codexRows.Add(Row("Possíveis obsoletos da OpenAI", "Modelos que o Codex instalado não lista mais: " + List(c.PossiveisObsoletos) + ". Continuam no seletor até você remover.", remove));
            }
            if (codexRows.Count > 0) report.Children.Add(Group(codexRows.ToArray()));
        }

        async Task RenderCodexAsync()
        {
            var st = CodexIntegration.LoadState();
            bool codexFound = CodexAppServerClient.FindCodexExecutable() is not null || File.Exists(Path.Combine(CodexAppServerClient.CodexHome(), "config.toml"));
            var last = CodexIntegration.LoadReport();
            bool someFailed = last?.Servicos.Any(s => s.Falhou) == true;
            repair.Visibility = Visibility.Collapsed;
            connect.IsEnabled = !busy && CodexIasComponent.Available && (codexFound || st.Ligado);
            if (!CodexIasComponent.Available)
            {
                setBadge(Tone.Neutral, "INDISPONÍVEL"); connect.Content = "Ligar no Codex";
                codexStatus.Text = "O componente das IAs no Codex não está nesta instalação da barra (instale a versão mais nova da barra).";
                return;
            }
            if (!st.Ligado)
            {
                connect.Content = "Ligar no Codex";
                if (!codexFound) { setBadge(Tone.Neutral, "INDISPONÍVEL"); codexStatus.Text = "Codex não encontrado neste PC."; }
                else { setBadge(Tone.Neutral, "DESLIGADO"); codexStatus.Text = "Desligado: o Codex usa só os modelos dele. Ao ligar, o config.toml do Codex ganha uma cópia de segurança e os modelos são verificados em seguida."; }
                return;
            }
            connect.Content = "Desligar (desfazer)";
            string route = st.Upstream is null ? "direto à OpenAI" : st.Upstream.Contains("127.0.0.1:11434", StringComparison.Ordinal) ? "pelo Ollama, como antes" : st.Upstream;
            string since = st.LigadoEm is { } on ? $"Ligado desde {on:dd/MM/yyyy HH:mm}. " : "Ligado. ";
            setBadge(Tone.Accent, "VERIFICANDO…");
            bool online = await CodexIasComponent.GatewayOnlineAsync();
            if (!online)
            {
                setBadge(Tone.Critical, "FALHA");
                codexStatus.Text = since + "O encaminhador do Codex (porta 11435) não está respondendo: o Codex não consegue usar nenhum modelo até ele voltar. Clique em \"Corrigir ligação\".";
                repair.Visibility = Visibility.Visible;
                return;
            }
            if (someFailed)
            {
                setBadge(Tone.Caution, "ONLINE · COM FALHAS");
                codexStatus.Text = since + $"Encaminhador respondendo. Modelos da OpenAI: {route}. Alguma IA falhou na última verificação (veja abaixo); \"Corrigir ligação\" refaz tudo.";
                repair.Visibility = Visibility.Visible;
                return;
            }
            setBadge(Tone.Success, "ONLINE");
            codexStatus.Text = since + $"Encaminhador respondendo. Modelos da OpenAI: {route}.";
        }

        // ------------------------------------------------------------ corrigir problemas: execução e resultado
        FrameworkElement RepairRow(AiRepair.Step step)
        {
            var row = new Grid { Padding = new Thickness(0, 9, 0, 9), ColumnSpacing = 14 };
            row.ColumnDefinitions.Add(new ColumnDefinition());
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var text = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
            text.Children.Add(new TextBlock { Text = step.Area, Style = (Style)Root.Resources["SettingTitle"] });
            text.Children.Add(Help(step.Detail));
            row.Children.Add(text);
            var right = new StackPanel { Spacing = 6, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
            var (b, set) = StatusBadge();
            switch (step.Outcome)
            {
                case AiRepair.Outcome.Ok: set(Tone.Success, "OK"); break;
                case AiRepair.Outcome.Fixed: set(Tone.Success, "CORRIGIDO"); break;
                case AiRepair.Outcome.NeedsYou: set(Tone.Caution, "PRECISA DE VOCÊ"); break;
                case AiRepair.Outcome.Failed: set(Tone.Critical, "FALHOU"); break;
                default: set(Tone.Neutral, "PULADO"); break;
            }
            right.Children.Add(b);
            Button? action = step.Action switch
            {
                "login-gemini" or "login-copilot" => new Button { Content = "Entrar" },
                "qira-ponte" => new Button { Content = "Ativar (administrador)" },
                "codex-ligar" => new Button { Content = "Tentar de novo" },
                { } a when a.StartsWith("verificar:", StringComparison.Ordinal) => new Button { Content = "Tentar de novo" },
                _ => null,
            };
            if (action is not null)
            {
                action.FontSize = 12; action.Padding = new Thickness(10, 4, 10, 5); action.HorizontalAlignment = HorizontalAlignment.Right;
                action.Click += async (_, _) =>
                {
                    switch (step.Action)
                    {
                        case "login-gemini": GeminiWebSession.ShowSignIn(); repairStatus.Text = "Entre na conta Google na janela que abriu e rode a correção de novo."; break;
                        case "login-copilot": CopilotWebSession.ShowSignIn(); repairStatus.Text = "Entre na conta Microsoft na janela que abriu e rode a correção de novo."; break;
                        case "qira-ponte":
                            if (!await ConfirmAsync("Ativar a ponte do Qira?",
                                    "A barra grava uma política do Windows que deixa o Lenovo Qira (QuantumAI.Hub) aceitar a conexão local da barra e do Codex na porta 9339, só neste PC (páginas da internet não conseguem usá-la). O Windows vai pedir administrador. Se o Qira estiver aberto, ele é reiniciado.", "Ativar"))
                                return;
                            repairStatus.Text = await AiRepair.EnableQiraBridgeAsync();
                            break;
                        case "codex-ligar": await RunAsync("Refazendo a ligação com o Codex…", ["--ligar"], refreshBarLists: true, repairStatus); break;
                        default: await RunAsync($"Verificando {step.Area} de novo…", ["--verificar", "--ia", step.Action!["verificar:".Length..]], refreshBarLists: false, repairStatus); break;
                    }
                    repairStatus.Visibility = Visibility.Visible;
                };
                right.Children.Add(action);
            }
            Grid.SetColumn(right, 1);
            row.Children.Add(right);
            return row;
        }

        async Task RunRepairAsync(bool force)
        {
            if (busy || AiRepair.Busy) return;
            if (force && !await ConfirmAsync("Refazer todas as ligações?",
                    "A barra refaz tudo, mesmo sem erro detectado: encerra e religa o encaminhador do Codex, reaplica o config.toml do Codex (com cópia de segurança, se algo mudar), reinicia a ponte antiga e as integrações, reabre as páginas escondidas do Gemini e do Copilot, refaz a ligação do Claude Code com a barra e verifica os modelos. Respostas em andamento na barra ou no Codex podem ser interrompidas. Leva cerca de 1 minuto.", "Refazer"))
                return;
            busy = true;
            check.IsEnabled = connect.IsEnabled = repair.IsEnabled = repairAll.IsEnabled = redoAll.IsEnabled = false;
            repairRing.IsActive = true; repairRing.Visibility = Visibility.Visible;
            repairResults.Children.Clear();
            repairStatus.Visibility = Visibility.Visible;
            repairStatus.Text = force ? "Refazendo todas as ligações…" : "Conferindo…";
            List<AiRepair.Step>? steps = null;
            try { steps = await AiRepair.RunAsync(force, message => repairStatus.Text = message, CancellationToken.None); }
            catch (Exception ex) { repairStatus.Text = "A correção parou: " + ex.Message; App.Log("Correção das IAs: " + ex); }
            busy = false;
            check.IsEnabled = connect.IsEnabled = repair.IsEnabled = repairAll.IsEnabled = redoAll.IsEnabled = true;
            repairRing.IsActive = false; repairRing.Visibility = Visibility.Collapsed;
            if (steps is not null)
            {
                int fixedCount = steps.Count(s => s.Outcome == AiRepair.Outcome.Fixed), pending = steps.Count(s => s.Outcome == AiRepair.Outcome.NeedsYou),
                    failed = steps.Count(s => s.Outcome == AiRepair.Outcome.Failed);
                repairStatus.Text = $"Concluído às {DateTime.Now:HH:mm}: {fixedCount} {(fixedCount == 1 ? "item corrigido" : "itens corrigidos")}, {pending} {(pending == 1 ? "pendência sua" : "pendências suas")}, {failed} {(failed == 1 ? "falha" : "falhas")}."
                    + (fixedCount > 0 ? " Se o Codex estava aberto, reinicie-o para ele ler a configuração de novo." : "");
                // Primeiro o que pede atenção, depois o que foi corrigido e, por fim, o que estava em ordem.
                var order = steps.OrderBy(s => s.Outcome switch { AiRepair.Outcome.Failed => 0, AiRepair.Outcome.NeedsYou => 1, AiRepair.Outcome.Fixed => 2, AiRepair.Outcome.Ok => 3, _ => 4 }).ToList();
                for (int i = 0; i < order.Count; i++)
                {
                    if (i > 0) repairResults.Children.Add(new Border { Height = 1, Background = ThemeBrushes.Get("DividerStrokeColorDefaultBrush") });
                    repairResults.Children.Add(RepairRow(order[i]));
                }
            }
            else if (AiRepair.Busy) repairStatus.Text = "Uma correção já está em andamento.";
            RenderReport();
            await RenderCodexAsync();
        }

        repairAll.Click += async (_, _) => await RunRepairAsync(force: false);
        redoAll.Click += async (_, _) => await RunRepairAsync(force: true);
        // Menu "…" da barra › Corrigir problemas das IAs: abre esta página e já roda a correção.
        _startRepair = () => { repairBorder.StartBringIntoView(); _ = RunRepairAsync(force: false); };

        check.Click += async (_, _) => await RunAsync("Verificando… Gemini e Copilot são lidos dos próprios sites (pode levar cerca de 1 minuto).", ["--verificar"], refreshBarLists: true);
        repair.Click += async (_, _) => await RunAsync("Corrigindo: reaplicando a ligação, religando o encaminhador e verificando os modelos…", ["--ligar"], refreshBarLists: true, codexFeedback);
        connect.Click += async (_, _) =>
        {
            if (busy) return;
            bool on = CodexIntegration.LoadState().Ligado;
            if (on && !await ConfirmAsync("Desligar as IAs da barra no Codex?",
                    "O config.toml do Codex volta a ser o de antes, as IAs da barra saem do seletor do Codex e o encaminhador é encerrado. Conversas do Codex que usavam essas IAs precisarão de outro modelo.", "Desligar"))
                return;
            await RunAsync(on ? "Desligando…" : "Ligando no Codex e verificando os modelos…", [on ? "--desligar" : "--ligar"], refreshBarLists: !on, codexFeedback);
            if (CodexIntegration.LoadCommand() is { Ok: true }) { codexFeedback.Text += " Reinicie o Codex para ver a mudança no seletor."; codexFeedback.Visibility = Visibility.Visible; }
        };

        // Estado ao vivo enquanto as Configurações estão abertas (só uma consulta local à porta 11435).
        var timer = DispatcherQueue.CreateTimer();
        timer.Interval = TimeSpan.FromSeconds(15);
        timer.Tick += async (_, _) => { if (!busy && page.Visibility == Visibility.Visible) await RenderCodexAsync(); };
        timer.Start();
        Closed += (_, _) => timer.Stop();
        page.RegisterPropertyChangedCallback(UIElement.VisibilityProperty, async (_, _) =>
        {
            if (page.Visibility != Visibility.Visible || busy) return;
            RenderReport();
            await RenderCodexAsync();
        });
        RenderReport();
        _ = RenderCodexAsync();
    }
}
