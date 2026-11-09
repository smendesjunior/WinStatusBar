using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading.Channels;

namespace CodexIAs;

/// <summary>
/// Resposta das IAs do componente (modelos codexias-*) no formato de streaming da Responses API, que o Codex espera.
/// Chamado pelo encaminhador (Gateway) na própria conexão do Codex.
/// </summary>
public static partial class BridgeServer
{

    /// <summary>Conexão encerrada pelo outro lado (o Codex parou o pedido).</summary>
    internal static bool Closed(Socket socket)
    {
        try { return socket.Poll(0, SelectMode.SelectRead) && socket.Available == 0; }
        catch { return true; }
    }

    internal static Task PlainAsync(Stream stream, int code, string message)
        => JsonAsync(stream, new JsonObject { ["error"] = new JsonObject { ["message"] = message, ["type"] = "local_gateway_error" } }, code);

    internal static async Task JsonAsync(Stream stream, JsonNode body, int code = 200)
    {
        byte[] data = Encoding.UTF8.GetBytes(body.ToJsonString());
        await stream.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 {code} {(code == 200 ? "OK" : "Error")}\r\nContent-Type: application/json; charset=utf-8\r\nContent-Length: {data.Length}\r\nConnection: close\r\n\r\n"));
        await stream.WriteAsync(data);
    }

    /// <summary>Compactação pedida pelo Codex: guarda só as últimas falas (o histórico vai inteiro em cada pedido).</summary>
    internal static JsonObject Compact(JsonNode payload)
    {
        var output = new JsonArray();
        if (payload["input"] is JsonArray input)
            foreach (var item in input.Where(i => i?["type"]?.GetValue<string>() == "message" && i["role"]?.GetValue<string>() is "user" or "assistant").TakeLast(6))
                output.Add(item!.DeepClone());
        return new JsonObject { ["output"] = output };
    }

    [GeneratedRegex(@"^\s*\*[^*\n]+\*\s*$")] private static partial Regex StatusLine();
    /// <summary>Linha de estado em itálico ("*Preparando…*"): não é resposta.</summary>
    public static bool IsStatusLine(string text) => StatusLine().IsMatch(text);

    /// <summary>Esforço pedido no seletor do Codex ("none" ou ausente = padrão da IA).</summary>
    public static string? EffortOf(JsonNode payload)
        => payload["reasoning"]?["effort"]?.GetValue<string>() is { Length: > 0 } e && e != "none" ? e : null;

    /// <summary>Responde um pedido do Codex para um modelo do componente, em streaming, vigiando a conexão.</summary>
    internal static async Task RespondAsync(Stream stream, Socket socket, string slug, (IProvider Provider, AiModel Model)? target, JsonNode payload, CancellationToken stop)
    {
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(stop);
        using var finished = new CancellationTokenSource();
        // Vigia a conexão: se o Codex parar o pedido, a IA para também (sem esperar a próxima escrita). Evita, por
        // exemplo, gastar um crédito do Qira depois que a pessoa desistiu.
        var watcher = Task.Run(async () =>
        {
            try
            {
                while (!finished.IsCancellationRequested && !cancel.IsCancellationRequested)
                {
                    await Task.Delay(1000, finished.Token);
                    if (!finished.IsCancellationRequested && Closed(socket)) { Log.Write("O Codex encerrou o pedido; a IA foi interrompida"); cancel.Cancel(); }
                }
            }
            catch (OperationCanceledException) { }
        });
        try { await RespondCoreAsync(stream, slug, target, payload, cancel); }
        finally
        {
            finished.Cancel();
            try { await watcher; } catch { }
        }
    }

