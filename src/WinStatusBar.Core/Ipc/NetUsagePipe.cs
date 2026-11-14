using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using System.Text.Json.Serialization;
using WinStatusBar.Core.Integration;

namespace WinStatusBar.Core.Ipc;

/// <summary>Uso da rede de um processo: bytes por segundo recebidos e enviados nos últimos segundos.</summary>
public sealed record NetUsageEntry(uint Pid, long RecvPerSec, long SendPerSec);
public sealed record NetUsageReading(List<NetUsageEntry> Top, DateTimeOffset ObservedAt);
[JsonSerializable(typeof(NetUsageReading))]
public partial class NetUsageJson : JsonSerializerContext;

/// <summary>
/// Uso da rede por processo, medido pelo auxiliar com permissão de administrador (o mesmo do sensor de temperatura:
/// a leitura dos eventos de rede do Windows exige essa permissão, como no Gerenciador de Tarefas). A barra só lê; o
/// canal aceita apenas o usuário atual e só confia num servidor que roda o auxiliar protegido.
/// </summary>
public static class NetUsagePipe
{
    public static string Name => "WinStatusBar.NetUsage." + SensorPermissionTask.CurrentSid;
    private static DateTimeOffset _lastStartAttempt;

    /// <summary>Leitura atual; null quando o auxiliar não está rodando (sem a permissão dos sensores).</summary>
    public static async Task<NetUsageReading?> ReadAsync(CancellationToken ct)
    {
        for (int attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                using var pipe = new NamedPipeClientStream(".", Name, PipeDirection.In, PipeOptions.Asynchronous, TokenImpersonationLevel.Anonymous);
                await pipe.ConnectAsync(attempt == 0 ? 200 : 1800, ct);
                if (!IsProtectedServer(pipe)) return null;
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(3));
                var value = await JsonSerializer.DeserializeAsync(pipe, NetUsageJson.Default.NetUsageReading, timeout.Token);
                return value is not null && DateTimeOffset.UtcNow - value.ObservedAt < TimeSpan.FromSeconds(30) ? value : null;
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return null; }
            catch (Exception ex) when (ex is IOException or TimeoutException or UnauthorizedAccessException or JsonException) { }
            if (attempt == 0 && DateTimeOffset.UtcNow - _lastStartAttempt > TimeSpan.FromMinutes(1))
            {
                _lastStartAttempt = DateTimeOffset.UtcNow;
                if (!SensorPermissionTask.EnsureRunning()) return null;
            }
            else return null;
        }
        return null;
    }

    /// <summary>Servidor (no auxiliar com permissão): responde cada conexão com a leitura atual de <paramref name="read"/>.</summary>
    public static async Task RunServerAsync(Func<NetUsageReading> read, CancellationToken ct)
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
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromSeconds(3));
                await JsonSerializer.SerializeAsync(server, read(), NetUsageJson.Default.NetUsageReading, timeout.Token);
                await server.FlushAsync(timeout.Token);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OperationCanceledException)
            { await Task.Delay(1000, ct); }
        }
    }

    private static bool IsProtectedServer(NamedPipeClientStream pipe)
    {
        if (!GetNamedPipeServerProcessId(pipe.SafePipeHandle.DangerousGetHandle(), out uint pid)) return false;
        IntPtr process = OpenProcess(0x1000, false, pid);
        if (process == IntPtr.Zero) return false;
        try
        {
            var path = new System.Text.StringBuilder(1024); uint size = 1024;
            return QueryFullProcessImageNameW(process, 0, path, ref size) && SensorPermissionTask.IsProtectedExecutable(path.ToString());
        }
        finally { CloseHandle(process); }
    }

    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetNamedPipeServerProcessId(IntPtr pipe, out uint pid);
    [DllImport("kernel32.dll")] private static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern bool QueryFullProcessImageNameW(IntPtr process, uint flags, System.Text.StringBuilder path, ref uint size);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
}
