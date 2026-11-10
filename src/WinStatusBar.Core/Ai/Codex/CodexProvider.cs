using System.Collections.Concurrent;
using System.Text;

namespace WinStatusBar.Core.Ai.Codex;

/// <summary>
/// Provedor Codex independente:
///  • Cotas: consulta oficial account/rateLimits/read via app-server (sob demanda, intervalo moderado)
///    + atualizações em tempo real lidas dos eventos token_count dos rollouts locais.
///  • Atividade: leitura somente-leitura e incremental dos rollouts (~/.codex/sessions) com FileSystemWatcher,
///    títulos reais de ~/.codex/session_index.jsonl.
/// </summary>
public sealed class CodexProvider : IAiQuotaProvider, IAgentActivityProvider, IDisposable
{
    private readonly ConcurrentDictionary<string, AgentSession> _sessions = new();
    private readonly ConcurrentDictionary<string, long> _offsets = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, byte> _dirty = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _titles = new();
    private readonly Lock _gate = new();
    private readonly CodexAppServerClient _client = new();
    /// <summary>Início desta instância: turnos abertos pela barra antes disso perderam o servidor (a barra fechou ou atualizou).</summary>
    private readonly DateTimeOffset _since = DateTimeOffset.Now;
    private FileSystemWatcher? _watcher, _indexWatcher;
    private Timer? _debounce, _reconcile;
    private readonly object _readGate = new();
    private CancellationTokenSource? _cts;
    private readonly object _titleReadGate = new();
    private int _failures;
    private int _reconciling, _processingDirty;
    private readonly object _quotaGate = new();

    public AgentKind Kind => AgentKind.Codex;
    public QuotaSnapshot? Quota { get; private set; }
    public string? Executable { get; private set; }
    public string? LastError { get; private set; }
    public DateTimeOffset? LastQuotaQuery { get; private set; }
    public bool Enabled { get; private set; }
    public TimeSpan QuotaInterval { get; set; } = TimeSpan.FromMinutes(10);

    public event EventHandler? QuotaChanged;
    public event EventHandler? SessionsChanged;
    public event Action<string>? Diagnostic;

    public IReadOnlyList<AgentSession> Sessions
    {
        get { lock (_gate) return _sessions.Values.Select(WithNote).ToList(); }
    }

    public string Home => CodexAppServerClient.CodexHome();

    private readonly Dictionary<string, (string Note, DateTimeOffset? KeepUntil)> _notes = new(StringComparer.Ordinal);
    private bool? _lastOfficialBlocked;

    /// <summary>Títulos conhecidos das conversas do Codex (session_index.jsonl e sessões abertas).</summary>
    public IReadOnlyCollection<string> KnownTitles
    {
        get
        {
            lock (_gate)
            {
                var titles = new HashSet<string>(_titles.Values, StringComparer.OrdinalIgnoreCase);
                foreach (var s in _sessions.Values)
                    if (!string.IsNullOrWhiteSpace(s.Title)) titles.Add(s.Title!);
                return titles;
            }
        }
    }

    /// <summary>Cópia da sessão (com a nota, se houver); null se não existir.</summary>
    public AgentSession? SessionById(string id)
    {
        lock (_gate) return _sessions.TryGetValue(id, out var s) ? WithNote(s) : null;
    }

    /// <summary>Nota curta que antecede a legenda da conversa ("{nota} · …") até <paramref name="keepUntil"/>; null remove.</summary>
    public void SetNote(string threadId, string? note, DateTimeOffset? keepUntil = null)
    {
        lock (_gate)
        {
            if (string.IsNullOrEmpty(note)) _notes.Remove(threadId);
            else _notes[threadId] = (note, keepUntil);
        }
        SessionsChanged?.Invoke(this, EventArgs.Empty);
    }

    private AgentSession WithNote(AgentSession s)
    {
        var clone = s.Clone();
        if (_notes.TryGetValue(s.Id, out var n) && (n.KeepUntil is null || n.KeepUntil > DateTimeOffset.Now))
            clone.Caption = string.IsNullOrEmpty(clone.Caption) ? n.Note : $"{n.Note} · {clone.Caption}";
        return clone;
    }

