using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using WinStatusBar.Core.Ai;
using WinStatusBar.Core.Metrics;
using WinStatusBar.Core.Settings;
using WinStatusBar.Services;

namespace WinStatusBar.Views;

/// <summary>
/// Conteúdo do menu de detalhes de uma cota (Codex ou Claude): cada período com uso, disponível e renovação,
/// janelas adicionais informadas pela conta, créditos e resets avulsos disponíveis. Nada é estimado:
/// o que o provedor não informa aparece como "não informado".
/// </summary>
public static class QuotaDetailsView
{
    public const double ContentWidth = 380;
    private static Brush? _accent;
    private static bool _resetPending;
    internal static bool ResetPending => _resetPending;
    private static string? _resetMessage;
    private static readonly Dictionary<string, string> ResetAttempts = new();

    /// <summary>Cor de destaque do Windows (UISettings), na variante legível para o tema do menu.</summary>
    private static Brush AccentBrush(bool dark)
    {
        try
        {
            var c = new Windows.UI.ViewManagement.UISettings().GetColorValue(
                dark ? Windows.UI.ViewManagement.UIColorType.AccentLight2 : Windows.UI.ViewManagement.UIColorType.AccentDark1);
            return new SolidColorBrush(c);
        }
        catch { return WinStatusBar.Services.ThemeBrushes.Get("AccentFillColorDefaultBrush"); }
    }

