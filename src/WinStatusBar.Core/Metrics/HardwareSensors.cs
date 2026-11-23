using System.Text.Json.Serialization;

namespace WinStatusBar.Core.Metrics;

/// <summary>Um sensor como o LibreHardwareMonitor o expõe (tipos e nomes originais, em inglês).</summary>
public sealed record HardwareSensorValue(string HardwareType, string Hardware, string SensorType, string Sensor, double Value, string? HardwareId = null);

public sealed record HardwareGpuReading(string Name, double? LoadPercent, double? Celsius, double? DedicatedUsedMB, double? SharedUsedMB, double? CoreClockMHz);

/// <summary>
/// Leituras de hardware do LibreHardwareMonitor (com o driver PawnIO, como no Lenovo Legion Toolkit), feitas pelo leitor
/// protegido. Cada campo nulo = não lido; a barra fica com a leitura do Windows naquele item.
/// </summary>
public sealed record HardwareReading(
    double? CpuLoadPercent, double? CpuAverageGHz, double? CpuPeakGHz, double? CpuCelsius, string? CpuTemperatureSensor,
    double? RamUsedGB, double? RamAvailableGB, List<HardwareGpuReading> Gpus, bool PawnIo, DateTimeOffset ObservedAt, bool Starting = false)
{
    public const string SourceName = "LibreHardwareMonitor";
    public bool IsFresh(DateTimeOffset now) => ObservedAt <= now.AddSeconds(5) && now - ObservedAt < TimeSpan.FromSeconds(15);
}

[JsonSerializable(typeof(HardwareReading))]
public partial class HardwareReadingJson : JsonSerializerContext;

/// <summary>Resumo dos sensores e substituição das leituras do Windows. Sem dependência do LibreHardwareMonitor (testável).</summary>
public static class HardwareSensors
{
    private static readonly string[] CpuTemperatures = ["CPU Package", "Core (Tctl/Tdie)", "Core (Tctl)", "Core (Tdie)", "CPU Cores", "Core Max", "Core Average"];

    public static HardwareReading Summarize(IEnumerable<HardwareSensorValue> sensors, bool pawnIo, DateTimeOffset now)
    {
        var all = sensors.Where(s => double.IsFinite(s.Value)).ToList();
        bool Is(HardwareSensorValue s, string type, string name) =>
            s.SensorType.Equals(type, StringComparison.OrdinalIgnoreCase) && s.Sensor.Equals(name, StringComparison.OrdinalIgnoreCase);

        var cpu = all.Where(s => s.HardwareType.Equals("Cpu", StringComparison.OrdinalIgnoreCase)).ToList();
        // Clock por núcleo ("CPU Core #1", "P-Core #1", "Core #1"): fora o barramento e os valores médios/efetivos.
        var clocks = cpu.Where(s => s.SensorType.Equals("Clock", StringComparison.OrdinalIgnoreCase) && s.Value > 0
                && s.Sensor.Contains('#') && !s.Sensor.Contains("Bus", StringComparison.OrdinalIgnoreCase)
                && !s.Sensor.Contains("Effective", StringComparison.OrdinalIgnoreCase) && !s.Sensor.Contains("Average", StringComparison.OrdinalIgnoreCase))
            .Select(s => s.Value).ToList();
        double? load = cpu.FirstOrDefault(s => Is(s, "Load", "CPU Total"))?.Value;
        HardwareSensorValue? temperature = null;
        foreach (string name in CpuTemperatures)
            if ((temperature = cpu.FirstOrDefault(s => Is(s, "Temperature", name) && s.Value is > 0 and < 130)) is not null) break;

        // Memória física: "Memory Used" e "Memory Available" (GB) do hardware de memória total, nunca o da memória virtual.
        var memory = all.Where(s => s.HardwareType.Equals("Memory", StringComparison.OrdinalIgnoreCase)
            && !s.Hardware.Contains("Virtual", StringComparison.OrdinalIgnoreCase)).ToList();
        double? used = memory.FirstOrDefault(s => Is(s, "Data", "Memory Used") && s.Value >= 0)?.Value;
        double? available = memory.FirstOrDefault(s => Is(s, "Data", "Memory Available") && s.Value >= 0)?.Value;

        var gpus = all.Where(s => s.HardwareType.StartsWith("Gpu", StringComparison.OrdinalIgnoreCase))
            .GroupBy(s => s.HardwareId ?? s.Hardware, StringComparer.OrdinalIgnoreCase) // duas placas iguais não se misturam
            .Select(g =>
            {
                var list = g.ToList();
                string gpuName = list[0].Hardware;
                double? gpuLoad = list.FirstOrDefault(s => Is(s, "Load", "GPU Core"))?.Value
                    ?? list.Where(s => s.SensorType.Equals("Load", StringComparison.OrdinalIgnoreCase) && s.Sensor.StartsWith("D3D 3D", StringComparison.OrdinalIgnoreCase))
                        .Select(s => (double?)s.Value).Max();
                double? gpuTemp = list.FirstOrDefault(s => Is(s, "Temperature", "GPU Core") && s.Value is > 0 and < 130)?.Value
                    ?? list.FirstOrDefault(s => Is(s, "Temperature", "GPU Hot Spot") && s.Value is > 0 and < 130)?.Value;
                double? dedicated = list.FirstOrDefault(s => Is(s, "SmallData", "GPU Memory Used"))?.Value
                    ?? list.FirstOrDefault(s => Is(s, "SmallData", "D3D Dedicated Memory Used"))?.Value;
                double? shared = list.FirstOrDefault(s => Is(s, "SmallData", "D3D Shared Memory Used"))?.Value;
                double? clock = list.FirstOrDefault(s => Is(s, "Clock", "GPU Core") && s.Value > 0)?.Value;
                return new HardwareGpuReading(gpuName, Clamp(gpuLoad), gpuTemp, dedicated, shared, clock);
            }).ToList();

        return new HardwareReading(Clamp(load),
            clocks.Count > 0 ? clocks.Average() / 1000.0 : null, clocks.Count > 0 ? clocks.Max() / 1000.0 : null,
            temperature?.Value, temperature?.Sensor, used, available, gpus, pawnIo, now);
    }