    private static async Task RespondCoreAsync(Stream stream, string slug, (IProvider Provider, AiModel Model)? target, JsonNode payload, CancellationTokenSource cancel)
    {
        var conversation = ConversationReader.Read(payload);
        string responseId = "resp_cdx_" + Guid.NewGuid().ToString("N"), messageId = "msg_cdx_" + Guid.NewGuid().ToString("N");
        bool streaming = payload["stream"]?.GetValue<bool>() ?? false;
        var updates = Channel.CreateUnbounded<string>(new() { SingleReader = true });
        string sent = "";

        async Task Event(JsonObject data)
        {
            if (!streaming || cancel.IsCancellationRequested) return;
            byte[] bytes = Encoding.UTF8.GetBytes($"event: {data["type"]}\ndata: {data.ToJsonString()}\n\n");
            try { await stream.WriteAsync(bytes, cancel.Token); await stream.FlushAsync(cancel.Token); }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException) { cancel.Cancel(); } // o Codex parou o pedido
        }
        JsonObject Response(string status, JsonArray? output = null) => new()
        {
            ["id"] = responseId, ["object"] = "response", ["created_at"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds(), ["status"] = status,
            ["model"] = slug, ["output"] = output ?? new JsonArray(),
        };

        if (streaming)
        {
            await stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Type: text/event-stream\r\nCache-Control: no-cache\r\nConnection: close\r\n\r\n"), cancel.Token);
            await Event(new() { ["type"] = "response.created", ["response"] = Response("in_progress") });
            await Event(new() { ["type"] = "response.output_item.added", ["output_index"] = 0,
                ["item"] = new JsonObject { ["type"] = "message", ["id"] = messageId, ["role"] = "assistant", ["status"] = "in_progress", ["content"] = new JsonArray() } });
        }

        // Um único escritor: os trechos chegam de qualquer thread; pausas recebem "in_progress" a cada 5 s, para o Codex
        // não desistir por inatividade.
        var writer = Task.Run(async () =>
        {
            var reader = updates.Reader;
            while (true)
            {
                var wait = reader.WaitToReadAsync(cancel.Token).AsTask();
                if (await Task.WhenAny(wait, Task.Delay(5000, cancel.Token)) != wait)
                { await Event(new() { ["type"] = "response.in_progress", ["response"] = Response("in_progress") }); continue; }
                if (!await wait) return;
                string latest = sent;
                while (reader.TryRead(out var text)) if (!IsStatusLine(text)) latest = text;
                if (latest.Length > sent.Length && latest.StartsWith(sent, StringComparison.Ordinal))
                    await Event(new() { ["type"] = "response.output_text.delta", ["item_id"] = messageId, ["output_index"] = 0, ["content_index"] = 0, ["delta"] = latest[sent.Length..] });
                sent = latest;
            }
        });

        string name = target?.Model.DisplayName ?? slug;
        Answer result;
        if (target is null) result = new("", "Modelo indisponível neste PC: " + slug + ". Na WinStatusBar, abra Configurações › Modelos e clique em Verificar e atualizar.");
        else if (conversation.Prompt.Length == 0) result = new("", "Nenhuma mensagem para enviar.");
        else
        {
            var (provider, model) = target.Value;
            string? effort = EffortOf(payload);
            if (effort is not null && model.Efforts.Count > 0 && !model.Efforts.Contains(effort)) effort = model.DefaultEffort;
            if (model.Efforts.Count == 0) effort = null;
            Log.Write($"Pedido: {model.DisplayName}{(effort is null ? "" : " · esforço " + effort)}");
            var started = DateTime.UtcNow;
            try { result = await provider.RunAsync(model, effort, conversation, text => updates.Writer.TryWrite(text), cancel.Token); }
            catch (OperationCanceledException) { result = new(sent, "Interrompido"); }
            catch (Exception ex) { result = new(sent, ex.Message); }
            Log.Write($"Resposta: {model.DisplayName} em {(DateTime.UtcNow - started).TotalSeconds:0.0} s{(result.Error is null ? "" : " · " + result.Error)}");
        }
        updates.Writer.TryComplete();
        try { await writer; } catch (OperationCanceledException) { }
        if (cancel.IsCancellationRequested) return;

        // Falhas viram texto da resposta: o Codex repetiria o pedido várias vezes se recebesse "response.failed".
        string answer = IsStatusLine(result.Text) ? "" : result.Text;
        string final = result.Error is { Length: > 0 } error
            ? (string.IsNullOrWhiteSpace(answer) ? "" : answer.TrimEnd() + "\n\n") + "⚠ " + name + ": " + error
            : answer;
        if (final.Length > sent.Length && final.StartsWith(sent, StringComparison.Ordinal))
            await Event(new() { ["type"] = "response.output_text.delta", ["item_id"] = messageId, ["output_index"] = 0, ["content_index"] = 0, ["delta"] = final[sent.Length..] });
        var item = new JsonObject
        {
            ["type"] = "message", ["id"] = messageId, ["role"] = "assistant", ["status"] = "completed",
            ["content"] = new JsonArray(new JsonObject { ["type"] = "output_text", ["text"] = final, ["annotations"] = new JsonArray() }),
        };
        int input = Math.Max(1, (conversation.Prompt.Length + conversation.History.Sum(h => h.Content.Length)) / 4), output = Math.Max(1, final.Length / 4);
        var done = Response("completed", new JsonArray(item.DeepClone()));
        done["usage"] = new JsonObject
        {
            ["input_tokens"] = input, ["input_tokens_details"] = new JsonObject { ["cached_tokens"] = 0 },
            ["output_tokens"] = output, ["output_tokens_details"] = new JsonObject { ["reasoning_tokens"] = 0 }, ["total_tokens"] = input + output,
        };
        if (!streaming) { await JsonAsync(stream, done); return; }
        await Event(new() { ["type"] = "response.output_item.done", ["output_index"] = 0, ["item"] = item });
        await Event(new() { ["type"] = "response.completed", ["response"] = done });
    }
}
