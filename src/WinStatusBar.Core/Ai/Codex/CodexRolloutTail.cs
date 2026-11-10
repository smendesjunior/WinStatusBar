using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;

namespace WinStatusBar.Core.Ai.Codex;

/// <summary>Como terminou o último turno de uma conversa, pelo último evento de ciclo do registro.</summary>
public enum RolloutVerdict { Empty, Running, Completed, Failed, Aborted, QuotaStop }

/// <summary>Modelo, nível, acesso e pasta registrados no turn_context de um turno.</summary>
public sealed record RolloutTurnContext(string? TurnId, string? Model, string? Effort, bool EffortFromCollaborationMode,
    string? SandboxMode, bool? NetworkAccess, IReadOnlyList<string> WritableRoots, string? ApprovalPolicy, string? Cwd);

/// <summary>O que o fim de um registro diz sobre a conversa.</summary>
public sealed record RolloutTail(RolloutVerdict Verdict, string? LastTurnId, DateTimeOffset? LastLifecycleAt, RolloutTurnContext? StoppedTurn,
    string? CurrentModel, string? CurrentEffort, long? ContextTokens, string? ReachedType, string? ThreadId, string? Source,
    string? Originator, string? ThreadSource, IReadOnlyList<string> WorkspaceRoots, long CompleteLength);

/// <summary>Lê o fim de um registro do Codex (rollout-*.jsonl) de trás para frente, em blocos, sem ler o arquivo inteiro.</summary>
public static class CodexRolloutTail
{
    public const int BlockSize = 256 * 1024;
    public const long MaxBytes = 64L * 1024 * 1024;
    private const int MaxMetaBytes = 1024 * 1024;