    private static double? Clamp(double? percent) => percent is double p ? Math.Clamp(p, 0, 100) : null;

    // ------------------------------------------------------------------ substituição das leituras do Windows

    public static CpuSample Apply(CpuSample windows, HardwareReading? hw)
    {
        if (hw is null || (hw.CpuLoadPercent is null && hw.CpuAverageGHz is null)) return windows;
        return windows with
        {
            UsagePercent = hw.CpuLoadPercent ?? windows.UsagePercent,
            CurrentGHz = hw.CpuAverageGHz ?? windows.CurrentGHz,
            PeakCoreGHz = hw.CpuPeakGHz ?? windows.PeakCoreGHz,
            Source = HardwareReading.SourceName,
        };
    }

    public static RamSample Apply(RamSample windows, HardwareReading? hw)
    {
        if (hw?.RamUsedGB is not double used || hw.RamAvailableGB is not double available) return windows;
        const double Gb = 1024d * 1024 * 1024;
        ulong total = (ulong)((used + available) * Gb), free = (ulong)(available * Gb);
        if (total == 0) return windows;
        return windows with { TotalBytes = total, AvailableBytes = free, MemoryLoad = (uint)Math.Round(used * 100 / (used + available)) };
    }

    /// <summary>GPU do LibreHardwareMonitor correspondente ao adaptador do Windows (mesmo nome; sem ambiguidade).</summary>
    public static HardwareGpuReading? Match(HardwareReading? hw, GpuAdapterInfo? adapter)
    {
        if (hw is null || adapter is null) return null;
        var same = hw.Gpus.Where(g => Normalize(g.Name) == Normalize(adapter.Name)).ToList();
        if (same.Count == 1) return same[0];
        var contains = hw.Gpus.Where(g => Normalize(adapter.Name).Contains(Normalize(g.Name)) || Normalize(g.Name).Contains(Normalize(adapter.Name))).ToList();
        return contains.Count == 1 ? contains[0] : null;
    }

    public static GpuSample Apply(GpuSample windows, HardwareReading? hw)
    {
        var gpu = Match(hw, windows.Adapter);
        if (gpu is null) return windows;
        const double Mb = 1024d * 1024;
        return windows with
        {
            UtilizationPercent = gpu.LoadPercent ?? windows.UtilizationPercent,
            DedicatedUsedBytes = gpu.DedicatedUsedMB is double d && !windows.Adapter!.IsIntegrated ? (ulong)(d * Mb) : windows.DedicatedUsedBytes,
            SharedUsedBytes = gpu.SharedUsedMB is double s ? (ulong)(s * Mb) : windows.SharedUsedBytes,
        };
    }

    public static TemperatureSample Apply(TemperatureSample windows, HardwareReading? hw, GpuAdapterInfo? adapter)
    {
        if (hw is null) return windows;
        var gpu = Match(hw, adapter);
        return windows with
        {
            Cpu = hw.CpuCelsius ?? windows.Cpu,
            Gpu = gpu?.Celsius ?? windows.Gpu,
            CpuSource = hw.CpuCelsius is null ? windows.CpuSource : $"{HardwareReading.SourceName} ({hw.CpuTemperatureSensor})",
        };
    }

    private static string Normalize(string name) => string.Join(' ', name.Replace("(R)", "", StringComparison.OrdinalIgnoreCase)
        .Replace("(TM)", "", StringComparison.OrdinalIgnoreCase).Split(' ', StringSplitOptions.RemoveEmptyEntries)).ToUpperInvariant();
}
