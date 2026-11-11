using System.Text.Json.Nodes;

namespace CodexIAs;

/// <summary>
/// Escritor da resposta no formato de streaming da Responses API: um item de mensagem no índice 0, texto só crescendo
/// (o Codex recebe só o que falta) e o item fechado no fim. Os eventos são os mesmos de antes da extração.
/// </summary>
internal sealed class ResponseStream(Func<JsonObject, Task> emit)
{
    private readonly string _messageId = "msg_cdx_" + Guid.NewGuid().ToString("N");

    /// <summary>Texto já mandado ao Codex.</summary>
    public string Sent { get; private set; } = "";

    public Task StartAsync() => emit(new JsonObject
    {
        ["type"] = "response.output_item.added", ["output_index"] = 0,
        ["item"] = new JsonObject { ["type"] = "message", ["id"] = _messageId, ["role"] = "assistant", ["status"] = "in_progress", ["content"] = new JsonArray() },
    });

    /// <summary>Novo retrato do texto: manda só a parte nova quando ele estende o que já foi enviado.</summary>
    public async Task TextAsync(string snapshot)
    {
        if (snapshot.Length > Sent.Length && snapshot.StartsWith(Sent, StringComparison.Ordinal))
            await emit(Delta(snapshot[Sent.Length..]));
        Sent = snapshot;
    }

    /// <summary>Fecha a mensagem com o texto final e devolve os itens da resposta.</summary>
    public async Task<JsonArray> FinishAsync(string final)
    {
        if (final.Length > Sent.Length && final.StartsWith(Sent, StringComparison.Ordinal))
            await emit(Delta(final[Sent.Length..]));
        Sent = final;
        var item = new JsonObject
        {
            ["type"] = "message", ["id"] = _messageId, ["role"] = "assistant", ["status"] = "completed",
            ["content"] = new JsonArray(new JsonObject { ["type"] = "output_text", ["text"] = final, ["annotations"] = new JsonArray() }),
        };
        await emit(new JsonObject { ["type"] = "response.output_item.done", ["output_index"] = 0, ["item"] = item.DeepClone() });
        return new JsonArray(item);
    }

    /// <summary>Fecha a mensagem e acrescenta uma chamada de ferramenta (índice 1) para o Codex executar.</summary>
    public async Task<JsonArray> FinishWithCallAsync(string final, JsonObject call)
    {
        var items = await FinishAsync(final);
        await emit(new JsonObject { ["type"] = "response.output_item.added", ["output_index"] = 1, ["item"] = call.DeepClone() });
        await emit(new JsonObject { ["type"] = "response.output_item.done", ["output_index"] = 1, ["item"] = call.DeepClone() });
        items.Add(call.DeepClone());
        return items;
    }

    private JsonObject Delta(string delta) => new()
    {
        ["type"] = "response.output_text.delta", ["item_id"] = _messageId, ["output_index"] = 0, ["content_index"] = 0, ["delta"] = delta,
    };
}
