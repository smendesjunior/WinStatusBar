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

    public static ActivityDigest Read(string? path)
    {
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return Parse([], hasTranscript: false);
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        const int limit = 2 * 1024 * 1024;
        bool partial = file.Length > limit;
        using var reader = new StreamReader(file, Encoding.UTF8);
        if (!partial)
        {
            var all = new List<string>();
            while (reader.ReadLine() is { } line) all.Add(line);
            return Parse(all, partial);
        }
        // Transcript grande (o app desktop do Claude grava blocos enormes): mensagens só do trecho final,
        // mas as operações de plano do arquivo inteiro, para a lista inicial de etapas não se perder.
        var earlierPlan = new List<string>();
        var tail = new LinkedList<string>();
        long tailChars = 0;
        DateTimeOffset? started = null;
        while (reader.ReadLine() is { } line)
        {
            if (started is null && line.Contains("\"timestamp\"", StringComparison.Ordinal)) started = TimestampOf(line);
            tail.AddLast(line); tailChars += line.Length;
            while (tailChars > limit && tail.First is { } first)
            {
                tail.RemoveFirst(); tailChars -= first.Value.Length;
                if (IsPlanLine(first.Value)) earlierPlan.Add(first.Value);
            }
        }
        var digest = Parse(earlierPlan.Concat(tail), partial);
        return started is null || digest.StartedAt <= started ? digest : digest with { StartedAt = started };
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
    public static ActivityDigest Parse(IEnumerable<string> lines, bool partial = false, bool hasTranscript = true)
    {
        string? request = null;
        var updates = new List<ActivityUpdate>();
        var steps = new List<ActivityStep>();
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
