using System.Diagnostics;
using System.Collections.ObjectModel;
using WinStatusBar.ViewModels;
using System.Text;
using System.Text.Json;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.ApplicationModel.DataTransfer;
using WinStatusBar.Core.Ai;
using WinStatusBar.Core.Ai.Codex;
using WinStatusBar.Core.Integration;
using WinStatusBar.Core.Metrics;
using WinStatusBar.Core.Settings;
using WinStatusBar.Services;
using static WinStatusBar.Interop.Win32;

namespace WinStatusBar.Views;

public sealed partial class SettingsWindow : Window
{
    private readonly App _app;
    private bool _loading = true; // eventos ValueChanged disparam durante o InitializeComponent: ignora até carregar

    private DispatcherQueueTimer? _saveTimer, _statusTimer;
    private string _visiblePage = "system";
    private readonly ObservableCollection<ModuleOption> _modules = new();
    private IReadOnlyList<GpuAdapterInfo> _listedGpus = [];
    private IReadOnlyList<NetworkInterfaceInfo> _listedNetworks = [];
    private bool _persistentSensorEnabled;
    private ModuleOption? _draggedModule;
    private Border? _insertionMarker;
    private const string ModuleDragFormat = "WinStatusBar.ModuleKey";

    public SettingsWindow(App app)
    {
        _app = app;
        InitializeComponent();
        Title = global::WinStatusBar.Services.Localization.T("Configurações do WinStatusBar");
        SystemBackdrop = new MicaBackdrop();
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(TitleBarArea);
        var monitor = GetMonitors().FirstOrDefault(m => m.IsPrimary);
        if (monitor is not null)
        {
            double scale = monitor.Dpi / 96.0;
            int width = Math.Min((int)(1080 * scale), monitor.Work.Width - 32);
            int height = Math.Min((int)(780 * scale), monitor.Work.Height - 32);
            AppWindow.MoveAndResize(new Windows.Graphics.RectInt32(monitor.Work.Left + (monitor.Work.Width - width) / 2,
                monitor.Work.Top + (monitor.Work.Height - height) / 2, width, height));
        }
        if (File.Exists(App.IconPath)) AppWindow.SetIcon(App.IconPath);
        string png = Path.Combine(AppContext.BaseDirectory, "Assets", "WinStatusBar.png");
        if (File.Exists(png)) TitleIcon.Source = new BitmapImage(new Uri(png));
        Root.RequestedTheme = App.IsDarkTheme ? ElementTheme.Dark : ElementTheme.Light;
        UpdateCaptionTheme();
        var languageBox = new ComboBox { Style = (Style)Root.Resources["SettingChoice"] };
        string[] codes = ["", "pt-BR", "en-US", "es", "ja-JP", "zh-Hans", "fr-FR"];
        string[] names = [Localization.T("Automático (idioma do Windows)"), global::WinStatusBar.Services.Localization.T("Português (Brasil)"), "English", "Español", "日本語", "简体中文", global::WinStatusBar.Services.Localization.T("Français")];
        for (int i = 0; i < codes.Length; i++) languageBox.Items.Add(new ComboBoxItem { Content = names[i], Tag = codes[i] });
        languageBox.SelectedIndex = Math.Max(0, Array.IndexOf(codes, _app.Settings.Current.Language ?? ""));
        languageBox.SelectionChanged += (_, _) => {
            if (languageBox.SelectedItem is ComboBoxItem selected) {
                _app.Settings.Current.Language = string.IsNullOrEmpty((string)selected.Tag) ? null : (string)selected.Tag;
                _app.Settings.Save();
            }
        };
        // Uma linha como as outras, no grupo "Idioma" (antes era um bloco solto acima de tudo na página Geral).
        SystemExtras.Children.Add(GroupTitle(Localization.T("Idioma")));
        SystemExtras.Children.Add(Group(Row(Localization.T("Idioma do aplicativo"), Localization.T("Reinicie o aplicativo para aplicar o idioma selecionado."), languageBox)));
        LoadValues();
        _loading = true;
        BuildExtraSettings();
        _loading = false;
        _saveTimer = DispatcherQueue.CreateTimer();
        _saveTimer.Interval = TimeSpan.FromMilliseconds(400);
        _saveTimer.IsRepeating = false;
        _saveTimer.Tick += (_, _) => Save();
        _statusTimer = DispatcherQueue.CreateTimer();
        _statusTimer.Interval = TimeSpan.FromSeconds(2);
        _statusTimer.Tick += (_, _) => { if (_visiblePage == "gpu") UpdateSensorStatus(); if (_visiblePage == "ai") UpdateClaudeQuotaText(); };
        _statusTimer.Start();
        _app.Hub.CpuSampled += RefreshHardwareChoices;
        Closed += (_, _) => { _app.Hub.CpuSampled -= RefreshHardwareChoices; FlushPendingSave(); _saveTimer.Stop(); _statusTimer.Stop(); };
        AppWindow.Closing += (_, e) => { if (_closingForExit) return; e.Cancel = true; HideForReuse(); };
    }

    private bool _closingForExit;

    /// <summary>Fecha de verdade (saída da barra), sem passar pelo "fechar só esconde".</summary>
    public void CloseForExit() { _closingForExit = true; Close(); }

    /// <summary>
    /// Fechar esconde, e a mesma janela volta na próxima abertura (como o painel do chat). No WinUI 1.8 uma janela fechada
    /// não é liberada (os eventos Closed e o x:Bind a prendem, microsoft-ui-xaml#9960) e ainda deixa a barra de título para
    /// trás (#12210): cada abertura de Configurações prendia de 10 a 36 MB (medido em 10/11/2026). Escondida, ela para os
    /// temporizadores e deixa de acompanhar a CPU; o estado ao vivo volta a ser lido ao reabrir.
    /// </summary>
    private void HideForReuse()
    {
        FlushPendingSave();
        _statusTimer.Stop();
        _app.Hub.CpuSampled -= RefreshHardwareChoices;
        AppWindow.Hide();
    }

    /// <summary>Itens do menu e do rodapé ("Sobre e suporte").</summary>
    private IEnumerable<NavigationViewItem> NavItems() =>
        Nav.MenuItems.OfType<NavigationViewItem>().Concat(Nav.FooterMenuItems.OfType<NavigationViewItem>());

