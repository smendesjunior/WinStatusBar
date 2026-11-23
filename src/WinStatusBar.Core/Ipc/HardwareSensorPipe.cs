using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using WinStatusBar.Core.Integration;
using WinStatusBar.Core.Metrics;

namespace WinStatusBar.Core.Ipc;

/// <summary>
/// Leituras do LibreHardwareMonitor feitas pelo leitor protegido (administrador, tarefa agendada). Mesmo modelo do sensor
/// de temperatura: só o usuário atual lê, e a barra só confia num servidor que roda o executável da pasta protegida.
/// O leitor abre o LibreHardwareMonitor só quando a barra pede, e o fecha depois de um minuto sem pedidos.
/// </summary>
public static class HardwareSensorPipe
{
    public static string Name => "WinStatusBar.Hardware." + SensorPermissionTask.CurrentSid;
    private static DateTimeOffset _lastStartAttempt;

    /// <summary>Leitura atual; nulo quando o leitor protegido não responde (sem autorização ou versão antiga).</summary>
    public static async Task<HardwareReading?> ReadAsync(CancellationToken ct, int connectMs = 250)
    {
        try
        {
            using var pipe = new NamedPipeClientStream(".", Name, PipeDirection.In, PipeOptions.Asynchronous, TokenImpersonationLevel.Anonymous);
            await pipe.ConnectAsync(connectMs, ct);
            if (!CpuSensorPipe.IsProtectedServer(pipe)) return null;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(3));
            var value = await JsonSerializer.DeserializeAsync(pipe, HardwareReadingJson.Default.HardwareReading, timeout.Token);
            return value is not null && value.IsFresh(DateTimeOffset.UtcNow) ? value : null;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return null; }
        catch (Exception ex) when (ex is IOException or TimeoutException or UnauthorizedAccessException or JsonException)
        {
            // Leitor parado: pede ao agendador para iniciá-lo, no máximo uma vez por minuto (sem esperar aqui).
            if (DateTimeOffset.UtcNow - _lastStartAttempt > TimeSpan.FromMinutes(1))
            {
                _lastStartAttempt = DateTimeOffset.UtcNow;
                _ = Task.Run(SensorPermissionTask.EnsureRunning);
            }
            return null;
        }
    }

    /// <summary>Servidor (no leitor protegido): responde cada conexão com a leitura atual de <paramref name="read"/>.</summary>
    public static async Task RunServerAsync(Func<HardwareReading> read, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var acl = new PipeSecurity();
                acl.SetOwner(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null));
                acl.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.NetworkSid, null), PipeAccessRights.FullControl, AccessControlType.Deny));
                acl.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
                acl.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(SensorPermissionTask.CurrentSid), PipeAccessRights.Read | PipeAccessRights.Synchronize, AccessControlType.Allow));
                await using var server = NamedPipeServerStreamAcl.Create(Name, PipeDirection.Out, 1, PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.FirstPipeInstance, 0, 16384, acl);
                await server.WaitForConnectionAsync(ct);
                var value = await Task.Run(read, ct);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromSeconds(3));
                await JsonSerializer.SerializeAsync(server, value, HardwareReadingJson.Default.HardwareReading, timeout.Token);
                await server.FlushAsync(timeout.Token);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch (Exception) when (!ct.IsCancellationRequested)
            {
                // Qualquer falha (inclusive do LibreHardwareMonitor) só atrasa: o canal nunca para de vez, porque o
                // processo continua vivo pelo sensor de temperatura e o agendador não o reiniciaria.
                try { await Task.Delay(1000, ct); } catch (OperationCanceledException) { return; }
            }
        }
    }
}