    public static UIElement Build(MonitoringHub hub, AppSettings s, AgentKind kind, ElementTheme theme)
    {
        var now = DateTimeOffset.Now;
        bool isCodex = kind == AgentKind.Codex;
        QuotaSnapshot? q = isCodex ? hub.Codex.Quota : hub.Claude.Quota;
        string? error = isCodex ? hub.Codex.LastError : hub.Claude.LastQuotaError;
        int interval = isCodex ? s.CodexQuotaIntervalMin : s.ClaudeQuotaIntervalMin;

        // Largura fixa: o menu é dimensionado na abertura e atualizado a cada 5 s; conteúdo de largura variável ficaria cortado.
        var root = new StackPanel { Spacing = 12, RequestedTheme = theme, Width = ContentWidth };
        _accent = AccentBrush(theme != ElementTheme.Light);

        // Cabeçalho
        var header = new Grid { ColumnSpacing = 10 };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var icon = isCodex ? hub.ViewModel.CodexIcon : hub.ViewModel.ClaudeIcon;
        if (icon is not null)
        {
            var logo = new Image { Width = 20, Height = 20, VerticalAlignment = VerticalAlignment.Center };
            ThemeIcon.SetSource(logo, icon);
            header.Children.Add(logo);
        }
        var titles = new StackPanel();
        titles.Children.Add(Text(isCodex ? global::WinStatusBar.Services.Localization.T("Cotas do Codex") : global::WinStatusBar.Services.Localization.T("Cotas do Claude"), "BodyStrongTextBlockStyle"));
        string plan = q?.PlanType is { Length: > 0 } p ? $"Plano {p}" : isCodex ? global::WinStatusBar.Services.Localization.T("Plano não informado") : global::WinStatusBar.Services.Localization.T("Assinatura Claude (Claude Code e app)");
        titles.Children.Add(Caption(plan));
        Grid.SetColumn(titles, 1);
        header.Children.Add(titles);
        var openLabel = isCodex ? "Abrir Codex" : "Abrir Claude";
        var openContent = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        openContent.Children.Add(new FontIcon { Glyph = "\uE8A7", FontSize = 13 });
        openContent.Children.Add(new TextBlock { Text = "Abrir", FontSize = 12 });
        var open = WinStatusBar.Views.Panels.PanelKit.Small(new Button { Content = openContent, Padding = new Thickness(8, 5, 8, 5),
            VerticalAlignment = VerticalAlignment.Center, MinWidth = 0 });
        ToolTipService.SetToolTip(open, openLabel);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(open, openLabel);
        open.Click += (_, _) => WindowsLauncherService.AgentApp(isCodex);
        Grid.SetColumn(open, 2); header.Children.Add(open);
        root.Children.Add(header);

        if (q?.Primary is null && q?.Secondary is null)
        {
            root.Children.Add(Text(error ?? global::WinStatusBar.Services.Localization.T("Aguardando o primeiro dado da conta…"), "BodyTextBlockStyle", wrap: true));
            root.Children.Add(Footer(q, interval, isCodex));
            return root;
        }

        // Períodos principais e adicionais
        if (q!.Primary is { } p5) root.Children.Add(WindowBlock(PeriodName(p5), p5, now));
        if (q.Secondary is { } p7) root.Children.Add(WindowBlock(PeriodName(p7), p7, now));
        var extras = isCodex ? q.AdditionalWindows : hub.Claude.UsageExtras;
        if (extras.Count > 0)
        {
            root.Children.Add(Divider(theme));
            root.Children.Add(Text(global::WinStatusBar.Services.Localization.T("Outros limites da conta"), "BodyStrongTextBlockStyle"));
            foreach (var w in extras) root.Children.Add(WindowBlock(w.Label, w, now));
        }

        // Uso desta semana por produto (como a página de uso do claude.ai)
        if (!isCodex && hub.Claude.UsageBreakdown.Count > 0)
        {
            root.Children.Add(Divider(theme));
            root.Children.Add(Text(global::WinStatusBar.Services.Localization.T("Uso desta semana por produto"), "BodyStrongTextBlockStyle"));
            foreach (var share in hub.Claude.UsageBreakdown) root.Children.Add(ShareRow(share.Name, share.Percent));
        }

        // Resets e créditos
        root.Children.Add(Divider(theme));
        // Codex informa os créditos de reset; o Claude não informa nada à barra, então lá o título não sugere disponibilidade.
        root.Children.Add(Text(isCodex ? global::WinStatusBar.Services.Localization.T("Resets disponíveis") : "Redefinições", "BodyStrongTextBlockStyle"));
        if (isCodex)
        {
            if (q.ResetCreditsAvailable is null) root.Children.Add(Caption(global::WinStatusBar.Services.Localization.T("Não informado pela conta nesta consulta.")));
            else if (q.ResetCreditsAvailable == 0) root.Children.Add(Caption(global::WinStatusBar.Services.Localization.T("Nenhum reset avulso disponível.")));
            else
            {
                root.Children.Add(Text(global::WinStatusBar.Services.Localization.F("{0} {1} — escolha abaixo qual usar", new object?[] {q.ResetCreditsAvailable,(q.ResetCreditsAvailable == 1 ? global::WinStatusBar.Services.Localization.T("reset disponível") : global::WinStatusBar.Services.Localization.T("resets disponíveis"))}), "BodyTextBlockStyle", wrap: true));
            }
            if (q.CreditsText is not null) root.Children.Add(Caption(q.CreditsText));
            var resetStatus = Caption(_resetMessage ?? "Escolha o crédito que deseja usar. Cada botão corresponde ao reset descrito acima dele.");
            foreach (var credit in q.ResetCredits.Where(c => c.Status is "available" or "unknown"))
            {
                var card = new StackPanel { Spacing = 5 };
                card.Children.Add(Text(credit.Title, "BodyStrongTextBlockStyle", wrap: true));
                if (!string.IsNullOrWhiteSpace(credit.Description)) card.Children.Add(Caption(credit.Description));
                card.Children.Add(Caption(credit.ExpiresAt is { } expires ? $"Válido até {expires.ToLocalTime():dd/MM/yyyy HH:mm}" : "Validade não informada"));
                var reset = WinStatusBar.Views.Panels.PanelKit.Small(new Button { Content = "Usar este reset", IsEnabled = credit.CanConsume(DateTimeOffset.UtcNow) && !_resetPending });
                if (string.IsNullOrWhiteSpace(credit.Id)) card.Children.Add(Caption("A conta não informou o identificador deste crédito. Abra o painel oficial para usá-lo."));
                reset.Click += async (_, _) =>
                {
                    if (_resetPending || credit.Id is not { } id) return;
                    var xamlRoot = root.XamlRoot;
                    var dialog = new ContentDialog { XamlRoot = xamlRoot, RequestedTheme = root.ActualTheme, Title = "Usar este reset do Codex?", Content = credit.Title + "\n" + credit.Description + "\n\nSerá consumido somente este crédito. Não adiciona saldo de API.", PrimaryButtonText = "Usar este reset", CloseButtonText = "Cancelar", DefaultButton = ContentDialogButton.Close };
                    if (await dialog.ShowAsync() != ContentDialogResult.Primary || _resetPending) return;
                    _resetPending = true; reset.IsEnabled = false;
                    if (!ResetAttempts.TryGetValue(id, out var attempt)) ResetAttempts[id] = attempt = Guid.NewGuid().ToString();
                    try
                    {
                        var executable = Core.Ai.Codex.CodexAppServerClient.FindCodexExecutable();
                        if (executable is null) { resetStatus.Text = "Codex não encontrado neste PC."; return; }
                        resetStatus.Text = "Solicitando o reset escolhido…";
                        var result = await new Core.Ai.Codex.CodexAppServerClient().QueryAsync(executable, TimeSpan.FromSeconds(45), CancellationToken.None, attempt, id);
                        _resetMessage = resetStatus.Text = result.Error ?? "Reset aplicado. Cotas atualizadas.";
                        if (result.Error is null || result.ResetDefinitive) ResetAttempts.Remove(id);
                        await hub.Codex.RefreshQuotaNowAsync();
                        await new ContentDialog { XamlRoot = xamlRoot, RequestedTheme = root.ActualTheme, Title = result.Error is null ? "Reset aplicado" : "Resultado da solicitação", Content = resetStatus.Text, CloseButtonText = "Fechar" }.ShowAsync();
                    }
                    catch (Exception ex) { _resetMessage = resetStatus.Text = "Não foi possível concluir: " + ex.Message; }
                    finally { _resetPending = false; }
                };
                card.Children.Add(reset); root.Children.Add(card);
            }
            if (q.ResetCreditsAvailable > 0 && q.ResetCredits.Count == 0)
                root.Children.Add(Caption("A conta informa créditos, mas não seus identificadores. Abra o Codex para escolher um reset."));
            if (q.ResetCredits.Count > 0 || _resetMessage is not null) root.Children.Add(resetStatus);

        }
        else
        {
            root.Children.Add(Caption("O Claude não informa à barra se há redefinição avulsa. Quando houver, ela aparece no painel oficial com a data de expiração. Os períodos acima renovam sozinhos no horário indicado."));
            var claudeReset = WinStatusBar.Views.Panels.PanelKit.Small(new Button { Content = "Conferir no Claude ↗" });
            claudeReset.Click += async (_, _) => await Windows.System.Launcher.LaunchUriAsync(new Uri("https://claude.ai/settings/usage"));
            root.Children.Add(claudeReset);
            root.Children.Add(Caption("Só abre o painel oficial; não usa redefinição nem ativa uso extra pago."));
            if (hub.Claude.UsageExtraText is { } extra) root.Children.Add(Caption(extra));
        }

        if (error is not null) root.Children.Add(Caption(global::WinStatusBar.Services.Localization.T("Última tentativa: ") + error));
        root.Children.Add(Footer(q, interval, isCodex));
        return root;
    }

