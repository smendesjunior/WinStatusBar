using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace WinStatusBar.ExecutorExpansion.Transport;

public sealed record BridgeEndpoint(string Product, int ProcessId, long ProcessStartedUtcTicks,
    string Pipe, string Token, DateTimeOffset ExpiresAt, int Protocol = 1);
public sealed record BridgeCall(string RequestId, string Token, JsonElement Request);
public sealed record BridgeAnswer(bool Success, string Status, string Text, string? Data = null);

/// <summary>Local, current-user-only transport; no Autodesk dependency and no off-process API calls.</summary>
public sealed class InProcessBridgeServer : IAsyncDisposable
{
    private readonly CancellationTokenSource lifetime = new(TimeSpan.FromHours(4));
    private readonly Dictionary<string, (string Hash, Task<BridgeAnswer> Task)> calls = new();
    private readonly Func<JsonElement, CancellationToken, Task<BridgeAnswer>> execute;
    private readonly string endpointPath;
    private readonly Task loop;
    public BridgeEndpoint Endpoint { get; }
    public InProcessBridgeServer(string product, string directory,
        Func<JsonElement, CancellationToken, Task<BridgeAnswer>> handler)
    {
        if (!System.Text.RegularExpressions.Regex.IsMatch(product, "^[a-z0-9-]{1,32}$")) throw new ArgumentException("product");
        execute = handler;
        using var process = Process.GetCurrentProcess();
        Endpoint = new(product, Environment.ProcessId, process.StartTime.ToUniversalTime().Ticks,
            "WinStatusBar.Bridge." + product + "." + Guid.NewGuid().ToString("N"),
            Convert.ToHexString(RandomNumberGenerator.GetBytes(32)), DateTimeOffset.UtcNow.AddHours(4));
        Directory.CreateDirectory(directory);
        endpointPath = Path.Combine(directory, product + "-" + Environment.ProcessId + ".json");
        string temporary = endpointPath + "." + Guid.NewGuid().ToString("N");
        File.WriteAllText(temporary, JsonSerializer.Serialize(Endpoint), new UTF8Encoding(false));
        File.Move(temporary, endpointPath, true);
        loop = Task.Run(ServeAsync);
    }
    private async Task ServeAsync()
    {
        try
        {
            while (!lifetime.IsCancellationRequested)
            {
                await using var pipe = new NamedPipeServerStream(Endpoint.Pipe, PipeDirection.InOut, 1,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await pipe.WaitForConnectionAsync(lifetime.Token).ConfigureAwait(false);
                using var connection = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
                connection.CancelAfter(TimeSpan.FromSeconds(120));
                try
                {
                    using var reader = new StreamReader(pipe, new UTF8Encoding(false), false, 4096, true);
                    using var writer = new StreamWriter(pipe, new UTF8Encoding(false), 4096, true) { AutoFlush = true };
                    string line = await ReadBoundedLineAsync(reader, connection.Token).ConfigureAwait(false);
                    BridgeCall? call;
                    try { call = JsonSerializer.Deserialize<BridgeCall>(line); }
                    catch (JsonException) { continue; }
                    if (call is null || !Guid.TryParse(call.RequestId, out _) || !Authenticated(call.Token)) continue;
                    string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(call.Request.GetRawText())));
                    BridgeAnswer answer;
                    if (calls.TryGetValue(call.RequestId, out var previous) && previous.Hash != hash)
                        answer = new(false, "bloqueada", "RequestId pertence a outro pedido.");
                    else
                    {
                        if (previous.Task is null)
                        {
                            if (calls.Count >= 1000)
                            {
                                answer = new(false, "bloqueada", "Sessão atingiu limite; consulte estado antes de iniciar nova sessão.");
                                await writer.WriteLineAsync(JsonSerializer.Serialize(answer).AsMemory(), connection.Token);
                                continue;
                            }
                            var operation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
                            operation.CancelAfter(TimeSpan.FromSeconds(90));
                            var actual = InvokeAsync(call.Request.Clone(), operation);
                            previous = (hash, actual);
                            calls.Add(call.RequestId, previous);
                        }
                        try { answer = await previous.Task.WaitAsync(connection.Token).ConfigureAwait(false); }
                        catch (OperationCanceledException) { answer = new(false, "parcial", "Prazo/conexão encerrado; efeito não confirmado. Reconsulte o MESMO RequestId, sem repetir a mutação."); }
                    }
                    string encoded = JsonSerializer.Serialize(answer);
                    if (encoded.Length > 4_000_000)
                        encoded = JsonSerializer.Serialize(new BridgeAnswer(false, "parcial", "Resposta excedeu limite de 4 MB; reduza a consulta. Nenhuma mutação deve ser repetida automaticamente."));
                    await writer.WriteLineAsync(encoded.AsMemory(), connection.Token).ConfigureAwait(false);
                }
                catch (Exception e) when (e is IOException or OperationCanceledException or JsonException) { }
            }
        }
        catch (OperationCanceledException) { }
        finally
        {
            // Delete only this exact session descriptor, never another Revit instance's session.
            try
            {
                var current = JsonSerializer.Deserialize<BridgeEndpoint>(File.ReadAllText(endpointPath));
                if (current?.Pipe == Endpoint.Pipe) File.Delete(endpointPath);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { }
        }
    }
    private async Task<BridgeAnswer> InvokeAsync(JsonElement request, CancellationTokenSource operation)
    {
        using (operation)
        {
            try { return await execute(request, operation.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) { return new(false, "parcial", "Operação interrompida; consulte estado antes de qualquer repetição."); }
            catch { return new(false, "parcial", "Falha no host; resultado não confirmado. Consulte o estado."); }
        }
    }
    private bool Authenticated(string? token)
    {
        if (token is null || token.Length != Endpoint.Token.Length) return false;
        return CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(token), Encoding.UTF8.GetBytes(Endpoint.Token));
    }
    internal static async Task<string> ReadBoundedLineAsync(StreamReader reader, CancellationToken ct, int maximumChars = 65536)
    {
        var text = new StringBuilder(); char[] one = new char[1];
        while (text.Length <= maximumChars)
        {
            if (await reader.ReadAsync(one.AsMemory(), ct).ConfigureAwait(false) == 0) throw new IOException("Conexão encerrada.");
            if (one[0] == '\n') return text.ToString().TrimEnd('\r');
            text.Append(one[0]);
        }
        throw new IOException("Mensagem excede o limite permitido.");
    }
    public async ValueTask DisposeAsync()
    {
        await lifetime.CancelAsync().ConfigureAwait(false);
        await loop.ConfigureAwait(false);
        lifetime.Dispose();
    }
}

