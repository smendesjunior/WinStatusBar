using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using WinStatusBar.Core.Ai.Codex;

namespace CodexIAs;

/// <summary>
/// Encaminhador do Codex (127.0.0.1:11435; só o próprio PC). O Codex manda todos os pedidos para cá
/// (openai_base_url) e cada um segue pelo modelo pedido:
/// - codexias-*: as IAs configuradas na barra, respondidas aqui mesmo (BridgeServer);
/// - openai-minha-api-*: a API pública da OpenAI, pela chave da pessoa (OpenAiKey);
/// - winstatusbar-*: a ponte antiga da barra (conversas antigas do Codex);
/// - o resto (modelos da conta): sem mudança, para onde o Codex já ia (OpenAI ou Ollama), com o mesmo pedido e a mesma
///   autorização.
/// Não registra pedidos, chaves, cabeçalhos nem respostas.
/// </summary>
internal sealed class Gateway : IDisposable
{
    private const int MaxBody = 64 * 1024 * 1024;
    private static readonly HashSet<string> Hop = new(StringComparer.OrdinalIgnoreCase)
        { "connection", "proxy-connection", "keep-alive", "transfer-encoding", "te", "trailer", "upgrade", "host", "content-length" };
    private static readonly HttpClient Http = new(new SocketsHttpHandler
    {
        AutomaticDecompression = DecompressionMethods.None, UseCookies = false, AllowAutoRedirect = false,
        ConnectTimeout = TimeSpan.FromSeconds(20), PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2),
    }) { Timeout = Timeout.InfiniteTimeSpan };

    /// <summary>Comando interno (/codexias/&lt;nome&gt;): devolve o código e o corpo da resposta.</summary>
    public delegate Task<(int Code, JsonNode Body)> Command(string name, JsonNode? body, CancellationToken ct);

    private readonly TcpListener _listener;
    private readonly ModelRegistry _registry;
    private readonly Command _command;
    private readonly CancellationTokenSource _stop = new();

    private Gateway(TcpListener listener, ModelRegistry registry, Command command) { _listener = listener; _registry = registry; _command = command; }

    /// <summary>Abre a porta 11435. Null quando outro programa já a usa.</summary>
    public static Gateway? Start(ModelRegistry registry, Command command)
    {
        var listener = new TcpListener(IPAddress.Loopback, Paths.GatewayPort);
        listener.Server.ExclusiveAddressUse = true;
        try { listener.Start(64); }
        catch (SocketException ex) { Log.Write($"Encaminhador: porta {Paths.GatewayPort} indisponível ({ex.SocketErrorCode})"); return null; }
        var gateway = new Gateway(listener, registry, command);
        _ = Task.Run(gateway.AcceptLoopAsync);
        return gateway;
    }

    private async Task AcceptLoopAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await _listener.AcceptTcpClientAsync(_stop.Token); }
            catch (OperationCanceledException) { return; }
            catch (ObjectDisposedException) { return; }
            catch (SocketException) { continue; }
            _ = Task.Run(() => HandleAsync(client));
        }
    }

    private sealed record Request(string Method, string Target, List<KeyValuePair<string, string>> Headers, byte[] Body)
    {
        public string? Header(string name) => Headers.FirstOrDefault(h => h.Key.Equals(name, StringComparison.OrdinalIgnoreCase)).Value;
        public string Path => Target.Split('?', 2)[0];
        public string Query => Target.IndexOf('?') is int q and >= 0 ? Target[q..] : "";
    }

    private async Task HandleAsync(TcpClient client)
    {
        using var _ = client;
        client.NoDelay = true;
        var stream = client.GetStream();
        try
        {
            Request? request;
            using (var reading = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token))
            {
                reading.CancelAfter(TimeSpan.FromSeconds(60));
                request = await ReadAsync(stream, reading.Token);
            }
            if (request is null) { await BridgeServer.PlainAsync(stream, 400, "Pedido inválido"); return; }
            await RouteAsync(request, stream, client.Client);
        }
        catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException or OperationCanceledException) { }
        catch (Exception ex) { Log.Write("Encaminhador: " + ex.GetType().Name + ": " + ex.Message); }
    }

    /// <summary>Lê um pedido HTTP/1.1 (cabeçalhos até 64 KB, corpo pelo Content-Length até 64 MB).</summary>
    private static async Task<Request?> ReadAsync(NetworkStream stream, CancellationToken ct)
    {
        var buffer = new byte[16 * 1024];
        int length = 0, headerEnd = -1;
        while (headerEnd < 0)
        {
            if (length == buffer.Length)
            {
                if (buffer.Length >= 64 * 1024) return null;
                Array.Resize(ref buffer, buffer.Length * 2);
            }
            int n = await stream.ReadAsync(buffer.AsMemory(length), ct);
            if (n == 0) return null;
            int from = Math.Max(0, length - 3);
            length += n;
            for (int i = from; i + 3 < length; i++)
                if (buffer[i] == '\r' && buffer[i + 1] == '\n' && buffer[i + 2] == '\r' && buffer[i + 3] == '\n') { headerEnd = i; break; }
        }
        var lines = Encoding.Latin1.GetString(buffer, 0, headerEnd).Split("\r\n");
        var first = lines[0].Split(' ');
        if (first.Length < 3 || !first[2].StartsWith("HTTP/1.", StringComparison.Ordinal)) return null;
        var headers = new List<KeyValuePair<string, string>>();
        foreach (var line in lines.Skip(1))
        {
            int colon = line.IndexOf(':');
            if (colon <= 0) return null;
            headers.Add(new(line[..colon].Trim(), line[(colon + 1)..].Trim()));
        }
        var request = new Request(first[0].ToUpperInvariant(), first[1], headers, []);
        if (request.Header("Transfer-Encoding") is not null) return request with { Method = "CHUNKED" };
        long size = long.TryParse(request.Header("Content-Length"), out long cl) ? cl : 0;
        if (size < 0 || size > MaxBody) return request with { Method = "TOOBIG" };
        var body = new byte[size];
        int already = Math.Min((int)size, length - headerEnd - 4);
        Array.Copy(buffer, headerEnd + 4, body, 0, already);
        for (int read = already; read < size;)
        {
            int n = await stream.ReadAsync(body.AsMemory(read), ct);
            if (n == 0) return null;
            read += n;
        }
        return request with { Body = body };
    }

    private static bool LocalHost(string? host)
    {
        if (string.IsNullOrEmpty(host)) return false;
        string name = host.StartsWith('[') ? host[..(host.IndexOf(']') + 1)] : host.Split(':')[0];
        return name is "127.0.0.1" or "localhost" or "[::1]";
    }

    /// <summary>Parte do caminho depois do endereço base do Codex ("/v1/responses" → "/responses"); null se não é dele.</summary>
    internal static string? Rest(string path, string basePath)
    {
        foreach (var prefix in new[] { basePath.TrimEnd('/'), "/v1", "/api/codex/v1" }.Where(p => p.Length > 0).Distinct())
            if (path.StartsWith(prefix + "/", StringComparison.Ordinal)) return path[prefix.Length..];
        return null;
    }

    private static byte[]? Unzstd(byte[] body)
    {
        try
        {
            using var input = new MemoryStream(body);
            using var zstd = new ZstdSharp.DecompressionStream(input);
            using var output = new MemoryStream();
            var chunk = new byte[81920];
            int n;
            while ((n = zstd.Read(chunk, 0, chunk.Length)) > 0)
            {
                output.Write(chunk, 0, n);
                if (output.Length > MaxBody) return null;
            }
            return output.ToArray();
        }
        catch { return null; }
    }

    private async Task RouteAsync(Request request, NetworkStream stream, Socket socket)
    {
        if (request.Method == "GET" && request.Path is "/health" or "/codexias/saude")
        {
            await BridgeServer.JsonAsync(stream, new JsonObject { ["status"] = "ok", ["app"] = "WinStatusBar.CodexIAs", ["versao"] = Program.Version, ["pid"] = Environment.ProcessId });
            return;
        }
        if (string.Equals(request.Header("Upgrade"), "websocket", StringComparison.OrdinalIgnoreCase))
        { await BridgeServer.PlainAsync(stream, 426, "Use HTTP Responses streaming"); return; }
        // Só programas deste PC: páginas da internet (Origin) e nomes de outro computador (DNS rebinding) ficam de fora.
        if (request.Header("Origin") is not null || !LocalHost(request.Header("Host")))
        { await BridgeServer.PlainAsync(stream, 403, "Local application access only"); return; }
        if (request.Method == "CHUNKED") { await BridgeServer.PlainAsync(stream, 400, "Content-Length required"); return; }
        if (request.Method == "TOOBIG") { await BridgeServer.PlainAsync(stream, 413, "Request too large"); return; }

        if (request.Path.StartsWith("/codexias/", StringComparison.Ordinal))
        {
            if (request.Header(CodexIntegration.CommandHeader) != CodexIntegration.CommandValue) { await BridgeServer.PlainAsync(stream, 403, "Comando interno"); return; }
            JsonNode? body = null;
            try { if (request.Body.Length > 0) body = JsonNode.Parse(request.Body); } catch { }
            var (code, answer) = await _command(request.Path["/codexias/".Length..], body, _stop.Token);
            await BridgeServer.JsonAsync(stream, answer, code);
            return;
        }

        byte[] decoded = request.Body;
        string encoding = (request.Header("Content-Encoding") ?? "identity").Trim().ToLowerInvariant();
        if (encoding == "zstd")
        {
            if (Unzstd(request.Body) is not { } plain) { await BridgeServer.PlainAsync(stream, 400, "Invalid compressed request"); return; }
            decoded = plain;
        }
        else if (encoding is not ("identity" or "")) { await BridgeServer.PlainAsync(stream, 415, "Unsupported Content-Encoding"); return; }
        JsonObject? payload = null;
        if (decoded.Length > 0)
        {
            try { payload = JsonNode.Parse(decoded) as JsonObject; }
            catch { if (request.Method == "POST") { await BridgeServer.PlainAsync(stream, 400, "Invalid JSON request"); return; } }
        }
        string model = payload?["model"] is JsonValue v && v.TryGetValue<string>(out var m) ? m : "";
        var state = CodexIntegration.LoadState();
        string? rest = Rest(request.Path, state.BasePath);

        if (model.StartsWith(CodexCatalog.Prefix, StringComparison.Ordinal))
        {
            if (rest is not ("/responses" or "/responses/compact") || payload is null) { await BridgeServer.PlainAsync(stream, 400, "Endpoint unavailable for these models"); return; }
            if (rest == "/responses/compact")
            {
                JsonObject compact;
                try { compact = BridgeServer.Compact(payload); }
                catch (Exception ex) when (BridgeServer.BadShape(ex)) { await BridgeServer.PlainAsync(stream, 400, BridgeServer.BadShapeMessage); return; }
                await BridgeServer.JsonAsync(stream, compact);
                return;
            }
            if (CompactionV2.Requested(payload))
            {
                try { await CompactionV2.RespondAsync(stream, model, payload); }
                catch (Exception ex) when (BridgeServer.BadShape(ex)) { await BridgeServer.PlainAsync(stream, 400, BridgeServer.BadShapeMessage); }
                return;
            }
            await BridgeServer.RespondAsync(stream, socket, model, _registry.Resolve(model), payload, _stop.Token);
            return;
        }
        if (model.StartsWith("winstatusbar-", StringComparison.Ordinal))
        {
            if (rest is not ("/responses" or "/responses/compact") || payload is null) { await BridgeServer.PlainAsync(stream, 400, "Endpoint unavailable for these models"); return; }
            if (BarBridge() is not { } bar) { await BridgeServer.PlainAsync(stream, 503, "A barra WinStatusBar não está aberta. Abra a barra e tente de novo."); return; }
            // Só o pedido decodificado: a autorização da conta do ChatGPT nunca vai para a barra.
            await ForwardAsync("POST", $"http://127.0.0.1:{bar.Port}/v1{rest}", Json(payload, bar.Token), Encoding.UTF8.GetBytes(payload.ToJsonString()), stream, socket, "a barra");
            return;
        }
        if (OpenAiKey.IsAlias(model) && payload is not null)
        {
            if (rest is not ("/responses" or "/responses/compact")) { await BridgeServer.PlainAsync(stream, 400, "Endpoint unavailable for OpenAI API alias"); return; }
            if (OpenAiKey.Get() is not { } key) { await BridgeServer.PlainAsync(stream, 401, "Chave da API OpenAI não cadastrada neste PC. Cadastre a chave e tente de novo."); return; }
            JsonObject api;
            CompactionV2.Unwrap(payload);
            try { api = OpenAiKey.Rewrite(payload); }
            catch (Exception ex) when (BridgeServer.BadShape(ex)) { await BridgeServer.PlainAsync(stream, 400, BridgeServer.BadShapeMessage); return; }
            // API pública: nunca a autorização da conta do ChatGPT nem os cabeçalhos internos do Codex.
            await ForwardAsync("POST", "https://api.openai.com/v1" + rest, Json(api, key), Encoding.UTF8.GetBytes(api.ToJsonString()), stream, socket, "a API da OpenAI");
            return;
        }

        // Modelos da conta e qualquer outro pedido: para onde o Codex já ia, sem mudança.
        string url;
        if (state.Upstream is { Length: > 0 } upstream)
            url = rest is not null ? upstream.TrimEnd('/') + rest + request.Query : new Uri(upstream).GetLeftPart(UriPartial.Authority) + request.Target;
        else
        {
            string openAi = request.Header("chatgpt-account-id") is not null ? "https://chatgpt.com/backend-api/codex" : "https://api.openai.com/v1";
            url = openAi + (rest ?? request.Path) + request.Query;
        }
        string where = state.Upstream is { } up && up.Contains("127.0.0.1:11434", StringComparison.Ordinal) ? "o Ollama" : "a OpenAI";
        // Conversa compactada antes por uma IA da barra: o resumo vira mensagem comum (a OpenAI recusaria o item marcado).
        // Só nesse caso o pedido é regravado, sem compressão; os demais seguem byte a byte como chegaram.
        if (payload is not null && CompactionV2.Unwrap(payload))
        {
            var headers = request.Headers.Where(h => !h.Key.Equals("Content-Encoding", StringComparison.OrdinalIgnoreCase)
                                                     && !h.Key.Equals("Content-Length", StringComparison.OrdinalIgnoreCase)).ToList();
            await ForwardAsync(request.Method, url, headers, Encoding.UTF8.GetBytes(payload.ToJsonString()), stream, socket, where);
            return;
        }
        await ForwardAsync(request.Method, url, request.Headers, request.Body, stream, socket, where);
    }

    private static List<KeyValuePair<string, string>> Json(JsonObject payload, string bearer) =>
    [
        new("Authorization", "Bearer " + bearer), new("Content-Type", "application/json"),
        new("Accept", payload["stream"] is JsonValue stream && stream.TryGetValue<bool>(out bool on) && on ? "text/event-stream" : "application/json"),
    ];

    private static (int Port, string Token)? BarBridge()
    {
        try
        {
            string file = Path.Combine(Paths.LocalAppData, "WinStatusBar", "codex-bridge.json");
            if (!File.Exists(file) || JsonNode.Parse(File.ReadAllText(file)) is not JsonObject info) return null;
            return (info["port"]!.GetValue<int>(), info["token"]!.GetValue<string>());
        }
        catch { return null; }
    }

    /// <summary>Repassa o pedido e devolve a resposta em fluxo (eventos do streaming chegam ao Codex na hora).</summary>
    private async Task ForwardAsync(string method, string url, IEnumerable<KeyValuePair<string, string>> headers, byte[] body,
        NetworkStream stream, Socket socket, string where)
    {
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
        using var finished = new CancellationTokenSource();
        // Se o Codex desistir do pedido, o destino para também (não gasta cota nem tokens à toa).
        var watcher = Task.Run(async () =>
        {
            try
            {
                while (!finished.IsCancellationRequested)
                {
                    await Task.Delay(1000, finished.Token);
                    if (BridgeServer.Closed(socket)) { cancel.Cancel(); return; }
                }
            }
            catch (OperationCanceledException) { }
        });
        bool started = false;
        try
        {
            using var message = new HttpRequestMessage(new HttpMethod(method), url);
            if (body.Length > 0 || method is "POST" or "PUT" or "PATCH") message.Content = new ByteArrayContent(body);
            foreach (var (name, value) in headers)
            {
                if (Hop.Contains(name)) continue;
                if (!message.Headers.TryAddWithoutValidation(name, value)) message.Content?.Headers.TryAddWithoutValidation(name, value);
            }
            using var response = await Http.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancel.Token);
            var head = new StringBuilder($"HTTP/1.1 {(int)response.StatusCode} {(string.IsNullOrEmpty(response.ReasonPhrase) ? "OK" : response.ReasonPhrase)}\r\n");
            foreach (var h in response.Headers.Concat(response.Content.Headers))
                if (!Hop.Contains(h.Key))
                    foreach (var value in h.Value) head.Append(h.Key).Append(": ").Append(value).Append("\r\n");
            head.Append("Connection: close\r\n\r\n");
            await stream.WriteAsync(Encoding.Latin1.GetBytes(head.ToString()), cancel.Token);
            started = true;
            await using var source = await response.Content.ReadAsStreamAsync(cancel.Token);
            var chunk = new byte[16 * 1024];
            int n;
            while ((n = await source.ReadAsync(chunk, cancel.Token)) > 0)
            {
                await stream.WriteAsync(chunk.AsMemory(0, n), cancel.Token);
                await stream.FlushAsync(cancel.Token);
            }
        }
        catch (Exception ex) when (!started && !cancel.IsCancellationRequested && ex is HttpRequestException or TaskCanceledException or IOException)
        {
            await BridgeServer.PlainAsync(stream, 502, where == "o Ollama"
                ? "O Ollama não respondeu (127.0.0.1:11434). Abra o Ollama e tente de novo."
                : $"Encaminhador: {where} não respondeu ({(ex.InnerException ?? ex).Message}). Tente de novo.");
        }
        catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or ObjectDisposedException or HttpRequestException) { }
        finally
        {
            finished.Cancel();
            try { await watcher; } catch { }
        }
    }

    public void Dispose()
    {
        _stop.Cancel();
        try { _listener.Stop(); } catch { }
    }
}
