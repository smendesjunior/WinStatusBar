using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading.Channels;

namespace WinStatusBar.Core.Ai;

/// <summary>
/// IAs da barra no seletor de modelos do Codex (Claude, Gemini, Copilot e Qira). O encaminhador local do Codex
/// (porta 11435) manda para cá os pedidos desses modelos; a barra responde no formato de streaming da Responses API.
/// Só texto: daqui as IAs não executam comandos nem editam arquivos pelo Codex (o Claude pode ler a pasta da conversa).
/// Segurança: só 127.0.0.1, sem cabeçalho Origin (navegadores) e com um segredo novo a cada início, gravado em
/// %LOCALAPPDATA%\WinStatusBar\codex-bridge.json, que só o próprio usuário lê.
/// </summary>
public sealed partial class CodexModelBridge : IDisposable
{
    public sealed record Model(string Slug, AgentPrompt.Target Target, string Name);
    public static readonly IReadOnlyList<Model> Models =
    [
        new("winstatusbar-claude", AgentPrompt.Target.Claude, "Claude"),
        new("winstatusbar-gemini", AgentPrompt.Target.Gemini, "Gemini"),
        new("winstatusbar-copilot", AgentPrompt.Target.Copilot, "Microsoft Copilot"),
        new("winstatusbar-qira", AgentPrompt.Target.Qira, "Lenovo Qira (híbrido)"),
    ];
    public const int PreferredPort = 11437;
    public delegate Task<AgentPrompt.Result> Runner(Model model, string prompt, AgentPrompt.Options options, Action<string> onText, CancellationToken ct);

    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _stop = new();
    private readonly byte[] _token;
    private readonly Runner _run;
    private readonly string _infoPath;
    public int Port { get; }

    private CodexModelBridge(TcpListener listener, Runner run, string infoPath)
    {
        _listener = listener; _run = run; _infoPath = infoPath;
        Port = ((IPEndPoint)listener.LocalEndpoint).Port;
        string token = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
        _token = Encoding.ASCII.GetBytes("Bearer " + token);
        Directory.CreateDirectory(Path.GetDirectoryName(infoPath)!);
        File.WriteAllText(infoPath, new JsonObject { ["port"] = Port, ["token"] = token, ["pid"] = Environment.ProcessId }.ToJsonString());
        _ = AcceptLoopAsync();
    }

    /// <summary>Abre na porta preferida (ou numa livre) e grava porta e segredo para o encaminhador.</summary>
    public static CodexModelBridge? Start(Runner run, string infoPath)
    {
        foreach (int port in new[] { PreferredPort, 0 })
        {
            var listener = new TcpListener(IPAddress.Loopback, port);
            try { listener.Start(); return new CodexModelBridge(listener, run, infoPath); }
            catch (SocketException) { listener.Stop(); }
        }
        return null;
    }

    public void Dispose()
    {
        _stop.Cancel();
        _listener.Stop();
        try { if (File.Exists(_infoPath) && File.ReadAllText(_infoPath).Contains($"\"pid\":{Environment.ProcessId}")) File.Delete(_infoPath); } catch { }
    }

