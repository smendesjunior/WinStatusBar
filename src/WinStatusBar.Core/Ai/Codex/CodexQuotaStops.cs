using System.Text.Json;
using System.Text.Json.Nodes;

namespace WinStatusBar.Core.Ai.Codex;

/// <summary>De onde veio uma conversa do Codex, para decidir se ela é do usuário.</summary>
public enum CodexThreadKind { User, ExecOrScript, Subagent, Bar, Ephemeral, UnknownSource, UnknownOrigin }

/// <summary>Uma conversa como aparece em thread/list (ou no session_meta do registro, no plano B).</summary>
public sealed record CodexThreadInfo(string Id, string? Title, string? Path, DateTimeOffset? UpdatedAt, string? Source, string? Originator,
    string? ParentThreadId, string? AgentRole, string? ThreadSource, bool Ephemeral, string? Model, string? Effort)
{
    public static CodexThreadInfo FromThreadList(JsonElement item) => new(
        CodexParsers.Str(item, "id") ?? "",
        CodexParsers.Str(item, "name"),
        CodexParsers.Str(item, "path"),
        Time(item, "updatedAt"),
        SourceOf(item),
        CodexParsers.Str(item, "originator"),
        CodexParsers.Str(item, "parentThreadId"),
        CodexParsers.Str(item, "agentRole"),
        CodexParsers.Str(item, "threadSource"),
        CodexParsers.Bool(item, "ephemeral") == true,
        CodexParsers.Str(item, "model"),
        CodexParsers.Str(item, "reasoningEffort"));

    public static CodexThreadInfo FromSessionMeta(JsonElement payload, string path, DateTimeOffset updatedAt, string? title)
    {
        string? parent = null;
        if (payload.TryGetProperty("source", out var src) && src.ValueKind == JsonValueKind.Object)
            foreach (var key in new[] { "subagent", "subAgent" })
                if (src.TryGetProperty(key, out var sub) && sub.ValueKind == JsonValueKind.Object
                    && sub.TryGetProperty("thread_spawn", out var spawn))
                    parent = CodexParsers.Str(spawn, "parent_thread_id");
        return new CodexThreadInfo(
            CodexParsers.Str(payload, "id") ?? CodexParsers.Str(payload, "session_id") ?? "",
            title, path, updatedAt, SourceOf(payload),
            CodexParsers.Str(payload, "originator"),
            parent,
            CodexParsers.Str(payload, "agent_role"),
            CodexParsers.Str(payload, "thread_source"),
            CodexParsers.Bool(payload, "ephemeral") == true,
            null, null);
    }

    /// <summary>"source" texto, ou objeto: {subAgent}/{subagent} → "subagent", {custom} → "custom", outro → nome da chave.</summary>
    private static string? SourceOf(JsonElement e)
    {
        if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty("source", out var src)) return null;
        if (src.ValueKind == JsonValueKind.String) return src.GetString();
        if (src.ValueKind != JsonValueKind.Object) return null;
        foreach (var p in src.EnumerateObject())
            return p.Name.Equals("subagent", StringComparison.OrdinalIgnoreCase) ? "subagent" : p.Name;
        return null;
    }

    private static DateTimeOffset? Time(JsonElement e, string name)
    {
        if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(name, out var v)) return null;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var secs)) return DateTimeOffset.FromUnixTimeSeconds(secs);
        if (v.ValueKind == JsonValueKind.String && DateTimeOffset.TryParse(v.GetString(), System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AssumeUniversal, out var at)) return at;
        return null;
    }
}

