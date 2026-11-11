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

    /// <summary>
    /// Compactação pedida pelo Codex com um modelo da barra escolhido: um resumo com o pacote de passagem (falas, ações e
    /// resultados, inclusive de outros modelos) e as últimas falas originais. Antes só ficavam as 6 últimas falas, e o
    /// trabalho feito até ali sumia da conversa.
    /// </summary>
    internal static JsonObject Compact(JsonNode payload)
    {
        var c = ConversationReader.Read(payload);
        var all = c.History.ToList();
        if (c.Prompt.Length > 0) all.Add((c.PromptIsResult ? ConversationReader.Result : "user", c.Prompt));
        var output = new JsonArray();
        string summary = ConversationReader.Transcript(all, CompactChars);
        if (summary.Length > 0)
            output.Add(new JsonObject
            {
                ["type"] = "message", ["role"] = "user",
                ["content"] = new JsonArray(new JsonObject { ["type"] = "input_text", ["text"] = "[Resumo da conversa até aqui, feito pela WinStatusBar ao compactar]\n" + summary }),
            });
        if (payload["input"] is JsonArray input)
            foreach (var item in input.Where(i => i?["type"]?.GetValue<string>() == "message" && i["role"]?.GetValue<string>() is "user" or "assistant"
                         && !ConversationReader.IsInjectedContext(TextOf(i))).TakeLast(4))
                output.Add(item!.DeepClone());
        return new JsonObject { ["output"] = output };
    }

    /// <summary>Tamanho do resumo da compactação (caracteres).</summary>
    internal const int CompactChars = 40000;

    private static string TextOf(JsonNode item)
        => item["content"] is JsonArray parts ? string.Join("\n", parts.Select(p => p?["text"]?.GetValue<string>() ?? "")).Trim()
            : item["content"] is JsonValue v && v.TryGetValue<string>(out var s) ? s.Trim() : "";

    /// <summary>A IA pode pedir ações ao Codex (só texto): Qira e o Claude com comandos agem por conta própria.</summary>
    internal static bool UsesActions((IProvider Provider, AiModel Model)? target, bool commands)
        => target is { } t && t.Provider is not Providers.QiraProvider && !(t.Provider is Providers.ClaudeProvider && commands);

    [GeneratedRegex(@"^\s*\*[^*\n]+\*\s*$")] private static partial Regex StatusLine();
    /// <summary>Linha de estado em itálico ("*Preparando…*"): não é resposta.</summary>
    public static bool IsStatusLine(string text) => StatusLine().IsMatch(text);

    /// <summary>Esforço pedido no seletor do Codex ("none" ou ausente = padrão da IA).</summary>
    public static string? EffortOf(JsonNode payload)
        => payload["reasoning"]?["effort"]?.GetValue<string>() is { Length: > 0 } e && e != "none" ? e : null;

    /// <summary>Intervalo do aviso "ainda trabalhando" (response.in_progress) nos silêncios; trocável só nos testes.</summary>
    internal static TimeSpan KeepAliveInterval { get; set; } = TimeSpan.FromSeconds(5);
    /// <summary>Com os comandos do Claude ligados, quanto o Claude segue rodando depois que o Codex fecha a conexão (para uma repetição se juntar).</summary>
    internal static TimeSpan CloseGrace { get; set; } = TimeSpan.FromSeconds(60);
    /// <summary>
    /// Andamento do Claude como itens "reasoning". Conferência C1 (10/11/2026): o codex exec aceita e mostra os itens
    /// (REASONING = PASS-default), mas no turno seguinte da mesma conversa com um GPT o Codex reenvia esses itens à OpenAI
    /// sem encrypted_content (OPENAI-HISTORY = unsafe), o que pode quebrar a conversa; fica só o aviso "ainda trabalhando".
    /// </summary>
    internal static bool ProgressAsReasoning { get; set; } = false;
    private static int _reasoningNoticeLogged;

    /// <summary>Responde um pedido do Codex para um modelo do componente, em streaming, vigiando a conexão.</summary>
    internal static async Task RespondAsync(Stream stream, Socket socket, string slug, (IProvider Provider, AiModel Model)? target, JsonNode payload, CancellationToken stop)
    {
        bool commands = target is { Provider: Providers.ClaudeProvider } && BarSettings.ClaudeCommands;
        using var run = CancellationTokenSource.CreateLinkedTokenSource(stop);
        using var finished = new CancellationTokenSource();
        var connection = new BridgeConnection(run, keepRunning: commands);
        // Vigia a conexão: se o Codex parar o pedido, a IA para também (sem esperar a próxima escrita). Evita, por
        // exemplo, gastar um crédito do Qira depois que a pessoa desistiu. Com os comandos do Claude ligados, espera 60 s
        // antes de cancelar: o Codex costuma repetir o pedido, que se junta à execução em andamento.
        var watcher = Task.Run(async () =>
        {
            try
            {
                while (!finished.IsCancellationRequested && !run.IsCancellationRequested && !connection.Lost)
                {
                    await Task.Delay(1000, finished.Token);
                    if (!finished.IsCancellationRequested && Closed(socket))
                    {
                        Log.Write(commands ? "O Codex encerrou o pedido; o Claude continua por 60 s à espera de uma repetição"
                            : "O Codex encerrou o pedido; a IA foi interrompida");
                        connection.Lose();
                    }
                }
            }
            catch (OperationCanceledException) { }
        });
        try { await RespondCoreAsync(stream, slug, target, payload, commands, run, connection); }
        finally
        {
            finished.Cancel();
            try { await watcher; } catch { }
        }
    }

    internal static async Task RespondCoreAsync(Stream stream, string slug, (IProvider Provider, AiModel Model)? target, JsonNode payload,
        bool commands, CancellationTokenSource run, BridgeConnection connection)
    {
        var conversation = ConversationReader.Read(payload);
        bool actions = conversation.Actions && UsesActions(target, commands);
        if (!actions) conversation = conversation with { Actions = false };
        string responseId = "resp_cdx_" + Guid.NewGuid().ToString("N");
        bool streaming = payload["stream"]?.GetValue<bool>() ?? false;
        var updates = Channel.CreateUnbounded<string>(new() { SingleReader = true });

        async Task Event(JsonObject data)
        {
            if (!streaming || connection.Lost || run.IsCancellationRequested) return;
            byte[] bytes = Encoding.UTF8.GetBytes($"event: {data["type"]}\ndata: {data.ToJsonString()}\n\n");
            try { await stream.WriteAsync(bytes, run.Token); await stream.FlushAsync(run.Token); }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException) { connection.Lose(); } // o Codex parou o pedido
        }
        JsonObject Response(string status, JsonArray? output = null) => new()
        {
            ["id"] = responseId, ["object"] = "response", ["created_at"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds(), ["status"] = status,
            ["model"] = slug, ["output"] = output ?? new JsonArray(),
        };
        var response = new ResponseStream(Event);

        if (streaming)
        {
            try { await stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Type: text/event-stream\r\nCache-Control: no-cache\r\nConnection: close\r\n\r\n"), run.Token); }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException) { connection.Lose(); }
            await Event(new() { ["type"] = "response.created", ["response"] = Response("in_progress") });
            await response.StartAsync();
        }

        // Um único escritor: os trechos chegam de qualquer thread; pausas recebem "in_progress" a cada KeepAliveInterval,
        // para o Codex não desistir por inatividade.
        var writer = Task.Run(async () =>
        {
            var reader = updates.Reader;
            while (true)
            {
                var wait = reader.WaitToReadAsync(run.Token).AsTask();
                if (await Task.WhenAny(wait, Task.Delay(KeepAliveInterval, run.Token)) != wait)
                { await Event(new() { ["type"] = "response.in_progress", ["response"] = Response("in_progress") }); continue; }
                if (!await wait) return;
                string latest = response.Sent;
                while (reader.TryRead(out var text)) if (!IsStatusLine(text)) latest = actions ? ActionProtocol.Visible(text) : text;
                await response.TextAsync(latest);
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
            Log.Write($"Pedido: {model.DisplayName}{(effort is null ? "" : " · esforço " + effort)}{(commands ? " · com comandos" : "")}");
            if (commands && !ProgressAsReasoning && Interlocked.Exchange(ref _reasoningNoticeLogged, 1) == 0)
                Log.Write("Andamento do Claude no Codex: só o aviso \"ainda trabalhando\" (o raciocínio fica desligado)");
            Action<string> onText = text => updates.Writer.TryWrite(text);
            Action<string> onStatus = _ => { };
            var started = DateTime.UtcNow;
            try
            {
                result = commands && ClaudeRunRegistry.KeyOf(slug, effort, conversation) is { } key
                    ? await ClaudeRunRegistry.Shared.RunAsync(key, (t, s, ct) => provider.RunAsync(model, effort, conversation, t, s, ct), onText, onStatus, run.Token)
                    : await provider.RunAsync(model, effort, conversation, onText, onStatus, run.Token);
            }
            catch (OperationCanceledException) { result = new(response.Sent, "Interrompido"); }
            catch (Exception ex) { result = new(response.Sent, ex.Message); }
            Log.Write($"Resposta: {model.DisplayName} em {(DateTime.UtcNow - started).TotalSeconds:0.0} s{(result.Error is null ? "" : " · " + result.Error)}");
        }
        updates.Writer.TryComplete();
        try { await writer; } catch (OperationCanceledException) { }
        if (run.IsCancellationRequested || connection.Lost) return;

        // Falhas viram texto da resposta: o Codex repetiria o pedido várias vezes se recebesse "response.failed".
        string answer = IsStatusLine(result.Text) ? "" : result.Text;
        string final = result.Error is { Length: > 0 } error
            ? (string.IsNullOrWhiteSpace(answer) ? "" : answer.TrimEnd() + "\n\n") + "⚠ " + name + ": " + error
            : answer;
        JsonArray items;
        if (actions && result.Error is null && ActionProtocol.Parse(answer) is { } step)
        {
            if (ActionProtocol.StepsSinceUser(conversation) + 1 > ActionProtocol.MaxSteps)
            {
                Log.Write($"Ações: limite de {ActionProtocol.MaxSteps} por mensagem atingido; a ação pedida não foi executada");
                items = await response.FinishAsync((step.Text.Length > 0 ? step.Text + "\n\n" : "")
                    + $"⚠ {name}: limite de {ActionProtocol.MaxSteps} ações seguidas atingido. Mande \"continue\" para seguir.");
            }
            else
            {
                Log.Write($"Ação pedida por {name}: {step.Action.Kind}");
                items = await response.FinishWithCallAsync(step.Text, ActionProtocol.CallItem(step.Action, conversation.Cwd));
            }
        }
        else items = await response.FinishAsync(final); // sem ação válida, o texto volta inteiro (o corte era só durante a chegada)
        int input = Math.Max(1, (conversation.Prompt.Length + conversation.History.Sum(h => h.Content.Length)) / 4), output = Math.Max(1, final.Length / 4);
        var done = Response("completed", items);
        done["usage"] = new JsonObject
        {
            ["input_tokens"] = input, ["input_tokens_details"] = new JsonObject { ["cached_tokens"] = 0 },
            ["output_tokens"] = output, ["output_tokens_details"] = new JsonObject { ["reasoning_tokens"] = 0 }, ["total_tokens"] = input + output,
        };
        if (!streaming) { await JsonAsync(stream, done); return; }
        await Event(new() { ["type"] = "response.completed", ["response"] = done });
    }
}

/// <summary>A conexão com o Codex: perdida, cancela a execução na hora, ou (comandos do Claude ligados) depois de <see cref="BridgeServer.CloseGrace"/>.</summary>
internal sealed class BridgeConnection(CancellationTokenSource run, bool keepRunning)
{
    private int _lost;
    public bool Lost => Volatile.Read(ref _lost) == 1;

    public void Lose()
    {
        if (Interlocked.Exchange(ref _lost, 1) == 1) return;
        try
        {
            if (keepRunning) run.CancelAfter(BridgeServer.CloseGrace);
            else run.Cancel();
        }
        catch (ObjectDisposedException) { }
    }
}
