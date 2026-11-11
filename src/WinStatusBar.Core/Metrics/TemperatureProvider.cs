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

    /// <summary>Entrada de <see cref="MissingNamespaces"/> para a classe WMI da Lenovo (ausente em PCs de outras marcas).</summary>
    private const string LenovoKey = "lenovo";

    public static double? ReadLenovoCpu()
    {
        if (MissingNamespaces.TryGetValue(LenovoKey, out var until) && DateTime.UtcNow < until) return null;
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
        catch (ManagementException ex) when (IsMissing(ex))
        {
            // PC sem a classe da Lenovo: a consulta falhava a cada 10 s; como os namespaces abaixo, tenta de novo em 10 min.
            MissingNamespaces[LenovoKey] = DateTime.UtcNow + MissingRetry;
        }
        catch (ManagementException) { }
        catch (UnauthorizedAccessException) { }
        catch (System.Runtime.InteropServices.COMException) { }
        return null;
    }

    /// <summary>A classe ou o namespace WMI não existe neste PC (não é falha passageira nem falta de permissão).</summary>
    private static bool IsMissing(ManagementException ex) =>
        ex.ErrorCode is ManagementStatus.InvalidNamespace or ManagementStatus.InvalidClass or ManagementStatus.NotFound;

    /// <summary>Namespaces WMI ausentes (LibreHardwareMonitor/OpenHardwareMonitor não instalados) até quando.</summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, DateTime> MissingNamespaces = new();
    private static readonly TimeSpan MissingRetry = TimeSpan.FromMinutes(10);

    /// <summary>
    /// Reserva quando o driver não informa a temperatura pelo kernel gráfico: nvidia-smi no máximo uma vez por minuto
    /// (cada execução custa ~44 ms de CPU e pode acordar a placa dedicada); entre uma e outra, vale a última leitura.
    /// </summary>
    private static readonly SemaphoreSlim NvidiaGate = new(1, 1);
    private static DateTime _nvidiaSmiAt = DateTime.MinValue;
    private static double? _nvidiaSmiLast;
    private static readonly TimeSpan NvidiaSmiInterval = TimeSpan.FromMinutes(1);

    private static async Task<double?> NvidiaSmiAsync(string gpuName, CancellationToken ct)
    {
        await NvidiaGate.WaitAsync(ct);
        try
        {
            if (DateTime.UtcNow - _nvidiaSmiAt < NvidiaSmiInterval) return _nvidiaSmiLast;
            _nvidiaSmiAt = DateTime.UtcNow;
            double? gpu = null;
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
            return _nvidiaSmiLast = gpu;
        }
        finally { NvidiaGate.Release(); }
    }

    public async Task<TemperatureSample> SampleAsync(GpuAdapterInfo? adapter, CancellationToken ct)
    {
        double? cpu = null, gpu = null;
        var readings = new List<CpuTemperatureReading>();
        var persistent = await CpuSensorPipe.ReadAsync(ct);
        if (persistent?.Celsius is double saved) readings.Add(new(global::WinStatusBar.Core.LocalizedText.T("Sensor Lenovo (autorização persistente)"), saved, true));
        else if (ReadLenovoCpu() is double lenovo) readings.Add(new(global::WinStatusBar.Core.LocalizedText.T("Sensor Lenovo (GetCPUTemp)"), lenovo, true));
        foreach (var ns in new[] { @"root\LibreHardwareMonitor", @"root\OpenHardwareMonitor" })
        {
            if (MissingNamespaces.TryGetValue(ns, out var until) && DateTime.UtcNow < until) continue;
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
                        double value;
                        try { value = Convert.ToDouble(row["Value"], CultureInfo.InvariantCulture); }
                        catch (Exception ex) when (ex is FormatException or InvalidCastException or OverflowException) { continue; } // valor ilegível: só este sensor
                        string name = row["Name"]?.ToString() ?? "";
                        readings.Add(new(name, value, id.Contains("cpu", StringComparison.OrdinalIgnoreCase)));
                    }
                }
            }
            catch (ManagementException ex) when (IsMissing(ex))
            {
                // Programa não instalado: cada tentativa custava ~5 ms de CPU aqui e mais no serviço WMI, a cada 10 s.
                MissingNamespaces[ns] = DateTime.UtcNow + MissingRetry;
            }
            catch (ManagementException) { }
            catch (UnauthorizedAccessException) { }
            catch (System.Runtime.InteropServices.COMException) { } // o serviço WMI falhou nesta leitura: fica sem este sensor
        }
        if (adapter is not null)
        {
            // Pelo kernel gráfico, sem abrir programas (WDDM 2.4+, a mesma leitura do Gerenciador de Tarefas).
            var (supported, celsius) = GpuKernelStats.Temperature(adapter.Luid);
            if (supported) gpu = celsius;
            else if (adapter.Name.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase)) gpu = await NvidiaSmiAsync(adapter.Name, ct);
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
