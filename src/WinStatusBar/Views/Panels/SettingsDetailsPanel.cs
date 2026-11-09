using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

using WinStatusBar.Core.Metrics;
using WinStatusBar.Services;

namespace WinStatusBar.Views.Panels;

internal sealed class SettingsDetailsPanel : IDetailsPanel
{
    private readonly TextBlock _status = new() { FontSize = 14, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _detail = PanelKit.Caption("", 11);
    private readonly FontIcon _statusIcon = new() { Glyph = "\uE895", FontSize = 48 };
    private readonly Border _badge = new() { Width = 22, Height = 22, CornerRadius = new CornerRadius(11), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom };
    private readonly TextBlock _badgeText = new() { FontSize = 15, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold };
    private readonly WindowsUpdateService _updates = WindowsUpdateService.Instance;
    private readonly WindowsUpdateManualController _manual;
    private readonly DispatcherQueue _dispatcher = DispatcherQueue.GetForCurrentThread();
    private readonly Button _restart, _check, _installAll;
    private IReadOnlyList<WindowsUpdateEntry> _available = [];
    private readonly TextBlock _installFeedback = PanelKit.Caption("", 11);
    
    private readonly StackPanel _details = new() { Spacing = 2 };
    private readonly StackPanel _restartConfirmation = new() { Spacing = 6, Visibility = Visibility.Collapsed };
    private readonly WindowsAppearanceControls _appearance = new();
    private readonly DispatcherQueueTimer _timer;
    private bool _open, _disposed, _restartBusy;
    private string? _renderSignature;
    private bool _updatesExpanded = true;
    public FrameworkElement Content { get; }

    public SettingsDetailsPanel(Action<string> open)
    {
        _manual = new(_updates.Read, _updates.Check);
        _updates.Changed += UpdateChanged;
        _installFeedback.Visibility = Visibility.Collapsed;
        var body = new StackPanel { Spacing = 8 };
        body.Children.Add(PanelKit.Header(Localization.T("Configurações"), PanelKit.Caption(Localization.T("Windows e personalização")),
            ActionButton("\uE8A7", "Abrir", () => open("ms-settings:"), "Abrir configurações gerais do Windows")));
        var update = new StackPanel { Spacing = 8, Margin = new Thickness(0, 4, 0, 4) };
        update.Children.Add(new TextBlock { Text = "Windows Update", FontSize = 24, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Margin = new Thickness(0, 4, 0, 12) });
        var statusRow = new Grid { ColumnSpacing = 12, Margin = new Thickness(0, 4, 0, 4) };
        statusRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        statusRow.ColumnDefinitions.Add(new ColumnDefinition());
        statusRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var iconHost = new Grid { Width = 58, Height = 58 };
        iconHost.Children.Add(_statusIcon); _badge.Child = _badgeText; iconHost.Children.Add(_badge);
        statusRow.Children.Add(iconHost);
        var statusText = new StackPanel { Spacing = 2 };
        statusText.Children.Add(_status); statusText.Children.Add(_detail);
        Grid.SetColumn(statusText, 1); statusRow.Children.Add(statusText);
        _installAll = PanelKit.Small(new Button { Content = "Baixar e instalar tudo", FontSize = 11, Padding = new Thickness(10, 8, 10, 8), VerticalAlignment = VerticalAlignment.Center, Visibility = Visibility.Collapsed });
        if (Application.Current.Resources.TryGetValue("AccentButtonStyle", out var accentStyle)) _installAll.Style = (Style)accentStyle;
        _installAll.Click += (_, _) => _ = InstallAsync(_available);
        Grid.SetColumn(_installAll, 2); statusRow.Children.Add(_installAll);
        update.Children.Add(statusRow);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(0, 4, 0, 0) };
        _check = ActionButton("\uE72C", "Verificar se há atualizações", () => { _manual.Check(); Render(_updates.Read()); });
        actions.Children.Add(_check);
        var openUpdate = new HyperlinkButton { Content = "Abrir no Windows", FontSize = 12, MinHeight = 32, Padding = new Thickness(10, 6, 10, 6) };
        openUpdate.Click += (_, _) => open("ms-settings:windowsupdate"); actions.Children.Add(openUpdate);
        _restart = ActionButton("\uE777", "Reiniciar agora", () => Toggle(_restartConfirmation));
        _restart.Style = (Style)Application.Current.Resources["AccentButtonStyle"]; // destaque, como "Reiniciar agora" no Windows
        _restart.Visibility = Visibility.Collapsed;
        PanelKit.Small(_restart);
        // Mesmo lugar do "Baixar e instalar tudo" (só um aparece por vez), como o botão principal no Windows Update.
        Grid.SetColumn(_restart, 2); statusRow.Children.Add(_restart);
        _restartConfirmation.Children.Add(PanelKit.Caption(Localization.T("Reiniciar este computador agora? Salve seus arquivos antes de continuar."), 12));
        var confirmActions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        confirmActions.Children.Add(ActionButton("\uE777", "Reiniciar agora", () => _ = RestartAsync()));
        confirmActions.Children.Add(ActionButton("\uE711", "Cancelar", () => _restartConfirmation.Visibility = Visibility.Collapsed));
        _restartConfirmation.Children.Add(confirmActions);
        update.Children.Add(_restartConfirmation); update.Children.Add(_details);
        update.Children.Add(_installFeedback);
        update.Children.Add(actions);
        body.Children.Add(update);
        body.Children.Add(_appearance.Colors);
        body.Children.Add(_appearance.Themes);
        var installedApps = new InstalledAppsPanel();
        var appsHeader = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, MinHeight = 30 };
        appsHeader.Children.Add(new FontIcon { Glyph = "\uE71D", FontSize = 18 });
        appsHeader.Children.Add(new TextBlock { Text = "Aplicativos instalados", FontSize = 14, VerticalAlignment = VerticalAlignment.Center });
        var appsSection = new Expander { Header = appsHeader, Content = installedApps, IsExpanded = false, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch };
        appsSection.Expanding += async (_, _) => await installedApps.RefreshAsync();
        body.Children.Add(appsSection);
        var displayRow = new Grid { ColumnSpacing = 10, MinHeight = 30 };
        displayRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        displayRow.ColumnDefinitions.Add(new ColumnDefinition());
        displayRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        displayRow.Children.Add(new FontIcon { Glyph = "\uE7F4", FontSize = 18, VerticalAlignment = VerticalAlignment.Center });
        var displayText = new TextBlock { Text = "Tela", FontSize = 14, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(displayText, 1); displayRow.Children.Add(displayText);
        var displayChevron = new FontIcon { Glyph = "\uE76C", FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) };
        Grid.SetColumn(displayChevron, 2); displayRow.Children.Add(displayChevron);
        var display = new Button
        {
            Content = displayRow, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch,
            MinHeight = appsSection.MinHeight > 0 ? appsSection.MinHeight : 48, Padding = new Thickness(16, 9, 16, 9),
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(display, "Configurações de tela do Windows");
        display.Click += (_, _) => open("ms-settings:display");
        body.Children.Add(display);
        var scroll = PanelKit.Body(body);
        // WinUI scrollbars overlay the viewport. Keep a dedicated lane outside
        // the cards, including when the scrollbar expands on pointer hover.
        scroll.Width = 480;
        body.Width = 460;
        body.HorizontalAlignment = HorizontalAlignment.Left;
        body.Margin = new Thickness(0, 0, 20, 0);
        Content = scroll;
        _timer = DispatcherQueue.GetForCurrentThread().CreateTimer();
        _timer.Interval = TimeSpan.FromSeconds(1); // além dos eventos nativos do orquestrador
        _timer.Tick += (_, _) => Render(_updates.Read());
    }

