using System.Diagnostics;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using WinStatusBar.Core.Ai;
using WinStatusBar.Core.Ai.Claude;
using WinStatusBar.Core.Ai.Codex;
using WinStatusBar.Core.Ai.Local;
using WinStatusBar.Core.Ipc;
using WinStatusBar.Core.Metrics;
using WinStatusBar.Core.Settings;
using WinStatusBar.ViewModels;
using static WinStatusBar.Interop.Win32;

namespace WinStatusBar.Services;

/// <summary>
/// Agendador central: um único laço assíncrono (fora da thread de UI) amostra CPU/RAM/rede a cada intervalo,
/// GPU no seu próprio intervalo, e aplica o resultado na UI via DispatcherQueue. Agentes e cotas são por eventos.
/// </summary>
public sealed class MonitoringHub : IDisposable
{
    private string? _lastProgressDiagnostic;
    private readonly DispatcherQueue _ui;
    private readonly SettingsService _settings;
    private CancellationTokenSource? _cts;
    private readonly Lock _sampleGate = new();
    private int _agentRefreshQueued;
    private int _hardwareChanged;

    public BarViewModel ViewModel { get; } = new();
    public CpuMetricsProvider Cpu { get; } = new();
    public RamMetricsProvider Ram { get; } = new();
    public BatteryMetricsProvider Battery { get; } = new();
    public BatterySample? LastBattery { get; private set; }
    public event Action? BatterySampled;
    public GpuMetricsProvider Gpu { get; } = new();
    public NetworkMetricsProvider Network { get; } = new();
    public CodexProvider Codex { get; } = new();
    public ClaudeCodeProvider Claude { get; } = new(Path.Combine(SettingsService.CacheDir, "claude-quota.json"));
    public EventPipeServer Pipe { get; } = new();
    public LocalAiProvider Local { get; } = new();
    public ApiActivityProvider ApiActivity { get; } = new();
    public TaskActivityService Activity { get; } = new();
    /// <summary>Conversas de chat em andamento (ChatGPT e Claude), lidas da janela dos aplicativos.</summary>
    public ChatActivityProvider ChatGptChat { get; }
    public ChatActivityProvider ClaudeChat { get; }
    public ChatActivityProvider QiraChat { get; }

    public TemperatureSample? LastTemperature { get; private set; }
    public CpuSample? LastCpu { get; private set; }
    /// <summary>Histórico por processador lógico (painel da CPU), amostrado junto com a barra.</summary>
    public CpuCoreHistory CoreHistory { get; } = new();
    /// <summary>Disparado na thread de UI logo após a barra receber a nova amostra de CPU.</summary>
    public event Action? CpuSampled;
    /// <summary>Históricos de 60 amostras para os painéis de memória, rede e GPU (contínuos, já preenchidos ao abrir).</summary>
    public RingHistory RamUsedHistory { get; } = new();
    public RingHistory NetUpHistory { get; } = new();
    public RingHistory NetDownHistory { get; } = new();
    public RingHistory GpuDedicatedHistory { get; } = new();
    public RingHistory GpuSharedHistory { get; } = new();
    /// <summary>Utilização por tipo de motor (3D, Copy, VideoDecode…) do adaptador exibido na barra.</summary>
    public IReadOnlyDictionary<string, RingHistory> GpuEngineHistory => _gpuEngines;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, RingHistory> _gpuEngines = new(StringComparer.OrdinalIgnoreCase);
    private long _historyLuid;
    public RamSample? LastRam { get; private set; }
    public GpuSample? LastGpu { get; private set; }
    public NetworkSample? LastNetwork { get; private set; }
    public TimeSpan LastCycleCost { get; private set; }
    public AgentSession? Priority { get; private set; }
    private TaskBriefingService? _briefing;
    internal TaskBriefingService Briefing => _briefing ??= new(() => Priority,
        () => _settings.Current.TaskBriefingEnabled, () => _settings.Current.TaskBriefingIntervalSeconds);

    public MonitoringHub(DispatcherQueue ui, SettingsService settings)
    {
        _ui = ui;
        _settings = settings;
        Activity.Add(Codex);
        Codex.Diagnostic += App.Log;
        Activity.Add(Claude);
        Activity.Add(Local);
        Activity.Add(ApiActivity);
        ChatGptChat = new ChatActivityProvider(ChatActivityProvider.App.ChatGpt, () => _settings.Current.CodexEnabled && _settings.Current.ChatActivityEnabled,
                                               () => Codex.Sessions.Where(x => x.State.IsActive() && !x.ObservationStale).Select(x => x.Title));
        ClaudeChat = new ChatActivityProvider(ChatActivityProvider.App.Claude, () => _settings.Current.ClaudeEnabled && _settings.Current.ChatActivityEnabled);
        Activity.Add(ChatGptChat);
        Activity.Add(ClaudeChat);
        QiraChat = new ChatActivityProvider(ChatActivityProvider.App.Qira, () => _settings.Current.QiraEnabled);
        Activity.Add(QiraChat);
        Activity.Changed += (_, _) => QueueAgentRefresh();
        GoogleUsageSession.Changed += () => QueueAgentRefresh();
        MicrosoftCreditsSession.Changed += () => QueueAgentRefresh();
        QiraBridge.QuotaChanged += () => QueueAgentRefresh();
        Codex.QuotaChanged += (_, _) => QueueAgentRefresh();
        Claude.QuotaChanged += (_, _) => QueueAgentRefresh();
        Pipe.MessageReceived += m => Claude.Apply(m);
        // Tarefas do Claude Code já em andamento quando a barra abre: lidas na hora, sem esperar o próximo evento.
        _ = Task.Run(() => { try { if (Claude.Bootstrap(TimeSpan.FromMinutes(10)) > 0) QueueAgentRefresh(); } catch (Exception ex) { App.Log("Claude (retomada): " + ex.Message); } });
        AgentState? lastLocal = null;
        Local.SessionsChanged += (_, _) =>
        {
            var l = Local.Sessions.FirstOrDefault();
            if (l?.State != lastLocal) { lastLocal = l?.State; App.Log(global::WinStatusBar.Services.Localization.F("Local: {0}", new object?[] {(l is null ? "sem servidor" : $"{l.State} · {l.Title} · {l.Caption}")})); }
        };
        Pipe.Error += ex => App.Log("Named Pipe: " + ex.GetType().Name + ": " + ex.Message);
    }

    public void Start()
    {
        ApplySettings();
        Pipe.Start();
        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        _ = Task.Run(() => LoopAsync(token));
        _ = Task.Run(() => ClaudeQuotaLoopAsync(token));
        ChatGptChat.Start();
        ClaudeChat.Start();
        QiraChat.Start();
        QueueAgentRefresh();
        Briefing.Changed += (_, _) => QueueAgentRefresh();
        Briefing.Start();
    }

    public void ApplySettings()
    {
        var s = _settings.Current;
        Gpu.PreferredAdapter = s.GpuAdapter;
        // A detecção de inferência local usa os contadores de GPU por processo.
        Gpu.MeasureUtilization = s.GpuUtilization || s.GpuPercent == GpuPercentMode.Utilization || s.LocalEnabled;
        Local.ExtraProcessNames = s.LocalServerProcesses;
        Network.ManualInterface = s.NetworkInterface;
        Codex.QuotaInterval = AppSettings.QuotaPoll(s.CodexQuotaIntervalMin);
        // Uma escolha só para todas as IAs (Configurações › IA › Cotas das IAs).
        var web = AppSettings.WebQuotaInterval(s.CodexQuotaIntervalMin);
        GoogleUsageSession.SetInterval(web); MicrosoftCreditsSession.SetInterval(web);
        // Claude: "tempo real" consulta a conta a cada 3 min (o serviço bloqueia consultas por minuto); eventos atualizam entre elas.
        Claude.PollInterval = s.ClaudeQuotaIntervalMin <= 0 ? TimeSpan.FromMinutes(3) : AppSettings.QuotaPoll(s.ClaudeQuotaIntervalMin);
        Claude.Reschedule();
        Activity.EnabledKinds.Clear();
        if (s.CodexEnabled) Activity.EnabledKinds.Add(AgentKind.Codex);
        if (s.ClaudeEnabled) Activity.EnabledKinds.Add(AgentKind.Claude);
        if (s.LocalEnabled) Activity.EnabledKinds.Add(AgentKind.Local);
        if (s.QiraEnabled) Activity.EnabledKinds.Add(AgentKind.Qira);
        if (s.ApiActivityEnabled) Activity.EnabledKinds.Add(AgentKind.Api);
        if (s.CodexEnabled && !Codex.Enabled) Codex.Start();
        if (!s.CodexEnabled && Codex.Enabled) Codex.Stop();
        ViewModel.UpdateTemperatures(LastTemperature ?? new TemperatureSample(null, null), s.ShowTemperatures);
        QueueAgentRefresh();
    }

