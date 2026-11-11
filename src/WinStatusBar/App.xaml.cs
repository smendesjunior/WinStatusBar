using System.Diagnostics;
using System.Security.Principal;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Windows.UI.ViewManagement;
using WinStatusBar.Core.Integration;
using WinStatusBar.Core.Settings;
using WinStatusBar.Services;
using WinStatusBar.Views;
using static WinStatusBar.Interop.Win32;

namespace WinStatusBar;

public partial class App : Application
{
    public static new App Current => (App)Application.Current;
    public static string ExePath => Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "WinStatusBar.exe");
    public static string IconPath => Path.Combine(AppContext.BaseDirectory, "Assets", "WinStatusBar.ico");
    public static bool IsDarkTheme { get; private set; } = true;

    private static readonly Lock LogGate = new();
    private Mutex? _singleInstance;
    private EventWaitHandle? _exitSignal, _showSignal;
    private readonly List<BarWindow> _bars = new();
    private SettingsWindow? _settingsWindow;
    private TrayIconService? _tray;
    private UISettings? _ui;
    private DispatcherQueue? _dispatcher;
    private DispatcherQueueTimer? _displayDebounce;
    private bool _exiting;
    /// <summary>Prazo da saída: passado isso, a vigia remove a AppBar e encerra o processo à força.</summary>
    private static readonly TimeSpan ExitTimeout = TimeSpan.FromSeconds(3);
    private readonly ExitWatchdog _exitWatchdog;

    public SettingsService Settings { get; } = new();
    public MonitoringHub Hub { get; private set; } = null!;
    /// <summary>Continuar pela API (interno: o serviço é interno).</summary>
    internal CodexApiResumeService ApiResume { get; private set; } = null!;
    public IReadOnlyList<BarWindow> Bars => _bars;

    public App()
    {
        InitializeComponent();
        _exitWatchdog = new ExitWatchdog(ExitTimeout, ForceExit);
        UnhandledException += (_, e) => { Log(global::WinStatusBar.Services.Localization.T("Exceção não tratada: ") + e.Exception); e.Handled = true; };
        AppDomain.CurrentDomain.UnhandledException += (_, e) => { Log("Falha fatal: " + e.ExceptionObject); ReleaseAppBars(); };
        AppDomain.CurrentDomain.ProcessExit += (_, _) => ReleaseAppBars();
    }

    private static string InstanceSuffix
    {
        get { try { return WindowsIdentity.GetCurrent().User?.Value ?? Environment.UserName; } catch { return Environment.UserName; } }
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        var cmd = Environment.GetCommandLineArgs();
        int replaceIndex = Array.IndexOf(cmd, "--replace-process");
        if (replaceIndex >= 0 && replaceIndex + 1 < cmd.Length && int.TryParse(cmd[replaceIndex + 1], out int oldPid))
        {
            try { using var previous = Process.GetProcessById(oldPid); previous.WaitForExit(10000); }
            catch (ArgumentException) { }
        }
        string suffix = InstanceSuffix;
        _singleInstance = new Mutex(true, "WinStatusBar.Instance." + suffix, out bool created);
        if (cmd.Contains("--exit"))
        {
            // Usado pelo desinstalador: pede ao processo em execução que encerre de forma limpa (remove a AppBar).
            if (EventWaitHandle.TryOpenExisting("WinStatusBar.Exit." + suffix, out var ev)) { ev.Set(); ev.Dispose(); }
            Exit();
            return;
        }
        if (!created)
        {
            if (EventWaitHandle.TryOpenExisting("WinStatusBar.Show." + suffix, out var ev)) { ev.Set(); ev.Dispose(); }
            Exit();
            return;
        }

#if DEBUG
        if (Environment.GetEnvironmentVariable("WINSTATUSBAR_VERIFY_SYSTEM_PANEL") == "1")
        {
            _dispatcher = DispatcherQueue.GetForCurrentThread();
            Settings.Load();
            Localization.Initialize(Settings.Current.Language);
            UpdateTheme();
            var panel = new Views.Panels.SettingsDetailsPanel(uri => Services.WindowsLauncherService.Shell(uri));
            var host = new Microsoft.UI.Xaml.Controls.Border
            {
                Padding = new Thickness(16), Child = panel.Content,
                RequestedTheme = IsDarkTheme ? ElementTheme.Dark : ElementTheme.Light,
                Background = ThemeBrushes.Get("SolidBackgroundFillColorBaseBrush"),
            };
            var preview = new Window { Title = "WinStatusBar · painel de configurações", Content = host };
            preview.AppWindow.Resize(new Windows.Graphics.SizeInt32(810, 1100));
            preview.Closed += (_, _) => { panel.Dispose(); Exit(); };
            preview.Activate(); panel.Start();
            return;
        }
#endif
        _dispatcher = DispatcherQueue.GetForCurrentThread();
        Settings.Load();
        AppUpdater.Start();
        if (Settings.RestoredFromCloud) Log("Preferências restauradas do OneDrive (instalação limpa).");
        else if (Settings.PendingRestore) StartLateRestoreWatch();
        else if (Settings.Current.SyncSettings && SettingsService.CloudSettingsPath is { } cloud && !File.Exists(cloud)) { try { Settings.Save(); } catch { } } // primeira cópia no OneDrive
        Localization.Initialize(Settings.Current.Language);
        ThemeBrushes.VerifyPalette();
        UpdateTheme();
        _ui = new UISettings();
        _ui.ColorValuesChanged += (_, _) => _dispatcher.TryEnqueue(() => { UpdateTheme(); foreach (var b in _bars) b.ApplyAppearance(); Hub.QueueAgentRefresh(); });

        Hub = new MonitoringHub(_dispatcher, Settings);
        Hub.ChatGptChat.CodexThreadTitles = () => Hub.Codex.KnownTitles;
        Hub.ChatGptChat.CodexBlockedReason = () => Hub.Codex.Quota is { } q && q.IsBlocked(DateTimeOffset.Now) ? Core.Ai.Codex.CodexQuotaText.ShortReason(q, DateTimeOffset.Now) : null;
        Hub.Briefing.SkipInference = s => s.Kind == Core.Ai.AgentKind.Codex && Hub.Codex.Quota?.IsBlocked(DateTimeOffset.Now) == true;
        Hub.Start();
        ApiResume = new CodexApiResumeService(Hub, Settings, _dispatcher);
        // A continuação pela API roda com a tela apagada e lê os tokens da conversa: com uma em aberto, o Codex segue sendo lido.
        Hub.Codex.ReconcileWhileAway = () => ApiResume?.Supervisor.HasUnfinished == true;
        ApiResume.Start();
        StartApiUsageAutoRefresh();
        CopilotWebSession.Install(_dispatcher!);
        QiraHubSession.Install();
        WinStatusBar.Core.Ai.AgentPrompt.OllamaKeepAlive = () => Settings.Current.LocalModelKeepAlive;
        GoogleUsageSession.Install(_dispatcher!);
        GeminiWebSession.Install(_dispatcher!);
        MicrosoftCreditsSession.Install(_dispatcher!);
        CodexBridgeHost.Start(); // Claude, Gemini, Copilot e Qira no seletor de modelos do Codex
        CodexIasComponent.EnsureRunning(); // IAs configuradas no seletor do Codex (encaminhador, com a ligação ligada)
        CodexIasComponent.AutoEnableOnce(); // primeira vez com esta versão num PC com o Codex: liga sozinha
        StartClaudeIntegrationWatch();
        // Área de transferência e emojis (Win+V / Win+.), antes das barras: o botão só aparece se estiver ativa.
        if (Settings.Current.ClipboardEnabled)
            try { Clip.ClipHost.ApplySettings(Settings.Current); } catch (Exception ex) { Log(global::WinStatusBar.Services.Localization.T("Área de transferência: ") + ex); }
        StartCommandHotkey();

        LoadShortcutIcons();
        RebuildBars();
        // Ponto de partida da comparação: sem ele, o primeiro aviso de tela depois de abrir (mesmo sem mudança nenhuma)
        // fechava e recriava todas as barras, cancelando a resposta de IA em andamento.
        _lastMonitorKey = MonitorKey(GetMonitors());
        if (Environment.GetEnvironmentVariable("WINSTATUSBAR_VERIFY_FOLDER") is string verifyFolder && Directory.Exists(verifyFolder))
        {
            var folderPreview = Views.Panels.RecentFolderFlyout.Create("Arquivos", () => verifyFolder);
            folderPreview.ShowAt(null);
        }

        Core.Presence.Changed += away => Log(away
            ? "Tela apagada ou sessão bloqueada: leituras e animações em pausa."
            : "Tela acesa: leituras e animações retomadas.");
        _tray = new TrayIconService(IconPath);
        MemoryTrim.Start(_dispatcher);
        _tray.HardwareChanged += Hub.HardwareChanged;
        _tray.CommandInvoked += OnTrayCommand;
        _tray.BalloonClicked += () => _dispatcher.TryEnqueue(() => { var click = _balloonClick; _balloonClick = null; click?.Invoke(); });
        _tray.MenuRequested += (x, y) => _dispatcher.TryEnqueue(() =>
            _bars.FirstOrDefault()?.ShowTrayMenu(x, y, OnTrayCommand));

        _exitSignal = new EventWaitHandle(false, EventResetMode.AutoReset, "WinStatusBar.Exit." + suffix);
        _showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, "WinStatusBar.Show." + suffix);
        // A vigia da saída começa já na thread do sinal: vale mesmo se a thread da interface não responder.
        StartSignalWatcher(_exitSignal, () => ExitApp(), onSignal: _exitWatchdog.Start);
        StartSignalWatcher(_showSignal, () => ShowSettings());

        _displayDebounce = _dispatcher.CreateTimer();
        _displayDebounce.Interval = TimeSpan.FromSeconds(1);
        _displayDebounce.IsRepeating = false;
        _displayDebounce.Tick += (_, _) => RebuildBarsIfMonitorsChanged();