    private static string PeriodName(QuotaWindow w) => w.WindowMinutes switch
    {
        300 => global::WinStatusBar.Services.Localization.T("Janela de 5 horas"),
        10080 => global::WinStatusBar.Services.Localization.T("Semana (7 dias)"),
        int m when m < 1440 => global::WinStatusBar.Services.Localization.F("Janela de {0} horas", new object?[] {m / 60}),
        int m => global::WinStatusBar.Services.Localization.F("Janela de {0} dias", new object?[] {m / 1440}),
        _ => w.Label,
    };

    private static UIElement WindowBlock(string title, QuotaWindow w, DateTimeOffset now)
    {
        var panel = new StackPanel { Spacing = 4 };
        var top = new Grid { Width = ContentWidth, ColumnSpacing = 12 };
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        top.Children.Add(Text(title, "BodyTextBlockStyle"));
        bool expired = w.IsExpired(now);
        var value = Text(expired ? "renovada" : global::WinStatusBar.Services.Localization.F("{0}% disponível", new object?[] {Math.Round(w.RemainingPercent)}), "BodyStrongTextBlockStyle");
        Grid.SetColumn(value, 1);
        top.Children.Add(value);
        panel.Children.Add(top);

        var bar = new ProgressBar { Minimum = 0, Maximum = 100, Value = expired ? 0 : w.UsedPercent, Height = 4, Width = ContentWidth };
        if (_accent is not null) bar.Foreground = _accent; // sempre a cor de destaque do Windows
        panel.Children.Add(bar);

        string usage = expired ? global::WinStatusBar.Services.Localization.T("O período renovou depois do último dado; aguardando nova leitura.")
            : $"{Math.Round(w.UsedPercent)}% usado · {Formatters.ResetTime(w.ResetsAt)}";
        panel.Children.Add(Caption(usage));
        return panel;
    }