    public void RestartIntegrations()
    {
        Pipe.Stop(); Pipe.Start();
        if (_settings.Current.CodexEnabled) Codex.Start();
        QueueAgentRefresh();
    }

    public async Task RefreshNowAsync()
    {
        HardwareChanged();
        await Task.WhenAll(_settings.Current.CodexEnabled ? Codex.RefreshQuotaNowAsync() : Task.CompletedTask,
            _settings.Current.ClaudeEnabled ? Claude.RefreshUsageAsync(force: true) : Task.CompletedTask);
        QueueAgentRefresh();
    }

    private async Task ClaudeQuotaLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await WinStatusBar.Core.Presence.WhenVisibleAsync(ct); // tela apagada: sem consultas até ela acender
                if (_settings.Current.ClaudeEnabled) await Claude.RefreshUsageAsync(ct: ct);
                await Task.Delay(TimeSpan.FromSeconds(5), ct); // a própria consulta respeita o intervalo e o Retry-After
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch (Exception ex)
            {
                App.Log(global::WinStatusBar.Services.Localization.T("Consulta Claude: ") + ex.GetType().Name);
                try { await Task.Delay(TimeSpan.FromMinutes(5), ct); } catch (OperationCanceledException) { return; }
            }
        }
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        using var temperatures = new TemperatureProvider();
        var batteryClock = Stopwatch.StartNew();
        bool firstBattery = true;
        var temperatureClock = Stopwatch.StartNew();
        bool firstTemperature = true;
        var gpuClock = Stopwatch.StartNew();
        var expireClock = Stopwatch.StartNew();
        var localClock = Stopwatch.StartNew();
        bool firstGpu = true;
        while (!ct.IsCancellationRequested)
        {
            if (WinStatusBar.Core.Presence.Away)
            {
                // Tela apagada ou sessão bloqueada: ninguém vê a barra. Para aqui e volta assim que a tela acender,
                // já com GPU, bateria e temperaturas lidas de novo.
                try { await WinStatusBar.Core.Presence.WhenVisibleAsync(ct); } catch (OperationCanceledException) { return; }
                firstGpu = firstBattery = firstTemperature = true;
            }
            var s = _settings.Current;
            var sw = Stopwatch.StartNew();
            try
            {
                CpuSample cpu; RamSample ram; NetworkSample net; GpuSample? gpu = null;
                lock (_sampleGate)
                {
                    cpu = Cpu.Sample();
                    CoreHistory.Sample();
                    ram = Ram.Sample();
                    net = Network.Sample();
                    bool hardwareChanged = Interlocked.Exchange(ref _hardwareChanged, 0) != 0;
                    if (firstGpu || hardwareChanged || gpuClock.ElapsedMilliseconds >= s.GpuIntervalMs) { gpu = Gpu.Sample(); gpuClock.Restart(); firstGpu = false; }
                    if (hardwareChanged) { firstTemperature = true; LastTemperature = null; }
                }
                LastCpu = cpu; LastRam = ram; LastNetwork = net; if (gpu is not null) LastGpu = gpu;
                RecordHistories(ram, net, gpu);
                _ui.TryEnqueue(DispatcherQueuePriority.Low, () =>
                {
                    ViewModel.UpdateCpu(cpu);
                    CpuSampled?.Invoke();
                    ViewModel.UpdateRam(ram);
                    ViewModel.UpdateNetwork(net);
                    if (gpu is not null) ViewModel.UpdateGpu(gpu, s);
                });
                if (firstBattery || batteryClock.Elapsed.TotalSeconds >= 5)
                {
                    var battery = Battery.Sample();
                    LastBattery = battery;
                    _ui.TryEnqueue(() => { ViewModel.UpdateBattery(battery); BatterySampled?.Invoke(); });
                    firstBattery = false;
                    batteryClock.Restart();
                }
                if (firstTemperature || temperatureClock.Elapsed.TotalSeconds >= 10)
                {
                    var t = s.ShowTemperatures ? await temperatures.SampleAsync(LastGpu?.Adapter, ct) : new TemperatureSample(null, null);
                    LastTemperature = t;
                    _ui.TryEnqueue(() => ViewModel.UpdateTemperatures(t, s.ShowTemperatures));
                    firstTemperature = false;
                    temperatureClock.Restart();
                }
                if (s.LocalEnabled && localClock.ElapsedMilliseconds >= 2000)
                {
                    localClock.Restart();
                    try { Local.Poll(Gpu.ProcessUsage); } catch (Exception ex) { App.Log(global::WinStatusBar.Services.Localization.T("Local: ") + ex.Message); }
                }
                if (expireClock.Elapsed > TimeSpan.FromSeconds(5))
                {
                    expireClock.Restart();
                    bool changed = Codex.Expire(DateTimeOffset.Now) | Claude.Expire(DateTimeOffset.Now);
                    // Atualiza também tempos relativos ("há 2 min") e quotas expiradas.
                    QueueAgentRefresh();
                    _ = changed;
                }
            }
            catch (Exception ex) { App.Log("Falha na coleta: " + ex.Message); }
            LastCycleCost = sw.Elapsed;
            try { await Task.Delay(Math.Max(250, s.MetricsIntervalMs - (int)sw.ElapsedMilliseconds), ct); } catch { return; }
        }
    }

    public async Task RefreshTemperaturesAsync()
    {
        using var temperatures = new TemperatureProvider();
        var sample = await Task.Run(() => temperatures.SampleAsync(LastGpu?.Adapter, _cts?.Token ?? CancellationToken.None));
        LastTemperature = sample;
        _ui.TryEnqueue(() => ViewModel.UpdateTemperatures(sample, _settings.Current.ShowTemperatures));
    }

    public void ResampleGpuNow()
    {
        Task.Run(() =>
        {
            GpuSample g;
            lock (_sampleGate) g = Gpu.Sample();
            LastGpu = g;
            _ui.TryEnqueue(() => ViewModel.UpdateGpu(g, _settings.Current));
        });
    }

    public void HardwareChanged()
    {
        Gpu.RequestAdapterRefresh();
        Interlocked.Exchange(ref _hardwareChanged, 1);
    }

    /// <summary>Coalesce eventos de agentes/cotas em uma única atualização da UI.</summary>
    public void QueueAgentRefresh()
    {
        if (Interlocked.Exchange(ref _agentRefreshQueued, 1) == 1) return;
        _ui.TryEnqueue(DispatcherQueuePriority.Low, () =>
        {
            Interlocked.Exchange(ref _agentRefreshQueued, 0);
            try { RefreshAgents(); } catch (Exception ex) { App.Log("Falha ao atualizar agentes: " + ex.Message); }
        });
    }

    private static AgentKind? ForegroundAgent()
    {
        try
        {
            GetWindowThreadProcessId(GetForegroundWindow(), out uint pid);
            if (pid == 0) return null;
            using var p = Process.GetProcessById((int)pid);
            string n = p.ProcessName;
            if (n.Contains("codex", StringComparison.OrdinalIgnoreCase) || n.Contains("chatgpt", StringComparison.OrdinalIgnoreCase)) return AgentKind.Codex;
            if (n.Contains("claude", StringComparison.OrdinalIgnoreCase)) return AgentKind.Claude;
            if (n.Equals("QuantumAI.Hub", StringComparison.OrdinalIgnoreCase)) return AgentKind.Qira;
        }
        catch { }
        return null;
    }

    private void RefreshAgents()
    {
        var s = _settings.Current;
        var vm = ViewModel;
        bool dark = App.IsDarkTheme;
        // Ícone oficial do pacote do app; em PCs só com o CLI, o ícone do próprio executável.
        vm.CodexIcon = IconService.CodexIcon(dark) ?? (Codex.Executable is { } cx && cx.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? IconService.ShellIcon(cx) : null);
        if (vm.ClaudeIcon is null) _ = LoadClaudeIconAsync();

        if (Local.ServerImagePath is { } localExe && vm.LocalIcon is null)
        {
            // Executáveis de servidores locais muitas vezes não têm ícone embutido (ex.: Ollama usa app.ico na pasta de instalação).
            string dir = Path.GetDirectoryName(localExe) ?? string.Empty;
            string? ico = null;
            try { ico = new[] { Path.Combine(dir, "app.ico") }.Concat(Directory.EnumerateFiles(dir, "*.ico")).FirstOrDefault(File.Exists); } catch { }
            vm.LocalIcon = IconService.ShellIcon(ico ?? localExe);
        }
        ImageSource? IconOf(AgentKind k) => k switch { AgentKind.Codex => vm.CodexIcon, AgentKind.Claude => vm.ClaudeIcon, AgentKind.Api => null, AgentKind.Qira => ExtraLogo("qira"), _ => vm.LocalIcon };

        var all = Activity.AllSessions().Where(x => x.Kind != AgentKind.Api || !s.DisabledApiActivityProviders.Contains(x.ProviderId ?? "custom")).ToList();
        AgentKind? pinnedKind = s.PinnedSession switch { "codex" => AgentKind.Codex, "claude" => AgentKind.Claude, "local" => AgentKind.Local, "qira" => AgentKind.Qira, _ => null };
        // Servidor local ocioso não disputa a barra no modo automático, mas aparece se "IA local" for escolhida.
        var candidates = all.Where(x => (pinnedKind == AgentKind.Local || !(x.Kind == AgentKind.Local && x.State == AgentState.Idle))
            && (x.State != AgentState.Idle || DateTimeOffset.Now - x.LastActivity < TimeSpan.FromHours(2) || x.Kind == pinnedKind)).ToList();
        var priority = TaskActivityService.ChoosePriority(candidates, s.PinnedSession, ForegroundAgent());
        // Agente escolhido explicitamente, mas sem nenhuma sessão: mostra o próprio agente ocioso (não troca para outro).
        if (pinnedKind is not null && priority is not null && priority.Kind != pinnedKind) priority = null;
        // IA adicional escolhida no menu ("ai:gemini", "ai:deepseek"…): a conversa mais relevante desse serviço feita na barra.
        if (s.PinnedSession is { } pin && pin.StartsWith("ai:", StringComparison.Ordinal))
            priority = TaskActivityService.ChoosePriority(all.Where(x => x.Kind == AgentKind.Api && x.ProviderId == pin[3..]), "auto", null);
        Priority = priority;
        BuildAgentOptions(all, s, IconOf, candidates);
        bool emphasizeAgent = priority?.State.IsActive() == true;
        // Fluent: camada translúcida (LayerFillColorDefault, no XAML; o acrílico de app ficava opaco porque não enxerga
        // o fundo do sistema da barra) com o traço de cartão do Windows 11,
        // igual no claro e no escuro. A atividade aparece pela bolinha de estado e pelo brilho do contorno (opcional),
        // sem borda/tinta de destaque (ficava pesado: contorno grosso no claro, halo ciano no escuro).
        vm.AgentBorder = (Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"];
        vm.AgentTintOpacity = 0;
        vm.AgentActive = emphasizeAgent;
        vm.TaskRunning = priority?.State is AgentState.Running or AgentState.WaitingResponse;
        vm.ClaudeWorking = priority is { Kind: AgentKind.Claude, State: AgentState.Running };
        vm.CodexWorking = priority is { Kind: AgentKind.Codex, State: AgentState.Running or AgentState.WaitingResponse };
        vm.AgentOllama = (priority?.Kind == AgentKind.Local || priority is null && pinnedKind == AgentKind.Local)
            && string.Equals(Local.ServerDisplay, "Ollama", StringComparison.OrdinalIgnoreCase);
        vm.OllamaWorking = vm.AgentOllama && priority?.State == AgentState.Running;
        vm.ExtraWorking = priority is { Kind: AgentKind.Api, State: AgentState.Running or AgentState.WaitingResponse } ? priority.ProviderId ?? "custom" : string.Empty;
        bool privacy = s.PrivacyMode;

        // Lista de sessões do painel
        vm.Sessions.Clear();
        foreach (var x in all)
        {
            vm.Sessions.Add(new SessionItem
            {
                Id = x.Id, Kind = x.Kind,
                Agent = x.DisplayAgent,
                Title = privacy ? global::WinStatusBar.Services.Localization.T("Sessão ") + x.DisplayAgent : Formatters.Truncate(x.DisplayTitle, 80),
                Caption = privacy ? x.State.ToPt() : Formatters.Truncate(x.Caption ?? x.ProjectName, 90),
                State = x.State.ToPt() + (x.ActiveSubagents > 0 ? $" · {x.ActiveSubagents} subagente(s)" : string.Empty),
                When = Relative(x.LastActivity),
                DotBrush = DotBrush(x.State),
                Icon = IconOf(x.Kind),
                Project = privacy ? string.Empty : x.ProjectName,
            });
        }
        vm.NoSessionsVisible = vm.Sessions.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        if (priority is null)
        {
            if (pinnedKind is { } pk)
            {
                vm.AgentName = pk.Display();
                vm.AgentIcon = IconOf(pk);
                vm.AgentOpenAi = pk == AgentKind.Codex;
                vm.TaskTitle = global::WinStatusBar.Services.Localization.T("Nenhuma tarefa recente");
                vm.TaskCaption = $"{pk.Display()} escolhido no menu — sem atividade no momento";
            }
            else if (s.PinnedSession is { } extra && extra.StartsWith("ai:", StringComparison.Ordinal) && ExtraAis().FirstOrDefault(a => a.Id == extra[3..]) is { Id: not null } chosen)
            {
                vm.AgentName = chosen.Name;
                vm.AgentIcon = chosen.Logo;
                vm.AgentOpenAi = false;
                vm.TaskTitle = global::WinStatusBar.Services.Localization.T("Nenhuma tarefa recente");
                vm.TaskCaption = $"{chosen.Name} escolhido no menu — sem atividade no momento";
            }
            else
            {
                vm.AgentName = global::WinStatusBar.Services.Localization.T("Automático");
                vm.AgentIcon = vm.CodexIcon ?? vm.ClaudeIcon;
                vm.AgentOpenAi = vm.CodexIcon is not null || vm.ClaudeIcon is null;
                vm.TaskTitle = global::WinStatusBar.Services.Localization.T("Nenhuma tarefa em execução");
                vm.TaskCaption = global::WinStatusBar.Services.Localization.T("Agentes de IA ociosos");
            }
            vm.AgentState = global::WinStatusBar.Services.Localization.T("Ocioso");
            vm.AgentDot = DotBrush(AgentState.Idle);
            vm.AgentTooltip = global::WinStatusBar.Services.Localization.T("Nenhuma sessão ativa — clique para escolher o agente exibido");
            vm.TaskTooltip = global::WinStatusBar.Services.Localization.T("Clique para escolher o agente exibido");
            vm.ProgressVisible = Visibility.Collapsed; vm.ProgressTextVisible = Visibility.Collapsed;
        }
        else
        {
            double? shownProgress = priority.ProgressIsPhaseEstimate && (!s.EstimateProgressWithoutPlan || !priority.State.IsActive()) ? null : priority.ProgressPercent;
            // O donut segue as mesmas etapas do acompanhamento (plano lido do transcript, ou roteiro deduzido),
            // que sobrevivem a um reinício da barra; o plano vindo dos hooks se perde e caía na estimativa por fase.
            var brief = Briefing.Current;
            if (brief.Session == priority.Kind + ":" + priority.Id && ActivityProgress.From(brief.Steps).Percent is { } planPercent)
                shownProgress = planPercent;
            vm.AgentName = priority.DisplayAgent;
            // Conversas da barra com outras IAs (Gemini, Copilot, DeepSeek, Grok): logo do serviço, não o glifo genérico.
            vm.AgentIcon = priority.Kind == AgentKind.Api ? ExtraLogo(priority.ProviderId ?? "") : IconOf(priority.Kind);
            vm.AgentOpenAi = priority.Kind == AgentKind.Codex;
            vm.AgentState = priority.State.ToPt();
            vm.AgentDot = DotBrush(priority.State);
            vm.AgentTooltip = $"{vm.AgentName} · {priority.State.ToPt()}" + $"\nFonte: {priority.ObservationSource}" + (priority.ObservationStale ? "\nObservação antiga · conclusão não confirmada" : "") + (priority.Model is null || privacy ? string.Empty : $"\nModelo: {priority.Model}")
                              + (priority.ContextUsedPercent is null ? string.Empty : global::WinStatusBar.Services.Localization.F("\nContexto da sessão: {0}% (não é cota)", new object?[] {Math.Round(priority.ContextUsedPercent.Value)}))
                              + global::WinStatusBar.Services.Localization.F("\nÚltima atividade: {0}", new object?[] {Relative(priority.LastActivity)}) + (all.Count > 1 ? global::WinStatusBar.Services.Localization.F("\n{0} sessões conhecidas — clique para ver", new object?[] {all.Count}) : string.Empty);
            if (!s.ShowTask || privacy)
            {
                vm.TaskTitle = privacy ? $"Tarefa do {vm.AgentName}" : vm.AgentName;
                vm.TaskCaption = priority.State.ToPt();
            }
            else
            {
                vm.TaskTitle = Formatters.Truncate(priority.DisplayTitle, 90);
                vm.TaskCaption = Formatters.Truncate(priority.Caption ?? (priority.State.IsActive() ? global::WinStatusBar.Services.Localization.T("Executando tarefa") : priority.State.ToPt()), 110)
                                 + (priority.ActiveSubagents > 0 ? $" · {priority.ActiveSubagents} subagente(s)" : string.Empty);
            }
            vm.TaskTooltip = privacy ? global::WinStatusBar.Services.Localization.T("Modo de privacidade ativo") : $"{priority.DisplayTitle}\n{priority.Caption}\nProjeto: {(string.IsNullOrEmpty(priority.ProjectDir) ? "—" : priority.ProjectDir)}"
                                       + (shownProgress is null ? global::WinStatusBar.Services.Localization.T("\nProgresso numérico não informado pelo agente") : $"\nProgresso estimado: {Math.Min(priority.ExecutionEnded ? 100 : 99, Math.Round(shownProgress.Value))}% · {priority.ProgressBasis}");
            vm.TaskTooltip += $"\nFonte: {priority.ObservationSource}\nÚltima atividade: {Relative(priority.LastActivity)}";
            if (priority.ObservationStale) vm.TaskTooltip += "\nSem observações recentes · conclusão não confirmada";
            if (priority.ScopeChanged) vm.TaskTooltip += "\nEstimativa recalculada: plano alterado ou retrabalho";
            bool running = priority.State is AgentState.Running or AgentState.WaitingResponse;
            if (priority.State.IsActive() && shownProgress is not null)
            {
                // Com porcentagem conhecida, o arco acompanha o número (antes girava sem relação com ele).
                vm.ProgressIndeterminate = false; vm.ProgressValue = Math.Min(99, shownProgress.Value);
                vm.ProgressText = Math.Min(99, Math.Round(shownProgress.Value)) + "%";
                // Percentual real (etapas do plano) no canto da legenda da tarefa, para acompanhar quanto falta.
                vm.ProgressVisible = Visibility.Visible; vm.ProgressTextVisible = Visibility.Visible;
            }
            else if (running)
            {
                vm.ProgressIndeterminate = true; vm.ProgressVisible = Visibility.Visible; vm.ProgressTextVisible = Visibility.Collapsed;
            }
            else if (priority.State == AgentState.Completed && shownProgress is not null)
            {
                // Concluída com plano: o círculo permanece completo, com 100% no centro.
                vm.ProgressIndeterminate = false; vm.ProgressValue = shownProgress.Value; vm.ProgressText = Math.Round(shownProgress.Value) + "%";
                vm.ProgressVisible = Visibility.Visible; vm.ProgressTextVisible = Visibility.Visible;
            }
            else { vm.ProgressIndeterminate = false; vm.ProgressVisible = Visibility.Collapsed; vm.ProgressTextVisible = Visibility.Collapsed; }
            string diagnostic = $"sessão={priority.Id}; turno={priority.TurnId}; texto={vm.ProgressText}; número={vm.ProgressTextVisible}; indeterminado={vm.ProgressIndeterminate}";
            if (_lastProgressDiagnostic != diagnostic)
            {
                _lastProgressDiagnostic = diagnostic;
                App.Log("Indicador de progresso: " + diagnostic);
            }
        }

        vm.UpdateQuotas(s.CodexEnabled ? Codex.Quota : null, s.ClaudeEnabled ? Claude.Quota : null, s, Codex.LastError, Claude.LastQuotaError);
        // Adaptativo: blocos de cota só para as IAs que existem neste PC.
        var (codexOk, claudeOk, _) = InstalledAgents();
        vm.CodexQuotaVisible = s.CodexEnabled && (codexOk || Codex.Quota?.Primary is not null) ? Visibility.Visible : Visibility.Collapsed;
        vm.ClaudeQuotaVisible = s.ClaudeEnabled && (claudeOk || Claude.Quota?.Primary is not null) ? Visibility.Visible : Visibility.Collapsed;
        var g = GoogleUsageSession.Current;
        bool gFresh = g is { SignedIn: true } && DateTimeOffset.Now - g.Updated < TimeSpan.FromHours(6);
        bool gRemaining = s.QuotaMode == QuotaDisplay.Remaining;
        string GText(double? used) => used is { } u ? $"{Math.Round(gRemaining ? 100 - u : u):0} %" : "—";
        vm.GeminiCurrent = gFresh ? GText(g!.CurrentPercent) : "—";
        vm.GeminiWeekly = gFresh ? GText(g!.WeeklyPercent) : "—";
        vm.GeminiQuotaVisible = g is { SignedIn: true } ? Visibility.Visible : Visibility.Collapsed;
        var mc = MicrosoftCreditsSession.Current;
        bool mcFresh = mc is { SignedIn: true, Total: > 0 } && DateTimeOffset.Now - mc.Updated < TimeSpan.FromHours(12);
        vm.CopilotCredits = mcFresh ? $"{(gRemaining ? mc!.Remaining : mc!.Total - mc.Remaining)}/{mc.Total}" : "—";
        vm.CopilotCreditsPercent = mcFresh ? $"{Math.Round(100.0 * (gRemaining ? mc!.Remaining : mc!.Total - mc.Remaining) / mc.Total):0} %" : "—";
        vm.CopilotQuotaVisible = CopilotWebSession.SignedIn ? Visibility.Visible : Visibility.Collapsed;
        var q = WinStatusBar.Core.Ai.QiraBridge.CurrentQuota;
        bool qFresh = q is { Limit: > 0 } && DateTimeOffset.Now - q.Updated < TimeSpan.FromDays(2);
        int qShown = qFresh ? (gRemaining ? q!.Limit - q.Used : q!.Used) : 0;
        vm.QiraCredits = qFresh ? (q!.Unlimited ? "∞" : $"{qShown}/{q.Limit}") : "—";
        vm.QiraCreditsPercent = qFresh && !q!.Unlimited ? $"{Math.Round(100.0 * qShown / q.Limit):0} %" : qFresh ? "ilimitado" : "—";
        vm.QiraQuotaVisible = s.QiraEnabled && QiraAppBridge.IsInstalled ? Visibility.Visible : Visibility.Collapsed;
        vm.QiraQuotaTooltip = qFresh
            ? $"Lenovo Qira · créditos da nuvem ({(gRemaining ? "restantes" : "usados")})\n{WinStatusBar.Core.Ai.QiraBridge.QuotaText(q!)}\nLido às {q!.Updated.LocalDateTime:HH:mm} no próprio Qira"
            : "Lenovo Qira: créditos ainda não lidos (abra o Qira pela barra uma vez)";
        vm.CopilotQuotaTooltip = mcFresh ? $"Copilot · créditos de IA da assinatura Microsoft ({(gRemaining ? "restantes" : "usados")})\n{vm.CopilotCredits} · recarregados no início de cada mês\nLido às {mc!.Updated.LocalDateTime:HH:mm} em account.microsoft.com"
            : "Copilot: créditos de IA ainda não lidos (account.microsoft.com, com o login do Copilot da barra)";
        vm.GeminiQuotaTooltip = gFresh
            ? $"Gemini · plano {g!.Plan ?? "—"} — {(gRemaining ? "restante" : "usado")}\nUso atual: {vm.GeminiCurrent} (renova {g.CurrentReset ?? "—"})\nSemanal: {vm.GeminiWeekly} (renova {g.WeeklyReset ?? "—"})\nLido às {g.Updated.LocalDateTime:HH:mm} em gemini.google.com/usage"
            : "Gemini: entre na conta Google em Configurações › IA › Gemini para ver as cotas";
        App.Current.UpdateTrayTooltip(global::WinStatusBar.Services.Localization.F("WinStatusBar\n{0}: {1}\nCodex 5h {2} · 7d {3}\nClaude 5h {4} · 7d {5}", new object?[] {vm.AgentName,vm.AgentState,vm.Codex5h,vm.Codex7d,vm.Claude5h,vm.Claude7d}));
    }

    // ------------------------------------------------------------------ menu de agentes
    private DateTime _installCheckedAt = DateTime.MinValue;
    private bool _codexInstalled, _claudeInstalled, _localInstalled;

    /// <summary>Detecta o que existe neste PC (revalidado a cada 2 min): o menu e os módulos se adaptam a cada computador.</summary>
    public (bool codex, bool claude, bool local) InstalledAgents()
    {
        if (DateTime.UtcNow - _installCheckedAt > TimeSpan.FromMinutes(2))
        {
            _installCheckedAt = DateTime.UtcNow;
            _codexInstalled = CodexAppServerClient.IsInstalled();
            _claudeInstalled = Directory.Exists(Core.Integration.ClaudeIntegrationInstaller.ClaudeConfigDir());
            _localInstalled = LocalAiProvider.IsAnyInstalled();
        }
        return (_codexInstalled, _claudeInstalled, _localInstalled || Local.ServerDisplay is not null);
    }

    private void BuildAgentOptions(IReadOnlyList<AgentSession> all, AppSettings s, Func<AgentKind, ImageSource?> iconOf, List<AgentSession> candidates)
    {
        var vm = ViewModel;
        bool privacy = s.PrivacyMode;
        // Escolha antiga de uma sessão específica: marca o agente dessa sessão (ou Automático, se ela não existir mais).
        string selected = s.PinnedSession is "codex" or "claude" or "local" or "qira" ? s.PinnedSession
            : s.PinnedSession?.StartsWith("ai:", StringComparison.Ordinal) == true ? s.PinnedSession
            : all.FirstOrDefault(x => x.Id == s.PinnedSession)?.Kind.Key() ?? "auto";
        var (codexOk, claudeOk, localOk) = InstalledAgents();
        var options = new List<AgentOption>();

        var autoPick = TaskActivityService.ChoosePriority(candidates.Where(x => !(x.Kind == AgentKind.Local && x.State == AgentState.Idle)), "auto", ForegroundAgent());
        options.Add(new AgentOption
        {
            Key = "auto", Name = global::WinStatusBar.Services.Localization.T("Automático"), Glyph = "",
            Title = autoPick is null ? global::WinStatusBar.Services.Localization.T("Nenhuma tarefa em andamento")
                : privacy ? $"Mostraria: {autoPick.DisplayAgent}" : $"Mostraria: {autoPick.DisplayAgent} — {Formatters.Truncate(autoPick.DisplayTitle, 60)}",
            Caption = global::WinStatusBar.Services.Localization.T("Prioriza pedidos de autorização ou resposta, depois tarefas em execução e a atividade mais recente"),
            State = string.Empty, DotBrush = DotBrush(AgentState.Idle), IsSelected = selected == "auto", Working = IsWorking(autoPick),
        });

        void AddAgent(AgentKind kind, bool installed, bool enabled)
        {
            if (!installed || !enabled) return;
            var sessions = all.Where(x => x.Kind == kind).ToList();
            var top = TaskActivityService.ChoosePriority(sessions, "auto", null);
            options.Add(new AgentOption
            {
                Key = kind.Key(), Name = kind.Display(), Icon = iconOf(kind),
                Title = top is null ? global::WinStatusBar.Services.Localization.T("Nenhuma tarefa recente") : privacy ? global::WinStatusBar.Services.Localization.F("Sessão ({0})", new object?[] {top.State.ToPt()}) : Formatters.Truncate(top.DisplayTitle, 70),
                Caption = top is null ? (kind == AgentKind.Qira ? "Converse pelo chat da barra (modo Híbrido)" : kind == AgentKind.Local ? global::WinStatusBar.Services.Localization.T("Nenhum servidor de IA local em execução") : global::WinStatusBar.Services.Localization.T("Sem atividade nas últimas horas"))
                    : privacy ? top.State.ToPt() : Formatters.Truncate(top.Caption ?? top.ProjectName, 90),
                State = (top?.State ?? AgentState.Idle).ToPt(), DotBrush = DotBrush(top?.State ?? AgentState.Idle),
                Detail = sessions.Count == 0 ? string.Empty : $"{sessions.Count} {(sessions.Count == 1 ? "sessão" : "sessões")} · {Relative(top!.LastActivity)}",
                Quota = kind switch { AgentKind.Codex => QuotaLine(Codex.Quota, s), AgentKind.Claude => QuotaLine(Claude.Quota, s), _ => string.Empty },
                IsSelected = selected == kind.Key(), Working = IsWorking(top),
            }.WithRight(RightColumn(kind, top, s)));
        }
        AddAgent(AgentKind.Codex, codexOk, s.CodexEnabled);
        AddAgent(AgentKind.Claude, claudeOk, s.ClaudeEnabled);
        AddAgent(AgentKind.Qira, QiraAppBridge.IsInstalled, s.QiraEnabled);
        // Só IAs configuradas aparecem (pedido de 07/11/2026): IA local exige servidor ativo, sessão observada ou escolha fixa.
        AddAgent(AgentKind.Local, localOk && (Local.ServerDisplay is not null || all.Any(x => x.Kind == AgentKind.Local) || selected == "local"), s.LocalEnabled);
        // Outras IAs configuradas (chave salva ou login do Gemini CLI): uma linha por serviço, com a cota que o serviço informa.
        foreach (var (id, name, logo) in ExtraAis())
        {
            var sessions = all.Where(x => x.Kind == AgentKind.Api && x.ProviderId == id).ToList();
            var top = TaskActivityService.ChoosePriority(sessions, "auto", null);
            options.Add(new AgentOption
            {
                Key = "ai:" + id, Name = name, Icon = logo, Glyph = "\uE99A",
                Title = top is null ? "Nenhuma conversa recente na barra" : privacy ? "Conversa na barra" : Formatters.Truncate(top.DisplayTitle, 70),
                Caption = top is null ? "Disponível no chat da barra" : top.Caption ?? "",
                State = (top?.State ?? AgentState.Idle).ToPt(), DotBrush = DotBrush(top?.State ?? AgentState.Idle),
                Detail = sessions.Count == 0 ? string.Empty : $"{sessions.Count} {(sessions.Count == 1 ? "conversa" : "conversas")} · {Relative(top!.LastActivity)}",
                Quota = ExtraQuota(id), IsSelected = selected == "ai:" + id, Working = IsWorking(top),
            }.WithRight(ExtraRight(id, top)));
        }
        // API própria (endereço configurado pela pessoa): conversas individuais, como antes.
        foreach (var session in all.Where(x => x.Kind == AgentKind.Api && (x.ProviderId ?? "custom") == "custom"))
            options.Add(new AgentOption { Key = session.Id, Name = session.DisplayAgent, Glyph = "\uE99A",
                Title = privacy ? "Conversa na barra" : session.DisplayTitle, Caption = session.Caption ?? "",
                State = session.State.ToPt(), DotBrush = DotBrush(session.State), Detail = Relative(session.LastActivity),
                IsSelected = s.PinnedSession == session.Id });

        // Ordem escolhida nas Configurações (arrastável); "Automático" fica sempre no topo.
        int Rank(AgentOption o) { string k = o.Key.StartsWith("ai:", StringComparison.Ordinal) ? o.Key[3..] : o.Key; int i = s.AiOrder.IndexOf(k); return i < 0 ? 1000 : i; }
        options = options.Take(1).Concat(options.Skip(1).Select((o, i) => (o, i)).OrderBy(x => Rank(x.o)).ThenBy(x => x.i).Select(x => x.o)).ToList();

        // Mesmos serviços na mesma ordem: troca só o conteúdo das linhas que mudaram. Apagar e recriar a lista
        // a cada atualização (ex.: o Claude Code trocando de etapa a cada poucos segundos) recriava todas as
        // linhas e fazia o menu aberto piscar, principalmente com o mouse em cima (o realce sumia e voltava).
        if (vm.AgentOptions.Count == options.Count && vm.AgentOptions.Zip(options).All(p => p.First.Option.Key == p.Second.Key))
        {
            for (int i = 0; i < options.Count; i++)
                if (!vm.AgentOptions[i].Option.SameContent(options[i])) vm.AgentOptions[i].Option = options[i];
            return;
        }
        vm.AgentOptions.Clear();
        foreach (var o in options) vm.AgentOptions.Add(new AgentOptionSlot(o));
    }

    // ------------------------------------------------------------------ coluna da direita do menu de agentes
    /// <summary>Anel de cota: rótulo da janela, número no centro, valor exibido (restante/usado), renovação e % restante (cor).</summary>
    internal sealed record Ring(string Label, string Center, double Value, string Reset, double Remaining);
    internal sealed record Right(string Status, string Foreground, string Background, bool NeedsSetup,
        Ring? Ring1 = null, Ring? Ring2 = null, string Meta = "", string RingNote = "");

    private static Right Pill(string status, bool setup = false) => status switch
    {
        "Disponível" => new(status, "SystemFillColorSuccessBrush", "SystemFillColorSuccessBackgroundBrush", false),
        "Em uso" => new(status, "AccentTextFillColorPrimaryBrush", "SubtleFillColorSecondaryBrush", false),
        "Erro" => new(status, "SystemFillColorCriticalBrush", "SystemFillColorCriticalBackgroundBrush", setup),
        "Não configurado" => new(status, "TextFillColorSecondaryBrush", "SystemFillColorNeutralBackgroundBrush", true),
        _ => new(status, "SystemFillColorCautionBrush", "SystemFillColorCautionBackgroundBrush", setup), // indisponível / esgotada
    };

    /// <summary>"renova 17:10" hoje; "renova sex 23:00" nos próximos dias.</summary>
    private static string ShortReset(DateTimeOffset? at)
    {
        if (at is null) return "";
        var local = at.Value.ToLocalTime();
        var pt = System.Globalization.CultureInfo.GetCultureInfo("pt-BR");
        return "renova " + (local.Date == DateTime.Today ? local.ToString("HH:mm", pt) : local.ToString("ddd HH:mm", pt).TrimEnd('.'));
    }

    private static Ring MakeRing(string label, double usedPercent, bool remainingMode, string reset)
    {
        double used = Math.Clamp(usedPercent, 0, 100), rem = 100 - used, shown = remainingMode ? rem : used;
        return new(label, $"{Math.Round(shown):0}%", shown, reset, rem);
    }

    private DateTimeOffset _accountsCheckedAt;
    private readonly Dictionary<string, AiConnectionStatus> _accounts = new();
    /// <summary>Login/chave de Codex e Claude lidos dos arquivos locais, no máximo a cada 30 s.</summary>
    private AiConnectionStatus Account(string id)
    {
        if (DateTimeOffset.Now - _accountsCheckedAt > TimeSpan.FromSeconds(30)) { _accounts.Clear(); _accountsCheckedAt = DateTimeOffset.Now; }
        if (!_accounts.TryGetValue(id, out var a)) _accounts[id] = a = AiAccountReader.Read(id);
        return a;
    }

    private Right RightColumn(AgentKind kind, AgentSession? top, AppSettings s)
    {
        if (kind == AgentKind.Local)
            return (top?.State == AgentState.Error ? Pill("Erro") : top?.State.IsActive() == true ? Pill("Em uso")
                : Local.ServerDisplay is not null ? Pill("Disponível") : Pill("Servidor parado")) with { RingNote = "Sem cota · roda no PC", Meta = "Roda no seu PC" };
        if (kind == AgentKind.Qira)
        {
            // Créditos da nuvem lidos do próprio Qira (Settings/GetCloudQuota); o processamento local não consome créditos.
            var q = QiraBridge.CurrentQuota;
            bool fresh = q is not null && DateTimeOffset.Now - q.Updated < TimeSpan.FromDays(2);
            Ring? ring = fresh && !q!.Unlimited ? MakeRing("Créditos", q.PercentUsed, s.QuotaMode == QuotaDisplay.Remaining, q.Renews is { } rn ? "renova " + rn.ToLocalTime().ToString("dd/MM") : "") : null;
            string status = top?.State.IsActive() == true ? "Em uso" : fresh && !q!.Unlimited && q.PercentRemaining <= 0 ? "Créditos esgotados" : "Disponível";
            return Pill(status) with
            {
                Ring1 = ring,
                RingNote = ring is null ? (fresh ? "Créditos ilimitados" : "Créditos: use o Qira na barra para ler") : "",
                Meta = fresh && !q!.Unlimited ? $"{q.Used}/{q.Limit} créditos da nuvem no ciclo · modo Híbrido" : "Modo Híbrido (o Qira escolhe entre o modelo dele e a nuvem)",
            };
        }
        var account = Account(kind == AgentKind.Codex ? "codex" : "claude");
        var quota = kind == AgentKind.Codex ? Codex.Quota : Claude.Quota;
        bool remaining = s.QuotaMode == QuotaDisplay.Remaining;
        var now = DateTimeOffset.Now;
        Ring? R(string label, QuotaWindow? w) => w is null ? null : MakeRing(label, w.IsExpired(now) ? 0 : w.UsedPercent, remaining, ShortReset(w.ResetsAt));
        var r1 = R("5h", quota?.Primary); var r2 = R("7 dias", quota?.Secondary);
        string? exhausted = quota?.Primary is { } p1 && !p1.IsExpired(now) && p1.UsedPercent >= 100 ? "Cota de 5h esgotada"
            : quota?.Secondary is { } p2 && !p2.IsExpired(now) && p2.UsedPercent >= 100 ? "Cota semanal esgotada" : null;
        var pill = !account.HasSession && !account.HasApiKey ? Pill("Não configurado")
            : top?.State == AgentState.Error ? Pill("Erro")
            : top?.State.IsActive() == true ? Pill("Em uso")
            : exhausted is not null ? Pill(exhausted)
            : Pill("Disponível");
        // Plano · API numa linha (IAs com cota de assinatura E API mostram as duas métricas).
        var pt = System.Globalization.CultureInfo.GetCultureInfo("pt-BR");
        var meta = new List<string>();
        if (account.Plan is { } plan) meta.Add(plan);
        if (kind == AgentKind.Codex && OpenAiDashboardSession.Current is { } snap)
        {
            meta.Add("API " + (snap.Balance is { } balance ? "US$ " + balance.ToString("N2", pt) : "saldo n/d"));
            meta.Add("usado US$ " + snap.Days.Sum(d => d.Cost).ToString("N2", pt));
        }
        else if (account.HasApiKey) meta.Add("API: chave configurada");
        return pill with { Ring1 = r1, Ring2 = r2, Meta = string.Join(" · ", meta), RingNote = r1 is null && r2 is null ? "Cota: aguardando dados" : "" };
    }

    private Right ExtraRight(string id, AgentSession? top)
    {
        bool remaining = _settings.Current.QuotaMode == QuotaDisplay.Remaining;
        Ring? r1 = null, r2 = null;
        var meta = new List<string>();
        string ringNote = "";
        string? exhausted = null;
        if (id == "gemini")
        {
            // Cotas do plano lidas de gemini.google.com/usage (Uso atual e Limite semanal).
            var g = GoogleUsageSession.Current;
            bool fresh = g is { SignedIn: true } && DateTimeOffset.Now - g.Updated < TimeSpan.FromHours(6);
            string Short(string? reset) => reset is null ? "" : "renova " + reset.Split(',')[0].Trim();
            if (fresh && g!.CurrentPercent is { } cur) r1 = MakeRing("Atual", cur, remaining, Short(g.CurrentReset));
            if (fresh && g!.WeeklyPercent is { } wk) r2 = MakeRing("Semana", wk, remaining, Short(g.WeeklyReset));
            if (fresh && g!.CurrentPercent >= 100) exhausted = "Cota atual esgotada";
            else if (fresh && g!.WeeklyPercent >= 100) exhausted = "Cota semanal esgotada";
            if (fresh) meta.Add("Plano " + (g!.Plan is { } pl ? char.ToUpper(pl[0]) + pl[1..].ToLowerInvariant() : "Gemini"));
            if (ApiKeyStore.Get(AdditionalAiAccounts.Gemini.Resource) is not null)
            {
                // Chave de API: uso real lido do Google AI Studio (pedidos do dia no modelo mais usado); sem leitura, só o nível.
                var api = GoogleUsageSession.Api;
                var busiest = api is not null && DateTimeOffset.Now - api.Updated < TimeSpan.FromHours(6) ? GoogleUsageSession.ApiModelsToShow().FirstOrDefault() : null;
                meta.Add(busiest is { RpdLimit: not null }
                    ? $"API: {(api!.Daily ? "" : "pico ")}{Core.Ai.AiStudioUsageText.Show(busiest.RpdUsed ?? 0)}/{Core.Ai.AiStudioUsageText.Show(busiest.RpdLimit)} pedidos {(api.Daily ? "hoje" : "por dia")}"
                    : "API " + (api?.Tier is { } tier ? tier : "nível gratuito"));
            }
            if (!fresh) ringNote = "Cotas: entre no Google";
        }
        else if (id == "copilot")
        {
            // Créditos de IA da assinatura Microsoft (account.microsoft.com), mesmo login do Copilot da barra.
            var c = MicrosoftCreditsSession.Current;
            if (c is { SignedIn: true, Total: > 0 } && DateTimeOffset.Now - c.Updated < TimeSpan.FromHours(12))
            {
                r2 = MakeRing("Créditos", 100.0 * (c.Total - c.Remaining) / c.Total, remaining, $"{c.Remaining} de {c.Total}");
                if (c.Remaining <= 0) exhausted = "Créditos esgotados";
                if (c.Plan is { } mp) meta.Add(mp);
                meta.Add("recarga mensal");
            }
            else ringNote = "Créditos: aguardando leitura";
        }
        else if (id == "deepseek") { ringNote = ExtraQuota(id).Replace("Saldo · ", "Saldo "); }
        else ringNote = "Cota não informada";
        Right Done(Right r) => r with { Ring1 = r1, Ring2 = r2, Meta = string.Join(" · ", meta), RingNote = ringNote };
        if (top?.State == AgentState.Error) return Done(Pill("Erro", setup: id == "gemini" && _settings.Current.GeminiAuth != "api"));
        if (top?.State.IsActive() == true) return Done(Pill("Em uso"));
        if (id == "gemini" && _settings.Current.GeminiAuth != "api" && !GeminiWebSession.SignedIn && ApiKeyStore.Get(AdditionalAiAccounts.Gemini.Resource) is null)
            return Done(Pill("Não configurado"));
        return Done(exhausted is not null ? Pill(exhausted) : Pill("Disponível"));
    }

    /// <summary>"Cota · 5h 14 % · 7d 57 % usados" — o mesmo dado dos módulos de cota da barra.</summary>
    private static string QuotaLine(QuotaSnapshot? q, AppSettings s)
    {
        if (q is null || q.Primary is null && q.Secondary is null) return "Cota: aguardando dados do serviço";
        bool remaining = s.QuotaMode == QuotaDisplay.Remaining;
        var now = DateTimeOffset.Now;
        return $"Cota · 5h {QuotaService.Text(q.Primary, remaining, now)} · 7d {QuotaService.Text(q.Secondary, remaining, now)} {(remaining ? "restantes" : "usados")}";
    }

    private static readonly Dictionary<string, ImageSource?> ExtraLogos = new();
    private static ImageSource? ExtraLogo(string id)
    {
        if (ExtraLogos.TryGetValue(id, out var cached)) return cached;
        string path = Path.Combine(AppContext.BaseDirectory, "Assets", "AgentLogos", id + ".svg");
        return ExtraLogos[id] = File.Exists(path) ? IconService.LogoSvg(IconService.BarLogoSize, new Uri(path)) : null;
    }

    private DateTimeOffset _extraCheckedAt;
    private List<(string Id, string Name, ImageSource? Logo)> _extraAis = [];
    /// <summary>IAs adicionais configuradas neste PC (consulta ao Cofre no máximo a cada 30 s).</summary>
    private List<(string Id, string Name, ImageSource? Logo)> ExtraAis()
    {
        if (DateTimeOffset.Now - _extraCheckedAt < TimeSpan.FromSeconds(30)) return _extraAis;
        _extraCheckedAt = DateTimeOffset.Now;
        var list = new List<(string, string, ImageSource?)>();
        foreach (var p in AdditionalAiAccounts.Providers.Where(p => p.Endpoint is not null))
        {
            bool configured = ApiKeyStore.Get(p.Resource) is not null
                || p.Id == "gemini" && (GeminiWebSession.SignedIn || WinStatusBar.Core.Ai.Gemini.GeminiCli.Find() is not null && WinStatusBar.Core.Ai.Gemini.GeminiCli.HasLogin());
            if (configured) list.Add((p.Id, p.Id == "gemini" ? "Gemini" : p.Name, ExtraLogo(p.Id)));
        }
        // Copilot: configurado quando há login no Copilot da barra.
        if (CopilotWebSession.SignedIn) list.Add(("copilot", "Microsoft Copilot", ExtraLogo("copilot")));
        return _extraAis = list;
    }

    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, (DateTimeOffset At, string Text)> _extraQuota = new();
    private static readonly System.Net.Http.HttpClient QuotaHttp = new() { Timeout = TimeSpan.FromSeconds(15) };
    /// <summary>
    /// Cota/saldo informados pelo próprio serviço. DeepSeek expõe o saldo pela chave (/user/balance); Gemini e Grok
    /// não expõem limites nem saldo pela chave de conversa, e a barra diz isso em vez de inventar um número.
    /// </summary>
    private string ExtraQuota(string id)
    {
        if (id != "deepseek")
            return id == "gemini" ? "Cota: o Google não informa limites pela chave nem pelo login do CLI"
                : "Cota: saldo não informado pela chave da API (veja o console oficial)";
        bool has = _extraQuota.TryGetValue(id, out var cached);
        if (has && DateTimeOffset.Now - cached.At < TimeSpan.FromMinutes(5)) return cached.Text;
        string? key = ApiKeyStore.Get(AdditionalAiAccounts.Providers.First(p => p.Id == id).Resource);
        if (key is null) return "Saldo: sem chave salva";
        _extraQuota[id] = (DateTimeOffset.Now, has ? cached.Text : "Saldo: consultando…");
        _ = Task.Run(async () =>
        {
            string text;
            try
            {
                using var request = new System.Net.Http.HttpRequestMessage(System.Net.Http.HttpMethod.Get, "https://api.deepseek.com/user/balance");
                request.Headers.Authorization = new("Bearer", key);
                using var response = await QuotaHttp.SendAsync(request);
                using var json = System.Text.Json.JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                text = !response.IsSuccessStatusCode ? $"Saldo: consulta recusada ({(int)response.StatusCode})"
                    : json.RootElement.TryGetProperty("balance_infos", out var infos) && infos.ValueKind == System.Text.Json.JsonValueKind.Array && infos.GetArrayLength() > 0
                        ? "Saldo · " + string.Join(" · ", infos.EnumerateArray().Select(b => (b.TryGetProperty("currency", out var c) ? c.GetString() : "") + " " + (b.TryGetProperty("total_balance", out var t) ? t.GetString() : "?")))
                        : "Saldo: não informado pelo serviço";
            }
            catch { text = "Saldo: não foi possível consultar agora"; }
            _extraQuota[id] = (DateTimeOffset.Now, text);
            QueueAgentRefresh();
        });
        return _extraQuota[id].Text;
    }

    /// <summary>Escolha feita no menu da barra: "auto", "codex", "claude", "local" ou "ai:&lt;serviço&gt;".</summary>
    /// <summary>Trabalhando de fato (não parada à espera de autorização ou resposta da pessoa, nem observação antiga).</summary>
    private static bool IsWorking(AgentSession? s) => s is { State: AgentState.Running or AgentState.WaitingResponse, ObservationStale: false };

    public void SelectAgent(string key)
    {
        _settings.Current.PinnedSession = key;
        try { _settings.Save(); } catch (Exception ex) { App.Log("Falha ao salvar a escolha do agente: " + ex.Message); }
        QueueAgentRefresh();
    }

    private async Task LoadClaudeIconAsync()
    {
        var icon = await IconService.ClaudeIconAsync();
        if (icon is not null && ViewModel.ClaudeIcon is null) { ViewModel.ClaudeIcon = icon; QueueAgentRefresh(); }
    }

    /// <summary>Cores de status Fluent (SystemFillColor*) do tema efetivo da barra.</summary>
    public static Brush DotBrush(AgentState state)
    {
        bool dark = App.IsDarkTheme;
        Windows.UI.Color c = state switch
        {
            AgentState.Running or AgentState.WaitingResponse => dark ? Rgb(0x6CCB5F) : Rgb(0x0F7B0F),
            AgentState.WaitingApproval or AgentState.WaitingInput => dark ? Rgb(0xFCE100) : Rgb(0x9D5D00),
            AgentState.Error => dark ? Rgb(0xFF99A4) : Rgb(0xC42B1C),
            AgentState.Completed => new UISettingsAccessor().Accent(dark),
            _ => dark ? Rgb(0x9D9D9D) : Rgb(0x8A8A8A),
        };
        return new SolidColorBrush(c);
    }

    private static Windows.UI.Color Rgb(int v) => Windows.UI.Color.FromArgb(255, (byte)(v >> 16), (byte)(v >> 8), (byte)v);

    private readonly struct UISettingsAccessor
    {
        public Windows.UI.Color Accent(bool dark)
        {
            try { return new Windows.UI.ViewManagement.UISettings().GetColorValue(dark ? Windows.UI.ViewManagement.UIColorType.AccentLight2 : Windows.UI.ViewManagement.UIColorType.AccentDark1); }
            catch { return Rgb(0x0078D4); }
        }
    }

    private static string Relative(DateTimeOffset t)
    {
        var d = DateTimeOffset.Now - t;
        if (d < TimeSpan.FromMinutes(1)) return global::WinStatusBar.Services.Localization.T("agora");
        if (d < TimeSpan.FromHours(1)) return global::WinStatusBar.Services.Localization.F("há {0} min", new object?[] {(int)d.TotalMinutes});
        if (d < TimeSpan.FromDays(1)) return global::WinStatusBar.Services.Localization.F("há {0} h", new object?[] {(int)d.TotalHours});
        return t.ToLocalTime().ToString("dd/MM HH:mm");
    }

    private void RecordHistories(RamSample ram, NetworkSample net, GpuSample? gpu)
    {
        RamUsedHistory.Add(ram.TotalBytes - ram.AvailableBytes);
        NetUpHistory.Add(net.UploadBytesPerSec ?? 0);
        NetDownHistory.Add(net.DownloadBytesPerSec ?? 0);
        if (gpu?.Adapter is not { } adapter) return;
        if (adapter.Luid != _historyLuid)
        {
            // Outro adaptador escolhido: o histórico anterior não se aplica.
            _historyLuid = adapter.Luid;
            GpuDedicatedHistory.Clear(); GpuSharedHistory.Clear(); _gpuEngines.Clear();
        }
        GpuDedicatedHistory.Add(gpu.DedicatedUsedBytes ?? 0);
        GpuSharedHistory.Add(gpu.SharedUsedBytes ?? 0);
        if (Gpu.LastEngineValues is { } engines)
            foreach (var (type, value) in GpuMetricsProvider.EngineTypeUtilization(engines, adapter.LuidKey))
                _gpuEngines.GetOrAdd(type, _ => new RingHistory()).Add(value);
    }

    public void Dispose()
    {
        _briefing?.Dispose();
        _cts?.Cancel();
        Pipe.Dispose();
        Codex.Dispose();
        Claude.Dispose();
        ChatGptChat.Dispose();
        ClaudeChat.Dispose();
        QiraChat.Dispose();
        lock (_sampleGate) { Cpu.Dispose(); Gpu.Dispose(); CoreHistory.Dispose(); }
    }
}

