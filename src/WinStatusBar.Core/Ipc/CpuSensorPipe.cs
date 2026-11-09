using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using System.Text.Json.Serialization;
using WinStatusBar.Core.Integration;
using WinStatusBar.Core.Metrics;
namespace WinStatusBar.Core.Ipc;

public sealed record CpuSensorReading(double? Celsius, DateTimeOffset ObservedAt);
[JsonSerializable(typeof(CpuSensorReading))]
public partial class CpuSensorJson : JsonSerializerContext;

public static class CpuSensorPipe
{
    public static string Name => "WinStatusBar.CpuSensor." + SensorPermissionTask.CurrentSid;
    private static DateTimeOffset _lastStartAttempt;
    public static async Task<CpuSensorReading?> ReadAsync(CancellationToken ct)
    {
        for (int attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                using var pipe = new NamedPipeClientStream(".", Name, PipeDirection.In, PipeOptions.Asynchronous, TokenImpersonationLevel.Anonymous);
                await pipe.ConnectAsync(attempt == 0 ? 180 : 1800, ct);
                if (!IsProtectedServer(pipe)) return null;
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(3));
                byte[] bytes = new byte[1024]; int length = 0, read;
                while (length < bytes.Length && (read = await pipe.ReadAsync(bytes.AsMemory(length), timeout.Token)) > 0) length += read;
                if (length == bytes.Length) return null;
                var value = JsonSerializer.Deserialize(bytes.AsSpan(0, length), CpuSensorJson.Default.CpuSensorReading);
                if (value is not null && IsFresh(value, DateTimeOffset.UtcNow)) return value;
                return null;
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
    internal static bool IsFresh(CpuSensorReading value, DateTimeOffset now) => value.ObservedAt <= now.AddSeconds(5)
        && now - value.ObservedAt < TimeSpan.FromSeconds(30) && TemperatureProvider.ValidateTemperature(value.Celsius) is not null;

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
    public static async Task RunWorkerAsync(CancellationToken ct)
    {
        if (!SensorPermissionTask.IsAdministrator() || !SensorPermissionTask.IsProtectedExecutable(Environment.ProcessPath))
            throw new UnauthorizedAccessException();
        using var singleton = new Mutex(true, "WinStatusBar.CpuSensorWorker." + SensorPermissionTask.CurrentSid, out bool created);
        if (!created) return;
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
                    PipeOptions.Asynchronous | PipeOptions.FirstPipeInstance, 0, 1024, acl);
                await server.WaitForConnectionAsync(ct);
                var value = new CpuSensorReading(TemperatureProvider.ReadLenovoCpu(), DateTimeOffset.UtcNow);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromSeconds(3));
                await JsonSerializer.SerializeAsync(server, value, CpuSensorJson.Default.CpuSensorReading, timeout.Token);
                await server.FlushAsync(timeout.Token);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OperationCanceledException)
            { await Task.Delay(1000, ct); }
        }
    }
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetNamedPipeServerProcessId(IntPtr pipe, out uint pid);
    [DllImport("kernel32.dll")] private static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern bool QueryFullProcessImageNameW(IntPtr process, uint flags, System.Text.StringBuilder path, ref uint size);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
}