    /// <summary>
    /// Abre uma página pelo nome. Os nomes das páginas antigas continuam valendo e levam ao grupo que as recebeu:
    /// "diagnostics" e "updates" a Sobre e suporte, "shortcuts" a Barra e aparência, "battery" a Rede e bateria.
    /// </summary>
    public void ShowPage(string tag)
    {
        string page = SettingsNavigation.Resolve(tag);
        if (NavItems().FirstOrDefault(i => (string)i.Tag == page) is not { } item) return;
        if (!ReferenceEquals(Nav.SelectedItem, item)) Nav.SelectedItem = item;
        else if (page == SettingsNavigation.Footer) RefreshDiagnostics();
        FrameworkElement? section = tag switch { "diagnostics" => DiagnosticsPage, "shortcuts" => ShortcutsHost, "battery" => _batterySection, _ => null };
        // Depois da troca de página (que volta a rolagem ao topo), leva o grupo pedido para a vista.
        if (section is not null)
            DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () => section.StartBringIntoView(new BringIntoViewOptions { VerticalAlignmentRatio = 0, AnimationDesired = false }));
    }

    public void ShowAndFocus(bool diagnostics, string? page = null)
    {
        // Reaberta depois de escondida (HideForReuse): volta a acompanhar a CPU, o estado ao vivo e o tema atual.
        _app.Hub.CpuSampled -= RefreshHardwareChoices;
        _app.Hub.CpuSampled += RefreshHardwareChoices;
        _statusTimer.Start();
        Root.RequestedTheme = App.IsDarkTheme ? ElementTheme.Dark : ElementTheme.Light;
        UpdateCaptionTheme();
        FlushPendingSave();
        LoadValues();
        LoadExtraValues();
        AppWindow.Show();
        Activate();
        if (AppWindow.Presenter is OverlappedPresenter p && p.State == OverlappedPresenterState.Minimized) p.Restore();
        SetForegroundWindow(WinRT.Interop.WindowNative.GetWindowHandle(this));
        if (diagnostics) ShowPage("diagnostics");
        else if (page is not null) ShowPage(page);
    }

    private AppSettings S => _app.Settings.Current;

    private static void Select(ComboBox box, string tag) =>
        box.SelectedItem = box.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == tag) ?? box.Items.FirstOrDefault();

    private static string? Tag(ComboBox box) => (box.SelectedItem as ComboBoxItem)?.Tag as string;

    private void LoadValues()
    {
        _loading = true;
        var s = S;
        Select(ThemeBox, s.Theme.ToString());
        Select(BackdropBox, s.Backdrop.ToString());
        TintSlider.Value = s.TintOpacity * 100;
        ScaleSlider.Value = s.Scale * 100;
        ModulesList.ItemsSource = _modules;
        _modules.Clear();
        foreach (var key in s.ModuleOrder)
        {
            var option = new ModuleOption { Key = key, Title = AppSettings.ModuleNames[key], IsShown = !s.HiddenModules.Contains(key) };
            option.PropertyChanged += (_, _) => Save();
            _modules.Add(option);
        }

        StartupSwitch.IsOn = s.StartWithWindows;
        Select(MonitorBox, s.Monitors.ToString());
        MetricsInterval.Value = s.MetricsIntervalMs / 1000.0;
        GpuInterval.Value = s.GpuIntervalMs / 1000.0;
        Select(FullscreenBox, s.Fullscreen.ToString());
        NotificationsSwitch.IsOn = s.Notifications;

        CodexSwitch.IsOn = s.CodexEnabled;
        ClaudeSwitch.IsOn = s.ClaudeEnabled;
        LocalSwitch.IsOn = s.LocalEnabled;
        ChatActivitySwitch.IsOn = s.ChatActivityEnabled;
        LocalProcessesBox.Text = string.Join(", ", s.LocalServerProcesses);
        Select(QuotaModeBox, s.QuotaMode.ToString());
        ShowTaskSwitch.IsOn = s.ShowTask;
        PrivacySwitch.IsOn = s.PrivacyMode;
        SelectInterval(CodexIntervalBox, s.CodexQuotaIntervalMin);
        PinnedBox.Items.Clear();
        PinnedBox.Items.Add(new ComboBoxItem { Content = global::WinStatusBar.Services.Localization.T("Automático (prioridade e atividade recente)"), Tag = "auto" });
        PinnedBox.Items.Add(new ComboBoxItem { Content = global::WinStatusBar.Services.Localization.T("Codex"), Tag = "codex" });
        PinnedBox.Items.Add(new ComboBoxItem { Content = global::WinStatusBar.Services.Localization.T("Claude Code"), Tag = "claude" });
        PinnedBox.Items.Add(new ComboBoxItem { Content = global::WinStatusBar.Services.Localization.T("IA local"), Tag = "local" });
        PinnedBox.Items.Add(new ComboBoxItem { Content = "Lenovo Qira", Tag = "qira" });
        foreach (var session in _app.Hub.Activity.AllSessions())
            PinnedBox.Items.Add(new ComboBoxItem { Content = s.PrivacyMode ? global::WinStatusBar.Services.Localization.F("{0}: sessão ({1})", new object?[] {session.DisplayAgent,session.State.ToPt()}) : $"{session.DisplayAgent}: {Formatters.Truncate(session.DisplayTitle, 50)}", Tag = session.Id });
        if (!string.IsNullOrEmpty(s.PinnedSession) && !PinnedBox.Items.OfType<ComboBoxItem>().Any(i => (string)i.Tag == s.PinnedSession))
            PinnedBox.Items.Add(new ComboBoxItem { Content = global::WinStatusBar.Services.Localization.T("Sessão escolhida anteriormente (sem atividade recente)"), Tag = s.PinnedSession });
        Select(PinnedBox, s.PinnedSession ?? "auto");
        UpdateClaudeIntegrationText();

        RefreshHardwareChoices(loadSettings: true);
        Select(GpuPercentBox, s.GpuPercent.ToString());
        SharedGpuSwitch.IsOn = s.ShowSharedGpuMemory;
        GpuUtilSwitch.IsOn = s.GpuUtilization;
        TemperaturesSwitch.IsOn = s.ShowTemperatures;
        _loading = false;
        UpdateDependentControls();
        _persistentSensorEnabled = SensorPermissionTask.IsEnabled();
        UpdateSensorStatus();
    }

    private void RefreshHardwareChoices() => RefreshHardwareChoices(loadSettings: false);

    private void RefreshHardwareChoices(bool loadSettings)
    {
        var gpus = _app.Hub.LastGpu?.Adapters ?? _app.Hub.Gpu.Adapters;
        var networks = NetworkMetricsProvider.SelectableInterfaces(_app.Hub.LastNetwork?.AllInterfaces ?? []);
        bool previousLoading = _loading;
        _loading = true;
        try
        {
            if (loadSettings || !_listedGpus.SequenceEqual(gpus))
            {
                string preferred = loadSettings ? _app.Settings.Current.GpuAdapter ?? "" : Tag(GpuBox) ?? "";
                _listedGpus = gpus.ToArray();
                GpuBox.Items.Clear();
                GpuBox.Items.Add(new ComboBoxItem { Content = Localization.T("Automático (preferir GPU dedicada)"), Tag = "" });
                foreach (var a in gpus)
                    GpuBox.Items.Add(new ComboBoxItem { Content = $"{a.Name} — {Formatters.Gb(a.DedicatedVideoMemory)} GB dedicada{(a.IsIntegrated ? " (integrada)" : "")}", Tag = a.Name });
                if (preferred.Length > 0 && !gpus.Any(a => a.Name.Equals(preferred, StringComparison.OrdinalIgnoreCase)))
                    GpuBox.Items.Add(new ComboBoxItem { Content = preferred + " — indisponível", Tag = preferred, IsEnabled = false });
                Select(GpuBox, preferred);
            }
            if (loadSettings || !_listedNetworks.SequenceEqual(networks))
            {
                string preferred = loadSettings ? _app.Settings.Current.NetworkInterface ?? "" : Tag(NetworkBox) ?? "";
                _listedNetworks = networks.ToArray();
                NetworkBox.Items.Clear();
                NetworkBox.Items.Add(new ComboBoxItem { Content = Localization.T("Automático · conexões ativas"), Tag = "" });
                foreach (var nic in networks)
                {
                    var item = new ComboBoxItem { Content = $"{nic.Alias} · {(nic.IsUp ? "conectada" : "sem conexão")}", Tag = nic.Alias };
                    ToolTipService.SetToolTip(item, nic.Description);
                    NetworkBox.Items.Add(item);
                }
                if (preferred.Length > 0 && !networks.Any(n => n.Alias.Equals(preferred, StringComparison.OrdinalIgnoreCase)))
                    NetworkBox.Items.Add(new ComboBoxItem { Content = preferred + " — indisponível", Tag = preferred, IsEnabled = false });
                Select(NetworkBox, preferred);
            }
        }
        finally { _loading = previousLoading; }
    }

    private void UpdateClaudeIntegrationText()
    {
        bool installed = ClaudeIntegrationInstaller.IsInstalled();
        ClaudeIntegrationText.Text = installed
            ? global::WinStatusBar.Services.Localization.T("Atividade das sessões: integração configurada. Isso não confirma o recebimento das cotas; veja a consulta da conta acima.")
            : global::WinStatusBar.Services.Localization.T("Atividade das sessões: integração não configurada. As cotas da conta podem funcionar independentemente desta opção.");
        RemoveClaudeButton.IsEnabled = installed;
        UpdateClaudeQuotaText();
    }

    private static void SelectInterval(ComboBox box, int minutes)
    {
        string tag = minutes.ToString();
        if (!box.Items.OfType<ComboBoxItem>().Any(i => (string)i.Tag == tag))
            box.Items.Add(new ComboBoxItem { Content = global::WinStatusBar.Services.Localization.F("A cada {0} minutos", new object?[] {minutes}), Tag = tag });
        Select(box, tag);
    }

    /// <summary>
    /// Uma linha de atualização para todas as IAs: a escolha vale para Codex e Claude (tempo real = a cada resposta) e,
    /// no mínimo a cada 5 min, para Gemini, Copilot e Qira; abaixo, o último valor lido de cada uma.
    /// </summary>
    private void UpdateClaudeQuotaText()
    {
        int minutes = IntervalOf(CodexIntervalBox);
        string Line(string name, WinStatusBar.Core.Ai.QuotaSnapshot? q, string? error, bool on) => !on ? $"{name}: desativado"
            : q?.Primary is null ? $"{name}: {(error is { Length: > 0 } ? error : "aguardando a primeira leitura")}"
            : $"{name}: 5h {Math.Round(100 - q.Primary.UsedPercent):0} %" + (q.Secondary is { } w ? $" · 7d {Math.Round(100 - w.UsedPercent):0} %" : "") + $" · lido às {q.ObservedAt.LocalDateTime:HH:mm}";
        var lines = new List<string>
        {
            Line("Codex", _app.Hub.Codex.Quota, _app.Hub.Codex.LastError is { } codexError ? WinStatusBar.Core.Ai.AiErrorExplainer.QuotaError(WinStatusBar.Core.Ai.AgentPrompt.Target.Codex, codexError) : null, CodexSwitch.IsOn),
            Line("Claude", _app.Hub.Claude.Quota, _app.Hub.Claude.LastQuotaError is { } claudeError ? WinStatusBar.Core.Ai.AiErrorExplainer.QuotaError(WinStatusBar.Core.Ai.AgentPrompt.Target.Claude, claudeError) : null, ClaudeSwitch.IsOn),
        };
        if (GoogleUsageSession.Current is { SignedIn: true } g)
            lines.Add($"Gemini: atual {(g.CurrentPercent is { } c ? $"{Math.Round(100 - c):0} %" : "—")} · semana {(g.WeeklyPercent is { } wk ? $"{Math.Round(100 - wk):0} %" : "—")} · lido às {g.Updated.LocalDateTime:HH:mm}");
        if (MicrosoftCreditsSession.Current is { SignedIn: true, Total: > 0 } m)
            lines.Add($"Copilot: {m.Remaining} de {m.Total} créditos · lido às {m.Updated.LocalDateTime:HH:mm}");
        if (WinStatusBar.Core.Ai.QiraBridge.CurrentQuota is { Limit: > 0 } qira)
            lines.Add($"Qira: {qira.Limit - qira.Used} de {qira.Limit} créditos · lido às {qira.Updated.LocalDateTime:HH:mm}");
        lines.Add(minutes <= 0 ? "Tempo real: Codex e Claude a cada resposta; Gemini, Copilot e Qira a cada 5 min (lidos nas páginas das contas)."
            : $"A cada {minutes} min" + (minutes < 5 ? "; Gemini, Copilot e Qira a cada 5 min (mínimo)." : " para todas as IAs."));
        CodexQuotaText.Text = string.Join("\n", lines);
        RefreshClaudeButton.IsEnabled = ClaudeSwitch.IsOn || CodexSwitch.IsOn;
    }

    private static int IntervalOf(ComboBox box) => int.TryParse(Tag(box), out var m) ? m : 0;

    private async void OnRefreshClaudeUsage(object sender, RoutedEventArgs e)
    {
        RefreshClaudeButton.IsEnabled = false;
        // Na linha visível (antes ia para a linha escondida do intervalo do Claude, e o clique parecia não fazer nada).
        CodexQuotaText.Text = Localization.T("Consultando as cotas…");
        try { await _app.Hub.RefreshNowAsync(); }
        catch (Exception ex) { App.Log(global::WinStatusBar.Services.Localization.T("Consulta Claude: ") + ex.GetType().Name); }
        finally { UpdateClaudeQuotaText(); }
    }

    private void UpdateDependentControls()
    {
        TintSlider.IsEnabled = Tag(BackdropBox) is not ("Solid" or "Adaptive");
        ShowTaskSwitch.IsEnabled = !PrivacySwitch.IsOn;
        LocalProcessesBox.IsEnabled = LocalSwitch.IsOn;
        // O intervalo e a forma de mostrar valem para todas as IAs (Gemini, Copilot e Qira também têm cotas): ficam sempre
        // disponíveis. "Atualizar cotas agora" consulta só o Codex e o Claude.
        RefreshClaudeButton.IsEnabled = ClaudeSwitch.IsOn || CodexSwitch.IsOn;
        GpuUtilSwitch.IsEnabled = !LocalSwitch.IsOn && Tag(GpuPercentBox) != "Utilization";
        ScaleValueText.Text = $"{ScaleSlider.Value:0}%";
        TintValueText.Text = TintSlider.Value == 0 ? global::WinStatusBar.Services.Localization.T("Padrão do Windows") : $"{TintSlider.Value:0}%";
    }

    private void UpdateSensorStatus()
    {
        if (!TemperaturesSwitch.IsOn)
        {
            TemperatureStatusText.Text = global::WinStatusBar.Services.Localization.T("Temperaturas ocultas");
            SensorAccessText.Text = global::WinStatusBar.Services.Localization.T("Ative a opção acima para acompanhar as leituras disponíveis.");
            SensorAccessButton.Visibility = Visibility.Collapsed;
            DisableSensorAccessButton.Visibility = _persistentSensorEnabled ? Visibility.Visible : Visibility.Collapsed;
            return;
        }
        if (PackageContext.IsPackaged)
        {
            // Versão Microsoft Store: sem tarefa elevada; mostra só as leituras acessíveis sem administrador.
            var packagedReading = _app.Hub.LastTemperature;
            SensorAccessText.Text = packagedReading?.Cpu is not null
                ? global::WinStatusBar.Services.Localization.F("Fonte da CPU: {0}. Atualização a cada 10 segundos.", new object?[] {packagedReading.CpuSource})
                : global::WinStatusBar.Services.Localization.T("Na versão da Microsoft Store, sensores que exigem permissão de administrador (como o da Lenovo) não são usados. As demais leituras continuam disponíveis.");
            SensorAccessButton.Visibility = DisableSensorAccessButton.Visibility = Visibility.Collapsed;
            return;
        }
        var reading = _app.Hub.LastTemperature;
        TemperatureStatusText.Text = reading is null ? global::WinStatusBar.Services.Localization.T("Aguardando a primeira leitura…")
            : global::WinStatusBar.Services.Localization.F("CPU: {0}     GPU: {1}", new object?[] {(reading.Cpu is double c ? $"{c:0} °C" : global::WinStatusBar.Services.Localization.T("indisponível")),(reading.Gpu is double g ? $"{g:0} °C" : global::WinStatusBar.Services.Localization.T("indisponível"))});
        using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
        bool admin = new System.Security.Principal.WindowsPrincipal(identity).IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
        SensorAccessText.Text = reading?.Cpu is not null ? global::WinStatusBar.Services.Localization.F("Fonte da CPU: {0}. Atualização a cada 10 segundos.", new object?[] {reading.CpuSource})
            : admin ? global::WinStatusBar.Services.Localization.T("A permissão atual vale somente para esta execução. Autorize abaixo para manter o acesso após reiniciar.")
            : global::WinStatusBar.Services.Localization.T("O sensor Lenovo já presente neste computador exige permissão de administrador. O botão reinicia somente o WinStatusBar após autorização do Windows; não instala componentes.");
        SensorAccessButton.Content = _persistentSensorEnabled ? global::WinStatusBar.Services.Localization.T("Renovar autorização permanente…") : "Autorizar leitura permanentemente…";
        SensorAccessButton.Visibility = _persistentSensorEnabled && reading?.Cpu is not null ? Visibility.Collapsed : Visibility.Visible;
        DisableSensorAccessButton.Visibility = _persistentSensorEnabled ? Visibility.Visible : Visibility.Collapsed;
        if (_persistentSensorEnabled) SensorAccessText.Text = global::WinStatusBar.Services.Localization.T("Autorização permanente ativa. A leitura continua após reiniciar o app ou o Windows, sem elevar a barra inteira.");
    }

    private void QueueSave()
    {
        if (_loading || _saveTimer is null) return;
        UpdateDependentControls();
        SaveStatusIcon.Glyph = "\uE895";
        SaveStatusText.Text = global::WinStatusBar.Services.Localization.T("Aplicando alteração…");
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    private void FlushPendingSave()
    {
        if (_saveTimer?.IsRunning == true) { _saveTimer.Stop(); Save(); }
    }

    private void Save(bool updatePinned = false)
    {
        if (_loading) return;
        _saveTimer?.Stop();
        if (double.IsNaN(MetricsInterval.Value) || double.IsNaN(GpuInterval.Value))
        {
            SaveStatusIcon.Glyph = "\uE7BA";
            SaveStatusText.Text = global::WinStatusBar.Services.Localization.T("Preencha os intervalos com um número válido para salvar.");
            return;
        }
        var s = JsonSerializer.Deserialize(JsonSerializer.Serialize(S, AppSettingsContext.Default.AppSettings), AppSettingsContext.Default.AppSettings)!;
        s.Theme = Enum.Parse<ThemeMode>(Tag(ThemeBox) ?? "System");
        s.Backdrop = Enum.Parse<BackdropKind>(Tag(BackdropBox) ?? "MicaAlt");
        s.TintOpacity = TintSlider.Value / 100.0;
        s.Scale = ScaleSlider.Value / 100.0;
        s.ModuleOrder = _modules.Select(m => m.Key).ToList();
        s.HiddenModules = _modules.Where(m => m.IsShown != true).Select(m => m.Key).ToHashSet();

        s.StartWithWindows = StartupSwitch.IsOn;
        s.Monitors = Enum.Parse<MonitorMode>(Tag(MonitorBox) ?? "Primary");
        if (!double.IsNaN(MetricsInterval.Value)) s.MetricsIntervalMs = (int)Math.Round(MetricsInterval.Value * 1000);
        if (!double.IsNaN(GpuInterval.Value)) s.GpuIntervalMs = (int)Math.Round(GpuInterval.Value * 1000);
        s.Fullscreen = Enum.Parse<FullscreenBehavior>(Tag(FullscreenBox) ?? "HideBehind");
        s.NetworkInterface = string.IsNullOrEmpty(Tag(NetworkBox)) ? null : Tag(NetworkBox);
        s.Notifications = NotificationsSwitch.IsOn;

        s.CodexEnabled = CodexSwitch.IsOn;
        s.ClaudeEnabled = ClaudeSwitch.IsOn;
        s.LocalEnabled = LocalSwitch.IsOn;
        s.ChatActivityEnabled = ChatActivitySwitch.IsOn;
        s.LocalServerProcesses = LocalProcessesBox.Text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        s.QuotaMode = Enum.Parse<QuotaDisplay>(Tag(QuotaModeBox) ?? "Remaining");
        s.ShowTask = ShowTaskSwitch.IsOn;
        s.PrivacyMode = PrivacySwitch.IsOn;
        if (updatePinned) s.PinnedSession = Tag(PinnedBox) ?? "auto";
        s.CodexQuotaIntervalMin = IntervalOf(CodexIntervalBox);
        s.ClaudeQuotaIntervalMin = s.CodexQuotaIntervalMin; // uma escolha para todas as IAs

        s.GpuAdapter = string.IsNullOrEmpty(Tag(GpuBox)) ? null : Tag(GpuBox);
        s.GpuPercent = Enum.Parse<GpuPercentMode>(Tag(GpuPercentBox) ?? "Vram");
        s.ShowSharedGpuMemory = SharedGpuSwitch.IsOn;
        s.GpuUtilization = GpuUtilSwitch.IsOn;
        s.ShowTemperatures = TemperaturesSwitch.IsOn;

        try
        {
            _app.Settings.Save(s);
            _app.ApplySettings();
            Root.RequestedTheme = App.IsDarkTheme ? ElementTheme.Dark : ElementTheme.Light;
        UpdateCaptionTheme();
            UpdateDependentControls();
            SaveStatusIcon.Glyph = "\uE73E";
            SaveStatusText.Text = global::WinStatusBar.Services.Localization.F("Alterações salvas · {0:HH:mm}", new object?[] {DateTime.Now});
            if (StartupService.IsEnabled() != s.StartWithWindows)
                SaveStatusText.Text = global::WinStatusBar.Services.Localization.T("Preferências salvas, mas não foi possível atualizar a inicialização do Windows.");
        }
        catch (Exception ex)
        {
            SaveStatusIcon.Glyph = "\uE7BA";
            SaveStatusText.Text = global::WinStatusBar.Services.Localization.T("Não foi possível salvar ou aplicar a alteração. Tente novamente.");
            App.Log(global::WinStatusBar.Services.Localization.T("Falha ao salvar configurações: ") + ex.Message);
        }
    }

    private void OnChanged(object sender, RoutedEventArgs e)
    {
        Save();
        if (!_loading && ReferenceEquals(sender, PrivacySwitch)) LoadValues();
    }
    private void OnChanged(object sender, SelectionChangedEventArgs e) => Save(ReferenceEquals(sender, PinnedBox));
    private void OnSliderChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e) => QueueSave();
    private void OnNumberChanged(NumberBox sender, NumberBoxValueChangedEventArgs args) => QueueSave();
    private void OnModulesReordered(ListViewBase sender, DragItemsCompletedEventArgs args) { Save(); }
    private void OnLocalProcessesChanged(object sender, TextChangedEventArgs args) => QueueSave();

    private void OnModuleDragStarting(UIElement sender, DragStartingEventArgs e)
    {
        string? key = (sender as FrameworkElement)?.Tag as string;
        _draggedModule = _modules.FirstOrDefault(m => m.Key == key);
        if (_draggedModule is null) { e.Cancel = true; return; }
        e.Data.SetData(ModuleDragFormat, _draggedModule.Key);
        e.Data.Properties.Title = _draggedModule.Title;
        e.Data.RequestedOperation = DataPackageOperation.Move;
    }

    private void OnModuleDragOver(object sender, DragEventArgs e)
    {
        if (_draggedModule is null || !e.DataView.Contains(ModuleDragFormat)) return;
        e.AcceptedOperation = DataPackageOperation.Move;
        e.DragUIOverride.Caption = global::WinStatusBar.Services.Localization.T("Mover para esta posição");
        e.Handled = true;
        var row = (Grid)sender;
        var marker = row.Children.OfType<Border>().First();
        if (_insertionMarker != marker && _insertionMarker is not null) _insertionMarker.Visibility = Visibility.Collapsed;
        _insertionMarker = marker;
        marker.VerticalAlignment = e.GetPosition(row).Y < row.ActualHeight / 2 ? VerticalAlignment.Top : VerticalAlignment.Bottom;
        marker.Visibility = Visibility.Visible;
    }

    private void OnModuleDragLeave(object sender, DragEventArgs e)
    {
        ((Grid)sender).Children.OfType<Border>().First().Visibility = Visibility.Collapsed;
    }

    private void OnModuleDrop(object sender, DragEventArgs e)
    {
        if (_draggedModule is null || !e.DataView.Contains(ModuleDragFormat)) return;
        var row = (Grid)sender;
        var target = _modules.FirstOrDefault(m => m.Key == row.Tag as string);
        if (target is null) return;
        int from = _modules.IndexOf(_draggedModule);
        int destination = _modules.IndexOf(target) + (e.GetPosition(row).Y >= row.ActualHeight / 2 ? 1 : 0);
        if (from < destination) destination--;
        if (from != destination) { _modules.Move(from, destination); Save(); }
        e.AcceptedOperation = DataPackageOperation.Move;
        e.Handled = true;
        ClearModuleDrag();
    }

    private void ClearModuleDrag()
    {
        if (_insertionMarker is not null) _insertionMarker.Visibility = Visibility.Collapsed;
        _insertionMarker = null;
        _draggedModule = null;
    }
    private void OnModuleDragCompleted(UIElement sender, DropCompletedEventArgs e) => ClearModuleDrag();

    private void OnRootSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (Nav is not null) Nav.PaneDisplayMode = e.NewSize.Width < 860 ? NavigationViewPaneDisplayMode.LeftCompact : NavigationViewPaneDisplayMode.Left;
    }

    private void UpdateCaptionTheme()
    {
        bool dark = Root.RequestedTheme == ElementTheme.Dark;
        var foreground = dark ? Microsoft.UI.Colors.White : Microsoft.UI.Colors.Black;
        AppWindow.TitleBar.ButtonForegroundColor = foreground;
        AppWindow.TitleBar.ButtonInactiveForegroundColor = dark ? Microsoft.UI.Colors.LightGray : Microsoft.UI.Colors.DimGray;
        AppWindow.TitleBar.ButtonBackgroundColor = Microsoft.UI.Colors.Transparent;
        AppWindow.TitleBar.ButtonInactiveBackgroundColor = Microsoft.UI.Colors.Transparent;
        AppWindow.TitleBar.ButtonHoverForegroundColor = foreground;
        AppWindow.TitleBar.ButtonPressedForegroundColor = foreground;
        AppWindow.TitleBar.ButtonHoverBackgroundColor = dark ? Windows.UI.Color.FromArgb(255, 60, 60, 60) : Windows.UI.Color.FromArgb(255, 225, 225, 225);
        AppWindow.TitleBar.ButtonPressedBackgroundColor = dark ? Windows.UI.Color.FromArgb(255, 50, 50, 50) : Windows.UI.Color.FromArgb(255, 210, 210, 210);
    }

    private void OnSettingRowSizeChanged(object sender, SizeChangedEventArgs e)
    {
        var row = (Grid)sender;
        bool narrow = e.NewSize.Width < 540;
        Grid.SetColumnSpan((FrameworkElement)row.Children[0], narrow ? 2 : 1);
        Grid.SetColumn((FrameworkElement)row.Children[1], narrow ? 0 : 1);
        Grid.SetRow((FrameworkElement)row.Children[1], narrow ? 1 : 0);
        Grid.SetColumnSpan((FrameworkElement)row.Children[1], narrow ? 2 : 1);
        row.ColumnDefinitions[1].Width = narrow ? new GridLength(0) : new GridLength(240);
    }

    private void OnNavSelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (_loading) return;
        FlushPendingSave();
        string tag = (args.SelectedItem as NavigationViewItem)?.Tag as string ?? "system";
        _visiblePage = tag;
        foreach (var pair in _organizedPages) pair.Value.Visibility = pair.Key == tag ? Visibility.Visible : Visibility.Collapsed;
        AppearancePage.Visibility = tag == "appearance" ? Visibility.Visible : Visibility.Collapsed;
        SystemPage.Visibility = tag == "system" ? Visibility.Visible : Visibility.Collapsed;
        AiPage.Visibility = tag == "ai" ? Visibility.Visible : Visibility.Collapsed;
        GpuPage.Visibility = tag == "gpu" ? Visibility.Visible : Visibility.Collapsed;
        CommandPage.Visibility = tag == "command" ? Visibility.Visible : Visibility.Collapsed;
        ClipPage.Visibility = tag == "clip" ? Visibility.Visible : Visibility.Collapsed;
        if (tag == "clip") UpdateClipStats();
        // O diagnóstico é um grupo de "Sobre e suporte": o relatório é refeito ao entrar na página.
        if (tag == SettingsNavigation.Footer) RefreshDiagnostics();
        if (tag == "gpu") { _persistentSensorEnabled = SensorPermissionTask.IsEnabled(); UpdateSensorStatus(); }
        if (tag == "ai") UpdateClaudeQuotaText();
        if (tag == "connections") _refreshPower?.Invoke();
        PageScroll.ChangeView(null, 0, null, true);
    }

    private void OnInstallClaude(object sender, RoutedEventArgs e)
    {
        try
        {
            string collector = PackageContext.CollectorPath(AppContext.BaseDirectory);
            ClaudeIntegrationInstaller.Install(collector);
            S.ClaudeIntegrationDeclined = false;
            _app.Settings.Save();
            UpdateClaudeIntegrationText();
        }
        catch (Exception ex) { ClaudeIntegrationText.Text = "Falha: " + ex.Message; }
    }

    private void OnRemoveClaude(object sender, RoutedEventArgs e)
    {
        // Remoção explícita: a barra não reinstala a integração sozinha depois disso.
        try { ClaudeIntegrationInstaller.Uninstall(); S.ClaudeIntegrationDeclined = true; _app.Settings.Save(); UpdateClaudeIntegrationText(); }
        catch (Exception ex) { ClaudeIntegrationText.Text = "Falha: " + ex.Message; }
    }

    private async void OnEnableSensors(object sender, RoutedEventArgs e) => await ChangeSensorPermissionAsync(true);
    private async void OnDisableSensors(object sender, RoutedEventArgs e) => await ChangeSensorPermissionAsync(false);

    private async Task ChangeSensorPermissionAsync(bool enable)
    {
        FlushPendingSave();
        SensorAccessButton.IsEnabled = DisableSensorAccessButton.IsEnabled = false;
        try
        {
            string helper = Path.Combine(AppContext.BaseDirectory, "Sensors", SensorPermissionTask.ExecutableName);
            if (!File.Exists(helper)) throw new FileNotFoundException(global::WinStatusBar.Services.Localization.T("Componente de sensores ausente no pacote do WinStatusBar."));
            var info = new ProcessStartInfo(helper) { UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden };
            info.ArgumentList.Add(enable ? "--enable" : "--disable");
            using var process = Process.Start(info);
            if (process is null) return;
            await process.WaitForExitAsync();
            _persistentSensorEnabled = SensorPermissionTask.IsEnabled();
            if (process.ExitCode != 0 || _persistentSensorEnabled != enable)
            { SensorAccessText.Text = global::WinStatusBar.Services.Localization.T("O Windows não concluiu a alteração da autorização. Nenhuma proteção do sistema foi desativada."); return; }
            await _app.Hub.RefreshTemperaturesAsync();
            UpdateSensorStatus();
            if (!enable) SensorAccessText.Text = global::WinStatusBar.Services.Localization.T("Autorização permanente removida. A tarefa de leitura foi encerrada e excluída.");
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        { SensorAccessText.Text = global::WinStatusBar.Services.Localization.T("Autorização cancelada. As permissões existentes foram mantidas."); }
        catch (Exception ex) { SensorAccessText.Text = global::WinStatusBar.Services.Localization.T("Não foi possível alterar a autorização: ") + ex.Message; }
        finally { SensorAccessButton.IsEnabled = DisableSensorAccessButton.IsEnabled = true; }
    }

    private void OnRefreshDiagnostics(object sender, RoutedEventArgs e) => RefreshDiagnostics();

    private void OnCopyDiagnostics(object sender, RoutedEventArgs e)
    {
        var dp = new DataPackage();
        dp.SetText(DiagnosticsText.Text);
        Clipboard.SetContent(dp);
    }

    private void RefreshDiagnostics() => DiagnosticsText.Text = BuildDiagnostics(_app);

    public static string BuildDiagnostics(App app)
    {
        var hub = app.Hub;
        var sb = new StringBuilder();
        using var me = Process.GetCurrentProcess();
        sb.AppendLine($"WinStatusBar {typeof(App).Assembly.GetName().Version} — {DateTime.Now:dd/MM/yyyy HH:mm:ss}");
        sb.AppendLine(global::WinStatusBar.Services.Localization.F("Executável: {0}", new object?[] {App.ExePath}));
        sb.AppendLine($"Dados: {SettingsService.DataDir}");
        sb.AppendLine(global::WinStatusBar.Services.Localization.F("Processo: CPU total {0:0.0}s · memória privada {1:0} MB · working set {2:0} MB · custo do ciclo {3:0.0} ms", new object?[] {me.TotalProcessorTime.TotalSeconds,me.PrivateMemorySize64 / 1048576.0,me.WorkingSet64 / 1048576.0,hub.LastCycleCost.TotalMilliseconds}));
        sb.AppendLine();
        // Resumo do que foi detectado automaticamente neste computador (útil ao instalar em outros PCs).
        var (codexOk, claudeOk, localOk) = hub.InstalledAgents();
        sb.AppendLine(global::WinStatusBar.Services.Localization.T("— Detecção deste PC"));
        sb.AppendLine(global::WinStatusBar.Services.Localization.F("  CPU: {0} · frequência atual {1}", new object?[] {hub.LastCpu?.Source ?? "—",(hub.LastCpu?.CurrentGHz is null ? global::WinStatusBar.Services.Localization.T("indisponível (contadores ausentes)") : global::WinStatusBar.Services.Localization.T("disponível"))}));
        sb.AppendLine(global::WinStatusBar.Services.Localization.F("  GPU: {0} · contadores de memória {1}", new object?[] {(hub.LastGpu?.Adapter is { } ga ? $"{ga.Name} ({(ga.IsIntegrated ? "integrada — mostra memória compartilhada" : "dedicada")})" : "nenhum adaptador DXGI"),(hub.LastGpu?.DedicatedUsedBytes is null && hub.LastGpu?.SharedUsedBytes is null ? "ausentes" : "presentes")}));
        sb.AppendLine(global::WinStatusBar.Services.Localization.F("  Temperatura: CPU {0} · GPU {1}", new object?[] {(hub.LastTemperature?.Cpu is null ? "sem sensor (oculta)" : hub.LastTemperature.CpuSource),(hub.LastTemperature?.Gpu is null ? "sem sensor (oculta)" : "nvidia-smi")}));
        sb.AppendLine(global::WinStatusBar.Services.Localization.F("  IAs: Codex {0} · Claude Code {1} · IA local {2}", new object?[] {(codexOk ? "instalado" : global::WinStatusBar.Services.Localization.T("não encontrado")),(claudeOk ? "instalado" : global::WinStatusBar.Services.Localization.T("não encontrado")),(localOk ? hub.Local.ServerDisplay ?? "instalada (parada)" : global::WinStatusBar.Services.Localization.T("não encontrada"))}));
        sb.AppendLine();
        sb.AppendLine("— Barras / AppBar");
        foreach (var b in app.Bars)
            sb.AppendLine(global::WinStatusBar.Services.Localization.F("  {0}: registrada={1} rect={2} dpi={3} reinícios do Explorer={4} erro={5}", new object?[] {b.MonitorDevice,b.AppBar?.IsRegistered,b.AppBar?.CurrentRect,b.AppBar?.CurrentDpi,b.AppBar?.ExplorerRestarts,b.AppBar?.LastError ?? "—"}));
        foreach (var m in GetMonitors())
            sb.AppendLine($"  Monitor {m.Device}{(m.IsPrimary ? " (principal)" : string.Empty)}: {m.Bounds} trabalho={m.Work} dpi={m.Dpi} ({m.Dpi * 100 / 96}%)");
        sb.AppendLine();
        sb.AppendLine("— Hardware");
        if (hub.LastTemperature is { } temp) sb.AppendLine(global::WinStatusBar.Services.Localization.F("  Temperaturas: CPU {0} °C · fonte {1} · GPU {2} °C", new object?[] {temp.Cpu?.ToString("0.0") ?? "—",temp.CpuSource,temp.Gpu?.ToString("0.0") ?? "—"}));
        if (hub.LastCpu is { } c) sb.AppendLine(global::WinStatusBar.Services.Localization.F("  CPU: {0} · {1} · {2} · fonte {3}", new object?[] {c.ProcessorName,Formatters.Percent(c.UsagePercent),Formatters.GHz(c.CurrentGHz),c.Source}));
        if (hub.LastRam is { } r) sb.AppendLine(global::WinStatusBar.Services.Localization.F("  RAM: {0} · instalada {1} GB · carga {2}%", new object?[] {Formatters.UsedTotal(r.UsedBytes, r.TotalBytes),Formatters.Gb(r.InstalledBytes),r.MemoryLoad}));
        if (hub.LastGpu is { } g)
        {
            sb.AppendLine(global::WinStatusBar.Services.Localization.F("  GPU monitorada: {0} ({1}) · dedicada {2} GB · compartilhada {3} GB · uso {4}%", new object?[] {g.Adapter?.Name,g.Adapter?.LuidKey,(g.DedicatedUsedBytes is null ? "—" : Formatters.Gb(g.DedicatedUsedBytes.Value)),(g.SharedUsedBytes is null ? "—" : Formatters.Gb(g.SharedUsedBytes.Value)),(g.UtilizationPercent is null ? "—" : g.UtilizationPercent.Value.ToString("0.0"))}));
            foreach (var a in g.Adapters) sb.AppendLine($"    adaptador: {a.Name} · dedicada {Formatters.Gb(a.DedicatedVideoMemory)} GB · compartilhada {Formatters.Gb(a.SharedSystemMemory)} GB · {a.LuidKey}{(a.IsIntegrated ? " · integrada" : string.Empty)}");
        }
        if (hub.LastNetwork is { } n) sb.AppendLine(global::WinStatusBar.Services.Localization.F("  Rede: ↑ {0} ↓ {1} · interfaces: {2}", new object?[] {Formatters.Speed(n.UploadBytesPerSec),Formatters.Speed(n.DownloadBytesPerSec),string.Join(", ", n.ActiveInterfaces)}));
        sb.AppendLine();
        sb.AppendLine(global::WinStatusBar.Services.Localization.T("— Codex"));
        sb.AppendLine(global::WinStatusBar.Services.Localization.F("  Executável: {0} · CODEX_HOME: {1}", new object?[] {hub.Codex.Executable ?? global::WinStatusBar.Services.Localization.T("não encontrado"),CodexAppServerClient.CodexHome()}));
        sb.AppendLine(global::WinStatusBar.Services.Localization.F("  Última consulta oficial: {0} · erro: {1}", new object?[] {hub.Codex.LastQuotaQuery?.ToString("HH:mm:ss") ?? "—",hub.Codex.LastError ?? "—"}));
        sb.AppendLine($"  Cota: 5h {Describe(hub.Codex.Quota?.Primary)} · 7d {Describe(hub.Codex.Quota?.Secondary)} · plano {hub.Codex.Quota?.PlanType ?? "—"}");
        sb.AppendLine();
        sb.AppendLine(global::WinStatusBar.Services.Localization.T("— Claude Code"));
        sb.AppendLine(global::WinStatusBar.Services.Localization.F("  Consulta da conta: {0} · próxima {1:HH:mm:ss} · erro {2}", new object?[] {hub.Claude.LastQuotaQuery?.ToLocalTime().ToString("HH:mm:ss") ?? "—",hub.Claude.NextQuotaQuery.ToLocalTime(),hub.Claude.LastQuotaError ?? "—"}));
        sb.AppendLine(global::WinStatusBar.Services.Localization.F("  Tipo de instalação: {0} · coletor: {1}", new object?[] {(PackageContext.IsPackaged ? "pacote MSIX (Microsoft Store)" : "instalador tradicional"),PackageContext.CollectorPath(AppContext.BaseDirectory)}));
        sb.AppendLine(global::WinStatusBar.Services.Localization.F("  Integração (hooks + statusLine):{0} · {1}", new object?[] {(ClaudeIntegrationInstaller.IsInstalled() ? "instalada" : global::WinStatusBar.Services.Localization.T("não instalada")),ClaudeIntegrationInstaller.SettingsFile}));
        sb.AppendLine(global::WinStatusBar.Services.Localization.F("  Pipe: {0} · mensagens {1} · último evento {2} · erro {3}", new object?[] {Core.Ipc.PipeNames.Events,hub.Pipe.Received,hub.Claude.LastEventAt?.ToString("HH:mm:ss") ?? "—",hub.Pipe.LastError ?? "—"}));
        sb.AppendLine($"  Cota: 5h {Describe(hub.Claude.Quota?.Primary)} · 7d {Describe(hub.Claude.Quota?.Secondary)}");
        sb.AppendLine();
        sb.AppendLine("— Modelos locais");
        sb.AppendLine($"  Servidor: {hub.Local.ServerDisplay ?? "nenhum detectado"} · {hub.Local.ServerImagePath ?? "—"}");
        sb.AppendLine(global::WinStatusBar.Services.Localization.F("  Carga do servidor: GPU {0} · CPU {1} núcleos · últimos clientes: {2}", new object?[] {(hub.Local.LastGpu is null ? "—" : hub.Local.LastGpu.Value.ToString("0") + "%"),hub.Local.LastCpuCores?.ToString("0.00") ?? "—",(hub.Local.LastClients.Count == 0 ? "—" : string.Join(", ", hub.Local.LastClients))}));
        sb.AppendLine();
        sb.AppendLine(global::WinStatusBar.Services.Localization.T("— Sessões"));
        foreach (var s in hub.Activity.AllSessions())
            sb.AppendLine($"  [{s.Kind}] {s.State.ToPt(),-22} {Formatters.Truncate(s.DisplayTitle, 50),-52} {s.LastActivity.ToLocalTime():HH:mm:ss} {Formatters.Truncate(s.Caption, 50)}");
        sb.AppendLine(global::WinStatusBar.Services.Localization.F("  Prioritária: {0} {1}", new object?[] {hub.Priority?.Kind,hub.Priority?.Id}));
        return sb.ToString();
    }

    private static string Describe(QuotaWindow? w) => w is null ? global::WinStatusBar.Services.Localization.T("indisponível")
        : $"{w.UsedPercent:0}% usado ({Formatters.ResetTime(w.ResetsAt)}; {w.Source})";
}