    private static readonly ConcurrentDictionary<string, (long Length, DateTime WriteUtc, RolloutTail Tail)> Cache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Lê o registro; null se não puder ser aberto. O resultado fica em cache por (caminho, tamanho, data de alteração).</summary>
    public static RolloutTail? Read(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists) return null;
            if (Cache.TryGetValue(path, out var hit) && hit.Length == info.Length && hit.WriteUtc == info.LastWriteTimeUtc) return hit.Tail;

            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.RandomAccess);
            long length = fs.Length;
            long complete = CompleteLengthOf(fs, length);
            string? meta = FirstLine(fs, complete);
            var tail = Analyze(LinesFromEnd(fs, complete), meta, complete);

            if (Cache.Count > 256) Cache.Clear();
            Cache[path] = (info.Length, info.LastWriteTimeUtc, tail);
            return tail;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>Tamanho até a última quebra de linha (inclusive): uma última linha incompleta fica de fora.</summary>
    private static long CompleteLengthOf(Stream stream, long length)
    {
        var buffer = new byte[64 * 1024];
        long end = length;
        while (end > 0)
        {
            int size = (int)Math.Min(buffer.Length, end);
            stream.Position = end - size;
            stream.ReadExactly(buffer, 0, size);
            int idx = Array.LastIndexOf(buffer, (byte)'\n', size - 1, size);
            if (idx >= 0) return end - size + idx + 1;
            end -= size;
            if (length - end > MaxBytes) break;
        }
        return 0;
    }

    /// <summary>Primeira linha do arquivo (candidata a session_meta), até 1 MB.</summary>
    private static string? FirstLine(Stream stream, long length)
    {
        if (length == 0) return null;
        int size = (int)Math.Min(MaxMetaBytes, length);
        var buffer = new byte[size];
        stream.Position = 0;
        stream.ReadExactly(buffer, 0, size);
        int idx = Array.IndexOf(buffer, (byte)'\n');
        if (idx < 0) return null;
        return Encoding.UTF8.GetString(buffer, 0, idx).TrimEnd('\r');
    }

    /// <summary>Linhas do fim para o começo, lendo blocos de trás para frente; trabalha em bytes para não partir caracteres UTF-8.</summary>
    internal static IEnumerable<string> LinesFromEnd(Stream stream, long length, long maxBytes = MaxBytes, int blockSize = BlockSize)
    {
        byte[] carry = [];
        long pos = length, floor = Math.Max(0, length - maxBytes);
        while (pos > floor)
        {
            int size = (int)Math.Min(blockSize, pos - floor);
            pos -= size;
            var buffer = new byte[size + carry.Length];
            stream.Position = pos;
            stream.ReadExactly(buffer, 0, size);
            Buffer.BlockCopy(carry, 0, buffer, size, carry.Length);

            int lineEnd = buffer.Length;
            for (int i = buffer.Length - 1; i >= 0; i--)
            {
                if (buffer[i] != (byte)'\n') continue;
                if (Decode(buffer, i + 1, lineEnd) is { } line) yield return line;
                lineEnd = i;
            }
            carry = buffer[..lineEnd];
        }
        // A primeira linha só é completa quando a leitura chegou ao começo do arquivo.
        if (pos == 0 && Decode(carry, 0, carry.Length) is { } first) yield return first;
    }

    private static string? Decode(byte[] buffer, int start, int end)
    {
        if (end > start && buffer[end - 1] == (byte)'\r') end--;
        return end > start ? Encoding.UTF8.GetString(buffer, start, end - start) : null;
    }

    /// <summary>Análise pura, do fim para o começo:
    /// 1) o primeiro evento de ciclo (task_started, task_complete, turn_aborted) decide; fora da parada pela cota, encerra;
    /// 2) na parada, o turn_context do mesmo turno (ou sem turn_id) é o turno parado; outro evento de ciclo antes dele o deixa null;
    /// 3) até lá vale o mais novo de: marcação (thread_settings_applied), contexto e motivo (token_count).</summary>
    public static RolloutTail Analyze(IEnumerable<string> linesFromEnd, string? sessionMetaLine = null, long completeLength = 0)
    {
        RolloutVerdict verdict = RolloutVerdict.Empty;
        string? lastTurn = null, currentModel = null, currentEffort = null, reached = null;
        DateTimeOffset? lifecycleAt = null;
        RolloutTurnContext? stopped = null;
        long? contextTokens = null;
        bool settingsSeen = false, decided = false;

        foreach (var line in linesFromEnd)
        {
            if (line.Length == 0 || line[0] != '{') continue;
            JsonDocument doc;
            try { doc = JsonDocument.Parse(line); } catch (JsonException) { continue; }
            using (doc)
            {
                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object) continue;
                string? type = CodexParsers.Str(root, "type");
                if (!root.TryGetProperty("payload", out var p) || p.ValueKind != JsonValueKind.Object) continue;
                if (type == "session_meta") { sessionMetaLine ??= line; continue; }
                string? ptype = CodexParsers.Str(p, "type");

                if (type == "turn_context")
                {
                    if (!decided || verdict != RolloutVerdict.QuotaStop) continue;
                    string? turn = CodexParsers.Str(p, "turn_id");
                    if (turn is not null && turn != lastTurn) continue;
                    stopped = TurnContext(p);
                    break;
                }
                if (type != "event_msg") continue;

                if (ptype is "task_started" or "task_complete" or "turn_aborted")
                {
                    if (decided) break; // na parada, outro evento de ciclo antes do turn_context: sem dados do turno
                    decided = true;
                    lastTurn = CodexParsers.Str(p, "turn_id");
                    lifecycleAt = Timestamp(root);
                    verdict = ptype switch
                    {
                        "task_started" => RolloutVerdict.Running,
                        "turn_aborted" => RolloutVerdict.Aborted,
                        _ => EndVerdict(p),
                    };
                    if (verdict != RolloutVerdict.QuotaStop) break;
                    continue;
                }
                if (ptype == "thread_settings_applied" && !settingsSeen && p.TryGetProperty("thread_settings", out var settings))
                {
                    settingsSeen = true;
                    currentModel = CodexParsers.Str(settings, "model");
                    currentEffort = CodexParsers.Str(settings, "reasoning_effort");
                }
                else if (ptype == "token_count")
                {
                    if (contextTokens is null && p.TryGetProperty("info", out var info) && info.ValueKind == JsonValueKind.Object
                        && info.TryGetProperty("last_token_usage", out var last))
                        contextTokens = CodexParsers.Long(last, "input_tokens");
                    if (reached is null && p.TryGetProperty("rate_limits", out var rl))
                        reached = CodexParsers.Str(rl, "rate_limit_reached_type") ?? CodexParsers.Str(rl, "rateLimitReachedType");
                }
            }
        }

        var (threadId, source, originator, threadSource, roots) = MetaOf(sessionMetaLine);
        return new RolloutTail(verdict, lastTurn, lifecycleAt, stopped, currentModel, currentEffort, contextTokens, reached,
            threadId, source, originator, threadSource, roots, completeLength);
    }

    private static RolloutVerdict EndVerdict(JsonElement p)
    {
        if (!p.TryGetProperty("error", out var error) || error.ValueKind != JsonValueKind.Object) return RolloutVerdict.Completed;
        return CodexParsers.Str(error, "codex_error_info") == CodexParsers.UsageLimitExceeded ? RolloutVerdict.QuotaStop : RolloutVerdict.Failed;
    }

    private static DateTimeOffset? Timestamp(JsonElement root) =>
        CodexParsers.Str(root, "timestamp") is { } ts && DateTimeOffset.TryParse(ts, System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AssumeUniversal, out var at) ? at : null;

    private static RolloutTurnContext TurnContext(JsonElement p)
    {
        var settings = p.TryGetProperty("collaboration_mode", out var mode) && mode.ValueKind == JsonValueKind.Object
            && mode.TryGetProperty("settings", out var s) && s.ValueKind == JsonValueKind.Object ? s : default;
        string? effort = CodexParsers.Str(p, "effort");
        bool fromMode = false;
        if (string.IsNullOrEmpty(effort))
        {
            effort = CodexParsers.Str(settings, "reasoning_effort");
            fromMode = effort is not null;
        }
        var sandbox = p.TryGetProperty("sandbox_policy", out var sp) && sp.ValueKind == JsonValueKind.Object ? sp : default;
        return new RolloutTurnContext(
            CodexParsers.Str(p, "turn_id"),
            CodexParsers.Str(p, "model") ?? CodexParsers.Str(settings, "model"),
            effort, fromMode,
            CodexParsers.Str(sandbox, "type"),
            CodexParsers.Bool(sandbox, "network_access"),
            Strings(sandbox, "writable_roots"),
            CodexParsers.Str(p, "approval_policy"),
            CodexParsers.Str(p, "cwd"));
    }

    private static IReadOnlyList<string> Strings(JsonElement e, string name)
    {
        if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(name, out var arr) || arr.ValueKind != JsonValueKind.Array) return [];
        var list = new List<string>();
        foreach (var item in arr.EnumerateArray())
            if (item.ValueKind == JsonValueKind.String && item.GetString() is { Length: > 0 } s) list.Add(s);
        return list;
    }

    private static (string? Id, string? Source, string? Originator, string? ThreadSource, IReadOnlyList<string> Roots) MetaOf(string? line)
    {
        if (string.IsNullOrEmpty(line) || line[0] != '{') return (null, null, null, null, []);
        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            if (CodexParsers.Str(root, "type") != "session_meta" || !root.TryGetProperty("payload", out var p)) return (null, null, null, null, []);
            string? source = p.TryGetProperty("source", out var src) ? src.ValueKind switch
            {
                JsonValueKind.String => src.GetString(),
                JsonValueKind.Object when src.TryGetProperty("subagent", out _) || src.TryGetProperty("subAgent", out _) => "subagent",
                JsonValueKind.Object => src.EnumerateObject().Select(x => x.Name).FirstOrDefault(),
                _ => null,
            } : null;
            return (CodexParsers.Str(p, "id") ?? CodexParsers.Str(p, "session_id"), source, CodexParsers.Str(p, "originator"),
                CodexParsers.Str(p, "thread_source"), Strings(p, "runtime_workspace_roots"));
        }
        catch (JsonException) { return (null, null, null, null, []); }
    }
}
