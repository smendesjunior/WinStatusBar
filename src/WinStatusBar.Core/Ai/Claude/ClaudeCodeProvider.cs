using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using WinStatusBar.Core.Ipc;

namespace WinStatusBar.Core.Ai.Claude;

/// <summary>
/// Provedor Claude Code independente. Recebe eventos dos hooks e da statusLine (via coletor + Named Pipe),
/// mantém o estado das sessões e as cotas de assinatura (rate_limits.five_hour / seven_day da statusLine).
/// O título real da sessão vem das entradas "custom-title" do transcript (apenas essas linhas são lidas).
/// </summary>
public sealed class ClaudeCodeProvider : IAiQuotaProvider, IAgentActivityProvider, IDisposable
{
    private readonly ConcurrentDictionary<string, AgentSession> _sessions = new();
    private readonly ConcurrentDictionary<string, (long length, string? title)> _titleCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Lock _gate = new();
    private readonly string? _cacheFile;
    private readonly ClaudeUsageClient _usage;
    private readonly TimeProvider _time;
    private bool _rateLimited;
    private int _consecutiveLimits;

    private string? NextQueryFile => _cacheFile is null ? null : _cacheFile + ".next";

    private void SaveNextQuery()
    {
        try { if (NextQueryFile is { } f) File.WriteAllText(f, NextQuotaQuery.ToUnixTimeSeconds().ToString()); } catch { }
    }

    private void LoadNextQuery()
    {
        try
        {
            if (NextQueryFile is { } f && File.Exists(f) && long.TryParse(File.ReadAllText(f), out var s))
            {
                var next = DateTimeOffset.FromUnixTimeSeconds(s);
                if (next > _time.GetUtcNow()) { NextQuotaQuery = next; _rateLimited = true; _consecutiveLimits = 1; }
            }
        }
        catch { }
    }
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    public DateTimeOffset? LastQuotaQuery { get; private set; }
    public DateTimeOffset NextQuotaQuery { get; private set; }
    public string? LastQuotaError { get; private set; }
    /// <summary>Intervalo da consulta à conta. "Tempo real" (configuração 0) = 3 minutos + eventos da statusLine
    /// (o serviço de uso da Anthropic bloqueia consultas por minuto com HTTP 429).</summary>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromMinutes(3);
    public IReadOnlyList<QuotaWindow> UsageExtras { get; private set; } = [];
    public string? UsageExtraText { get; private set; }
    /// <summary>Uso desta semana por produto (Claude Code, Conversas, Cowork, Outro).</summary>
    public IReadOnlyList<ProductShare> UsageBreakdown { get; private set; } = [];

    /// <summary>Antecipa a próxima consulta quando o intervalo configurado diminui.</summary>
    public void Reschedule()
    {
        var limit = (LastQuotaQuery ?? DateTimeOffset.MinValue) + PollInterval;
        if (!_rateLimited && NextQuotaQuery > limit) NextQuotaQuery = limit;
    }

    public async Task RefreshUsageAsync(bool force = false, CancellationToken ct = default)
    {
        if (!await _refreshGate.WaitAsync(0, ct)) return;
        try
        {
            var now = _time.GetUtcNow();
            if (!force && now < NextQuotaQuery) return;
            // Mesmo o botão manual respeita o intervalo de segurança e o Retry-After do servidor.
            if (force && LastQuotaQuery is not null && (now - LastQuotaQuery < TimeSpan.FromSeconds(30)
                || (_rateLimited && now < NextQuotaQuery))) return;
            LastQuotaQuery = now;
            var result = await _usage.FetchAsync(ct);
            LastQuotaError = result.Error;
            _rateLimited = result.RateLimited;
            if (result.RateLimited)
            {
                // Bloqueio do serviço (HTTP 429): respeita o Retry-After e dobra a espera a cada bloqueio seguido (5 → 10 → 20 … até 60 min).
                _consecutiveLimits++;
                var backoff = TimeSpan.FromMinutes(Math.Min(60, 5 * Math.Pow(2, _consecutiveLimits - 1)));
                NextQuotaQuery = _time.GetUtcNow() + (result.RetryDelay > backoff ? result.RetryDelay : backoff);
                SaveNextQuery(); // reabrir a barra não fura o bloqueio
            }
            else
            {
                _consecutiveLimits = 0;
                NextQuotaQuery = _time.GetUtcNow() + PollInterval;
            }
            if (result.Message is not null)
            {
                UsageExtras = result.Extras;
                UsageExtraText = result.ExtraUsage;
            if (result.Breakdown.Count > 0) UsageBreakdown = result.Breakdown;
                ApplyStatusLine(result.Message);
            }
            else QuotaChanged?.Invoke(this, EventArgs.Empty);
        }
        finally { _refreshGate.Release(); }
    }