/// <summary>Uma conversa do usuário parada pela cota, com os dados do turno que parou.</summary>
public sealed record QuotaStop(string ThreadId, string Title, string RolloutPath, long RolloutOffset, string StoppedTurnId, DateTimeOffset StoppedAt,
    string? Model, string? Effort, bool EffortFromCollaborationMode, string? SandboxMode, bool? NetworkAccess, IReadOnlyList<string> WritableRoots,
    string? ApprovalPolicy, string? Cwd, IReadOnlyList<string> ExtraRoots, string? CurrentModel, string? CurrentEffort, long? ContextTokens, string? ReachedType)
{
    /// <summary>A conversa foi marcada com outro modelo ou nível depois da parada.</summary>
    public bool Remarked =>
        (CurrentModel is not null && Model is not null && !string.Equals(CurrentModel, Model, StringComparison.OrdinalIgnoreCase))
        || (CurrentEffort is not null && Effort is not null && !string.Equals(CurrentEffort, Effort, StringComparison.OrdinalIgnoreCase));

    /// <summary>Motivo para não poder continuar; null quando o turno tem modelo, nível e acesso registrados.</summary>
    public string? Unavailable => string.IsNullOrEmpty(Model) || string.IsNullOrEmpty(Effort) || string.IsNullOrEmpty(SandboxMode)
        ? CodexQuotaText.MissingTurnData : null;

    internal static QuotaStop From(CodexThreadInfo thread, RolloutTail tail, string rolloutPath)
    {
        var turn = tail.StoppedTurn;
        string? cwd = turn?.Cwd;
        static string Key(string p) => p.TrimEnd('\\', '/');
        var extra = tail.WorkspaceRoots
            .Where(r => cwd is null || !string.Equals(Key(r), Key(cwd), StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        return new QuotaStop(thread.Id,
            string.IsNullOrWhiteSpace(thread.Title) ? LocalizedText.T("Conversa sem título") : thread.Title!,
            rolloutPath, tail.CompleteLength, tail.LastTurnId ?? "", tail.LastLifecycleAt ?? DateTimeOffset.MinValue,
            turn?.Model, turn?.Effort, turn?.EffortFromCollaborationMode ?? false, turn?.SandboxMode, turn?.NetworkAccess,
            turn?.WritableRoots ?? [], turn?.ApprovalPolicy, cwd, extra,
            tail.CurrentModel ?? thread.Model, tail.CurrentEffort ?? thread.Effort, tail.ContextTokens, tail.ReachedType);
    }
}

/// <summary>Quantas conversas a busca deixou de fora, por motivo.</summary>
public sealed record QuotaStopExclusions(int Interrupted, int ExecOrScripts, int Subagents, int Bar, int Other);

/// <summary>Resultado de uma busca de conversas paradas pela cota.</summary>
public sealed record QuotaStopScan(IReadOnlyList<QuotaStop> Stops, QuotaStopExclusions Excluded, bool UsedFallback, DateTimeOffset ScannedAt)
{
    /// <summary>Linha do log com as contagens (sem tradução).</summary>
    public string LogLine =>
        $"Conversas paradas pela cota: {Stops.Count} (excluídas: interrompidas por você={Excluded.Interrupted}, exec/scripts={Excluded.ExecOrScripts}, " +
        $"subagentes={Excluded.Subagents}, barra={Excluded.Bar}{(Excluded.Other > 0 ? $", outras={Excluded.Other}" : "")})";
}

/// <summary>Detector de conversas do usuário paradas pela cota do Codex: thread/list + fim de cada registro; plano B pelos registros locais.</summary>
public static class CodexQuotaStops
{
    public delegate Task<JsonElement> ThreadListCall(JsonObject request, CancellationToken ct);

    public static readonly TimeSpan Window = TimeSpan.FromDays(14);
    private const int MaxPages = 4;
    private static readonly HashSet<string> UserSources = new(StringComparer.OrdinalIgnoreCase) { "vscode", "cli" };
    private static readonly HashSet<string> UserOrigins = new(StringComparer.OrdinalIgnoreCase)
        { "Codex Desktop", "codex_work_desktop", "codex_cli_rs", "codex_vscode" };

    /// <summary>Busca única com cache (ver <see cref="QuotaStopFinder"/>): espera a tela acesa e usa o app-server do Codex.</summary>
    public static Task<QuotaStopScan> FindAsync(string codexHome, TimeSpan maxAge, CancellationToken ct, Action<string>? log = null) =>
        QuotaStopFinder.Default.FindAsync(codexHome, maxAge, ct, log);

    public static CodexThreadKind Classify(CodexThreadInfo thread)
    {
        if (thread.Ephemeral) return CodexThreadKind.Ephemeral;
        if (!string.IsNullOrEmpty(thread.ParentThreadId) || !string.IsNullOrEmpty(thread.AgentRole)
            || string.Equals(thread.Source, "subagent", StringComparison.OrdinalIgnoreCase)
            || thread.ThreadSource is "subagent" or "agent_created_thread")
            return CodexThreadKind.Subagent;
        if (string.Equals(thread.Originator, "winstatusbar", StringComparison.OrdinalIgnoreCase)) return CodexThreadKind.Bar;
        if (string.Equals(thread.Source, "exec", StringComparison.OrdinalIgnoreCase)
            || string.Equals(thread.Originator, "codex_exec", StringComparison.OrdinalIgnoreCase))
            return CodexThreadKind.ExecOrScript;
        if (thread.Source is null || !UserSources.Contains(thread.Source)) return CodexThreadKind.UnknownSource;
        if (thread.Originator is null || !UserOrigins.Contains(thread.Originator)) return CodexThreadKind.UnknownOrigin;
        return CodexThreadKind.User;
    }

    public static bool IsUserThread(CodexThreadInfo thread) => Classify(thread) == CodexThreadKind.User;

    /// <summary>Tira o prefixo de caminho longo: \\?\C:\… → C:\…; \\?\UNC\srv\… → \\srv\….</summary>
    internal static string? NormalizePath(string? path)
    {
        if (string.IsNullOrEmpty(path)) return path;
        if (path.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase)) return @"\\" + path[8..];
        if (path.StartsWith(@"\\?\", StringComparison.Ordinal)) return path[4..];
        return path;
    }

    internal static async Task<QuotaStopScan> ScanAsync(string codexHome, ThreadListCall list, DateTimeOffset now, CancellationToken ct, Action<string>? log = null)
    {
        var since = now - Window;
        List<CodexThreadInfo> threads;
        bool fallback = false;
        try
        {
            threads = await ListAsync(list, since, ct).ConfigureAwait(false);
        }
        catch (Exception e) when (!(e is OperationCanceledException && ct.IsCancellationRequested))
        {
            fallback = true;
            log?.Invoke($"Conversas paradas pela cota: lista oficial indisponível ({e.Message}); usando os registros locais");
            log?.Invoke("Conversas paradas pela cota: no modo local não dá para excluir as conversas arquivadas");
            threads = LocalThreads(codexHome, since);
        }

        int interrupted = 0, exec = 0, subagents = 0, bar = 0, other = 0;
        var stops = new List<QuotaStop>();
        foreach (var thread in threads)
        {
            ct.ThrowIfCancellationRequested();
            switch (Classify(thread))
            {
                case CodexThreadKind.ExecOrScript: exec++; continue;
                case CodexThreadKind.Subagent: subagents++; continue;
                case CodexThreadKind.Bar: bar++; continue;
                case CodexThreadKind.UnknownOrigin:
                    other++;
                    log?.Invoke($"Conversas paradas pela cota: origem desconhecida ignorada ({thread.Originator}) na conversa {thread.Id}");
                    continue;
                case CodexThreadKind.Ephemeral or CodexThreadKind.UnknownSource: other++; continue;
            }
            string? path = NormalizePath(thread.Path);
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) path = FindRollout(codexHome, thread.Id);
            if (path is null) continue;
            var tail = CodexRolloutTail.Read(path);
            if (tail is null) continue;
            if (tail.Verdict == RolloutVerdict.Aborted) { interrupted++; continue; }
            if (tail.Verdict != RolloutVerdict.QuotaStop || tail.LastLifecycleAt is null || tail.LastLifecycleAt < since) continue;
            stops.Add(QuotaStop.From(thread, tail, path));
        }

        var scan = new QuotaStopScan(stops.OrderByDescending(s => s.StoppedAt).ToList(),
            new QuotaStopExclusions(interrupted, exec, subagents, bar, other), fallback, now);
        log?.Invoke(scan.LogLine);
        return scan;
    }

    private static async Task<List<CodexThreadInfo>> ListAsync(ThreadListCall list, DateTimeOffset since, CancellationToken ct)
    {
        var threads = new List<CodexThreadInfo>();
        string? cursor = null;
        for (int page = 0; page < MaxPages; page++)
        {
            var request = new JsonObject { ["archived"] = false, ["sortKey"] = "updated_at", ["useStateDbOnly"] = true, ["limit"] = 50 };
            if (cursor is not null) request["cursor"] = cursor;
            var result = await list(request, ct).ConfigureAwait(false);
            bool reachedOld = false;
            if (result.ValueKind == JsonValueKind.Object && result.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
                foreach (var item in data.EnumerateArray())
                {
                    var thread = CodexThreadInfo.FromThreadList(item);
                    if (thread.UpdatedAt is { } updated && updated < since) { reachedOld = true; continue; }
                    if (thread.Id.Length > 0) threads.Add(thread);
                }
            cursor = CodexParsers.Str(result, "nextCursor");
            if (reachedOld || string.IsNullOrEmpty(cursor)) break;
        }
        return threads;
    }

    /// <summary>Plano B: session_meta dos registros alterados na janela, com os títulos do session_index.jsonl. Nunca lê archived_sessions.</summary>
    private static List<CodexThreadInfo> LocalThreads(string codexHome, DateTimeOffset since)
    {
        var threads = new List<CodexThreadInfo>();
        string sessions = Path.Combine(codexHome, "sessions");
        if (!Directory.Exists(sessions)) return threads;
        var titles = SessionTitleIndex.Read(Path.Combine(codexHome, "session_index.jsonl"));
        foreach (var file in SafeFiles(sessions, "rollout-*.jsonl"))
        {
            try
            {
                var info = new FileInfo(file);
                if (info.LastWriteTimeUtc < since.UtcDateTime) continue;
                string? first;
                using (var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (var reader = new StreamReader(fs))
                    first = reader.ReadLine();
                if (first is null || first.Length == 0 || first[0] != '{') continue;
                using var doc = JsonDocument.Parse(first);
                var root = doc.RootElement;
                if (CodexParsers.Str(root, "type") != "session_meta" || !root.TryGetProperty("payload", out var payload)) continue;
                string? id = CodexParsers.Str(payload, "id") ?? CodexParsers.Str(payload, "session_id");
                string? title = id is not null && titles is not null && titles.TryGetValue(id, out var t) ? t : null;
                threads.Add(CodexThreadInfo.FromSessionMeta(payload, file, new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero), title));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { }
        }
        return threads;
    }

    private static string? FindRollout(string codexHome, string id)
    {
        string sessions = Path.Combine(codexHome, "sessions");
        if (!Directory.Exists(sessions) || id.Length == 0) return null;
        return SafeFiles(sessions, $"rollout-*-{id}.jsonl").FirstOrDefault();
    }

    private static IEnumerable<string> SafeFiles(string dir, string pattern)
    {
        try { return Directory.EnumerateFiles(dir, pattern, new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true }).ToList(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return []; }
    }
}

/// <summary>Conexão usada por uma busca: a chamada thread/list e quem a mantém aberta.</summary>
internal sealed record ThreadListConnection(CodexQuotaStops.ThreadListCall List, IAsyncDisposable? Owner);

/// <summary>Só uma busca por vez: quem chega durante uma busca entra nela; o resultado fica em cache por maxAge.
/// A busca espera a tela acesa e só então começa a contar o tempo limite.</summary>
internal sealed class QuotaStopFinder(Func<CancellationToken, Task> waitVisible, Func<CancellationToken, Task<ThreadListConnection>> connect,
    Func<DateTimeOffset> clock, TimeSpan scanTimeout)
{
    internal static QuotaStopFinder Default { get; } = new(Presence.WhenVisibleAsync, async ct =>
    {
        var connection = await CodexComposerConnection.OpenAsync(ct).ConfigureAwait(false);
        return new ThreadListConnection((request, t) => connection.CallAsync("thread/list", request, t), connection);
    }, () => DateTimeOffset.Now, TimeSpan.FromSeconds(90));

    private readonly object _gate = new();
    private Task<QuotaStopScan>? _running;
    private QuotaStopScan? _last;

    internal Task<QuotaStopScan> FindAsync(string codexHome, TimeSpan maxAge, CancellationToken ct, Action<string>? log = null)
    {
        Task<QuotaStopScan> task;
        lock (_gate)
        {
            if (_running is null && _last is not null && clock() - _last.ScannedAt < maxAge) return Task.FromResult(_last);
            // Task.Run: o finally da busca precisa deste lock, então ele só limpa _running depois desta atribuição.
            _running ??= Task.Run(() => RunAsync(codexHome, log));
            task = _running;
        }
        return task.WaitAsync(ct);
    }

    private async Task<QuotaStopScan> RunAsync(string codexHome, Action<string>? log)
    {
        try
        {
            await waitVisible(CancellationToken.None).ConfigureAwait(false);
            using var timeout = new CancellationTokenSource(scanTimeout);
            ThreadListConnection connection;
            try { connection = await connect(timeout.Token).ConfigureAwait(false); }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                string message = e.Message;
                connection = new ThreadListConnection((_, _) => throw new IOException(message), null);
            }
            try
            {
                var scan = await CodexQuotaStops.ScanAsync(codexHome, connection.List, clock(), timeout.Token, log).ConfigureAwait(false);
                lock (_gate) _last = scan;
                return scan;
            }
            finally
            {
                if (connection.Owner is not null) await connection.Owner.DisposeAsync().ConfigureAwait(false);
            }
        }
        finally
        {
            lock (_gate) _running = null;
        }
    }
}
