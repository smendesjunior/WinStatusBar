using System.Text;
using System.Text.Json;

namespace WinStatusBar.Core.Ai;

public sealed record ActivityStep(string Text, string Status);
public sealed record ActivityUpdate(string Text, DateTimeOffset? At);
public sealed record ActivityDigest(string? Request, IReadOnlyList<ActivityUpdate> Updates,
    IReadOnlyList<ActivityStep> Steps, bool Partial, bool HasTranscript)
{
    /// <summary>Primeiro horário registrado na conversa (início da tarefa), mesmo depois de a barra reiniciar.</summary>
    public DateTimeOffset? StartedAt { get; init; }

    private const int TailLimit = 2 * 1024 * 1024;

    /// <summary>
    /// Até onde cada transcript já foi lido. Eles só crescem no fim; antes, cada mudança relia o arquivo inteiro
    /// (8,8 MB numa sessão longa, várias vezes por minuto), e as linhas grandes se acumulavam no heap de objetos grandes
    /// do .NET (~280 MB medidos em 09/11/2026), além de manter o disco ocupado.
    /// </summary>
    private sealed class Cursor
    {
        public long Offset;                      // fim da última linha completa já lida
        public List<ActivityStep> EarlierSteps = []; // plano das linhas que já saíram do trecho final
        public readonly LinkedList<string> Tail = new();
        public long TailChars;
        public DateTimeOffset? Started;
        public bool Partial;
    }

    private static readonly Dictionary<string, Cursor> Cursors = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Lock CursorGate = new();
    /// <summary>Bytes lidos do arquivo na última leitura (testes).</summary>
    internal static long LastBytesRead { get; private set; }

    public static ActivityDigest Read(string? path)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return Parse([], hasTranscript: false);
        lock (CursorGate)
        {
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            string key = Path.GetFullPath(path);
            if (!Cursors.TryGetValue(key, out var cursor) || file.Length < cursor.Offset) // novo, ou trocado por um menor
            {
                if (!Cursors.ContainsKey(key) && Cursors.Count >= 20) Cursors.Remove(Cursors.Keys.First());
                Cursors[key] = cursor = new Cursor();
            }
            long before = cursor.Offset;
            file.Seek(cursor.Offset, SeekOrigin.Begin);
            ReadNewLines(file, cursor);
            LastBytesRead = cursor.Offset - before;
            // Transcript grande (o app desktop do Claude grava blocos enormes): mensagens só do trecho final, mas o plano
            // do arquivo inteiro, para a lista inicial de etapas não se perder.
            var digest = Parse(cursor.Tail, cursor.Partial, initialSteps: cursor.EarlierSteps);
            return cursor.Started is null || digest.StartedAt <= cursor.Started ? digest : digest with { StartedAt = cursor.Started };
        }
    }

    /// <summary>Lê só as linhas completas acrescentadas; a última, ainda sendo gravada, fica para a próxima leitura.</summary>
    private static void ReadNewLines(FileStream file, Cursor cursor)
    {
        var buffer = new byte[64 * 1024];
        using var line = new MemoryStream();
        long consumed = 0;
        int n;
        while ((n = file.Read(buffer, 0, buffer.Length)) > 0)
        {
            int start = 0;
            for (int i = 0; i < n; i++)
            {
                if (buffer[i] != (byte)'\n') continue;
                line.Write(buffer, start, i - start);
                consumed += line.Length + 1;
                Add(cursor, Encoding.UTF8.GetString(line.GetBuffer(), 0, (int)line.Length).TrimEnd('\r'));
                line.SetLength(0);
                start = i + 1;
            }
            line.Write(buffer, start, n - start);
        }
        cursor.Offset += consumed;
    }

    private static void Add(Cursor cursor, string line)
    {
        if (cursor.Started is null && line.Contains("\"timestamp\"", StringComparison.Ordinal)) cursor.Started = TimestampOf(line);
        cursor.Tail.AddLast(line); cursor.TailChars += line.Length;
        while (cursor.TailChars > TailLimit && cursor.Tail.First is { } first)
        {
            cursor.Tail.RemoveFirst(); cursor.TailChars -= first.Value.Length;
            cursor.Partial = true;
            if (IsPlanLine(first.Value)) cursor.EarlierSteps = Parse([first.Value], initialSteps: cursor.EarlierSteps).Steps.ToList();
        }
    }

    private static DateTimeOffset? TimestampOf(string line)
    {
        try
        {
            using var doc = JsonDocument.Parse(line);
            return doc.RootElement.ValueKind == JsonValueKind.Object && DateTimeOffset.TryParse(Text(doc.RootElement, "timestamp"), out var at) ? at : null;
        }
        catch (JsonException) { return null; }
    }

    private static bool IsPlanLine(string line)
        => line.Contains("update_plan", StringComparison.Ordinal) || line.Contains("\"TodoWrite\"", StringComparison.Ordinal);

    /// <summary>Extrai apenas mensagens públicas e planos registrados; não lê raciocínio interno nem saídas de ferramentas.</summary>
    public static ActivityDigest Parse(IEnumerable<string> lines, bool partial = false, bool hasTranscript = true, IReadOnlyList<ActivityStep>? initialSteps = null)
    {
        string? request = null;
        var updates = new List<ActivityUpdate>();
        var steps = initialSteps is null ? new List<ActivityStep>() : new List<ActivityStep>(initialSteps);
        DateTimeOffset? started = null;
        foreach (string line in lines)
        {
            try
            {
                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object) continue;
                DateTimeOffset? at = DateTimeOffset.TryParse(Text(root, "timestamp"), out var date) ? date : null;
                if (at is not null && (started is null || at < started)) started = at;
                JsonElement message;
                if (Text(root, "type") == "response_item" && root.TryGetProperty("payload", out var payload)) message = payload;
                // Claude: somente registros da conversa ("user"/"assistant"). Cópias como "api-request-blob"
                // repetem mensagens antigas e podiam reaplicar um plano já superado.
                else if (Text(root, "type") is null or "user" or "assistant"
                    && root.TryGetProperty("message", out var claude) && claude.ValueKind == JsonValueKind.Object) message = claude;
                else continue;
                if (message.ValueKind != JsonValueKind.Object) continue;
                string? kind = Text(message, "type"), role = Text(message, "role");
                if (Text(message, "channel") is "analysis" or "justify" or "confidence") continue;
                if (kind is "function_call" or "custom_tool_call")
                {
                    string? name = Text(message, "name");
                    if (name?.EndsWith("update_plan", StringComparison.Ordinal) == true)
                    {
                        string? arguments = Text(message, "arguments") ?? Text(message, "input");
                        if (arguments is not null) { using var input = JsonDocument.Parse(arguments); Plan(input.RootElement, steps); }
                    }
                    continue;
                }
                if (role is not ("user" or "assistant") || !message.TryGetProperty("content", out var content)) continue;
                var text = new List<string>();
                if (content.ValueKind == JsonValueKind.String) text.Add(content.GetString() ?? "");
                else if (content.ValueKind == JsonValueKind.Array)
                    foreach (var block in content.EnumerateArray())
                    {
                        string? type = Text(block, "type");
                        if (type is "text" or "input_text" or "output_text")
                        { if (Text(block, "text") is { } value) text.Add(value); }
                        else if (role == "assistant" && type == "tool_use"
                            && Text(block, "name") is { } name && (name == "TodoWrite" || name.EndsWith("update_plan", StringComparison.Ordinal))
                            && block.TryGetProperty("input", out var input)) Plan(input, steps);
                    }
                string combined = Short(string.Join("\n", text), 1800);
                if (string.IsNullOrWhiteSpace(combined)) continue;
                if (role == "user")
                {
                    // Eventos de ambiente/instruções não são um pedido novo da pessoa.
                    if (CodexModelBridge.IsInjectedContext(combined)) continue;
                    request ??= combined;
                    updates.Add(new("Pedido ou orientação do usuário: " + combined, at));
                }
                else if (updates.LastOrDefault()?.Text != combined)
                {
                    updates.Add(new(combined, at));
                }
                if (updates.Count > 24) updates.RemoveAt(0);
            }
            catch (JsonException) { } // arquivo sendo escrito: linha incompleta não interrompe a atualização
        }
        return new(request, updates, steps, partial, hasTranscript) { StartedAt = started };
    }

    private static void Plan(JsonElement input, List<ActivityStep> steps)
    {
        if (input.ValueKind != JsonValueKind.Object) return;
        foreach (string key in new[] { "plan", "todos", "items" })
            if (input.TryGetProperty(key, out var list) && list.ValueKind == JsonValueKind.Array)
            {
                var replacement = new List<ActivityStep>();
                foreach (var item in list.EnumerateArray())
                    if (item.ValueKind == JsonValueKind.Object && (Text(item, "step") ?? Text(item, "content") ?? Text(item, "text") ?? Text(item, "description")) is { } label)
                        replacement.Add(new(Short(label, 400), Text(item, "status") ?? "pending"));
                steps.Clear(); steps.AddRange(replacement.Take(60)); return;
            }
        if (PlanTracker.OperationOf(input) is not { } operation || steps.Count == 0) return;
        string state = string.Concat(steps.Select(s => s.Status == "completed" ? 'c' : s.Status == "in_progress" ? 'i' : 'p'));
        string updated = PlanTracker.Apply(state, operation) ?? state;
        for (int i = 0; i < steps.Count; i++) steps[i] = steps[i] with { Status = updated[i] == 'c' ? "completed" : updated[i] == 'i' ? "in_progress" : "pending" };
    }

    private static string? Text(JsonElement element, string key) => element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    private static string Short(string text, int length) => text.Length <= length ? text.Trim() : text[..length].Trim() + "…";
}
