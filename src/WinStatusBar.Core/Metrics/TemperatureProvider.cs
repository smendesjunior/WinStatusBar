using System.Diagnostics;
using System.Globalization;
using System.Management;
using WinStatusBar.Core.Ipc;
using System.Security.Principal;
namespace WinStatusBar.Core.Metrics;

public sealed record TemperatureSample(double? Cpu, double? Gpu, string CpuSource = "Sensor indisponível");
internal sealed record CpuTemperatureReading(string Name, double Value, bool IsCpu);
public sealed class TemperatureProvider : IDisposable
{
    internal static CpuTemperatureReading? SelectCpu(IEnumerable<CpuTemperatureReading> readings) => readings
        .Where(r => double.IsFinite(r.Value) && r.Value > 0 && r.Value < 130)
        .Where(r => r.IsCpu || r.Name.Equals(global::WinStatusBar.Core.LocalizedText.T("CPU Proximity"), StringComparison.OrdinalIgnoreCase))
        .OrderBy(r => r.Name.Equals(global::WinStatusBar.Core.LocalizedText.T("CPU Proximity"), StringComparison.OrdinalIgnoreCase) ? 0
            : r.Name.Equals(global::WinStatusBar.Core.LocalizedText.T("CPU Package"), StringComparison.OrdinalIgnoreCase) ? 1 : 2)
        .ThenByDescending(r => r.Value).FirstOrDefault();

    public void Dispose() { }

    internal static double? ValidateTemperature(object? value)
    {
        if (value is null) return null;
        try
        {
            double temperature = Convert.ToDouble(value, CultureInfo.InvariantCulture);
            return double.IsFinite(temperature) && temperature > 0 && temperature < 130 ? temperature : null;
        }
        catch (Exception ex) when (ex is FormatException or InvalidCastException or OverflowException) { return null; }
    }

    public static double? ReadLenovoCpu()
    {
        try
        {
            using var query = new ManagementObjectSearcher(@"root\wmi", "SELECT * FROM LENOVO_GAMEZONE_DATA");
            query.Options.Timeout = TimeSpan.FromSeconds(2);
            using var rows = query.Get();
            foreach (ManagementObject row in rows)
            {
                using (row)
                using (var result = row.InvokeMethod("GetCPUTemp", null, new InvokeMethodOptions { Timeout = TimeSpan.FromSeconds(2) }))
                {
                    if (result is not null && ValidateTemperature(result["Data"]) is double value) return value;
                }
            }
        }
        catch (ManagementException) { }
        catch (UnauthorizedAccessException) { }
        catch (System.Runtime.InteropServices.COMException) { }
        return null;
    }

    public async Task<TemperatureSample> SampleAsync(string? gpuName, CancellationToken ct)
    {
        double? cpu = null, gpu = null;
        var readings = new List<CpuTemperatureReading>();
        var persistent = await CpuSensorPipe.ReadAsync(ct);
        if (persistent?.Celsius is double saved) readings.Add(new(global::WinStatusBar.Core.LocalizedText.T("Sensor Lenovo (autorização persistente)"), saved, true));
        else if (ReadLenovoCpu() is double lenovo) readings.Add(new(global::WinStatusBar.Core.LocalizedText.T("Sensor Lenovo (GetCPUTemp)"), lenovo, true));
        foreach (var ns in new[] { @"root\LibreHardwareMonitor", @"root\OpenHardwareMonitor" })
        {
            try
            {
                using var query = new ManagementObjectSearcher(ns, "SELECT Name, Value, Identifier FROM Sensor WHERE SensorType='Temperature'");
                query.Options.Timeout = TimeSpan.FromSeconds(2);
                using var rows = query.Get();
                foreach (ManagementObject row in rows)
                {
                    using (row)
                    {
                        string id = row["Identifier"]?.ToString() ?? "";
                        double value = Convert.ToDouble(row["Value"], CultureInfo.InvariantCulture);
                        string name = row["Name"]?.ToString() ?? "";
                        readings.Add(new(name, value, id.Contains("cpu", StringComparison.OrdinalIgnoreCase)));
                    }
                }
            }
            catch (ManagementException) { }
            catch (UnauthorizedAccessException) { }
        }
        if (gpuName?.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase) == true)
        {
            try
            {
                var info = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "nvidia-smi.exe"))
                { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
                info.ArgumentList.Add("--query-gpu=name,temperature.gpu");
                info.ArgumentList.Add("--format=csv,noheader,nounits");
                using var process = Process.Start(info);
                if (process is not null)
                {
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    timeout.CancelAfter(TimeSpan.FromSeconds(3));
                    try
                    {
                        var output = await process.StandardOutput.ReadToEndAsync(timeout.Token);
                        await process.WaitForExitAsync(timeout.Token);
                        foreach (string line in output.Split('\n'))
                        {
                            int comma = line.LastIndexOf(',');
                            if (comma > 0 && line[..comma].Trim().Equals(gpuName, StringComparison.OrdinalIgnoreCase)
                                && double.TryParse(line[(comma + 1)..].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
                                && value > 0 && value < 130) gpu = value;
                        }
                    }
                    finally { if (!process.HasExited) process.Kill(true); }
                }
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or OperationCanceledException or InvalidOperationException) { }
        }
        var selected = SelectCpu(readings);
        cpu = selected?.Value;
        string source = selected?.Name ?? global::WinStatusBar.Core.LocalizedText.T("Sensor indisponível");
        if (selected is null)
        {
            using var identity = WindowsIdentity.GetCurrent();
            bool admin = new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
            source += admin ? global::WinStatusBar.Core.LocalizedText.T(": hardware ou driver não disponibilizou leitura") : global::WinStatusBar.Core.LocalizedText.T(": sensor Lenovo requer executar como administrador; nenhum driver é instalado");

        }
        return new(cpu, gpu, source);
    }
}
