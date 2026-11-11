using System.Text;
using System.Text.Json.Nodes;

namespace CodexIAs;

/// <summary>
/// Compactação "v2" do Codex com uma IA da barra escolhida. O Codex não chama mais /responses/compact: manda um pedido
/// comum a /responses com um item <c>{"type":"compaction_trigger"}</c> no fim do "input" e espera, na resposta, exatamente
/// um item <c>{"type":"compaction","encrypted_content":…}</c> (codex-rs/core/src/compact_remote_v2.rs). Antes o encaminhador
/// respondia como a uma pergunta comum, o Codex recusava ("expected exactly one compaction output item") e o turno falhava.
///
/// Aqui o "conteúdo cifrado" é o resumo em texto, marcado com <see cref="Marker"/>: nos pedidos seguintes ele volta no
/// "input" e o ConversationReader o lê como resumo da conversa. Se a conversa for depois para a OpenAI (conta ou API), o
/// item marcado vira uma mensagem comum (<see cref="Unwrap"/>), porque a OpenAI só aceita o conteúdo que ela mesma cifrou.
/// </summary>
internal static class CompactionV2
{
    internal const string Marker = "winstatusbar-resumo-v1:";
    private const string Heading = "[Resumo da conversa até aqui, feito pela WinStatusBar ao compactar]\n";

    private static string? TypeOf(JsonNode? item)
        => item is JsonObject o && o["type"] is JsonValue v && v.TryGetValue<string>(out var t) ? t : null;

    /// <summary>O pedido é uma compactação v2 (traz o item compaction_trigger).</summary>
    public static bool Requested(JsonNode payload)
        => payload["input"] is JsonArray input && input.Any(i => TypeOf(i) == "compaction_trigger");

    /// <summary>Resumo de um item de compactação feito aqui; null para os demais (inclusive os cifrados pela OpenAI).</summary>
    public static string? SummaryOf(JsonNode? item)
        => TypeOf(item) is "compaction" or "compaction_summary" && item!["encrypted_content"] is JsonValue v
           && v.TryGetValue<string>(out var s) && s.StartsWith(Marker, StringComparison.Ordinal) ? s[Marker.Length..] : null;

    /// <summary>Resumo da conversa do pedido (o mesmo pacote de passagem da compactação antiga).</summary>
    internal static string Summary(JsonNode payload)
    {
        var c = ConversationReader.Read(payload);
        var all = c.History.ToList();
        if (c.Prompt.Length > 0) all.Add((c.PromptIsResult ? ConversationReader.Result : "user", c.Prompt));
        string summary = ConversationReader.Transcript(all, BridgeServer.CompactChars);
        return summary.Length > 0 ? summary : "(conversa sem falas para resumir)";
    }

    public static JsonObject Item(string summary) => new()
    {
        ["type"] = "compaction", ["encrypted_content"] = Marker + summary,
    };

    /// <summary>Responde a compactação: um único item "compaction", em streaming ou não, como o Codex pediu.</summary>
    internal static async Task RespondAsync(Stream stream, string slug, JsonNode payload)
    {
        string summary = Summary(payload);
        var item = Item(summary);
        string id = "resp_cdx_" + Guid.NewGuid().ToString("N");
        int input = Math.Max(1, summary.Length / 4);
        JsonObject Response(string status, JsonArray output) => new()
        {
            ["id"] = id, ["object"] = "response", ["created_at"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds(), ["status"] = status,
            ["model"] = slug, ["output"] = output,
        };
        var done = Response("completed", new JsonArray(item.DeepClone()));
        done["usage"] = new JsonObject
        {
            ["input_tokens"] = input, ["input_tokens_details"] = new JsonObject { ["cached_tokens"] = 0 },
            ["output_tokens"] = 1, ["output_tokens_details"] = new JsonObject { ["reasoning_tokens"] = 0 }, ["total_tokens"] = input + 1,
        };
        Log.Write($"Compactação do Codex respondida pelo encaminhador ({slug}): resumo de {summary.Length} caracteres");
        bool streaming = payload["stream"] is JsonValue sv && sv.TryGetValue<bool>(out bool on) && on;
        if (!streaming) { await BridgeServer.JsonAsync(stream, done); return; }

        var sb = new StringBuilder();
        void Event(JsonObject data) => sb.Append("event: ").Append(data["type"]).Append("\ndata: ").Append(data.ToJsonString()).Append("\n\n");
        Event(new() { ["type"] = "response.created", ["response"] = Response("in_progress", new JsonArray()) });
        Event(new() { ["type"] = "response.output_item.added", ["output_index"] = 0, ["item"] = item.DeepClone() });
        Event(new() { ["type"] = "response.output_item.done", ["output_index"] = 0, ["item"] = item.DeepClone() });
        Event(new() { ["type"] = "response.completed", ["response"] = done });
        await stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Type: text/event-stream\r\nCache-Control: no-cache\r\nConnection: close\r\n\r\n"));
        await stream.WriteAsync(Encoding.UTF8.GetBytes(sb.ToString()));
        await stream.FlushAsync();
    }

    /// <summary>
    /// Troca os itens de compactação feitos aqui por uma mensagem comum com o resumo (para pedidos que vão à OpenAI).
    /// Devolve true quando trocou algo.
    /// </summary>
    public static bool Unwrap(JsonObject payload)
    {
        if (payload["input"] is not JsonArray input) return false;
        bool changed = false;
        for (int i = 0; i < input.Count; i++)
        {
            if (SummaryOf(input[i]) is not { } summary) continue;
            input[i] = new JsonObject
            {
                ["type"] = "message", ["role"] = "user",
                ["content"] = new JsonArray(new JsonObject { ["type"] = "input_text", ["text"] = Heading + summary }),
            };
            changed = true;
        }
        return changed;
    }
}