    private async Task AcceptLoopAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await _listener.AcceptTcpClientAsync(_stop.Token); }
            catch { if (_stop.IsCancellationRequested) return; continue; }
            _ = Task.Run(async () => { using (client) try { await HandleAsync(client.GetStream()); } catch { } });
        }
    }

    // ------------------------------------------------------------------ HTTP mínimo (uma requisição por conexão)
    private async Task HandleAsync(NetworkStream stream)
    {
        // Prazo para receber o pedido inteiro: uma conexão parada não fica presa para sempre.
        using var receiving = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
        receiving.CancelAfter(TimeSpan.FromSeconds(30));
        var head = new MemoryStream();
        var one = new byte[1];
        while (head.Length < 65536 && await stream.ReadAsync(one, receiving.Token) == 1)
        {
            head.WriteByte(one[0]);
            if (head.Length >= 4 && head.GetBuffer()[(int)head.Length - 4] == '\r' && one[0] == '\n' && head.GetBuffer()[(int)head.Length - 2] == '\r') break;
        }
        string[] lines = Encoding.ASCII.GetString(head.GetBuffer(), 0, (int)head.Length).Split("\r\n");
        string[] request = lines[0].Split(' ');
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (string line in lines.Skip(1))
            if (line.IndexOf(':') is > 0 and var colon) headers[line[..colon].Trim()] = line[(colon + 1)..].Trim();
        if (request.Length < 2 || request[0] != "POST") { await PlainAsync(stream, 405, "Use POST"); return; }
        if (!int.TryParse(headers.GetValueOrDefault("Content-Length"), out int length) || length is < 0 or > 64 * 1024 * 1024)
        { await PlainAsync(stream, 411, "Content-Length obrigatório"); return; }
        // O corpo é lido antes de qualquer recusa: fechar com dados não lidos derruba a conexão sem entregar a resposta.
        var body = new byte[length];
        await stream.ReadExactlyAsync(body, receiving.Token);
        if (headers.ContainsKey("Origin")) { await PlainAsync(stream, 403, "Acesso só por aplicativos locais"); return; }
        byte[] auth = Encoding.ASCII.GetBytes(headers.GetValueOrDefault("Authorization") ?? "");
        if (!CryptographicOperations.FixedTimeEquals(auth, _token)) { await PlainAsync(stream, 401, "Segredo da barra inválido"); return; }
        JsonNode? payload;
        try { payload = JsonNode.Parse(body); } catch (JsonException) { await PlainAsync(stream, 400, "JSON inválido"); return; }
        var model = Models.FirstOrDefault(m => m.Slug == payload?["model"]?.GetValue<string>());
        if (model is null) { await PlainAsync(stream, 404, "Modelo desconhecido"); return; }
        string path = request[1].Split('?')[0];
        if (path.EndsWith("/responses/compact", StringComparison.Ordinal)) { await JsonAsync(stream, Compact(payload!)); return; }
        if (!path.EndsWith("/responses", StringComparison.Ordinal)) { await PlainAsync(stream, 404, "Endereço indisponível"); return; }
        await RespondAsync(stream, model, payload!);
    }

    private static Task PlainAsync(NetworkStream stream, int code, string message)
        => JsonAsync(stream, new JsonObject { ["error"] = new JsonObject { ["message"] = message, ["type"] = "winstatusbar_bridge" } }, code);

    private static async Task JsonAsync(NetworkStream stream, JsonNode body, int code = 200)
    {
        byte[] data = Encoding.UTF8.GetBytes(body.ToJsonString());
        await stream.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 {code} {(code == 200 ? "OK" : "Error")}\r\nContent-Type: application/json; charset=utf-8\r\nContent-Length: {data.Length}\r\nConnection: close\r\n\r\n"));
        await stream.WriteAsync(data);
    }

    // ------------------------------------------------------------------ conversa
    public sealed record Conversation(string Prompt, IReadOnlyList<(string Role, string Content)> History, string? Cwd);

    [GeneratedRegex(@"<cwd>(.*?)</cwd>", RegexOptions.Singleline)] private static partial Regex CwdTag();
    [GeneratedRegex(@"^\s*<([A-Za-z_][\w-]*)[^>]*>.*</\1>\s*$", RegexOptions.Singleline)] private static partial Regex WrappedTag();

    [GeneratedRegex(@"^\s*\*[^*\n]+\*\s*$")] private static partial Regex StatusLine();
    /// <summary>Linha de estado das IAs da barra, em itálico ("*Contando tokens...*", "*Gerando imagem…*"): não é resposta.</summary>
    public static bool IsStatusLine(string text) => StatusLine().IsMatch(text);

    /// <summary>Mensagens que o Codex injeta como "user" (ambiente, AGENTS.md, permissões, avisos), que não são pedidos da pessoa.</summary>
    public static bool IsInjectedContext(string text)
        => text.StartsWith("# AGENTS.md", StringComparison.Ordinal) || text.StartsWith("<environment_context>", StringComparison.Ordinal)
           || text.StartsWith("<turn_aborted>", StringComparison.Ordinal) || text.StartsWith("<permissions", StringComparison.Ordinal) || WrappedTag().IsMatch(text);

    /// <summary>Pedido atual, histórico (só falas da pessoa e respostas) e pasta da conversa a partir do "input" do Codex.</summary>
    public static Conversation ReadConversation(JsonNode payload)
    {
        var turns = new List<(string Role, string Content)>();
        string? cwd = null;
        if (payload["input"] is JsonArray input)
            foreach (var item in input)
            {
                if (item?["type"]?.GetValue<string>() != "message" || item["role"]?.GetValue<string>() is not { } role || role is not ("user" or "assistant")) continue;
                var parts = new List<string>();
                if (item["content"] is JsonValue plain && plain.TryGetValue<string>(out var s)) parts.Add(s);
                else if (item["content"] is JsonArray content)
                    foreach (var part in content)
                        switch (part?["type"]?.GetValue<string>())
                        {
                            case "input_text" or "output_text" or "text": parts.Add(part["text"]?.GetValue<string>() ?? ""); break;
                            case "input_image": parts.Add(LocalizedText.T("[imagem anexada no Codex]")); break;
                        }
                string text = string.Join("\n", parts).Trim();
                if (text.Length == 0) continue;
                if (role == "user" && text.StartsWith("<environment_context>", StringComparison.Ordinal) && CwdTag().Match(text) is { Success: true } m)
                    cwd = m.Groups[1].Value.Trim();
                if (role == "user" && IsInjectedContext(text)) continue;
                turns.Add((role, text));
            }
        int last = turns.FindLastIndex(t => t.Role == "user");
        if (last < 0) return new("", turns, cwd);
        return new(turns[last].Content, turns.Take(last).ToList(), cwd);
    }

    /// <summary>Compactação pedida pelo Codex: guarda só as últimas falas (o histórico vai inteiro em cada pedido).</summary>
    private static JsonObject Compact(JsonNode payload)
    {
        var output = new JsonArray();
        if (payload["input"] is JsonArray input)
            foreach (var item in input.Where(i => i?["type"]?.GetValue<string>() == "message" && i["role"]?.GetValue<string>() is "user" or "assistant").TakeLast(6))
                output.Add(item!.DeepClone());
        return new JsonObject { ["output"] = output };
    }

    // ------------------------------------------------------------------ streaming no formato da Responses API
    private async Task RespondAsync(NetworkStream stream, Model model, JsonNode payload)
    {
        var conversation = ReadConversation(payload);
        string responseId = "resp_wsb_" + Guid.NewGuid().ToString("N"), messageId = "msg_wsb_" + Guid.NewGuid().ToString("N");
        bool streaming = payload["stream"]?.GetValue<bool>() ?? false;
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
        var updates = Channel.CreateUnbounded<string>(new() { SingleReader = true });
        string sent = "";

        async Task Event(JsonObject data)
        {
            if (!streaming) return;
            byte[] bytes = Encoding.UTF8.GetBytes($"event: {data["type"]}\ndata: {data.ToJsonString()}\n\n");
            try { await stream.WriteAsync(bytes, cancel.Token); await stream.FlushAsync(cancel.Token); }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException) { cancel.Cancel(); } // o Codex parou o pedido
        }
        JsonObject Response(string status, JsonArray? output = null) => new()
        {
            ["id"] = responseId, ["object"] = "response", ["created_at"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds(), ["status"] = status,
            ["model"] = model.Slug, ["output"] = output ?? new JsonArray(),
        };

        if (streaming)
        {
            await stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Type: text/event-stream\r\nCache-Control: no-cache\r\nConnection: close\r\n\r\n"), cancel.Token);
            await Event(new() { ["type"] = "response.created", ["response"] = Response("in_progress") });
            await Event(new() { ["type"] = "response.output_item.added", ["output_index"] = 0,
                ["item"] = new JsonObject { ["type"] = "message", ["id"] = messageId, ["role"] = "assistant", ["status"] = "in_progress", ["content"] = new JsonArray() } });
        }

        // Um único escritor: os trechos chegam de qualquer thread; pausas longas recebem "in_progress" para o Codex não
        // desistir por inatividade enquanto o Qira ou o Copilot pensam.
        var writer = Task.Run(async () =>
        {
            var reader = updates.Reader;
            while (true)
            {
                var wait = reader.WaitToReadAsync(cancel.Token).AsTask();
                if (await Task.WhenAny(wait, Task.Delay(15000, cancel.Token)) != wait)
                { await Event(new() { ["type"] = "response.in_progress", ["response"] = Response("in_progress") }); continue; }
                if (!await wait) return;
                string latest = sent;
                while (reader.TryRead(out var text)) if (!IsStatusLine(text)) latest = text;
                if (latest.Length > sent.Length && latest.StartsWith(sent, StringComparison.Ordinal))
                    await Event(new() { ["type"] = "response.output_text.delta", ["item_id"] = messageId, ["output_index"] = 0, ["content_index"] = 0, ["delta"] = latest[sent.Length..] });
                sent = latest;
            }
        });

        AgentPrompt.Result result;
        if (conversation.Prompt.Length == 0) result = new("", null, LocalizedText.T("Nenhuma mensagem para enviar."));
        else
        {
            // Ephemeral só no Claude (sem sessão salva no CLI); Copilot e Gemini pela conta recusam chamadas "auxiliares".
            var options = new AgentPrompt.Options([], false, false, null, conversation.History, Ephemeral: model.Target == AgentPrompt.Target.Claude, WorkDir: conversation.Cwd);
            try { result = await _run(model, conversation.Prompt, options, text => updates.Writer.TryWrite(text), cancel.Token); }
            catch (OperationCanceledException) { result = new(sent, null, LocalizedText.T("Interrompido")); }
            catch (Exception ex) { result = new(sent, null, ex.Message); }
        }
        updates.Writer.TryComplete();
        try { await writer; } catch (OperationCanceledException) { }
        if (cancel.IsCancellationRequested) return;

        // Falhas viram texto da resposta: o Codex repetiria o pedido várias vezes se recebesse "response.failed".
        string answer = IsStatusLine(result.Text) ? "" : result.Text;
        string final = result.Error is { Length: > 0 } error
            ? (string.IsNullOrWhiteSpace(answer) ? "" : answer.TrimEnd() + "\n\n") + "⚠ " + model.Name + ": " + error
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
