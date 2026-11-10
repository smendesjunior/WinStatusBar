using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
using WinStatusBar.Core;
using WinStatusBar.Core.Ai;
using WinStatusBar.Core.Ai.Codex;
using WinStatusBar.Core.Ai.Codex.ApiResume;
using WinStatusBar.Core.Integration;
using WinStatusBar.Core.Settings;

namespace WinStatusBar.Services;

/// <summary>
/// Host do supervisor da continuação pela API na barra (uma instância em App.ApiResume): chave, painel, detector, balões,
/// nota "Pela API" na sessão, PC acordado enquanto algo roda e Ctrl+C pelo auxiliar --parar-codex.
/// A chave só é carregada para o início e vai só ao ambiente do processo filho; nunca é registrada.
/// </summary>
internal sealed class CodexApiResumeService : IApiResumeHost
{
    private readonly MonitoringHub _hub;
    private readonly SettingsService _settings;
    private readonly DispatcherQueue _ui;
    private readonly OpenAiModelCheck _models = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly SemaphoreSlim _scanGate = new(1, 1);
    private readonly HashSet<string> _dismissed = new(StringComparer.Ordinal);
    private DispatcherQueueTimer? _dashboardTimer;
    private bool _stopped, _awake;
    private (bool Value, DateTimeOffset At)? _hasKey;

    public CodexApiResumeService(MonitoringHub hub, SettingsService settings, DispatcherQueue ui)
    {
        _hub = hub;
        _settings = settings;
        _ui = ui;
        Supervisor = new ApiResumeSupervisor(this, new DetachedProcessLauncher(), new ApiResumeStore(ApiResumeStore.DefaultRoot));
    }

    public ApiResumeSupervisor Supervisor { get; }
    /// <summary>Versão da Loja: fechar ou atualizar a barra encerraria a continuação no meio, então o botão fica desligado.</summary>
    public bool IsStoreBuild => PackageContext.IsPackaged;
    public bool CodexBlocked => _hub.Codex.Quota?.IsBlocked(Now) == true;
    public QuotaStopScan? LastScan { get; private set; }

    /// <summary>Há chave? Só confere a existência (arquivo protegido ou credencial do Cofre), nunca carrega a chave. Cache de 30 s.</summary>
    public bool HasKey
    {
        get
        {
            if (_hasKey is { } cached && Now - cached.At < TimeSpan.FromSeconds(30)) return cached.Value;
            bool exists = OpenAiApiKey.Exists() || (OpenAiApi() is { } api && ApiKeyStore.Exists(api.Resource));
            _hasKey = (exists, Now);
            return exists;
        }
    }

    /// <summary>Não terminadas e terminadas há menos de 24 h ainda não dispensadas; mais novas primeiro.</summary>
    public IReadOnlyList<ApiResumeJob> VisibleJobs => Supervisor.Jobs
        .Where(j => !j.IsFinished || (!_dismissed.Contains(j.Id) && Now - (j.EndedAt ?? j.UpdatedAt) < TimeSpan.FromHours(24)))
        .OrderByDescending(j => j.CreatedAt).ToList();

    /// <summary>A continuação da sessão do Codex mostrada: não terminada, ou terminada há menos de 15 min.</summary>
    public ApiResumeJob? JobFor(AgentSession? session) => session is { Kind: AgentKind.Codex } s
        ? Supervisor.Jobs.Where(j => j.ThreadId == s.Id && (!j.IsFinished || Now - (j.EndedAt ?? j.UpdatedAt) < TimeSpan.FromMinutes(15)))
            .OrderByDescending(j => j.CreatedAt).FirstOrDefault()
        : null;

    /// <summary>Uma continuação ativa de outra conversa (para a faixa no Acompanhamento).</summary>
    public ApiResumeJob? ActiveElsewhere(AgentSession? session) =>
        Supervisor.Jobs.FirstOrDefault(j => j.IsActive && j.ThreadId != session?.Id);

    public void Start()
    {
        _ = CodexModelLevels.EnsureAsync(_cts.Token);
        _hub.Codex.QuotaChanged += OnQuotaChanged;
        Supervisor.Changed += OnSupervisorChanged;
        _ = StartSupervisorAsync();
    }

    private async Task StartSupervisorAsync()
    {
        try { await Supervisor.StartAsync(_cts.Token).ConfigureAwait(false); }
        catch (Exception e) { App.Log($"Continuar pela API: não iniciou ({e.GetType().Name}: {ApiResumeErrors.Redact(e.Message)})"); }
    }

    private void OnQuotaChanged(object? sender, EventArgs e) => Supervisor.QuotaChanged();

    /// <summary>A seção do menu da cota apareceu: busca as conversas paradas (cache de 60 s).</summary>
    public void SectionShown() => _ = ScanAsync(TimeSpan.FromSeconds(60));

