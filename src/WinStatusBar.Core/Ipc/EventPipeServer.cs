using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;

namespace WinStatusBar.Core.Ipc;

/// <summary>Servidor de Named Pipe local. ACL: somente o usuário atual. Uma mensagem JSON por conexão.</summary>
public sealed class EventPipeServer : IDisposable
{
    private CancellationTokenSource? _cts;
    public event Action<EventMessage>? MessageReceived;
    public event Action<Exception>? Error;
    private long _received;
    public long Received => Interlocked.Read(ref _received);
    public string? LastError { get; private set; }

    public void Start()
    {
        Stop();
        _cts = new CancellationTokenSource();
        // Capture this generation's token before Stop/Start can replace the field.
        var token = _cts.Token;
        for (int i = 0; i < 2; i++) _ = Task.Run(() => LoopAsync(token));
    }

    public void Stop() { _cts?.Cancel(); _cts = null; }

    private static NamedPipeServerStream Create()
    {
        var security = new PipeSecurity();
        var user = WindowsIdentity.GetCurrent().User!;
        security.AddAccessRule(new PipeAccessRule(user, PipeAccessRights.FullControl, AccessControlType.Allow));
        // A ACL explícita (somente o usuário atual) substitui PipeOptions.CurrentUserOnly, que não pode ser combinada com PipeSecurity.
        return NamedPipeServerStreamAcl.Create(PipeNames.Events, PipeDirection.In, NamedPipeServerStream.MaxAllowedServerInstances,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 4096, 0, security);
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await using var server = Create();
                await server.WaitForConnectionAsync(ct);
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(TimeSpan.FromSeconds(3));
                using var ms = new MemoryStream();
                var buffer = new byte[4096];
                int n;
                while ((n = await server.ReadAsync(buffer, cts.Token)) > 0)
                {
                    ms.Write(buffer, 0, n);
                    if (ms.Length > 64 * 1024) break; // mensagens são pequenas; descarta excessos
                }
                var json = Encoding.UTF8.GetString(ms.GetBuffer(), 0, (int)ms.Length);
                EventMessage? msg = null;
                try { msg = JsonSerializer.Deserialize(json, EventMessageContext.Default.EventMessage); } catch (JsonException) { }
                if (msg is not null) { Interlocked.Increment(ref _received); MessageReceived?.Invoke(msg); }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch (Exception ex)
            {
                if (LastError != ex.Message) Error?.Invoke(ex);
                LastError = ex.Message;
                try { await Task.Delay(500, ct); } catch { return; }
            }
        }
    }

    public void Dispose() => Stop();
}