    public ClaudeCodeProvider(string? quotaCacheFile = null) : this(new ClaudeUsageClient(), TimeProvider.System, quotaCacheFile) { }

    internal ClaudeCodeProvider(ClaudeUsageClient usage, TimeProvider time, string? quotaCacheFile = null)
    {
        _usage = usage;
        _time = time;
        _cacheFile = quotaCacheFile;
        LoadQuotaCache();
        LoadNextQuery();
    }

    public AgentKind Kind => AgentKind.Claude;
    public QuotaSnapshot? Quota { get; private set; }
    public DateTimeOffset? LastEventAt { get; private set; }
    public long EventsReceived { get; private set; }
    public event EventHandler? QuotaChanged;
    public event EventHandler? SessionsChanged;

    public IReadOnlyList<AgentSession> Sessions
    {
        get { lock (_gate) return _sessions.Values.Select(s => s.Clone()).ToList(); }
    }

    /// <summary>
    /// Ao iniciar, as sessões do Claude Code em andamento só apareciam no próximo evento dos hooks (o que pode levar
    /// minutos num passo longo). Lê o fim dos transcripts alterados há pouco e já marca como em execução as que não
    /// terminaram; os eventos seguintes continuam mandando no estado. Devolve quantas sessões foram retomadas.
    /// </summary>
    public int Bootstrap(TimeSpan window, string? root = null)
    {
        root ??= Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "projects");
        if (!Directory.Exists(root)) return 0;
        var now = DateTime.UtcNow;
        int found = 0;
        IEnumerable<string> files;
        try { files = Directory.EnumerateFiles(root, "*.jsonl", SearchOption.AllDirectories).ToList(); } catch { return 0; }
        foreach (var file in files)
        {
            try
            {
                if (Path.GetFileName(file).StartsWith("agent-", StringComparison.OrdinalIgnoreCase)) continue; // subagentes
                var written = File.GetLastWriteTimeUtc(file);
                if (now - written > window) continue;
                var state = TranscriptTail(file);
                if (state is not { Running: true } t) continue;
                string sid = t.SessionId ?? Path.GetFileNameWithoutExtension(file);
                if (_sessions.TryGetValue(sid, out var known) && known.State.IsActive()) continue;
                Apply(new EventMessage { Event = "UserPromptSubmit", SessionId = sid, Cwd = t.Cwd, TranscriptPath = file, PromptSnippet = t.Prompt,
                    Timestamp = new DateTimeOffset(written, TimeSpan.Zero).ToUnixTimeMilliseconds() });
                found++;
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        return found;
    }

    internal sealed record TailState(bool Running, string? SessionId, string? Cwd, string? Prompt);

    /// <summary>
    /// Estado pelo fim do transcript: a última fala do assistente terminada (stop_reason "end_turn") = concluída; um
    /// resultado de ferramenta, um pedido da pessoa ou uma resposta ainda sem fim = em execução.
    /// </summary>
    internal static TailState? TranscriptTail(string file)
    {
        using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        long start = Math.Max(0, fs.Length - 256 * 1024);
        fs.Seek(start, SeekOrigin.Begin);
        using var reader = new StreamReader(fs, Encoding.UTF8);
        var lines = reader.ReadToEnd().Split('\n');
        string? sid = null, cwd = null, prompt = null;
        bool? running = null;
        for (int i = lines.Length - 1; i >= 0 && (running is null || sid is null || cwd is null); i--)
        {
            JsonDocument doc;
            try { doc = JsonDocument.Parse(lines[i]); } catch (JsonException) { continue; }
            using (doc)
            {
                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object) continue;
                sid ??= root.TryGetProperty("sessionId", out var s) && s.ValueKind == JsonValueKind.String ? s.GetString() : null;
                cwd ??= root.TryGetProperty("cwd", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString() : null;
                if (running is not null) continue;
                string? type = root.TryGetProperty("type", out var ty) && ty.ValueKind == JsonValueKind.String ? ty.GetString() : null;
                if (type is not ("user" or "assistant") || !root.TryGetProperty("message", out var msg) || msg.ValueKind != JsonValueKind.Object) continue;
                if (type == "assistant")
                {
                    string? stop = msg.TryGetProperty("stop_reason", out var sr) && sr.ValueKind == JsonValueKind.String ? sr.GetString() : null;
                    running = stop is not ("end_turn" or "stop_sequence" or "max_tokens");
                }
                else
                {
                    string text = msg.TryGetProperty("content", out var content) ? (content.ValueKind == JsonValueKind.String ? content.GetString() ?? "" : content.ToString()) : "";
                    running = !text.Contains("[Request interrupted", StringComparison.Ordinal);
                    if (content.ValueKind == JsonValueKind.String) prompt = text.Length > 160 ? text[..160] : text;
                }
            }
        }
        return running is null ? null : new TailState(running.Value, sid, cwd, prompt);
    }

    public void Apply(EventMessage m)
    {
        EventsReceived++;
        LastEventAt = DateTimeOffset.Now;
        if (m.Kind == "statusline") ApplyStatusLine(m);
        else ApplyHook(m);
    }

    private AgentSession Get(string id)
        => _sessions.GetOrAdd(id, k => new AgentSession { Kind = AgentKind.Claude, Id = k });

    private void ApplyHook(EventMessage m)
    {
        if (string.IsNullOrEmpty(m.SessionId)) return;
        var now = DateTimeOffset.FromUnixTimeMilliseconds(m.Timestamp).ToLocalTime();
        lock (_gate)
        {
            if (m.Event == "SessionEnd")
            {
                // Sessão encerrada: nunca mais "executando"; permanece no painel como concluída e envelhece para ociosa.
                if (_sessions.TryGetValue(m.SessionId, out var ended))
                {
                    if (ended.State.IsActive()) ended.State = AgentState.Unknown;
                    ended.Caption = global::WinStatusBar.Core.LocalizedText.T("Sessão encerrada");
                    ended.ActiveSubagents = 0;
                    ended.LastActivity = now;
                }
            }
            else
            {
                var s = Get(m.SessionId);
                s.ProjectDir = m.Cwd ?? s.ProjectDir;
                s.TranscriptPath = m.TranscriptPath ?? s.TranscriptPath;
                if (now < s.LastStateEvent) return;
                s.LastStateEvent = now;
                s.LastActivity = now;
                if (m.Model is not null) s.Model = m.Model;
                ClaudeStateMachine.Apply(s, m, now);
                RefreshTitle(s, m);
            }
        }
        SessionsChanged?.Invoke(this, EventArgs.Empty);
    }

    private void ApplyStatusLine(EventMessage m)
    {
        if (!string.IsNullOrEmpty(m.SessionId))
        {
            lock (_gate)
            {
                var s = Get(m.SessionId);
                s.Model = m.Model ?? s.Model;
                s.ProjectDir = m.ProjectDir ?? m.Cwd ?? s.ProjectDir;
                s.TranscriptPath = m.TranscriptPath ?? s.TranscriptPath;
                s.ContextUsedPercent = m.ContextUsedPercent ?? s.ContextUsedPercent;
                if (!string.IsNullOrWhiteSpace(m.SessionName)) s.Title = m.SessionName;
            }
            SessionsChanged?.Invoke(this, EventArgs.Empty);
        }
        var q = ToQuota(m, DateTimeOffset.Now);
        if (q is not null)
        {
            Quota = q;
            SaveQuotaCache(m);
            QuotaChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    internal static QuotaSnapshot? ToQuota(EventMessage m, DateTimeOffset now)
    {
        QuotaWindow? W(double? used, long? reset, string label, int mins) => used is null ? null
            : new QuotaWindow(AgentKind.Claude, label, used.Value, reset is null ? null : DateTimeOffset.FromUnixTimeSeconds(reset.Value), mins, now, m.QuotaSource ?? global::WinStatusBar.Core.LocalizedText.T("Claude Code statusLine"));
        var p = W(m.FiveHourUsed, m.FiveHourResetsAt, "5h", 300);
        var s = W(m.SevenDayUsed, m.SevenDayResetsAt, "7d", 10080);
        return p is null && s is null ? null : new QuotaSnapshot { Primary = p, Secondary = s, ObservedAt = now };
    }

    private void RefreshTitle(AgentSession s, EventMessage m)
    {
        string? title = ReadCustomTitle(s.TranscriptPath);
        if (!string.IsNullOrWhiteSpace(title)) { s.Title = title; return; }
        if (string.IsNullOrWhiteSpace(s.Title) && !string.IsNullOrWhiteSpace(m.PromptSnippet)) s.Title = m.PromptSnippet;
    }

    /// <summary>
    /// Lê somente a última linha "custom-title" do transcript (cauda de até 512 KB), com cache por tamanho. Quando o arquivo
    /// só cresceu, examina apenas o trecho novo (com folga de 4 KB para uma linha cortada), não a cauda inteira de novo.
    /// </summary>
    internal string? ReadCustomTitle(string? path)
    {
        if (string.IsNullOrEmpty(path)) return null;
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists) return null;
            if (_titleCache.TryGetValue(path, out var cached) && cached.length == info.Length) return cached.title;
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            long start = Math.Max(0, fs.Length - 512 * 1024);
            if (cached.length > 0 && cached.length <= fs.Length) start = Math.Max(start, cached.length - 4096);
            fs.Seek(start, SeekOrigin.Begin);
            var buf = new byte[fs.Length - start];
            int read = 0; while (read < buf.Length) { int n = fs.Read(buf, read, buf.Length - read); if (n <= 0) break; read += n; }
            string? title = FindLastCustomTitle(Encoding.UTF8.GetString(buf, 0, read)) ?? cached.title;
            _titleCache[path] = (info.Length, title);
            return title;
        }
        catch { return null; }
    }

    internal static string? FindLastCustomTitle(string text)
    {
        string? title = null;
        int idx = 0;
        while ((idx = text.IndexOf("\"type\":\"custom-title\"", idx, StringComparison.Ordinal)) >= 0)
        {
            int lineStart = text.LastIndexOf('\n', idx) + 1;
            int lineEnd = text.IndexOf('\n', idx); if (lineEnd < 0) lineEnd = text.Length;
            try
            {
                using var doc = JsonDocument.Parse(text.AsMemory(lineStart, lineEnd - lineStart));
                if (doc.RootElement.TryGetProperty("customTitle", out var t) && t.ValueKind == JsonValueKind.String) title = t.GetString();
            }
            catch (JsonException) { }
            idx = lineEnd;
        }
        return title;
    }

    public bool Expire(DateTimeOffset now)
    {
        bool changed = false;
        lock (_gate)
        {
            foreach (var s in _sessions.Values)
            {
                var idle = now - s.LastActivity;
                if (s.State.IsActive() && idle > TimeSpan.FromMinutes(2) && !s.ObservationStale) { s.ObservationStale = true; changed = true; }
                else if (s.State is AgentState.Completed or AgentState.Error && idle > TimeSpan.FromMinutes(15)) { s.State = AgentState.Idle; changed = true; }
            }
            foreach (var id in _sessions.Values.Where(s => !s.State.IsActive() && now - s.LastActivity > TimeSpan.FromHours(12)).Select(s => s.Id).ToList())
            { _sessions.TryRemove(id, out _); changed = true; }
        }
        if (Quota is not null && (Quota.Primary?.IsExpired(now) == true || Quota.Secondary?.IsExpired(now) == true)) changed = true;
        return changed;
    }

    private void SaveQuotaCache(EventMessage m)
    {
        if (_cacheFile is null) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_cacheFile)!);
            var slim = new EventMessage { Kind = "statusline", QuotaSource = m.QuotaSource, FiveHourUsed = m.FiveHourUsed, FiveHourResetsAt = m.FiveHourResetsAt, SevenDayUsed = m.SevenDayUsed, SevenDayResetsAt = m.SevenDayResetsAt, Timestamp = m.Timestamp };
            File.WriteAllText(_cacheFile, JsonSerializer.Serialize(slim, EventMessageContext.Default.EventMessage));
        }
        catch { }
    }

    private void LoadQuotaCache()
    {
        if (_cacheFile is null || !File.Exists(_cacheFile)) return;
        try
        {
            var m = JsonSerializer.Deserialize(File.ReadAllText(_cacheFile), EventMessageContext.Default.EventMessage);
            if (m is null) return;
            Quota = ToQuota(m, DateTimeOffset.FromUnixTimeMilliseconds(m.Timestamp));
        }
        catch { }
    }

    public void Dispose() => _usage.Dispose();
}