public static class InProcessBridgeClient
{
    public static string DefaultDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WinStatusBar", "Bridges");
    public static BridgeEndpoint[] Discover(string product, string? directory = null)
    {
        directory ??= DefaultDirectory;
        if (!Directory.Exists(directory)) return [];
        var endpoints = new List<BridgeEndpoint>();
        foreach (string path in Directory.EnumerateFiles(directory, "*.json"))
        {
            try
            {
                var e = JsonSerializer.Deserialize<BridgeEndpoint>(File.ReadAllText(path));
                if (e is null || e.Product != product || e.Protocol != 1 || e.ExpiresAt <= DateTimeOffset.UtcNow ||
                    !e.Pipe.StartsWith("WinStatusBar.Bridge." + product + ".", StringComparison.Ordinal)) continue;
                using var process = Process.GetProcessById(e.ProcessId);
                if (process.StartTime.ToUniversalTime().Ticks == e.ProcessStartedUtcTicks) endpoints.Add(e);
            }
            catch (Exception e) when (e is IOException or JsonException or ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception or UnauthorizedAccessException) { }
        }
        return endpoints.ToArray();
    }
    public static async Task<BridgeAnswer> SendAsync(BridgeEndpoint endpoint, string requestId, JsonElement request, CancellationToken ct)
    {
        if (!Guid.TryParse(requestId, out _) || endpoint.ExpiresAt <= DateTimeOffset.UtcNow) throw new ArgumentException("Sessão/requestId inválido.");
        await using var pipe = new NamedPipeClientStream(".", endpoint.Pipe, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await pipe.ConnectAsync(5000, ct).ConfigureAwait(false);
        using var reader = new StreamReader(pipe, new UTF8Encoding(false), false, 4096, true);
        using var writer = new StreamWriter(pipe, new UTF8Encoding(false), 4096, true) { AutoFlush = true };
        string line = JsonSerializer.Serialize(new BridgeCall(requestId, endpoint.Token, request));
        if (line.Length > 65536) throw new ArgumentException("Pedido excede 64 KiB.");
        await writer.WriteLineAsync(line.AsMemory(), ct).ConfigureAwait(false);
        return JsonSerializer.Deserialize<BridgeAnswer>(await InProcessBridgeServer.ReadBoundedLineAsync(reader, ct, 4_000_000).ConfigureAwait(false))
            ?? throw new IOException("Resposta inválida do host.");
    }
}