internal static class AgentOptionRight
{
    private static Microsoft.UI.Xaml.Media.Brush? B(string key)
        => Microsoft.UI.Xaml.Application.Current.Resources.TryGetValue(key, out var v) ? v as Microsoft.UI.Xaml.Media.Brush : null;

    /// <summary>Cor do anel pela situação (sempre pelo % restante): verde, âmbar ≤ 35 %, vermelho ≤ 10 %.</summary>
    private static Microsoft.UI.Xaml.Media.Brush? RingBrush(MonitoringHub.Ring? r) => r is null ? null
        : B(r.Remaining <= 10 ? "SystemFillColorCriticalBrush" : r.Remaining <= 35 ? "SystemFillColorCautionBrush" : "SystemFillColorSuccessBrush");

    /// <summary>Última atividade bem curta: primeiro trecho (antes de "·" ou ponto), até 48 caracteres.</summary>
    private static string ShortSummary(string text)
    {
        string t = text.Split('·', '\n')[0].Trim();
        int dot = t.IndexOf(". ", StringComparison.Ordinal);
        if (dot > 0) t = t[..dot];
        return t.Length > 48 ? t[..47].TrimEnd() + "…" : t;
    }

    public static AgentOption WithRight(this AgentOption o, MonitoringHub.Right r)
    {
        // Um anel só fica na coluna da direita, alinhado com o segundo anel dos demais serviços.
        var (r1, r2) = r.Ring2 is null && r.Ring1 is not null ? (null, r.Ring1) : (r.Ring1, r.Ring2);
        string when = o.Detail.Contains('·') ? o.Detail[(o.Detail.LastIndexOf('·') + 1)..].Trim() : o.Detail;
        return new()
        {
            Key = o.Key, Name = o.Name, Icon = o.Icon, Glyph = o.Glyph, Title = o.Title, Caption = o.Caption, State = o.State,
            DotBrush = o.DotBrush, Detail = o.Detail, Quota = o.Quota, IsSelected = o.IsSelected,
            Status = r.Status, StatusForeground = B(r.Foreground), StatusBackground = B(r.Background), NeedsSetup = r.NeedsSetup,
            Line1 = o.State + " · " + o.Title + (when.Length > 0 ? " · " + when : ""),
            // Resumo breve da última tarefa (andamento ou desfecho), só quando há atividade dessa IA.
            Summary = o.Detail.Length > 0 && !string.IsNullOrWhiteSpace(o.Caption) ? ShortSummary(o.Caption) : "",
            Line2 = r.Meta,
            Ring1Label = r1?.Label ?? "", Ring1Center = r1?.Center ?? "", Ring1Value = r1?.Value ?? 0, Ring1Reset = r1?.Reset ?? "", Ring1Brush = RingBrush(r1),
            Ring2Label = r2?.Label ?? "", Ring2Center = r2?.Center ?? "", Ring2Value = r2?.Value ?? 0, Ring2Reset = r2?.Reset ?? "", Ring2Brush = RingBrush(r2),
            RingNote = r1 is null && r2 is null ? r.RingNote : "",
        };
    }
}