#if DEBUG
        if (Environment.GetEnvironmentVariable("WINSTATUSBAR_VERIFY_SETTINGS") == "1") _dispatcher.TryEnqueue(() => ShowSettings());
        if (Environment.GetEnvironmentVariable("WINSTATUSBAR_VERIFY_API_VIEW") == "1")
            _dispatcher.TryEnqueue(() => {
                var preview = new Microsoft.UI.Xaml.Window { Title = "API · verificação visual", Content = new Microsoft.UI.Xaml.Controls.ScrollViewer { Content = new Microsoft.UI.Xaml.Controls.Border { Padding = new Microsoft.UI.Xaml.Thickness(16), RequestedTheme = Microsoft.UI.Xaml.ElementTheme.Dark, Background = WinStatusBar.Services.ThemeBrushes.Get("SolidBackgroundFillColorBaseBrush"), Child = WinStatusBar.Views.ApiUsageView.Build() } } };
                preview.AppWindow.Resize(new Windows.Graphics.SizeInt32(650, 1080)); preview.Activate();
            });
        if (Environment.GetEnvironmentVariable("WINSTATUSBAR_VERIFY_DASHBOARD") == "1")
            _dispatcher.TryEnqueue(() => WinStatusBar.Views.OpenAiDashboardView.ShowWindow());
        if (Environment.GetEnvironmentVariable("WINSTATUSBAR_VERIFY_CARET") == "1")
        {
            var caretCheck = _dispatcher.CreateTimer();
            caretCheck.Interval = TimeSpan.FromMilliseconds(500);
            caretCheck.IsRepeating = false;
            caretCheck.Tick += (_, _) => _bars.FirstOrDefault()?.FocusCommandBar();
            caretCheck.Start();
        }
