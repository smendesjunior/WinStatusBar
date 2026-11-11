using System.Collections.Concurrent;
using System.Globalization;

namespace WinStatusBar.Core.Ai.Codex.ApiResume;

/// <summary>Estado da cota pela última leitura oficial: só uma leitura oficial bloqueada deixa a fila andar.</summary>
internal enum QuotaGate { Blocked, Back, Unknown }

/// <summary>
/// Conduz as continuações pela API: fila (uma de cada vez ou todas), espera pelo app do Codex, início destacado, leitura dos
/// eventos, gasto, Parar (Ctrl+C pelo auxiliar), fim, reconexão depois de a barra reabrir e a volta da cota.
/// Um único laço aplica os pedidos da tela e avança cada continuação; cada mudança de estado é gravada antes da ação.
/// A barra nunca encerra o codex.exe sozinha.
/// </summary>
public sealed class ApiResumeSupervisor(IApiResumeHost host, IApiResumeLauncher launcher, ApiResumeStore store)
{
    private const string Prefix = "Continuar pela API: ";
    private static readonly TimeSpan SpendEvery = TimeSpan.FromMinutes(3);
    private static readonly TimeSpan[] RefreshAfterEnd = [TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(15), TimeSpan.FromMinutes(60), TimeSpan.FromMinutes(180)];
    private static readonly TimeSpan SecondInterrupt = TimeSpan.FromSeconds(15), OfferForce = TimeSpan.FromSeconds(30);

    public event Action? Changed;

    private readonly List<ApiResumeJob> _jobs = [];
    private readonly object _lock = new();
    private readonly SemaphoreSlim _tick = new(1, 1);
    private readonly SemaphoreSlim _wake = new(0);
    private readonly ConcurrentQueue<Func<Task>> _requests = new();
    private readonly Dictionary<string, WaitState> _waits = new(StringComparer.Ordinal);
    /// <summary>Conflitos -32600 seguidos por continuação (a sonda diz livre, mas o Codex diz que a conversa está aberta).</summary>
    private readonly Dictionary<string, int> _conflicts = new(StringComparer.Ordinal);
    private IReadOnlyList<ApiResumeJob> _published = [];
    private CancellationTokenSource? _loop;
    private Task? _loopTask;

    /// <summary>Nos testes, o laço não roda sozinho: cada passo vem de <see cref="TickAsync"/>.</summary>
    internal bool ManualTicks { get; init; }

    private sealed class WaitState
    {
        public bool? AppOpen;
        public Task? ChatGptExit;
        public DateTimeOffset? ProbeUntil;
        public DateTimeOffset NextProbe;
    }

    public IReadOnlyList<ApiResumeJob> Jobs => _published;
    public bool HasUnfinished => _published.Any(j => !j.IsFinished);
    public bool KeepAwake => _published.Any(j => j.IsActive);

    internal static TimeSpan PollInterval(bool screenOn) => TimeSpan.FromSeconds(screenOn ? 2 : 10);

    internal static QuotaGate Gate(QuotaSnapshot? quota, DateTimeOffset now) =>
        quota?.Access?.Official != true ? QuotaGate.Unknown : quota.IsBlocked(now) ? QuotaGate.Blocked : QuotaGate.Back;

    // ---------------- pedidos da tela ----------------