    private async Task<QuotaStopScan?> ScanAsync(TimeSpan maxAge)
    {
        if (!await _scanGate.WaitAsync(0).ConfigureAwait(false)) return LastScan;
        try
        {
            var scan = await CodexQuotaStops.FindAsync(_hub.Codex.Home, maxAge, _cts.Token, App.Log).ConfigureAwait(false);
            LastScan = scan;
            _hub.Codex.AdoptQuotaStops(scan.Stops, Now);
            return scan;
        }
        catch (Exception e) when (e is not OperationCanceledException || !_cts.IsCancellationRequested)
        {
            App.Log($"Continuar pela API: busca de conversas paradas falhou ({e.GetType().Name})");
            return LastScan;
        }
        finally { _scanGate.Release(); }
    }

    /// <summary>Monta a lista para marcar: conferência grátis do modelo, da pasta e da trava de cada conversa parada.</summary>
    public async Task<ApiResumeChoices> PrepareAsync(CancellationToken ct)
    {
        var scan = await ScanAsync(TimeSpan.FromSeconds(60)).ConfigureAwait(false)
            ?? new QuotaStopScan([], new QuotaStopExclusions(0, 0, 0, 0, 0), false, Now);
        string? key = LoadKey(out string source);
        var returns = _hub.Codex.Quota?.BlockedUntil(Now);
        var queued = Supervisor.Jobs.Where(j => !j.IsFinished).Select(j => j.ThreadId).ToHashSet(StringComparer.Ordinal);
        string? organization = null;
        var candidates = new List<ApiResumeCandidate>();
        foreach (var stop in scan.Stops)
        {
            ModelCheck? check = null;
            if (key is not null && !string.IsNullOrEmpty(stop.Model))
            {
                check = await _models.CheckAsync(stop.Model, key, fresh: false, ct).ConfigureAwait(false);
                if (check.Result == ModelCheckResult.Ok) organization ??= check.Organization;
            }
            var lockState = ProbeLock(stop.ThreadId);
            candidates.Add(ApiResumeCandidates.Evaluate(stop, Levels(stop.Model ?? ""), check, DirectoryExists, lockState,
                queued.Contains(stop.ThreadId), returns));
        }
        key = null;
        App.Log(ApiResumeCandidates.Log(candidates, scan.Excluded));
        return new ApiResumeChoices(candidates, scan.Excluded, scan.UsedFallback, source.Length == 0 ? null : source,
            organization, OpenAiDashboardSession.Current?.Account);
    }

    /// <summary>Confirma as marcadas: pede uma base recente do painel e põe na fila (da mais nova para a mais antiga).</summary>
    public int Confirm(IReadOnlyList<QuotaStop> stops, bool parallel)
    {
        RequestDashboardRefresh();
        if (stops.Count >= 2 && _settings.Current.CodexApiParallel != parallel)
        {
            _settings.Current.CodexApiParallel = parallel;
            _settings.Save();
        }
        return Supervisor.Enqueue(stops.OrderByDescending(s => s.StoppedAt).ToList(), stops.Count >= 2 && parallel);
    }

    /// <summary>[OK] no cartão de uma continuação terminada: some da lista.</summary>
    public void Acknowledge(string jobId)
    {
        _dismissed.Add(jobId);
        OnSupervisorChanged();
    }

    /// <summary>Barra fechando: só para timers e o laço e grava. Nunca espera nem encerra o codex.exe.</summary>
    public void Shutdown()
    {
        _stopped = true;
        _dashboardTimer?.Stop();
        _hub.Codex.QuotaChanged -= OnQuotaChanged;
        Supervisor.Changed -= OnSupervisorChanged;
        SetThreadExecutionState(ES_CONTINUOUS);
        _cts.Cancel();
        _ = Supervisor.ShutdownAsync();
    }

    private void OnSupervisorChanged()
    {
        bool awake = Supervisor.KeepAwake;
        if (Supervisor.Jobs.Any(j => j.IsFinished && j.Notified && j.EndedAt is { } end && Now - end < TimeSpan.FromSeconds(10)))
            _ = ScanAsync(TimeSpan.Zero); // fim de uma continuação: a lista de paradas muda
        _ui.TryEnqueue(() =>
        {
            if (_stopped) return;
            // PC acordado enquanto algo inicia, roda ou para; a tela pode apagar normalmente.
            if (awake != _awake)
            {
                _awake = awake;
                SetThreadExecutionState(awake ? ES_CONTINUOUS | ES_SYSTEM_REQUIRED : ES_CONTINUOUS);
            }
            if (awake && _dashboardTimer is null)
            {
                _dashboardTimer = _ui.CreateTimer();
                _dashboardTimer.Interval = TimeSpan.FromMinutes(3);
                _dashboardTimer.Tick += (_, _) => RequestDashboardRefresh();
            }
            if (awake) _dashboardTimer?.Start(); else _dashboardTimer?.Stop();
            _hub.QueueAgentRefresh();
        });
    }

    // ---------------- IApiResumeHost ----------------

    public DateTimeOffset Now => DateTimeOffset.Now;
    public bool ScreenOn => !Presence.Away;
    public QuotaSnapshot? CodexQuota => _hub.Codex.Quota;
    public DashboardSnapshot? Dashboard => OpenAiDashboardSession.Current;