#endif
        QueueStartupSync(); // fora da thread da interface: a consulta ao Agendador de Tarefas atrasava a primeira pintura
        Log($"Iniciado. Monitores: {GetMonitors().Count}. Tema escuro: {IsDarkTheme}.");
        if (!cmd.Contains("--startup") && !LaunchedByStartupTask() &&!File.Exists(Path.Combine(SettingsService.DataDir, ".welcomed")))
        {
            try { Directory.CreateDirectory(SettingsService.DataDir); File.WriteAllText(Path.Combine(SettingsService.DataDir, ".welcomed"), DateTime.Now.ToString("s")); } catch { }
            if (Settings.Current.Notifications) _tray.ShowBalloon("WinStatusBar", global::WinStatusBar.Services.Localization.T("A barra está ativa. Clique com o botão direito no ícone da bandeja para opções."));
        }
    }

    /// <summary>
    /// Instalação limpa sem backup ainda visível (OneDrive sincronizando após formatar): procura a cada 30 s por até
    /// 30 min. Ao achar, restaura as preferências e reconstrói as barras; sem backup, passa a gravar a cópia normal.
    /// </summary>
    private void StartLateRestoreWatch()
    {
        Log("Instalação limpa: aguardando o backup do OneDrive (até 30 min); nada é gravado no OneDrive até lá.");
        _ = Task.Run(async () =>
        {
            while (!_exiting && Settings.PendingRestore)
            {
                await Task.Delay(TimeSpan.FromSeconds(30));
                bool restored = false;
                try { restored = Settings.TryLateRestore(); } catch (Exception ex) { Log("Restauração do OneDrive: " + ex.Message); }
                if (!restored) continue;
                _dispatcher?.TryEnqueue(() =>
                {
                    Log("Preferências restauradas do OneDrive (backup encontrado depois da abertura).");
                    Localization.Initialize(Settings.Current.Language);
                    UpdateTheme();
                    RebuildBars();
                    if (Settings.Current.Notifications) _tray?.ShowBalloon("WinStatusBar", global::WinStatusBar.Services.Localization.T("Suas preferências foram restauradas do OneDrive."));
                });
                return;
            }
            try { Settings.TryLateRestore(); } catch { } // fim da espera: grava a primeira cópia
        });
    }

    /// <summary>
    /// Adaptativo para outros PCs: se o Claude Code for instalado depois da barra, ativa a integração
    /// (hooks + statusLine com backup e preservação do que existir). Verifica ao iniciar e a cada 10 minutos.
    /// Não age se o usuário removeu a integração ou se a barra não estiver instalada (execução de desenvolvimento).
    /// </summary>
    private void StartClaudeIntegrationWatch()
    {
        string collector = PackageContext.CollectorPath(AppContext.BaseDirectory);
        bool packaged = PackageContext.IsPackaged;
        bool installedCopy = packaged || AppContext.BaseDirectory.StartsWith(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs"), StringComparison.OrdinalIgnoreCase);
        if (!installedCopy || !File.Exists(collector)) return;
        _ = Task.Run(async () =>
        {
            while (!_exiting)
            {
                try
                {
                    var s = Settings.Current;
                    // Na versão da Store a integração é ativada pelo usuário nas configurações (opt-in).
                    if (!packaged && s.ClaudeEnabled && !s.ClaudeIntegrationDeclined
                        && Directory.Exists(ClaudeIntegrationInstaller.ClaudeConfigDir()) && !ClaudeIntegrationInstaller.IsInstalled())
                        Log(global::WinStatusBar.Services.Localization.T("Claude Code detectado: ") + ClaudeIntegrationInstaller.Install(collector));
                    // A statusLine anterior encadeada foi desinstalada (ex.: TrafficMonitor): deixa de referenciá-la.
                    else if (ClaudeIntegrationInstaller.IsInstalled() && ClaudeIntegrationInstaller.HasBrokenOriginal())
                        Log("statusLine anterior removida do encadeamento: " + ClaudeIntegrationInstaller.Install(collector));
                }
                catch (Exception ex) { Log(global::WinStatusBar.Services.Localization.T("Integração automática do Claude: ") + ex.Message); }
                await Task.Delay(TimeSpan.FromMinutes(10));
            }
        });
    }

    private static bool LaunchedByStartupTask()
    {
        if (!PackageContext.IsPackaged) return false;
        try { return Microsoft.Windows.AppLifecycle.AppInstance.GetCurrent().GetActivatedEventArgs().Kind == Microsoft.Windows.AppLifecycle.ExtendedActivationKind.StartupTask; }
        catch { return false; }
    }

    private void StartSignalWatcher(EventWaitHandle handle, Action action, Action? onSignal = null)
    {
        var t = new Thread(() =>
        {
            while (!_exiting)
            {
                if (handle.WaitOne(1000) && !_exiting)
                {
                    onSignal?.Invoke();
                    _dispatcher!.TryEnqueue(() => action());
                }
            }
        }) { IsBackground = true, Name = "WinStatusBar.Signal" };
        t.Start();
    }

    private static readonly Lock StartupGate = new();
    /// <summary>Último valor de "Iniciar com o Windows" já mandado conferir (null: ainda nenhum).</summary>
    private bool? _startupSynced;

    /// <summary>
    /// Confere a inicialização automática fora da thread da interface (a consulta abre o schtasks.exe e espera por ele):
    /// uma vez ao abrir e, depois, só quando a opção muda. Antes rodava na thread da interface a cada ajuste aplicado.
    /// </summary>
    private void QueueStartupSync()
    {
        bool wanted = Settings.Current.StartWithWindows;
        if (_startupSynced == wanted) return;
        _startupSynced = wanted;
        _ = Task.Run(SyncStartup);
    }

    private void SyncStartup()
    {
        // Apenas na instalação (fora de ambientes virtualizados) e quando o usuário altera a opção.
        // Uma conferência por vez; cada uma lê a opção na hora, então a última a rodar deixa o estado final certo.
        lock (StartupGate)
        {
            try
            {
                bool installed = PackageContext.IsPackaged || AppContext.BaseDirectory.StartsWith(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs"), StringComparison.OrdinalIgnoreCase);
                if (installed && StartupService.IsEnabled() != Settings.Current.StartWithWindows)
                    StartupService.Set(Settings.Current.StartWithWindows, ExePath);
            }
            catch (Exception ex) { Log(global::WinStatusBar.Services.Localization.T("Inicialização automática: ") + ex.Message); }
        }
    }

    public void UpdateTheme()
    {
        var mode = Settings.Current.Theme;
        if (mode == ThemeMode.System)
        {
            try
            {
                var bg = new UISettings().GetColorValue(UIColorType.Background);
                IsDarkTheme = bg.R + bg.G + bg.B < 382;
            }
            catch { IsDarkTheme = true; }
        }
        else IsDarkTheme = mode == ThemeMode.Dark;
        ThemeBrushes.Refresh();
        IconService.ClearThemeCache();
    }

    private void LoadShortcutIcons()
    {
        var vm = Hub.ViewModel;
        vm.TaskManagerIcon = IconService.ShellIcon(WindowsLauncherService.TaskManagerPath);
        vm.SettingsIcon = IconService.ShellIcon(@"shell:AppsFolder\" + WindowsLauncherService.SettingsAumid);
        vm.StoreIcon = IconService.ShellIcon(@"shell:AppsFolder\" + WindowsLauncherService.StoreAumid);
    }

    /// <summary>Cria uma barra por monitor conforme as configurações (principal ou todos).</summary>
    public void RebuildBars()
    {
        var monitors = GetMonitors();
        var wanted = Settings.Current.Monitors == MonitorMode.All ? monitors.Select(m => m.Device).ToList()
            : monitors.Where(m => m.IsPrimary).Select(m => m.Device).Take(1).ToList();
        if (wanted.Count == 0 && monitors.Count > 0) wanted.Add(monitors[0].Device);

        foreach (var bar in _bars.Where(b => !wanted.Contains(b.MonitorDevice)).ToList())
        {
            _bars.Remove(bar);
            bar.CloseBar();
        }
        foreach (var device in wanted.Where(d => _bars.All(b => b.MonitorDevice != d)))
        {
            var bar = new BarWindow(Hub, Settings, device);
            bar.DisplayChanged += (_, _) => { _displayDebounce?.Stop(); _displayDebounce?.Start(); };
            _bars.Add(bar);
            bar.ShowBar();
        }
    }

    private string _lastMonitorKey = string.Empty;

    /// <summary>O que identifica o arranjo de telas: dispositivo, principal, área e escala de cada monitor.</summary>
    private static string MonitorKey(IEnumerable<MonitorInfo> monitors) =>
        string.Join("|", monitors.Select(m => $"{m.Device}:{m.IsPrimary}:{m.Bounds}:{m.Dpi}"));

    private void RebuildBarsIfMonitorsChanged()
    {
        string key = MonitorKey(GetMonitors());
        if (key == _lastMonitorKey) return;
        _lastMonitorKey = key;
        // Monitor principal pode ter mudado: realoca as barras.
        foreach (var b in _bars.ToList()) { _bars.Remove(b); b.CloseBar(); }
        RebuildBars();
    }

    /// <summary>Atalho global da barra de comando (padrão Alt+Espaço): abre a caixa já com o cursor nela.</summary>
    internal GlobalHotkey? CommandHotkey { get; private set; }

    private void StartCommandHotkey()
    {
        try
        {
            CommandHotkey = new GlobalHotkey();
            CommandHotkey.Set(Settings.Current.CommandBar ? Settings.Current.CommandHotkey : null);
            CommandHotkey.Pressed += () => _dispatcher?.TryEnqueue(() =>
            {
                // Barra do monitor onde está o mouse (ou a primeira).
                var bar = _bars.FirstOrDefault(b => b.ContainsCursor()) ?? _bars.FirstOrDefault();
                bar?.FocusCommandBar();
            });
            CommandHotkey.Start();
        }
        catch (Exception ex) { Log("Atalho da barra de comando: " + ex.Message); }
    }
    public void ApplySettings()
    {
        UpdateTheme();
        try { Clip.ClipHost.ApplySettings(Settings.Current); } catch (Exception ex) { Log(global::WinStatusBar.Services.Localization.T("Área de transferência: ") + ex.Message); }
        CommandHotkey?.Set(Settings.Current.CommandBar ? Settings.Current.CommandHotkey : null);
        Hub.ApplySettings();
        QueueStartupSync();
        RebuildBars();
        foreach (var b in _bars) b.ApplyAppearance();
        Hub.ResampleGpuNow();
        Hub.QueueAgentRefresh();
    }

    private void OnTrayCommand(TrayIconService.Command c)
    {
        _dispatcher?.TryEnqueue(async () =>
        {
            switch (c)
            {
                case TrayIconService.Command.Settings: ShowSettings(); break;
                case TrayIconService.Command.Diagnostics: ShowSettings(diagnostics: true); break;
                case TrayIconService.Command.Refresh: await Hub.RefreshNowAsync(); break;
                case TrayIconService.Command.RestartIntegration:
                    Hub.RestartIntegrations();
                    foreach (var b in _bars) b.ReRegister();
                    break;
                case TrayIconService.Command.Exit: ExitApp(); break;
                case TrayIconService.Command.Clipboard: Clip.ClipHost.Show("clip"); break;
                case TrayIconService.Command.Emoji: Clip.ClipHost.Show("emoji"); break;
                case TrayIconService.Command.ClipPause:
                    Clip.ClipHost.Paused = !Clip.ClipHost.Paused;
                    Settings.Current.ClipboardPaused = Clip.ClipHost.Paused;
                    Settings.Save();
                    break;
                case TrayIconService.Command.ClipData: Clip.ClipHost.OpenDataFolder(); break;
            }
        });
    }

    private DispatcherQueueTimer? _apiUsageTimer;
    /// <summary>
    /// Métricas da OpenAI API (saldo, tokens, gastos, requisições) atualizadas sozinhas em segundo plano, no intervalo
    /// escolhido nas configurações, mesmo com o menu fechado. Só com a sincronização da conta habilitada pela pessoa.
    /// </summary>
    private void StartApiUsageAutoRefresh()
    {
        if (_dispatcher is null) return;
        _apiUsageTimer = _dispatcher.CreateTimer();
        _apiUsageTimer.Interval = TimeSpan.FromMinutes(1);
        _apiUsageTimer.Tick += (_, _) =>
        {
            // Tela apagada ou sessão bloqueada: nada a mostrar; na volta, o próximo minuto atualiza o que venceu.
            if (WinStatusBar.Core.Presence.Away) return;
            // Créditos do Qira: no intervalo das cotas (mínimo 5 min), só se a interface do Qira da barra já estiver aberta.
            if (DateTimeOffset.Now - (WinStatusBar.Core.Ai.QiraBridge.CurrentQuota?.Updated ?? DateTimeOffset.MinValue) >= WinStatusBar.Core.Settings.AppSettings.WebQuotaInterval(Settings.Current.CodexQuotaIntervalMin))
                _ = QiraHubSession.RefreshQuotaAsync();
            try
            {
                var s = Settings.Current;
                if (!s.ApiUsageVisible || !WinStatusBar.Views.OpenAiDashboardView.Enabled) return;
                var updated = OpenAiDashboardSession.Current?.Updated ?? DateTimeOffset.MinValue;
                if (DateTimeOffset.Now - updated >= TimeSpan.FromMinutes(s.ApiUsageRefreshMinutes)) WinStatusBar.Views.OpenAiDashboardView.Refresh();
            }
            catch (Exception ex) { Log("Atualização automática das métricas OpenAI: " + ex.Message); }
        };
        _apiUsageTimer.Start();
    }

    public void ShowSettings(bool diagnostics = false, bool repair = false)
    {
        if (_settingsWindow is null)
        {
            _settingsWindow = new SettingsWindow(this);
            _settingsWindow.Closed += (_, _) => _settingsWindow = null;
        }
        _settingsWindow.ShowAndFocus(diagnostics);
        if (repair) _settingsWindow.StartRepair(); // menu "…" › Corrigir problemas das IAs
    }

    public void UpdateTrayTooltip(string text) => _tray?.SetTooltip(text);

    private Action? _balloonClick;

    /// <summary>Balão da bandeja (respeita a opção de notificações); <paramref name="onClick"/> roda se o usuário clicar nele.</summary>
    public void Notify(string title, string message, Action? onClick = null)
    {
        if (!Settings.Current.Notifications) return;
        _balloonClick = onClick;
        _tray?.ShowBalloon(title, message);
    }

    private void ReleaseAppBars()
    {
        // Também roda fora da thread da interface (vigia da saída, ProcessExit): a lista pode mudar no meio.
        try { foreach (var b in _bars.ToList()) { try { b.AppBar?.Unregister(); } catch { } } } catch { }
    }

    public void ExitApp()
    {
        if (_exiting) return;
        _exiting = true;
        _exitWatchdog.Start(); // limite de segurança: mesmo que alguma etapa abaixo trave, o processo termina
        Log("Encerrando.");
        // Primeiro os timers que (re)criam janelas WebView2 ocultas (cotas do Gemini, créditos da Microsoft, painel da
        // OpenAI e créditos do Qira): depois do "Encerrando." eles recriavam essas janelas e o processo nunca acabava.
        ExitStep("timers", () =>
        {
            _apiUsageTimer?.Stop();
            _displayDebounce?.Stop();
            GoogleUsageSession.Stop();
            MicrosoftCreditsSession.Stop();
            ApiResume?.Shutdown(); // só laço e timers; a continuação pela API segue rodando
        });
        foreach (var b in _bars.ToList()) ExitStep("barra", b.CloseBar);
        _bars.Clear();
        ExitStep("configurações", () => _settingsWindow?.CloseForExit());
        ExitStep("bandeja", () => _tray?.Dispose());
        ExitStep("ponte do Codex", CodexBridgeHost.Stop);
        ExitStep("monitoramento", () => Hub.Dispose());
        ExitStep("instância única", () => _singleInstance?.ReleaseMutex());
        // No modo padrão (OnLastWindowClose) o Exit() do WinUI só pede para as janelas fecharem e espera a última
        // fechar para encerrar o laço de mensagens; uma janela oculta que sobra (ou que um timer recria) deixava o
        // processo vivo para sempre. No modo explícito, o Exit() fecha as janelas e posta WM_QUIT na hora.
        ExitStep("fechamento das janelas", () =>
        {
            DispatcherShutdownMode = DispatcherShutdownMode.OnExplicitShutdown;
            Exit();
        });
        // Tudo o que importa já foi feito (AppBar, bandeja, ponte, preferências, janelas fechadas): o processo termina
        // aqui. Esperar o WinUI se desmontar deixava continuações atrasadas (WebView2, atualização da barra) rodarem sem
        // o XAML; uma exceção nelas derrubava o processo (CoreMessagingXP, 0xc000027b) e o relatório de erros do
        // Windows segurava a saída por 10 a 25 s.
        Log("Encerrado.");
        TerminateSelf(0);
    }

    /// <summary>Termina o processo na hora, sem o desmonte do WinUI nem a descarga das DLLs.</summary>
    private static void TerminateSelf(uint exitCode)
    {
        TerminateProcess(GetCurrentProcess(), exitCode);
        Environment.Exit((int)exitCode); // só se o Windows recusar o término
    }

    /// <summary>Onde a saída está, para o registro da saída forçada (a vigia lê de outra thread).</summary>
    private volatile string _exitStage = "a interface não atendeu ao pedido de saída";

    /// <summary>Uma etapa da saída que falha é registrada e não impede as seguintes nem o fim do processo.</summary>
    private void ExitStep(string name, Action step)
    {
        _exitStage = name;
        try { step(); }
        catch (Exception ex) { Log($"Encerrando ({name}): {ex.GetType().Name}: {ex.Message}"); }
    }

    /// <summary>
    /// Saída forçada pela vigia: o processo não terminou no prazo. Remove o registro das barras na AppBar (o espaço
    /// reservado no topo da tela), com prazo próprio, e encerra o processo.
    /// </summary>
    private void ForceExit()
    {
        Log($"Saída forçada: o processo não terminou em {ExitTimeout.TotalSeconds:0} s depois do pedido de saída (etapa: {_exitStage}).");
        Task.Run(ReleaseAppBars).Wait(TimeSpan.FromSeconds(1));
        TerminateSelf(1);
    }

    public static void Log(string message)
    {
        try
        {
            lock (LogGate)
            {
                Directory.CreateDirectory(SettingsService.DataDir);
                var path = SettingsService.LogPath;
                if (File.Exists(path) && new FileInfo(path).Length > 1024 * 1024) File.Move(path, path + ".old", overwrite: true);
                // Chaves e tokens (sk-…, ghp_…, AIza…, chaves privadas) nunca vão inteiros para o registro.
                string line = global::WinStatusBar.Core.Clip.SecretMask.Mask(message);
                File.AppendAllText(path, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {line}{Environment.NewLine}");
            }
        }
        catch { }
        Debug.WriteLine(message);
    }
}