    private static void Toggle(FrameworkElement element) => element.Visibility = element.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
    public void Start()
    {
        if (_disposed || _open) return;
        _open = true;
        _appearance.Refresh();
        Render(_manual.Open()); // Passive snapshot only; never Search/StartScan.
        _timer.Start();
    }
    public void Stop() { _open = false; _timer.Stop(); _restartConfirmation.Visibility = Visibility.Collapsed; }
    public void Dispose() { Stop(); _disposed = true; _updates.Changed -= UpdateChanged; }
    private void UpdateChanged() => _dispatcher.TryEnqueue(() => { if (_open && !_disposed) Render(_updates.Read()); });

    private void Render(WindowsUpdateStatus result)
    {
        // Como nas Configurações do Windows, reinicialização pendente tem prioridade: o orquestrador continua
        // "trabalhando" enquanto a atualização espera o reinício, e isso não é instalação em andamento.
        bool restartPending = result.State == WindowsUpdateState.RestartRequired;
        bool scanning = _updates.IsScanning && !restartPending;
        bool working = _updates.IsWorking && !restartPending;
        bool requesting = _updates.IsRequesting && !restartPending;
        bool active = scanning || working || requesting;
        string signature = $"{result.State}|{result.LastSearch}|{result.Error}|{scanning}|{working}|{requesting}|{_updatesExpanded}|" + string.Join(";", result.Pending.Select(e => $"{e.UpdateId}|{e.Title}|{e.Status}|{e.ErrorCode}|{e.Downloaded}|{_updates.Activity(e.UpdateId)}"));
        if (signature == _renderSignature) return;
        _renderSignature = signature;
        _status.Text = scanning ? "Verificando se há atualizações…" : WindowsUpdatePresentation.Heading(result);
        if (working) _status.Text = "Atualizações em andamento";
        if (requesting && !scanning && !working) _status.Text = "Solicitando início…";
        // Mesma frase das Configurações quando há horário ativo; a estimativa em minutos não é exposta pela API nativa.
        var activeHours = restartPending ? WindowsUpdateService.ActiveHours() : null;
        _detail.Text = result.Error ?? (restartPending
            ? activeHours is { } hours ? $"O dispositivo será reiniciado fora do horário ativo ({hours.Start}h às {hours.End}h)."
                : "Reinicie para concluir a instalação das atualizações."
            : result.LastSearch is DateTime last ? $"Última verificação: {last:g}" : "Clique para verificar se há atualizações.");
        _statusIcon.Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0, 153, 224));
        _statusIcon.Glyph = "\uE895";
        _badge.Visibility = active ? Visibility.Collapsed : Visibility.Visible;
        bool success = result.State == WindowsUpdateState.UpToDate && result.Error is null;
        _badge.Background = new SolidColorBrush(success ? Windows.UI.Color.FromArgb(255, 15, 145, 72) : Windows.UI.Color.FromArgb(255, 255, 185, 0));
        _badgeText.Text = success ? "✓" : "!"; // o Windows mostra "!" amarelo também para reinicialização necessária
        _badgeText.Foreground = new SolidColorBrush(success ? Microsoft.UI.Colors.White : Microsoft.UI.Colors.Black);
        _available = result.Pending.Where(x => x.CanInstall).ToArray();
        _installAll.Visibility = _available.Count > 0 && result.State != WindowsUpdateState.RestartRequired ? Visibility.Visible : Visibility.Collapsed;
        _installAll.IsEnabled = !active;
        _installAll.Content = result.State == WindowsUpdateState.Failed ? "Tentar novamente" : _available.All(x => x.Downloaded) ? "Instalar tudo" : "Baixar e instalar tudo";
        _check.IsEnabled = !active;
        _restart.Visibility = WindowsUpdatePresentation.NeedsRestart(result) ? Visibility.Visible : Visibility.Collapsed;
        if (result.State != WindowsUpdateState.RestartRequired) _restartConfirmation.Visibility = Visibility.Collapsed;
        _installFeedback.Visibility = Visibility.Collapsed;
        _details.Children.Clear();
        if (result.Pending.Count > 0)
        {
            var heading = new StackPanel { Spacing = 3 };
            heading.Children.Add(new TextBlock { Text = "Atualizações disponíveis", FontSize = 12 });
            heading.Children.Add(PanelKit.Caption("Essas atualizações serão concluídas automaticamente.", 10));
            _details.Children.Add(Card(heading, new CornerRadius(5, 5, 0, 0)));
            foreach (var entry in result.Pending) AddEntry(entry, active, working);
        }
        // Like Settings, the up-to-date state has no empty placeholder card.
    }
    private static Border Card(UIElement content, CornerRadius radius)
        => new() { Child = content, Padding = new Thickness(12), CornerRadius = radius, Background = PanelKit.Res("CardBackgroundFillColorDefaultBrush") };

    private void AddEntry(WindowsUpdateEntry entry, bool active, bool working)
    {
        bool rebootPending = entry.Status == "Reinicialização pendente";
        var row = new Grid { ColumnSpacing = 12, MinHeight = 30 };
        row.ColumnDefinitions.Add(new ColumnDefinition());
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var text = new StackPanel { Spacing = 3, VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock { Text = entry.Title, FontSize = 11, TextWrapping = TextWrapping.Wrap });
        if (entry.ErrorCode is int error) text.Children.Add(PanelKit.Caption(WindowsUpdatePresentation.ErrorMessage(error), 10));
        else if (!rebootPending && entry.Status is not ("Download" or "Install" or "Commit" or "None")) text.Children.Add(PanelKit.Caption(entry.Status, 10));
        row.Children.Add(text);
        var activity = _updates.Activity(entry.UpdateId);
        var side = new StackPanel { Spacing = 5, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
        // Estado à direita, como nas Configurações: "Reinicialização pendente" no lugar de "Preparando…".
        if (rebootPending) side.Children.Add(PanelKit.Caption("Reinicialização pendente", 10));
        else if (activity is not null && working)
        {
            side.MinWidth = 90;
            side.Children.Add(PanelKit.Caption(activity.Label, 10));
            side.Children.Add(new ProgressBar { Minimum = 0, Maximum = 100, Value = activity.Percent ?? 0, IsIndeterminate = activity.Percent is null, Height = 3 });
        }
        else if (active) side.Children.Add(PanelKit.Caption(_updates.IsScanning ? "Verificando…" : working ? "Preparando…" : "Aguardando início…", 10));
        else if (_available.Count == 1 && entry.CanInstall)
        {
            var install = new HyperlinkButton { Content = entry.ActionLabel, FontSize = 11, MinWidth = 0, HorizontalAlignment = HorizontalAlignment.Right, Padding = new Thickness(0, 6, 0, 6) };
            install.Click += (_, _) => _ = InstallAsync(_available);
            side.Children.Add(install);
        }
        else side.Children.Add(PanelKit.Caption(entry.Downloaded ? "Instalação pendente" : "Download pendente", 10));
        Grid.SetColumn(side, 1); row.Children.Add(side);
        _details.Children.Add(Card(row, new CornerRadius(0, 0, 5, 5)));
    }
    private async Task InstallAsync(IReadOnlyList<WindowsUpdateEntry> entries)
    {
        if (entries.Count == 0 || _updates.IsRequesting || _updates.IsWorking || _updates.IsScanning) return;
        await _updates.InstallAsync();
        if (_open && !_disposed) Render(_updates.Read());
    }
    private async Task RestartAsync()
    {
        if (_restartBusy) return;
        _restartBusy = true; _restartConfirmation.IsHitTestVisible = false;
        try { await WindowsAppearancePreferences.RestartAsync(); }
        catch (Exception ex) { _detail.Text = ex.Message; }
        finally { _restartBusy = false; _restartConfirmation.IsHitTestVisible = true; _restartConfirmation.Visibility = Visibility.Collapsed; }
    }

    internal static Button ActionButton(string glyph, string title, Action action, string? accessibleName = null)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        row.Children.Add(new FontIcon { Glyph = glyph, FontSize = 14 });
        row.Children.Add(new TextBlock { Text = Localization.T(title), FontSize = 12 });
        var button = new Button { Content = row, MinHeight = 32, Padding = new Thickness(10, 6, 10, 6), HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Left };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(button, Localization.T(accessibleName ?? title));
        if (accessibleName is not null) ToolTipService.SetToolTip(button, Localization.T(accessibleName));
        button.Click += (_, _) => action();
        return button;
    }
}







