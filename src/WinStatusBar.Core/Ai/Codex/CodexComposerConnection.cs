using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
namespace WinStatusBar.Core.Ai.Codex;
/// <summary>Scoped stdio RPC session; never changes global Codex configuration or credentials.</summary>
public sealed class CodexComposerConnection : IAsyncDisposable
{
    private readonly Process _process;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly SemaphoreSlim _write = new(1, 1);
    private readonly ConcurrentDictionary<int, TaskCompletionSource<JsonElement>> _pending = new();
    private readonly Task _reader;
    private int _id;
    public event Action<string, JsonElement>? Notification;
    public Func<string, JsonElement, CancellationToken, Task<JsonNode>>? Request { get; set; }
    private CodexComposerConnection(Process process)
    {
        _process = process;
        _ = process.StandardError.ReadToEndAsync(); // esvazia o stderr para o processo nunca travar com o canal cheio
        _reader = ReadAsync();
    }
    public static async Task<CodexComposerConnection> OpenAsync(CancellationToken ct)
    {
        var exe = CodexAppServerClient.FindCodexExecutable() ?? throw new InvalidOperationException("Codex não encontrado.");
        bool cmd = exe.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase);
        var psi = new ProcessStartInfo(cmd ? Path.Combine(Environment.SystemDirectory, "cmd.exe") : exe)
        {
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            UseShellExecute = false, CreateNoWindow = true, StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
        };
        if (cmd) { psi.ArgumentList.Add("/d"); psi.ArgumentList.Add("/c"); psi.ArgumentList.Add(exe); }
        psi.ArgumentList.Add("app-server"); psi.ArgumentList.Add("--listen"); psi.ArgumentList.Add("stdio://");
        var connection = new CodexComposerConnection(Process.Start(psi) ?? throw new IOException("Falha ao abrir Codex."));
        try
        {
            await connection.CallAsync("initialize", new JsonObject { ["clientInfo"] = new JsonObject { ["name"] = "winstatusbar", ["version"] = "1.0.0" }, ["capabilities"] = new JsonObject { ["experimentalApi"] = true } }, ct);
            await connection.SendAsync(new JsonObject { ["method"] = "initialized" }, ct);
            return connection;
        }
        catch { await connection.DisposeAsync(); throw; }
    }
    public async Task<JsonElement> CallAsync(string method, JsonNode args, CancellationToken ct)
    {
        int id = Interlocked.Increment(ref _id);
        var tcs = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = tcs;
        try
        {
            await SendAsync(new JsonObject { ["id"] = id, ["method"] = method, ["params"] = args }, ct);
            var message = await tcs.Task.WaitAsync(ct);
            if (message.TryGetProperty("error", out var error)) throw new IOException(error.ToString());
            return message.GetProperty("result").Clone();
        }
        finally { _pending.TryRemove(id, out _); }
    }
    private async Task SendAsync(JsonNode value, CancellationToken ct)
    {
        await _write.WaitAsync(ct);
        try { await _process.StandardInput.WriteLineAsync(value.ToJsonString().AsMemory(), ct); await _process.StandardInput.FlushAsync(ct); }
        finally { _write.Release(); }
    }
    private async Task ReadAsync()
    {
        Exception failure = new IOException("Codex encerrou a conexão.");
        try
        {
            while (await _process.StandardOutput.ReadLineAsync(_lifetime.Token) is { } line)
            {
                if (!line.StartsWith('{')) continue;
                using var doc = JsonDocument.Parse(line);
                var message = doc.RootElement;
                if (message.TryGetProperty("method", out var method))
                {
                    var args = message.TryGetProperty("params", out var p) ? p.Clone() : default;
                    if (message.TryGetProperty("id", out var requestId))
                        _ = ReplyAsync(requestId.Clone(), method.GetString()!, args);
                    else Notification?.Invoke(method.GetString()!, args);
                }
                else if (message.TryGetProperty("id", out var id) && id.TryGetInt32(out int n) && _pending.TryGetValue(n, out var tcs))
                    tcs.TrySetResult(message.Clone());
            }
        }
        catch (Exception ex) { failure = ex; }
        finally
        {
            foreach (var request in _pending.Values) request.TrySetException(failure);
            Notification?.Invoke("connection/closed", JsonSerializer.SerializeToElement(new { message = failure.Message }));
        }
    }
    private async Task ReplyAsync(JsonElement id, string method, JsonElement args)
    {
        try
        {
            JsonNode result = Request is not null ? await Request(method, args, _lifetime.Token)
                : method.EndsWith("requestApproval", StringComparison.Ordinal) ? new JsonObject { ["decision"] = "decline" }
                : throw new NotSupportedException("Solicitação não suportada: " + method);
            await SendAsync(new JsonObject { ["id"] = JsonNode.Parse(id.GetRawText()), ["result"] = result }, _lifetime.Token);
        }
        catch (Exception ex)
        {
            try { await SendAsync(new JsonObject { ["id"] = JsonNode.Parse(id.GetRawText()), ["error"] = new JsonObject { ["code"] = -32603, ["message"] = ex.Message } }, _lifetime.Token); } catch { }
        }
    }
    public async ValueTask DisposeAsync()
    {
        _lifetime.Cancel();
        try { if (!_process.HasExited) _process.Kill(entireProcessTree: true); } catch { }
        try { await _reader.WaitAsync(TimeSpan.FromSeconds(3)); } catch { }
        _process.Dispose();
    }
}