    /// <summary>Busca de conversas paradas pela cota (trocável nos testes).</summary>
    internal Func<CancellationToken, Task<QuotaStopScan>>? FindQuotaStops { get; set; }
    /// <summary>Última busca disparada pela cota oficial bloqueada.</summary>
    internal Task? LastQuotaScan { get; private set; }
    /// <summary>Resultado da última busca bem-sucedida (o menu da cota começa por ela).</summary>
    public QuotaStopScan? LastStopScan { get; private set; }

    internal async Task ScanQuotaStopsAsync(CancellationToken ct)
    {
        if (FindQuotaStops is not { } find) return;
        try
        {
            var scan = await find(ct).ConfigureAwait(false);
            LastStopScan = scan;
            AdoptQuotaStops(scan.Stops, DateTimeOffset.Now);
        }
        catch (Exception e)
        {
            // A próxima leitura oficial bloqueada tenta de novo (senão só depois de a conta liberar e bloquear outra vez).
            lock (_quotaGate) _lastOfficialBlocked = null;
            Diagnostic?.Invoke($"Conversas paradas pela cota: a busca falhou ({e.GetType().Name}: {e.Message}); nova tentativa na próxima consulta da cota");
        }
    }

    /// <summary>Mostra as conversas paradas pela cota mesmo quando o registro é antigo (a leitura inicial só pega as últimas 6 h).
    /// Pula as que já mostram esta parada ou algo mais novo. Devolve quantas foram adotadas.</summary>
    public int AdoptQuotaStops(IReadOnlyList<QuotaStop> stops, DateTimeOffset now)
    {
        int adopted = 0;
        lock (_gate)
        {
            foreach (var stop in stops)
            {
                if (_sessions.TryGetValue(stop.ThreadId, out var existing) && existing.LastStateEvent >= stop.StoppedAt)
                {
                    // Já mostra esta parada (ou algo mais novo): só marca que é uma conversa do usuário listada pelo detector.
                    if (existing.StopReason == CodexParsers.UsageLimitExceeded && existing.TurnId == stop.StoppedTurnId) existing.UserQuotaStop = true;
                    continue;
                }
                var s = existing ?? new AgentSession { Kind = AgentKind.Codex, Id = stop.ThreadId, ObservationSource = "Registro local do Codex" };
                s.Title ??= stop.Title;
                s.State = AgentState.Interrupted; s.ExecutionEnded = true; s.ObservationStale = false;
                s.TurnId = stop.StoppedTurnId; s.StopReason = CodexParsers.UsageLimitExceeded; s.LastReachedType = stop.ReachedType;
                s.UserQuotaStop = true;
                s.Caption = CodexQuotaText.StoppedCaption(stop.ReachedType);
                s.LastActivity = stop.StoppedAt; s.LastStateEvent = stop.StoppedAt;
                s.ProjectDir ??= stop.Cwd; s.Model ??= stop.Model; s.TranscriptPath ??= stop.RolloutPath;
                _sessions[stop.ThreadId] = s;
                if (stop.RolloutOffset > 0) _offsets.TryAdd(stop.RolloutPath, stop.RolloutOffset);
                adopted++;
            }
        }
        if (adopted > 0)
        {
            Diagnostic?.Invoke($"Conversas paradas pela cota: {adopted} adotada(s)");
            SessionsChanged?.Invoke(this, EventArgs.Empty);
        }
        return adopted;
    }

    /// <summary>A conta liberou: as conversas paradas pela cota passam a "Cota liberada · pode continuar no app".</summary>
    public int ClearQuotaStops(DateTimeOffset now)
    {
        int cleared = 0;
        lock (_gate)
        {
            foreach (var s in _sessions.Values)
            {
                if (s.StopReason != CodexParsers.UsageLimitExceeded || s.State != AgentState.Interrupted) continue;
                s.StopReason = null; s.Caption = CodexQuotaText.Released; s.LastActivity = now;
                cleared++;
            }
        }
        if (cleared > 0)
        {
            Diagnostic?.Invoke($"Codex cota: liberadas {cleared} conversa(s) parada(s) pela cota");
            SessionsChanged?.Invoke(this, EventArgs.Empty);
        }
        return cleared;
    }