    public string? FindCodexExecutable() => CodexAppServerClient.FindCodexExecutable();

    public string? LoadKey(out string sourceName)
    {
        if (OpenAiApiKey.Read() is { Length: > 0 } fileKey) { sourceName = OpenAiApiKey.FileSourceName; return fileKey; }
        if (OpenAiApi() is { } api && ApiKeyStore.Get(api.Resource) is { Length: > 0 } vaultKey)
        {
            sourceName = Localization.F("API própria «{0}» (Cofre de Credenciais do Windows)", api.Name);
            return vaultKey;
        }
        sourceName = "";
        return null;
    }

    private CustomApi? OpenAiApi() => _settings.Current.CustomApis.FirstOrDefault(a =>
        a.Kind == "openai" && Uri.TryCreate(a.BaseUrl.Trim(), UriKind.Absolute, out var u)
        && u.Host.Equals("api.openai.com", StringComparison.OrdinalIgnoreCase));

    public string? ReadCodexConfig()
    {
        try
        {
            string path = Path.Combine(_hub.Codex.Home, "config.toml");
            return File.Exists(path) ? File.ReadAllText(path) : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }

    public Task<ModelCheck> CheckModelAsync(string model, string apiKey, bool fresh, CancellationToken ct) =>
        _models.CheckAsync(model, apiKey, fresh, ct);

    public CodexModelLevels.Levels? Levels(string model) => CodexModelLevels.Get(model);

    public bool DirectoryExists(string path)
    {
        try { return Directory.Exists(path); } catch { return false; }
    }

    public RolloutTail? ReadTail(string rolloutPath) => CodexRolloutTail.Read(rolloutPath);

    public ThreadLockState ProbeLock(string threadId)
    {
        try { return CodexThreadLock.Probe(CodexThreadLock.PathFor(_hub.Codex.Home, threadId)); }
        catch (ArgumentException) { return ThreadLockState.Unknown; }
    }

    public bool ChatGptAppRunning()
    {
        var processes = Process.GetProcessesByName("ChatGPT");
        try { return processes.Length > 0; }
        finally { foreach (var p in processes) p.Dispose(); }
    }

    /// <summary>Espera o app terminar aguardando os processos (sem sondar).</summary>
    public async Task WaitChatGptExitAsync(CancellationToken ct)
    {
        var processes = Process.GetProcessesByName("ChatGPT");
        try { await Task.WhenAll(processes.Select(p => p.WaitForExitAsync(ct))).ConfigureAwait(false); }
        catch (OperationCanceledException) { }
        catch (InvalidOperationException) { }
        finally { foreach (var p in processes) p.Dispose(); }
    }

    public TokenUsage? ExecTurnTokens(string threadId, DateTimeOffset startedAt) =>
        _hub.Codex.SessionById(threadId) is { TurnTrigger: "exec" } s && s.StartedAt >= startedAt ? s.TurnTokens : null;

    public void RequestDashboardRefresh() => _ui.TryEnqueue(() =>
    {
        if (!_stopped && WinStatusBar.Views.OpenAiDashboardView.Enabled && !Presence.Away) WinStatusBar.Views.OpenAiDashboardView.Refresh();
    });

    /// <summary>Ctrl+C pelo auxiliar sem console (Automation --parar-codex): a barra nunca mexe no próprio console.</summary>
    public async Task<InterruptOutcome> SendInterruptAsync(int pid, DateTimeOffset createdAt)
    {
        string helper = Path.Combine(AppContext.BaseDirectory, "Automation", "WinStatusBar.Automation.exe");
        if (!File.Exists(helper)) return InterruptOutcome.HelperMissing;
        try
        {
            var psi = new ProcessStartInfo(helper) { UseShellExecute = false, CreateNoWindow = true };
            foreach (var arg in ConsoleInterrupt.HelperArgs(pid, createdAt)) psi.ArgumentList.Add(arg);
            using var p = Process.Start(psi);
            if (p is null) return InterruptOutcome.HelperFailed;
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            try { await p.WaitForExitAsync(timeout.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) { return InterruptOutcome.HelperFailed; }
            return ConsoleInterrupt.FromExitCode(p.ExitCode);
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or IOException) { return InterruptOutcome.HelperFailed; }
    }

    public void SetNote(string threadId, string? note, DateTimeOffset? keepUntil) => _hub.Codex.SetNote(threadId, note, keepUntil);

    public void Notify(ApiResumeNotice notice) => _ui.TryEnqueue(() =>
    {
        if (_stopped) return;
        string? id = notice.ThreadId;
        App.Current.Notify(notice.Title, notice.Message, id is null ? null : () => WindowsLauncherService.CodexThread(id));
    });

    public void Log(string line) => App.Log(line);

    // ---------------- energia ----------------

    private const uint ES_CONTINUOUS = 0x80000000, ES_SYSTEM_REQUIRED = 0x00000001;

    [DllImport("kernel32.dll")]
    private static extern uint SetThreadExecutionState(uint flags);
}