/// <summary>Máquina de estados dos eventos de hook do Claude Code.</summary>
public static class ClaudeStateMachine
{
    public static void Apply(AgentSession s, EventMessage m, DateTimeOffset now)
    {
        s.ObservationStale = false;
        switch (m.Event)
        {
            case "SessionStart":
                if (!s.State.IsActive()) { s.State = AgentState.Idle; s.Caption = global::WinStatusBar.Core.LocalizedText.T("Sessão iniciada"); }
                break;
            case "UserPromptSubmit":
                s.ProgressIsPhaseEstimate = false;
                s.ExecutionEnded = false; s.ScopeChanged = false;
                s.State = AgentState.Running; s.StartedAt = now; s.ProgressPercent = null; s.PlanSteps = null; s.ActiveSubagents = 0;
                s.Caption = global::WinStatusBar.Core.LocalizedText.T("Processando a solicitação…");
                break;
            case "PreToolUse":
                s.ExecutionEnded = false;
                s.State = AgentState.Running;
                ExecutionProgress.Tool(s, m.ToolName);
                if (m.Detail is not null) s.Caption = m.Detail;
                break;
            case "PostToolUse":
                if (s.State is AgentState.WaitingApproval or AgentState.WaitingInput or AgentState.Idle or AgentState.Completed) s.State = AgentState.Running;
                // Só depois que a ferramenta terminou: "advance" aplicado também no PreToolUse contaria duas vezes.
                if (m.Plan is { } op)
                {
                    s.PlanSteps = PlanTracker.Apply(s.PlanSteps, op);
                    var percent = PlanTracker.Percent(s.PlanSteps);
                    s.ScopeChanged = s.ProgressPercent is { } before && percent is { } after && after < before;
                    s.ProgressPercent = percent ?? s.ProgressPercent;
                    s.ProgressIsPhaseEstimate = false;
                }
                else if (m.Progress is not null) s.ProgressPercent = m.Progress;
                break;
            case "PostToolUseFailure":
                s.State = AgentState.Running; s.Caption = "Uma ferramenta falhou; continuando…";
                ExecutionProgress.Observe(s, "retry");
                break;
            case "PermissionRequest":
                s.State = AgentState.WaitingApproval; s.Caption = global::WinStatusBar.Core.LocalizedText.T("Aguardando sua autorização");
                break;
            case "Notification":
                switch (m.NotificationType)
                {
                    case "permission_prompt": s.State = AgentState.WaitingApproval; s.Caption = global::WinStatusBar.Core.LocalizedText.T("Aguardando sua autorização"); break;
                    case "idle_prompt" or "agent_needs_input" or "elicitation_dialog" or "elicitation_url_dialog":
                        s.State = AgentState.WaitingInput; s.Caption = global::WinStatusBar.Core.LocalizedText.T("Aguardando sua resposta"); break;
                    case "agent_completed": ExecutionProgress.FinishResponse(s); break;
                }
                break;
            case "SubagentStart":
                s.State = AgentState.Running; s.ActiveSubagents++;
                s.Caption = m.Detail is not null ? $"Subagente: {m.Detail}" : global::WinStatusBar.Core.LocalizedText.T("Executando subagente…");
                break;
            case "SubagentStop":
                s.ActiveSubagents = Math.Max(0, s.ActiveSubagents - 1);
                break;
            case "TaskCreated":
                if (m.Detail is not null) s.Caption = global::WinStatusBar.Core.LocalizedText.F("Nova tarefa: {0}", new object?[] {m.Detail});
                break;
            case "TaskCompleted":
                if (m.Detail is not null) s.Caption = global::WinStatusBar.Core.LocalizedText.F("Tarefa concluída: {0}", new object?[] {m.Detail});
                break;
            case "Stop":
                ExecutionProgress.FinishResponse(s); s.ActiveSubagents = 0;
                break;
            case "StopFailure":
                s.ExecutionEnded = true;
                s.State = AgentState.Error; s.Caption = global::WinStatusBar.Core.LocalizedText.T("Falha na execução"); s.ActiveSubagents = 0;
                break;
        }
    }
}