    public void Start()
    {
        Stop();
        Enabled = true;
        _lastOfficialBlocked = null;
        FindQuotaStops ??= ct => CodexQuotaStops.FindAsync(Home, TimeSpan.Zero, ct, m => Diagnostic?.Invoke(m));
        _cts = new CancellationTokenSource();
        Executable = CodexAppServerClient.FindCodexExecutable();
        string sessionsDir = Path.Combine(Home, "sessions");
        LoadTitles();
        if (Directory.Exists(sessionsDir))
        {
            var token = _cts.Token;
            _ = Task.Run(() => ScanRecent(sessionsDir, token), token);
            _watcher = new FileSystemWatcher(sessionsDir, "*.jsonl")
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName,
                InternalBufferSize = 32 * 1024,
            };
            _watcher.Changed += OnRolloutEvent;
            _watcher.Created += OnRolloutEvent;
            _watcher.Renamed += OnRolloutEvent;
            _watcher.Error += (_, _) => Reconcile();
            _watcher.EnableRaisingEvents = true;
        }
        if (Directory.Exists(Home))
        {
            _indexWatcher = new FileSystemWatcher(Home, "session_index.jsonl") { NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName };
            _indexWatcher.Changed += (_, _) => { if (LoadTitles()) Raise(); };
            _indexWatcher.Created += (_, _) => { if (LoadTitles()) Raise(); };
            _indexWatcher.Renamed += (_, _) => { if (LoadTitles()) Raise(); };
            _indexWatcher.Error += (_, _) => { if (LoadTitles()) Raise(); };
            _indexWatcher.EnableRaisingEvents = true;
        }
        _debounce = new Timer(_ => ProcessDirty(), null, 300, 300);
        _reconcile = new Timer(_ => Reconcile(), null, 5000, 5000);
        _ = QuotaLoopAsync(_cts.Token);
    }

    public void Stop()
    {
        Enabled = false;
        _cts?.Cancel(); _cts = null;
        _watcher?.Dispose(); _watcher = null;
        _indexWatcher?.Dispose(); _indexWatcher = null;
        _debounce?.Dispose(); _debounce = null;
        _reconcile?.Dispose(); _reconcile = null;
    }

    public Task RefreshQuotaNowAsync() => QueryQuotaAsync(_cts?.Token ?? CancellationToken.None);

    private async Task QuotaLoopAsync(CancellationToken ct)
    {
        try { await Task.Delay(TimeSpan.FromSeconds(4), ct); } catch { return; }
        var last = DateTimeOffset.MinValue;
        while (!ct.IsCancellationRequested)
        {
            // Tela apagada ou sessão bloqueada: não abre o Codex só para ler a cota; ao acender, consulta se já venceu.
            try { await Presence.WhenVisibleAsync(ct); } catch { return; }
            // Reavalia a cada 5 s: mudanças no intervalo configurado valem imediatamente.
            // Backoff exponencial em falhas (até 1 h) para não sobrecarregar o Codex.
            var due = _failures == 0 ? QuotaInterval : TimeSpan.FromMinutes(Math.Min(60, Math.Pow(2, _failures)));
            // O horário previsto de volta passou: pede uma consulta oficial na hora (só ela libera o bloqueio).
            if (DateTimeOffset.Now - last >= due || ReturnDue(Quota, last, DateTimeOffset.Now))
            {
                last = DateTimeOffset.Now;
                await QueryQuotaAsync(ct);
            }
            try { await Task.Delay(TimeSpan.FromSeconds(5), ct); } catch { return; }
        }
    }

    private async Task QueryQuotaAsync(CancellationToken ct)
    {
        Executable ??= CodexAppServerClient.FindCodexExecutable();
        if (Executable is null) { LastError = global::WinStatusBar.Core.LocalizedText.T("Codex não encontrado"); _failures++; return; }
        var result = await _client.QueryAsync(Executable, TimeSpan.FromSeconds(45), ct);
        LastQuotaQuery = DateTimeOffset.Now;
        ApplyQueryResult(result, DateTimeOffset.Now);
    }

    /// <summary>Resultado da consulta oficial: sucesso junta a cota; falha mantém o que já se sabe (inclusive o bloqueio).</summary>
    internal void ApplyQueryResult(CodexAppServerClient.QueryResult result, DateTimeOffset now)
    {
        if (result.Quota is not null)
        {
            _failures = 0; LastError = null;
            ApplyQuota(result.Quota, official: true, now);
        }
        else
        {
            _failures++;
            LastError = result.Error;
            if (Quota is null) { Quota = new QuotaSnapshot { Error = result.Error }; QuotaChanged?.Invoke(this, EventArgs.Empty); }
        }
    }

    /// <summary>Junta a cota nova e avisa; registra no log só quando o bloqueio começa ou termina.</summary>
    internal void ApplyQuota(QuotaSnapshot incoming, bool official, DateTimeOffset now)
    {
        bool wasBlocked, blocked, scan = false, release = false;
        QuotaSnapshot merged;
        lock (_quotaGate)
        {
            var current = Quota;
            wasBlocked = current?.IsBlocked(now) == true;
            merged = MergeQuota(current, incoming, official);
            if (ReferenceEquals(merged, current)) return;
            Quota = merged;
            blocked = merged.IsBlocked(now);
            if (official)
            {
                // Busca as conversas paradas na 1ª leitura oficial bloqueada e em cada passagem de liberado para bloqueado.
                scan = blocked && _lastOfficialBlocked != true;
                release = !blocked;
                _lastOfficialBlocked = blocked;
            }
        }
        if (blocked != wasBlocked) Diagnostic?.Invoke(blocked ? CodexQuotaText.BlockLog(merged, now) : "Codex cota: uso liberado");
        if (release) ClearQuotaStops(now);
        if (scan) LastQuotaScan = ScanQuotaStopsAsync(_cts?.Token ?? CancellationToken.None);
        QuotaChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Oficial: vale a nova (com janelas) ou a nova com os detalhes da atual. Registro: só vence se for mais novo
    /// (pelo horário do evento) ou se a atual não tiver janelas; senão a atual fica (mesma referência).</summary>
    internal static QuotaSnapshot MergeQuota(QuotaSnapshot? current, QuotaSnapshot incoming, bool official)
    {
        bool incomingWindows = incoming.Primary is not null || incoming.Secondary is not null;
        if (official) return incomingWindows ? incoming : incoming.WithDetailsFrom(current);
        bool currentWindows = current?.Primary is not null || current?.Secondary is not null;
        return current is null || !currentWindows || incoming.ObservedAt >= current.ObservedAt ? incoming.WithDetailsFrom(current) : current;
    }

    /// <summary>A volta prevista caiu entre a última consulta e agora: hora de perguntar à conta.</summary>
    internal static bool ReturnDue(QuotaSnapshot? quota, DateTimeOffset lastQuery, DateTimeOffset now) =>
        quota?.BlockedUntil(lastQuery) is { } at && lastQuery < at && at <= now;

    private void OnRolloutEvent(object sender, FileSystemEventArgs e)
    {
        _dirty[e.FullPath] = 0;
        // Fixed periodic drain avoids starvation during continuous writes.
    }

    private void ScanRecent(string sessionsDir, CancellationToken token)
    {
        var cutoff = DateTime.Now.AddHours(-6);
        IEnumerable<FileInfo> files;
        try
        {
            files = new DirectoryInfo(sessionsDir).EnumerateFiles("*.jsonl", SearchOption.AllDirectories)
                .Where(f => f.LastWriteTime > cutoff).OrderBy(f => f.LastWriteTime).ToList();
        }
        catch { return; }
        foreach (var f in files) { if (token.IsCancellationRequested) return; ReadFile(f.FullName, initial: true); }
        // Sessões que estavam "executando" mas não registram nada há muito tempo não estão mais ativas.
        Expire(DateTimeOffset.Now);
        Raise();
    }

    private void Reconcile()
    {
        if (!Enabled || Interlocked.Exchange(ref _reconciling, 1) != 0) return;
        try
        {
            foreach (var path in Directory.EnumerateFiles(Path.Combine(Home, "sessions"), "*.jsonl", SearchOption.AllDirectories))
                if (_offsets.ContainsKey(path) || File.GetLastWriteTimeUtc(path) > DateTime.UtcNow.AddHours(-12))
                    if (!_offsets.TryGetValue(path, out var offset) || new FileInfo(path).Length != offset) _dirty[path] = 0;
            if (LoadTitles()) Raise();
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        finally { Volatile.Write(ref _reconciling, 0); }
    }

    private void ProcessDirty()
    {
        if (!Enabled || Interlocked.Exchange(ref _processingDirty, 1) != 0) return;
        try
        {
        bool changed = false;
        foreach (var path in _dirty.Keys.ToList())
        {
            _dirty.TryRemove(path, out _);
            changed |= ReadFile(path, initial: false);
        }
        if (changed) Raise();
        }
        finally { Volatile.Write(ref _processingDirty, 0); }
    }

    internal bool ReadFile(string path, bool initial)
    {
        lock (_readGate) return ReadFileSerial(path, initial);
    }

    private bool ReadFileSerial(string path, bool initial)
    {
        string? id = CodexParsers.ThreadIdFromRolloutPath(path);
        if (id is null) return false;
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            long length = fs.Length;
            long offset = _offsets.GetValueOrDefault(path, -1);
            if (offset < 0 || offset > length) offset = 0;
            if (offset == length) return false;
            AgentSession session;
            lock (_gate)
            {
                if (offset == 0) _sessions[id] = new AgentSession { Kind = AgentKind.Codex, Id = id, State = AgentState.Unknown, ObservationSource = "Registro local do Codex" };
                session = _sessions.GetOrAdd(id, key => new AgentSession { Kind = AgentKind.Codex, Id = key, State = AgentState.Unknown });
            }
            var previousState = session.State;
            var previousProgress = session.ProgressPercent;
            session.TranscriptPath = path;
            fs.Seek(offset, SeekOrigin.Begin);
            // Replay complete newline-terminated records. Offset advances only after successful application.
            QuotaSnapshot? quota = null;
            var block = new byte[65536];
            using var pendingLine = new MemoryStream();
            long consumed = offset, position = offset;
            int bytes;
            while (position < length && (bytes = fs.Read(block, 0, (int)Math.Min(block.Length, length - position))) > 0)
            {
                int start = 0;
                for (int i = 0; i < bytes; i++)
                {
                    if (block[i] != (byte)'\n') continue;
                    pendingLine.Write(block, start, i - start);
                    string line = Encoding.UTF8.GetString(pendingLine.GetBuffer(), 0, (int)pendingLine.Length).TrimEnd('\r');
                    lock (_gate)
                    {
                        var q = CodexParsers.ApplyRolloutLine(session, line);
                        if (q is not null) quota = q.WithDetailsFrom(quota);
                    }
                    pendingLine.SetLength(0);
                    consumed = position + i + 1;
                    start = i + 1;
                }
                pendingLine.Write(block, start, bytes - start);
                position += bytes;
            }
            _offsets[path] = consumed;
            lock (_gate) if (_titles.TryGetValue(id, out var title)) session.Title = title;
            if (quota is not null) ApplyQuota(quota, official: false, DateTimeOffset.Now); // mantém resets/créditos vindos da consulta oficial
            if (previousState != session.State) Diagnostic?.Invoke($"Codex estado: sessão={id} turno={session.TurnId} {previousState} -> {session.State}; offset={consumed}; fonte=rollout");
            if (previousProgress != session.ProgressPercent) Diagnostic?.Invoke($"Codex progresso: sessão={id} turno={session.TurnId} valor={session.ProgressPercent:0.#}; base={session.ProgressBasis}");
            return true;
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    private bool LoadTitles()
    {
        lock (_titleReadGate)
        {
            var map = SessionTitleIndex.Read(Path.Combine(Home, "session_index.jsonl"));
            if (map is null) return false;
            lock (_gate)
            {
                // A temporarily empty/partial index must not erase known user titles.
                if (map.Count == 0 && _titles.Count > 0) return false;
                bool changed = map.Count != _titles.Count || map.Any(x => !_titles.TryGetValue(x.Key, out var title) || title != x.Value);
                if (!changed) return false;
                _titles.Clear();
                foreach (var item in map) _titles[item.Key] = item.Value;
                foreach (var session in _sessions.Values) if (_titles.TryGetValue(session.Id, out var title)) session.Title = title;
                return true;
            }
        }
    }

    /// <summary>Envelhecimento de estados: chamado periodicamente pelo TaskActivityService.</summary>
    public bool Expire(DateTimeOffset now)
    {
        bool changed = false;
        var orphanExec = FindOrphanExecSessions(now);
        lock (_gate)
        {
            foreach (var s in _sessions.Values)
            {
                var idle = now - s.LastActivity;
                // Turno órfão: aberto por uma barra anterior, cujo servidor do Codex fechou junto com ela (fechar ou atualizar),
                // ou por um `codex exec` cujo processo já terminou sem gravar o fim do turno.
                // Sem isso, a tarefa aparece como "Executando" para sempre. Silêncio sozinho não encerra tarefas do app Codex.
                if (s.State.IsActive() && ((s.Originator == "winstatusbar" && s.LastActivity < _since) || orphanExec.Contains(s.Id)))
                {
                    s.State = AgentState.Interrupted; s.ExecutionEnded = true; s.ObservationStale = false; s.Caption = "Execução interrompida"; changed = true;
                    Diagnostic?.Invoke($"Codex estado: sessão={s.Id} turno={s.TurnId} órfão ({s.Originator}) -> Interrupted");
                }
                else if (s.State.IsActive() && idle > TimeSpan.FromMinutes(2) && !s.ObservationStale)
                { s.ObservationStale = true; changed = true; }
                else if (s.State is AgentState.Completed or AgentState.Error or AgentState.Interrupted && idle > TimeSpan.FromMinutes(15) && !Held(s, now))
                { s.State = AgentState.Idle; changed = true; }
            }
            foreach (var old in _sessions.Values.Where(s => !s.State.IsActive() && now - s.LastActivity > TimeSpan.FromHours(12) && !Held(s, now)).Select(s => s.Id).ToList())
            { _sessions.TryRemove(old, out _); changed = true; }
        }
        return changed;
    }

    /// <summary>Início dos processos `codex exec` vivos (null = consulta indisponível). Substituível nos testes.</summary>
    internal Func<IReadOnlyList<DateTimeOffset>?> ExecProcessStarts { get; set; } = CodexExecProcesses.StartTimes;
    private IReadOnlyList<DateTimeOffset>? _execStarts;
    private DateTimeOffset _execStartsAt = DateTimeOffset.MinValue;

    /// <summary>Conversa do usuário (listada pelo detector) parada pela cota enquanto o bloqueio dura: não envelhece nem some.
    /// O bloqueio vale até a conta confirmar a liberação, mesmo que uma janela em 100 % já tenha vencido.</summary>
    private bool Held(AgentSession s, DateTimeOffset now) =>
        s.UserQuotaStop && s.StopReason == CodexParsers.UsageLimitExceeded && (Quota?.IsBlocked(now) == true || _lastOfficialBlocked == true);

    /// <summary>
    /// Sessões do `codex exec` ainda marcadas como ativas, em silêncio há mais de 2 min, sem nenhum processo
    /// `codex exec` que possa ser o dono delas (iniciado até 30 s depois do turno). A consulta de processos
    /// roda fora da trava e só quando há candidatas, no máximo a cada 20 s.
    /// </summary>
    private HashSet<string> FindOrphanExecSessions(DateTimeOffset now)
    {
        List<(string Id, DateTimeOffset Start)> candidates;
        lock (_gate)
            candidates = _sessions.Values
                .Where(s => s.State.IsActive() && (s.Originator == "codex_exec" || s.TurnTrigger == "exec") && now - s.LastActivity > TimeSpan.FromMinutes(2))
                .Select(s => (s.Id, s.StartedAt ?? s.LastActivity)).ToList();
        if (candidates.Count == 0) return [];
        if (now - _execStartsAt > TimeSpan.FromSeconds(20) || now < _execStartsAt)
        {
            _execStarts = ExecProcessStarts();
            _execStartsAt = now;
        }
        if (_execStarts is not { } starts) return [];
        return candidates.Where(c => !starts.Any(t => t <= c.Start + TimeSpan.FromSeconds(30))).Select(c => c.Id).ToHashSet();
    }

    private void Raise() => SessionsChanged?.Invoke(this, EventArgs.Empty);

    public void Dispose() => Stop();
}
