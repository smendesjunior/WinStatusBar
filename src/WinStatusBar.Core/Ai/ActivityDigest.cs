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
    /// Uma linha do transcript já interpretada: só o que o resumo usa dela (mensagem pública curta, planos e horário).
    /// Antes (até 10/11/2026) o trecho final guardava as linhas brutas — até 2 milhões de caracteres por conversa, com
    /// linhas de centenas de KB no heap de objetos grandes — e o interpretava inteiro de novo a cada mudança do arquivo,
    /// várias vezes por minuto enquanto a IA trabalha. Agora cada linha é lida e interpretada uma única vez.
    /// </summary>
    private sealed class Entry
    {
        public int Chars;                // tamanho da linha original: conta para o limite do trecho final
        public DateTimeOffset? At;
        public List<JsonElement>? Plans; // update_plan / TodoWrite da linha, na ordem
        public string? Text;             // mensagem pública já filtrada (até 1800 caracteres)
        public bool User;
    }

    /// <summary>
    /// Até onde cada transcript já foi lido. Eles só crescem no fim; antes, cada mudança relia o arquivo inteiro
    /// (8,8 MB numa sessão longa, várias vezes por minuto), e as linhas grandes se acumulavam no heap de objetos grandes
    /// do .NET (~280 MB medidos em 09/11/2026), além de manter o disco ocupado.
    /// </summary>
    private sealed class Cursor
    {
        public long Offset;                      // fim da última linha completa já lida
        public List<ActivityStep> EarlierSteps = []; // plano das linhas que já saíram do trecho final
        public readonly LinkedList<Entry> Tail = new();
        public long TailChars;
        public DateTimeOffset? Started;
        public bool Partial;
    }

    private static readonly Dictionary<string, Cursor> Cursors = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Lock CursorGate = new();
    // Reaproveitados sob CursorGate: as linhas são interpretadas direto do UTF-8, sem virar texto antes.
    private static readonly byte[] ReadBuffer = new byte[64 * 1024];
    private static byte[] _lineBuffer = new byte[64 * 1024];
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
            var digest = Fold(cursor.Tail, cursor.Partial, hasTranscript: true, cursor.EarlierSteps);
            return cursor.Started is null || digest.StartedAt <= cursor.Started ? digest : digest with { StartedAt = cursor.Started };
        }
    }

    /// <summary>Lê só as linhas completas acrescentadas; a última, ainda sendo gravada, fica para a próxima leitura.</summary>
    private static void ReadNewLines(FileStream file, Cursor cursor)
    {
        int length = 0;
        long consumed = 0;
        int n;
        while ((n = file.Read(ReadBuffer, 0, ReadBuffer.Length)) > 0)
        {
            int start = 0;
            for (int i = 0; i < n; i++)
            {
                if (ReadBuffer[i] != (byte)'\n') continue;
                Append(ReadBuffer.AsSpan(start, i - start), ref length);
                consumed += length + 1;
                int end = length;
                while (end > 0 && _lineBuffer[end - 1] == (byte)'\r') end--;
                Add(cursor, _lineBuffer.AsMemory(0, end));
                length = 0;
                start = i + 1;
            }
            Append(ReadBuffer.AsSpan(start, n - start), ref length);
        }
        cursor.Offset += consumed;
    }

    private static void Append(ReadOnlySpan<byte> bytes, ref int length)
    {
        if (length + bytes.Length > _lineBuffer.Length) Array.Resize(ref _lineBuffer, Math.Max(_lineBuffer.Length * 2, length + bytes.Length));
        bytes.CopyTo(_lineBuffer.AsSpan(length));
        length += bytes.Length;
    }

    private static void Add(Cursor cursor, ReadOnlyMemory<byte> line)
    {
        var entry = Interpret(line, Encoding.UTF8.GetCharCount(line.Span));
        if (cursor.Started is null && entry.At is not null) cursor.Started = entry.At;
        cursor.Tail.AddLast(entry); cursor.TailChars += entry.Chars;
        while (cursor.TailChars > TailLimit && cursor.Tail.First is { } first)
        {
            cursor.Tail.RemoveFirst(); cursor.TailChars -= first.Value.Chars;
            cursor.Partial = true;
            if (first.Value.Plans is { } plans)
            {
                var steps = new List<ActivityStep>(cursor.EarlierSteps);
                foreach (var plan in plans) Plan(plan, steps);
                cursor.EarlierSteps = steps;
            }
        }
    }

    /// <summary>Extrai apenas mensagens públicas e planos registrados; não lê raciocínio interno nem saídas de ferramentas.</summary>
    public static ActivityDigest Parse(IEnumerable<string> lines, bool partial = false, bool hasTranscript = true, IReadOnlyList<ActivityStep>? initialSteps = null)
        => Fold(lines.Select(line => Interpret(Encoding.UTF8.GetBytes(line), line.Length)), partial, hasTranscript, initialSteps);

    private static ActivityDigest Fold(IEnumerable<Entry> entries, bool partial, bool hasTranscript, IReadOnlyList<ActivityStep>? initialSteps)
    {
        string? request = null;
        var updates = new List<ActivityUpdate>();
        var steps = initialSteps is null ? new List<ActivityStep>() : new List<ActivityStep>(initialSteps);
        DateTimeOffset? started = null;
        foreach (var entry in entries)
        {
            if (entry.At is { } at && (started is null || at < started)) started = at;
            if (entry.Plans is { } plans) foreach (var plan in plans) Plan(plan, steps);
            if (entry.Text is not { } combined) continue;
            if (entry.User)
            {
                request ??= combined;
                updates.Add(new("Pedido ou orientação do usuário: " + combined, entry.At));
            }
            else if (updates.LastOrDefault()?.Text != combined)
            {
                updates.Add(new(combined, entry.At));
            }
            if (updates.Count > 24) updates.RemoveAt(0);
        }
        return new(request, updates, steps, partial, hasTranscript) { StartedAt = started };
    }

    private static Entry Interpret(ReadOnlyMemory<byte> utf8, int chars)
    {
        var entry = new Entry { Chars = chars };
        try
        {
            using var doc = JsonDocument.Parse(utf8);
            Interpret(doc.RootElement, entry);
        }
        catch (JsonException) { } // arquivo sendo escrito: linha incompleta não interrompe a atualização
        return entry;
    }

    private static void Interpret(JsonElement root, Entry entry)
    {
        if (root.ValueKind != JsonValueKind.Object) return;
        entry.At = DateTimeOffset.TryParse(Text(root, "timestamp"), out var date) ? date : null;
        JsonElement message;
        if (Text(root, "type") == "response_item" && root.TryGetProperty("payload", out var payload)) message = payload;
        // Claude: somente registros da conversa ("user"/"assistant"). Cópias como "api-request-blob"
        // repetem mensagens antigas e podiam reaplicar um plano já superado.
        else if (Text(root, "type") is null or "user" or "assistant"
            && root.TryGetProperty("message", out var claude) && claude.ValueKind == JsonValueKind.Object) message = claude;
        else return;
        if (message.ValueKind != JsonValueKind.Object) return;
        string? kind = Text(message, "type"), role = Text(message, "role");
        if (Text(message, "channel") is "analysis" or "justify" or "confidence") return;
        if (kind is "function_call" or "custom_tool_call")
        {
            string? name = Text(message, "name");
            if (name?.EndsWith("update_plan", StringComparison.Ordinal) == true)
            {
                string? arguments = Text(message, "arguments") ?? Text(message, "input");
                if (arguments is not null) { using var input = JsonDocument.Parse(arguments); AddPlan(entry, input.RootElement); }
            }
            return;
        }
        if (role is not ("user" or "assistant") || !message.TryGetProperty("content", out var content)) return;
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
                    && block.TryGetProperty("input", out var input)) AddPlan(entry, input);
            }
        string combined = Short(string.Join("\n", text), 1800);
        if (string.IsNullOrWhiteSpace(combined)) return;
        // Eventos de ambiente/instruções não são um pedido novo da pessoa.
        if (role == "user" && CodexModelBridge.IsInjectedContext(combined)) return;
        entry.Text = combined;
        entry.User = role == "user";
    }

    /// <summary>Guarda só a entrada do plano (pequena), sem o documento da linha inteira.</summary>
    private static void AddPlan(Entry entry, JsonElement input) => (entry.Plans ??= []).Add(input.Clone());

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