    /// <summary>Linha "Produto ▬▬▬▬ 87%": nome, barra fina na cor de destaque e percentual.</summary>
    private static UIElement ShareRow(string name, double percent)
    {
        var row = new Grid { Width = ContentWidth, ColumnSpacing = 12 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(110) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(40) });
        var label = Text(name, "BodyTextBlockStyle");
        var bar = new ProgressBar { Minimum = 0, Maximum = 100, Value = percent, Height = 4, VerticalAlignment = VerticalAlignment.Center };
        if (_accent is not null) bar.Foreground = _accent;
        var value = Caption($"{Math.Round(percent)}%");
        value.HorizontalAlignment = HorizontalAlignment.Right; value.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(bar, 1); Grid.SetColumn(value, 2);
        row.Children.Add(label); row.Children.Add(bar); row.Children.Add(value);
        return row;
    }

    private static UIElement Footer(QuotaSnapshot? q, int interval, bool isCodex)
    {
        string mode = interval <= 0 ? "tempo real" : $"a cada {interval} min";
        string source = q?.Primary?.Source ?? q?.Secondary?.Source ?? (isCodex ? global::WinStatusBar.Services.Localization.T("Codex") : global::WinStatusBar.Services.Localization.T("Conta Claude"));
        string when = q is null ? "—" : q.ObservedAt.ToLocalTime().ToString("HH:mm:ss");
        return Caption(global::WinStatusBar.Services.Localization.F("Fonte: {0} · último dado às {1} · atualização automática: {2}", new object?[] {source,when,mode}));
    }

    private static TextBlock Text(string text, string style, bool wrap = false) => new()
    {
        Text = text,
        Style = (Style)Application.Current.Resources[style],
        MaxWidth = ContentWidth,
        TextWrapping = wrap ? TextWrapping.Wrap : TextWrapping.NoWrap,
        TextTrimming = TextTrimming.CharacterEllipsis,
    };

    private static TextBlock Caption(string text) => new()
    {
        Text = text,
        MaxWidth = ContentWidth,
        Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
        Opacity = 0.78, // texto secundário que acompanha o tema (claro/escuro) do menu
        TextWrapping = TextWrapping.Wrap,
    };

    private static UIElement Divider(ElementTheme theme) => new Border
    {
        Height = 1,
        Background = WinStatusBar.Services.ThemeBrushes.ForTheme("DividerStrokeColorDefaultBrush", theme),
        Margin = new Thickness(0, 2, 0, 2),
    };
}