    /// <summary>Põe na fila, da parada mais nova para a mais antiga; pula conversa que já tem continuação não terminada.
    /// Turno inválido entra já bloqueado. Devolve quantas entraram.</summary>
    public int Enqueue(IReadOnlyList<QuotaStop> stops, bool parallel)
    {
        int added = 0;
        lock (_lock)
        {
            var now = host.Now;
            foreach (var stop in stops.OrderByDescending(s => s.StoppedAt))
            {
                if (_jobs.Any(j => j.ThreadId == stop.ThreadId && !j.IsFinished)) continue;
                var spec = CodexResumeCommand.ToSpec(stop, host.DirectoryExists, out _);
                var job = ApiResumeJob.From(stop, spec with { ExtraDirs = [.. stop.ExtraRoots.Concat(stop.WritableRoots).Where(d => !string.IsNullOrWhiteSpace(d)
                    && !string.Equals(d.TrimEnd('\\', '/'), (stop.Cwd ?? "").TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase)).Distinct(StringComparer.OrdinalIgnoreCase)] }, now);
                job.Parallel = parallel;
                if (CodexResumeCommand.Check(stop, host.Levels(stop.Model ?? "")) is { } problem)
                {
                    job.State = ApiResumeState.Blocked;
                    job.Error = ApiResumeErrors.For(problem, stop.Model, stop.Effort, stop.Cwd, host.CodexQuota?.BlockedUntil(now));
                    job.EndedAt = now;
                    host.Log($"{Prefix}bloqueada conversa={job.ThreadId} motivo={job.Error.Short}");
                }
                else host.Log($"{Prefix}fila +1 conversa={job.ThreadId} modelo={job.Model} nível={job.Effort} acesso={job.SandboxMode} aprovação={job.ApprovalPolicy}");
                _jobs.Add(job);
                added++;
            }
        }
        if (added > 0) { Save(); Wake(); }
        return added;
    }

    public void Stop(string id) => Post(() => StopAsync(id));
    public void ForceKill(string id) => Post(() => { ForceKillNow(id); return Task.CompletedTask; });
    public void AllowWhenQuotaBack(string id) => Post(() =>
    {
        if (Find(id) is { State: ApiResumeState.QuotaBackWaitingUser } job)
        {
            job.AllowWhenQuotaBack = true;
            job.State = ApiResumeState.Queued;
            job.UpdatedAt = host.Now;
            Save();
        }
        return Task.CompletedTask;
    });
    public void RemoveFromQueue(string id) => Post(() =>
    {
        if (Find(id) is { State: ApiResumeState.Queued or ApiResumeState.WaitingForApp or ApiResumeState.QuotaBackWaitingUser } job)
        {
            job.State = ApiResumeState.Removed;
            job.EndedAt = job.UpdatedAt = host.Now;
            _waits.Remove(job.Id);
            Save();
        }
        return Task.CompletedTask;
    });
    /// <summary>[Tentar de novo] e [Continuar de novo pela API]: só depois de Falhou ou Concluída; a conferência do registro decide.</summary>
    public void Retry(string id) => Post(() =>
    {
        if (Find(id) is { State: ApiResumeState.Failed or ApiResumeState.Completed } job)
        {
            ResetRun(job);
            job.State = ApiResumeState.Queued;
            job.EndedAt = null;
            job.Notified = false;
            job.RefreshesDone = 0;
            job.EndedWhileBarClosed = false;
            job.CreatedAt = job.UpdatedAt = host.Now;
            Save();
        }
        return Task.CompletedTask;
    });
    public void QuotaChanged() => Post(() => Task.CompletedTask);

    private void Post(Func<Task> request) { _requests.Enqueue(request); Wake(); }
    private void Wake() { if (_wake.CurrentCount == 0) _wake.Release(); }
    private ApiResumeJob? Find(string id) { lock (_lock) return _jobs.FirstOrDefault(j => j.Id == id); }

    // ---------------- ciclo de vida ----------------

    /// <summary>Carrega o registro, reconecta as continuações vivas e classifica as que terminaram com a barra fechada.</summary>
    public async Task StartAsync(CancellationToken ct)
    {
        await _tick.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var now = host.Now;
            List<ApiResumeJob> loaded;
            lock (_lock)
            {
                loaded = ApiResumeStore.Prune(store.Load(host.Log), now);
                _jobs.Clear();
                _jobs.AddRange(loaded);
                _waits.Clear();
            }
            try { store.DeleteOrphanFolders(loaded); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            foreach (var job in loaded.Where(j => j.IsActive))
            {
                if (job.Pid is { } pid && job.ProcessCreatedAt is { } created && launcher.IsSame(pid, created))
                    host.Log($"{Prefix}reconectado pid={pid} (barra reiniciada)");
                else
                {
                    job.EndedWhileBarClosed = true;
                    await FinishAsync(job, now).ConfigureAwait(false);
                }
            }
            Save();
        }
        finally { _tick.Release(); }
        if (!ManualTicks)
        {
            _loop = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var token = _loop.Token;
            _loopTask = Task.Run(() => LoopAsync(token), CancellationToken.None);
        }
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { await TickAsync(ct).ConfigureAwait(false); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception e) { host.Log($"{Prefix}erro no laço ({e.GetType().Name}: {ApiResumeErrors.Redact(e.Message)})"); }
            try { await _wake.WaitAsync(PollInterval(host.ScreenOn), ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
        }
    }

    /// <summary>Para o laço e os timers e grava. Nunca encerra o codex.exe: a continuação segue rodando.</summary>
    public async Task ShutdownAsync()
    {
        _loop?.Cancel();
        if (_loopTask is { } loop)
        {
            try { await loop.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false); } catch (Exception) { }
        }
        Save();
        foreach (var job in _published.Where(j => j.IsActive && j.Pid is not null))
            host.Log($"{Prefix}barra encerrando; conversa={job.ThreadId} segue rodando (pid {job.Pid})");
    }

