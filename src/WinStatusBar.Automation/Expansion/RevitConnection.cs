using System.Text.Json;
using WinStatusBar.Core.Ai;
using WinStatusBar.ExecutorExpansion.Transport;

namespace WinStatusBar.ExecutorExpansion;

public static class RevitConnection
{
    public static async Task<DesktopActionResult> ExecuteAsync(DesktopActionRequest r, CancellationToken ct)
    {
        var sessions = InProcessBridgeClient.Discover("revit");
        if (r.Op == "revit_discover")
            return new(JsonSerializer.Serialize(sessions.Select(s => new { s.Product, s.ProcessId, s.ExpiresAt })), Method: "Revit/local-session-discovery");
        if (r.Op != "revit_request") throw new ArgumentException("op inválida.");
        if (!int.TryParse(r.Application, out int pid) || !Guid.TryParse(r.Name, out _))
            return new("Informe application=PID do Revit e name=GUID estável deste pedido. Mantenha o mesmo GUID ao consultar resultado incerto.", Success: false, Status: DesktopStatus.Blocked);
        var endpoint = sessions.SingleOrDefault(s => s.ProcessId == pid);
        if (endpoint is null)
            return new("Não há bridge Revit ativa para esse PID. O comando do add-in precisa ser habilitado dentro do Revit; as ferramentas gerais continuam disponíveis.", Success: false, Status: DesktopStatus.Blocked);
        using var json = JsonDocument.Parse(r.Text ?? throw new ArgumentException("Informe text com JSON RevitRequest."));
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(r.TimeoutSeconds ?? 110, 5, 1800)));
        try
        {
            var answer = await InProcessBridgeClient.SendAsync(endpoint, r.Name!, json.RootElement, deadline.Token);
            string text = answer.Text + "\nRequestId: " + r.Name + (answer.Data is null ? "" : "\n" + answer.Data);
            return new(text, Success: answer.Success, Status: answer.Status, Method: "Revit/pipe+ExternalEvent");
        }
        catch (Exception e) when (e is IOException or TimeoutException || e is OperationCanceledException && !ct.IsCancellationRequested)
        {
            return new("Conexão/prazo encerrado; efeito não confirmado. Consulte o MESMO RequestId: " + r.Name +
                ". Não envie outra mutação com novo GUID.", Success: false, Status: DesktopStatus.Partial, Method: "Revit/pipe+ExternalEvent");
        }
    }
}