    /// <summary>Aplica os pedidos e avança cada continuação um passo.</summary>
    internal async Task TickAsync(CancellationToken ct = default)
    {
        await _tick.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            while (_requests.TryDequeue(out var request)) await request().ConfigureAwait(false);
            var now = host.Now;
            ApplyQuotaBack(now);
            List<ApiResumeJob> jobs;
            lock (_lock) jobs = [.. _jobs];
            bool dirty = false;
            foreach (var job in jobs)
            {
                string before = Signature(job);
                await StepAsync(job, now, ct).ConfigureAwait(false);
                dirty |= Signature(job) != before;
            }
            if (dirty) Save();
        }
        finally { _tick.Release(); }
    }

    private static string Signature(ApiResumeJob j) =>
        $"{j.State}|{j.Detail}|{j.EventsOffset}|{j.TurnStarted}|{j.Tokens}|{j.Spend}|{j.Summary}|{j.ForceOffered}|{j.Interrupts}|{j.RefreshesDone}|{j.Reminders}";

    private async Task StepAsync(ApiResumeJob job, DateTimeOffset now, CancellationToken ct)
    {
        switch (job.State)
        {
            case ApiResumeState.Queued:
                if (MayAdvance(job, now)) await TryStartAsync(job, now, ct).ConfigureAwait(false);
                break;
            case ApiResumeState.WaitingForApp:
                if (MayAdvance(job, now)) await WaitStepAsync(job, now, ct).ConfigureAwait(false);
                break;
            case ApiResumeState.Starting:
            case ApiResumeState.Running:
            case ApiResumeState.Stopping:
                await RunStepAsync(job, now).ConfigureAwait(false);
                break;
            case ApiResumeState.Completed:
            case ApiResumeState.Stopped:
            case ApiResumeState.Failed:
                RefreshStep(job, now);
                break;
        }
    }

    /// <summary>A cota precisa estar bloqueada pela leitura oficial (ou liberada com "mesmo assim"); na fila, só a primeira anda.</summary>
    private bool MayAdvance(ApiResumeJob job, DateTimeOffset now)
    {
        var gate = Gate(host.CodexQuota, now);
        if (gate == QuotaGate.Unknown || (gate == QuotaGate.Back && !job.AllowWhenQuotaBack)) return false;
        if (job.Parallel) return true;
        lock (_lock)
        {
            var head = _jobs.Where(j => !j.Parallel && j.State is ApiResumeState.Queued or ApiResumeState.WaitingForApp
                    or ApiResumeState.Starting or ApiResumeState.Running or ApiResumeState.Stopping)
                .OrderBy(j => j.CreatedAt).FirstOrDefault();
            return ReferenceEquals(head, job);
        }
    }

    /// <summary>A cota voltou: as que esperam param até você decidir ("mesmo assim" ou "tirar da fila"); as ativas seguem.</summary>
    private void ApplyQuotaBack(DateTimeOffset now)
    {
        if (Gate(host.CodexQuota, now) != QuotaGate.Back) return;
        List<ApiResumeJob> waiting;
        lock (_lock) waiting = _jobs.Where(j => j.State is ApiResumeState.Queued or ApiResumeState.WaitingForApp && !j.AllowWhenQuotaBack).ToList();
        if (waiting.Count == 0) return;
        foreach (var job in waiting)
        {
            job.State = ApiResumeState.QuotaBackWaitingUser;
            job.UpdatedAt = now;
            _waits.Remove(job.Id);
        }
        Save();
        host.Notify(new ApiResumeNotice(LocalizedText.T("Continuar pela API"), ApiResumeText.QuotaBackNotice(waiting.Count), null));
    }

    // ---------------- vez da continuação (regra 4) ----------------

    private async Task TryStartAsync(ApiResumeJob job, DateTimeOffset now, CancellationToken ct)
    {
        var returns = host.CodexQuota?.BlockedUntil(now);
        string? exe = DetachedProcess.RequireCodexExe(host.FindCodexExecutable());
        if (exe is null) { Block(job, ApiResumeProblem.CodexNotFound, now, returns); return; }
        string? key = host.LoadKey(out _);
        if (string.IsNullOrEmpty(key)) { Block(job, ApiResumeProblem.NoKey, now, returns); return; }
        var facts = CodexResumeCommand.ReadConfig(host.ReadCodexConfig());
        if (facts.ForcedChatGptLogin) { Block(job, ApiResumeProblem.ForcedChatGptLogin, now, returns); return; }

        ModelCheck check;
        try { check = await host.CheckModelAsync(job.Model, key, fresh: true, ct).ConfigureAwait(false); }
        catch (Exception e) when (e is not OperationCanceledException) { check = new ModelCheck(ModelCheckResult.OtherError, null, e.Message, now); }
        switch (check.Result)
        {
            case ModelCheckResult.ModelNotFound: Block(job, ApiResumeProblem.ModelNotInApi, now, returns, technical: check.Detail); return;
            case ModelCheckResult.KeyRejected: Block(job, ApiResumeProblem.KeyRejected, now, returns, technical: check.Detail); return;
            case ModelCheckResult.OtherError: Fail(job, AiErrorExplainer.ExplainApiResume(check.Detail, job.Model, returns), now); return;
        }
        if (host.Levels(job.Model) is { Efforts.Count: > 0 } levels && !levels.Efforts.Contains(job.Effort, StringComparer.Ordinal))
        { Block(job, ApiResumeProblem.EffortNotAccepted, now, returns); return; }
        if (string.IsNullOrEmpty(job.Cwd) || !host.DirectoryExists(job.Cwd)) { Block(job, ApiResumeProblem.FolderUnavailable, now, returns); return; }

        var tail = host.ReadTail(job.RolloutPath);
        if (tail is null) { Block(job, ApiResumeProblem.ThreadNotFound, now, returns); return; }
        bool sameStop = tail.Verdict == RolloutVerdict.QuotaStop && tail.LastTurnId == job.StoppedTurnId;
        bool ownApiTurn = job.ApiTurnId is not null && tail.LastTurnId == job.ApiTurnId && tail.Verdict != RolloutVerdict.Running;
        if (!sameStop && !ownApiTurn)
        {
            if (tail.Verdict == RolloutVerdict.QuotaStop) Block(job, ApiResumeProblem.StoppedAgainByQuota, now, returns, at: tail.LastLifecycleAt);
            else
            {
                job.State = ApiResumeState.AlreadyContinued;
                job.Error = ApiResumeErrors.For(ApiResumeProblem.AlreadyContinued, at: tail.LastLifecycleAt);
                job.EndedAt = job.UpdatedAt = now;
                host.Log($"{Prefix}já continuou conversa={job.ThreadId}");
                Save();
                host.Notify(new ApiResumeNotice(job.Title, ApiResumeText.State(job, now), job.ThreadId));
            }
            return;
        }

        if (host.ProbeLock(job.ThreadId) != ThreadLockState.Free) { EnterWaiting(job, now, ct); return; }
        Launch(job, exe, key, tail, facts, now);
    }

    private void Launch(ApiResumeJob job, string exe, string key, RolloutTail tail, CodexConfigFacts facts, DateTimeOffset now)
    {
        var present = job.ExtraDirs.Where(host.DirectoryExists).ToList();
        foreach (var missing in job.ExtraDirs.Except(present))
            host.Log($"{Prefix}pasta extra ausente conversa={job.ThreadId} pasta={missing}");
        IReadOnlyList<string> args;
        try { args = CodexResumeCommand.Arguments(job.ToSpec() with { ExtraDirs = present }, facts.EnvExcludes, facts.Profile); }
        catch (ArgumentException) { Block(job, ApiResumeProblem.MissingTurnData, now, null); return; }

        ResetRun(job);
        job.Baseline = host.Dashboard;
        job.RolloutOffset = tail.CompleteLength;
        job.State = ApiResumeState.Starting;
        job.UpdatedAt = now;
        Save(); // "Iniciando" fica gravado antes do processo existir

        DetachedStart started;
        try
        {
            Directory.CreateDirectory(store.FolderOf(job.Id));
            started = launcher.Start(exe, args, job.Cwd, store.EventsFile(job.Id), store.StderrFile(job.Id), DetachedProcess.ResumeEnvironment(key));
        }
        catch (Exception e)
        {
            Fail(job, AiErrorExplainer.ExplainApiResume(e.Message, job.Model, host.CodexQuota?.BlockedUntil(now)), now);
            return;
        }
        job.Pid = started.Pid;
        job.ProcessCreatedAt = started.CreatedAt;
        job.Executable = exe;
        job.StartedAt = now;
        job.State = ApiResumeState.Running;
        job.UpdatedAt = now;
        Save();
        host.Log($"{Prefix}iniciado pid={started.Pid} conversa={job.ThreadId} exe={exe}");
        host.SetNote(job.ThreadId, LocalizedText.T("Pela API"), null);
    }

    private static void ResetRun(ApiResumeJob job)
    {
        job.Pid = null; job.ProcessCreatedAt = null; job.StartedAt = null; job.Executable = null;
        job.EventsOffset = 0; job.TurnStarted = false; job.Detail = null; job.Summary = null;
        job.Tokens = null; job.Spend = null; job.SpendAt = null; job.Error = null;
        job.StopRequestedAt = null; job.Interrupts = 0; job.ForceOffered = false;
    }

    private void Block(ApiResumeJob job, ApiResumeProblem problem, DateTimeOffset now, DateTimeOffset? returns,
        DateTimeOffset? at = null, string? technical = null)
    {
        job.State = ApiResumeState.Blocked;
        job.Error = ApiResumeErrors.For(problem, job.Model, job.Effort, job.Cwd, returns, at, technical);
        job.EndedAt = job.UpdatedAt = now;
        _waits.Remove(job.Id);
        host.Log($"{Prefix}bloqueada conversa={job.ThreadId} motivo={job.Error.Short}");
        Save();
        host.Notify(new ApiResumeNotice(job.Title, ApiResumeText.Notice(job, now).Message, job.ThreadId));
    }

    private void Fail(ApiResumeJob job, AiErrorExplanation error, DateTimeOffset now)
    {
        job.State = ApiResumeState.Failed;
        job.Error = error;
        job.EndedAt = job.UpdatedAt = now;
        _waits.Remove(job.Id);
        host.Log($"{Prefix}fim conversa={job.ThreadId} resultado={job.State} duração=0min00s entrada=0 cache=0 saída=0 US$=n/d");
        Save();
        host.Notify(new ApiResumeNotice(job.Title, ApiResumeText.Notice(job, now).Message, job.ThreadId));
    }

    // ---------------- espera pelo app (regra 5) ----------------

    private void EnterWaiting(ApiResumeJob job, DateTimeOffset now, CancellationToken ct, bool afterConflict = false)
    {
        // Depois de um conflito, é a mesma espera: não reinicia os lembretes nem manda outro balão.
        if (job.State != ApiResumeState.WaitingForApp && !afterConflict)
        {
            job.State = ApiResumeState.WaitingForApp;
            job.WaitingSince = now;
            job.Reminders = 0;
            job.LastReminderAt = null;
        }
        if (!_waits.TryGetValue(job.Id, out var ws)) _waits[job.Id] = ws = new WaitState();
        bool app = host.ChatGptAppRunning();
        SetWaitingDetail(job, ws, app);
        if (app) { ws.ChatGptExit = host.WaitChatGptExitAsync(_loop?.Token ?? ct); ws.ProbeUntil = null; }
        else ws.NextProbe = now + TimeSpan.FromSeconds(5);
        job.State = ApiResumeState.WaitingForApp;
        job.WaitingSince ??= now;
        if (afterConflict && _conflicts.GetValueOrDefault(job.Id) is >= 3 and var conflicts && !app)
        {
            // A sonda diz livre, mas o Codex insiste: recua (30 s, 2 min, depois 5 min) em vez de relançar a cada 5 s.
            ws.NextProbe = now + (conflicts == 3 ? TimeSpan.FromSeconds(30) : conflicts == 4 ? TimeSpan.FromMinutes(2) : TimeSpan.FromMinutes(5));
            job.Detail = ApiResumeText.WaitingFor(false);
        }
        job.UpdatedAt = now;
        if (job.Reminders == 0)
        {
            job.Reminders = 1;
            job.LastReminderAt = now;
            host.Notify(new ApiResumeNotice(job.Title, job.Detail!, job.ThreadId));
        }
        Save();
    }

    private void SetWaitingDetail(ApiResumeJob job, WaitState ws, bool app)
    {
        job.Detail = ApiResumeText.WaitingFor(app);
        if (ws.AppOpen == app) return;
        ws.AppOpen = app;
        host.Log($"{Prefix}aguardando o app do Codex soltar a conversa {job.ThreadId} (app aberto={(app ? "sim" : "não")})");
    }

    private async Task WaitStepAsync(ApiResumeJob job, DateTimeOffset now, CancellationToken ct)
    {
        if (!_waits.TryGetValue(job.Id, out var ws))
        {
            // depois de reabrir a barra: começa a esperar de novo, sem repetir o primeiro balão
            _waits[job.Id] = ws = new WaitState { NextProbe = now };
        }
        if (job.Reminders == 1 && job.WaitingSince is { } since && now - since >= TimeSpan.FromMinutes(30)
            || job.Reminders >= 2 && job.LastReminderAt is { } last && now - last >= TimeSpan.FromHours(2))
        {
            job.Reminders++;
            job.LastReminderAt = now;
            host.Notify(new ApiResumeNotice(job.Title, job.Detail ?? ApiResumeText.WaitingFor(false), job.ThreadId));
        }

        if (ws.ChatGptExit is { } exit)
        {
            if (!exit.IsCompleted) return; // espera o app fechar, sem sondar
            ws.ChatGptExit = null;
            ws.ProbeUntil = now + TimeSpan.FromSeconds(15);
            ws.NextProbe = now;
        }
        if (now < ws.NextProbe) return;

        if (host.ProbeLock(job.ThreadId) == ThreadLockState.Free)
        {
            host.Log($"{Prefix}trava liberada conversa={job.ThreadId}");
            _waits.Remove(job.Id);
            await TryStartAsync(job, now, ct).ConfigureAwait(false);
            return;
        }
        bool app = host.ChatGptAppRunning();
        if (ws.ProbeUntil is { } until && now < until && !app) { ws.NextProbe = now + TimeSpan.FromSeconds(1); return; }
        ws.ProbeUntil = null;
        SetWaitingDetail(job, ws, app);
        if (app) ws.ChatGptExit = host.WaitChatGptExitAsync(_loop?.Token ?? ct); // o app abriu de novo: volta a esperar
        else ws.NextProbe = now + TimeSpan.FromSeconds(5);
    }

    // ---------------- execução, parada e fim (regras 7–9) ----------------

    private async Task RunStepAsync(ApiResumeJob job, DateTimeOffset now)
    {
        if (job.Pid is not { } pid || job.ProcessCreatedAt is not { } created) { await FinishAsync(job, now).ConfigureAwait(false); return; }
        var events = CodexExecEvents.ReadNew(store.EventsFile(job.Id), job.EventsOffset, out long offset);
        foreach (var e in events) Observe(job, e);
        job.EventsOffset = offset;
        if (job.StartedAt is { } started)
        {
            job.Tokens = host.ExecTurnTokens(job.ThreadId, started) ?? job.Tokens;
            if (now - (job.SpendAt ?? started) >= SpendEvery)
            {
                job.Spend = SpendOf(job, started);
                job.SpendAt = now;
            }
        }
        if (!launcher.IsSame(pid, created)) { await FinishAsync(job, now).ConfigureAwait(false); return; }

        if (job.State == ApiResumeState.Stopping && job.StopRequestedAt is { } asked)
        {
            if (job.Interrupts == 1 && now - asked >= SecondInterrupt)
            {
                job.Interrupts = 2;
                await InterruptAsync(job, pid, created).ConfigureAwait(false);
            }
            if (!job.ForceOffered && now - asked >= OfferForce)
            {
                job.ForceOffered = true;
                job.Error = ApiResumeErrors.For(ApiResumeProblem.StopNotAnswered);
            }
        }
    }

    private static void Observe(ApiResumeJob job, ExecEvent e)
    {
        switch (e.Kind)
        {
            case ExecEventKind.TurnStarted: job.TurnStarted = true; break;
            case ExecEventKind.Status: job.Detail = e.Text; break;
            case ExecEventKind.AgentMessage when !string.IsNullOrWhiteSpace(e.Text): job.Summary = e.Text; break;
        }
    }

    private async Task StopAsync(string id)
    {
        if (Find(id) is not { State: ApiResumeState.Starting or ApiResumeState.Running } job || job.Pid is not { } pid
            || job.ProcessCreatedAt is not { } created) return;
        job.StopRequestedAt = host.Now;
        job.State = ApiResumeState.Stopping;
        job.Interrupts = 1;
        job.UpdatedAt = host.Now;
        Save(); // "Parando" gravado antes do Ctrl+C
        await InterruptAsync(job, pid, created).ConfigureAwait(false);
    }

    private async Task InterruptAsync(ApiResumeJob job, int pid, DateTimeOffset created)
    {
        var outcome = await host.SendInterruptAsync(pid, created).ConfigureAwait(false);
        host.Log($"{Prefix}Ctrl+C enviado pid={pid} resultado={outcome}");
        if (outcome == InterruptOutcome.HelperMissing)
        {
            job.ForceOffered = true;
            job.Error = ApiResumeErrors.For(ApiResumeProblem.StopHelperMissing);
        }
        Save();
    }

    /// <summary>Encerrar à força: só depois de oferecido (30 s sem parar, ou sem o auxiliar) e só se for o mesmo processo.</summary>
    private void ForceKillNow(string id)
    {
        if (Find(id) is not { State: ApiResumeState.Stopping, ForceOffered: true } job || job.Pid is not { } pid
            || job.ProcessCreatedAt is not { } created || !launcher.IsSame(pid, created)) return;
        bool killed = launcher.KillTree(pid, created);
        host.Log($"{Prefix}encerrado à força pid={pid} resultado={(killed ? "ok" : "falhou")}");
    }

    /// <summary>Classifica o fim pelos eventos e pelo registro (nunca pelo código de saída) e avisa.</summary>
    private Task FinishAsync(ApiResumeJob job, DateTimeOffset now)
    {
        bool turnStarted = job.TurnStarted, completed = false;
        TokenUsage? finalUsage = null;
        string? failure = null;
        var all = CodexExecEvents.ReadNew(store.EventsFile(job.Id), 0, out long end);
        job.EventsOffset = end;
        foreach (var e in all)
        {
            Observe(job, e);
            switch (e.Kind)
            {
                case ExecEventKind.TurnStarted: turnStarted = true; break;
                case ExecEventKind.TurnCompleted: completed = true; finalUsage = e.Usage ?? finalUsage; break;
                case ExecEventKind.TurnFailed or ExecEventKind.Error: failure = e.Text ?? failure; break;
            }
        }
        string stderr = "";
        try { if (File.Exists(store.StderrFile(job.Id))) stderr = File.ReadAllText(store.StderrFile(job.Id)); } catch (IOException) { }
        var tail = host.ReadTail(job.RolloutPath);
        bool tailAfterStart = tail?.LastLifecycleAt is { } at && job.StartedAt is { } started && at > started;
        var returns = host.CodexQuota?.BlockedUntil(now);

        if (completed || tail?.Verdict == RolloutVerdict.Completed && tailAfterStart)
            job.State = ApiResumeState.Completed;
        else if (job.StopRequestedAt is not null || tail?.Verdict == RolloutVerdict.Aborted && tailAfterStart)
            job.State = ApiResumeState.Stopped;
        else if (!turnStarted && (CodexExecEvents.IsWriterConflict(stderr) || CodexExecEvents.IsWriterConflict(failure)))
        {
            // O app abriu a conversa antes do início: volta a esperar, sem custo e sem limite de tentativas.
            host.Log($"{Prefix}o app abriu a conversa antes do início (-32600); aguardando");
            _conflicts[job.Id] = _conflicts.GetValueOrDefault(job.Id) + 1;
            ResetRun(job);
            EnterWaiting(job, now, CancellationToken.None, afterConflict: true);
            return Task.CompletedTask;
        }
        else if (failure is not null)
        { job.State = ApiResumeState.Failed; job.Error = AiErrorExplainer.ExplainApiResume(failure, job.Model, returns); }
        else if (LastLine(stderr) is { } line)
        { job.State = ApiResumeState.Failed; job.Error = AiErrorExplainer.ExplainApiResume(line, job.Model, returns); }
        else
        { job.State = ApiResumeState.Failed; job.Error = ApiResumeErrors.For(ApiResumeProblem.EndedWithoutTurnEnd); }

        _conflicts.Remove(job.Id);
        job.TurnStarted = turnStarted;
        job.ApiTurnId = turnStarted ? tail?.LastTurnId : null;
        job.EndedAt = job.UpdatedAt = now;
        if (job.StartedAt is { } s)
        {
            job.Tokens = finalUsage ?? host.ExecTurnTokens(job.ThreadId, s) ?? job.Tokens;
            job.Spend = SpendOf(job, s);
            job.SpendAt = now;
        }
        _waits.Remove(job.Id);
        host.SetNote(job.ThreadId, LocalizedText.T("Pela API"), now.AddMinutes(15));
        var duration = now - (job.StartedAt ?? now);
        if (duration < TimeSpan.Zero) duration = TimeSpan.Zero;
        host.Log($"{Prefix}fim conversa={job.ThreadId} resultado={job.State} duração={(int)duration.TotalMinutes}min{duration.Seconds:00}s " +
            $"entrada={job.Tokens?.Input ?? 0} cache={job.Tokens?.CachedInput ?? 0} saída={job.Tokens?.Output ?? 0} " +
            $"US$={(job.Spend is { } spend ? spend.ToString("0.00", CultureInfo.InvariantCulture) : "n/d")}");
        job.Notified = true;
        Save();
        var (title, message) = ApiResumeText.Notice(job, now);
        host.Notify(new ApiResumeNotice(title, message, job.ThreadId));
        return Task.CompletedTask;
    }

    private static string? LastLine(string text) =>
        text.Split('\n').Select(l => l.Trim()).LastOrDefault(l => l.Length > 0);

    /// <summary>Atualiza o painel 5, 15, 60 e 180 minutos depois do fim: o custo aparece com atraso (às vezes de horas).</summary>
    private void RefreshStep(ApiResumeJob job, DateTimeOffset now)
    {
        if (job.EndedAt is not { } ended || job.StartedAt is null) return;
        if (job.RefreshesDone < RefreshAfterEnd.Length && now >= ended + RefreshAfterEnd[job.RefreshesDone])
        {
            job.RefreshesDone++;
            host.RequestDashboardRefresh();
        }
        if (job.RefreshesDone > 0) job.Spend = SpendOf(job, job.StartedAt.Value);
    }

    /// <summary>Com o painel lido, vale o que ele diz, inclusive "custo ainda não informado" (null); sem painel, fica o último valor.</summary>
    private decimal? SpendOf(ApiResumeJob job, DateTimeOffset started) =>
        host.Dashboard is null ? job.Spend : DashboardMetrics.SpendSince(job.Baseline, host.Dashboard, started);

    private void Save()
    {
        lock (_lock)
        {
            try { store.Save(_jobs); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { host.Log($"{Prefix}não foi possível gravar jobs.json ({e.Message})"); }
            _published = _jobs.Select(j => j.Clone()).ToList();
        }
        Changed?.Invoke();
    }
}
