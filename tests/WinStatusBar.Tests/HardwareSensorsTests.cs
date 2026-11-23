using WinStatusBar.Core.Ipc;
using WinStatusBar.Core.Metrics;
using Xunit;

namespace WinStatusBar.Tests;

/// <summary>Sensores de hardware (LibreHardwareMonitor): resumo dos sensores e substituição das leituras do Windows.</summary>
public class HardwareSensorsTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 23, 12, 0, 0, TimeSpan.Zero);

    private static HardwareSensorValue V(string hwType, string hw, string type, string name, double value) => new(hwType, hw, type, name, value);

    private static List<HardwareSensorValue> Laptop() =>
    [
        V("Cpu", "Intel Core i7-13700H", "Load", "CPU Total", 49),
        V("Cpu", "Intel Core i7-13700H", "Clock", "Bus Speed", 100),
        V("Cpu", "Intel Core i7-13700H", "Clock", "P-Core #1", 4800),
        V("Cpu", "Intel Core i7-13700H", "Clock", "P-Core #2", 4600),
        V("Cpu", "Intel Core i7-13700H", "Clock", "E-Core #1", 3800),
        V("Cpu", "Intel Core i7-13700H", "Clock", "Cores (Average Effective)", 1200),
        V("Cpu", "Intel Core i7-13700H", "Temperature", "Core Max", 85),
        V("Cpu", "Intel Core i7-13700H", "Temperature", "CPU Package", 82),
        V("Memory", "Total Memory", "Data", "Memory Used", 12),
        V("Memory", "Total Memory", "Data", "Memory Available", 4),
        V("Memory", "Virtual Memory", "Data", "Memory Used", 30),
        V("GpuNvidia", "NVIDIA GeForce RTX 4060 Laptop GPU", "Load", "GPU Core", 37),
        V("GpuNvidia", "NVIDIA GeForce RTX 4060 Laptop GPU", "Temperature", "GPU Core", 64),
        V("GpuNvidia", "NVIDIA GeForce RTX 4060 Laptop GPU", "SmallData", "GPU Memory Used", 2048),
        V("GpuIntel", "Intel(R) UHD Graphics", "Load", "D3D 3D", 5),
        V("GpuIntel", "Intel(R) UHD Graphics", "SmallData", "D3D Shared Memory Used", 512),
    ];

    [Fact]
    public void SummarizesCpuAverageAndPeakFromCoreClocksOnly()
    {
        var r = HardwareSensors.Summarize(Laptop(), pawnIo: true, Now);
        Assert.Equal(4.4, r.CpuAverageGHz!.Value, 3); // (4800 + 4600 + 3800) / 3, sem barramento nem média efetiva
        Assert.Equal(4.8, r.CpuPeakGHz!.Value, 3);
        Assert.Equal(49, r.CpuLoadPercent);
        Assert.Equal(82, r.CpuCelsius);                // "CPU Package" vem antes de "Core Max"
        Assert.Equal("CPU Package", r.CpuTemperatureSensor);
        Assert.Equal(12, r.RamUsedGB);                 // nunca a memória virtual
        Assert.Equal(4, r.RamAvailableGB);
        Assert.Equal(2, r.Gpus.Count);
        Assert.True(r.PawnIo);
    }

    [Fact]
    public void AmdTemperatureNameIsRecognized()
    {
        var r = HardwareSensors.Summarize([V("Cpu", "AMD Ryzen 7", "Temperature", "Core (Tctl/Tdie)", 70)], false, Now);
        Assert.Equal(70, r.CpuCelsius);
        Assert.Null(r.CpuAverageGHz);
    }

    [Fact]
    public void WithoutCpuClocksWindowsFrequencyIsKept()
    {
        var hw = HardwareSensors.Summarize([V("Memory", "Total Memory", "Data", "Memory Used", 1)], false, Now);
        var windows = new CpuSample(20, 2.4, "CPU", 20, "PDH", 3.0);
        Assert.Same(windows, HardwareSensors.Apply(windows, hw));
    }

    [Fact]
    public void ReplacesCpuRamGpuAndTemperatures()
    {
        var hw = HardwareSensors.Summarize(Laptop(), true, Now);
        var cpu = HardwareSensors.Apply(new CpuSample(30, 2.4, "CPU", 20, "PDH", 3.0), hw);
        Assert.Equal((49d, 4.4, 4.8, HardwareReading.SourceName), (cpu.UsagePercent!.Value, Math.Round(cpu.CurrentGHz!.Value, 3), cpu.PeakCoreGHz!.Value, cpu.Source));

        var ram = HardwareSensors.Apply(new RamSample(10, 5, 16, 50), hw);
        Assert.Equal(16UL * 1024 * 1024 * 1024, ram.TotalBytes);
        Assert.Equal(4UL * 1024 * 1024 * 1024, ram.AvailableBytes);
        Assert.Equal(16UL, ram.InstalledBytes);
        Assert.Equal(75u, ram.MemoryLoad);

        var nvidia = new GpuAdapterInfo("NVIDIA GeForce RTX 4060 Laptop GPU", 0x10DE, 1, 8UL << 30, 8UL << 30, false);
        var gpu = HardwareSensors.Apply(new GpuSample(nvidia, 1, 0, 10, [nvidia]), hw);
        Assert.Equal(37, gpu.UtilizationPercent);
        Assert.Equal(2048UL * 1024 * 1024, gpu.DedicatedUsedBytes);

        var t = HardwareSensors.Apply(new TemperatureSample(70, 50, "Sensor Lenovo"), hw, nvidia);
        Assert.Equal((82d, 64d), (t.Cpu!.Value, t.Gpu!.Value));
        Assert.Contains("LibreHardwareMonitor", t.CpuSource);
    }

    [Fact]
    public void IntegratedGpuMatchesIgnoringTrademarksAndKeepsDedicated()
    {
        var hw = HardwareSensors.Summarize(Laptop(), true, Now);
        var intel = new GpuAdapterInfo("Intel(R) UHD Graphics", 0x8086, 2, 128UL << 20, 8UL << 30, true);
        var gpu = HardwareSensors.Apply(new GpuSample(intel, 7, 100, 1, [intel]), hw);
        Assert.Equal(5, gpu.UtilizationPercent);
        Assert.Equal(7UL, gpu.DedicatedUsedBytes);
        Assert.Equal(512UL * 1024 * 1024, gpu.SharedUsedBytes);
    }

    [Fact]
    public void UnknownAdapterKeepsWindowsValues()
    {
        var hw = HardwareSensors.Summarize(Laptop(), true, Now);
        var other = new GpuAdapterInfo("Microsoft Basic Render Driver", 0x1414, 3, 0, 0, true);
        var windows = new GpuSample(other, 1, 2, 3, [other]);
        Assert.Same(windows, HardwareSensors.Apply(windows, hw));
        var t = new TemperatureSample(null, null);
        Assert.Null(HardwareSensors.Apply(t, null, other).Cpu);
    }

    [Fact]
    public void StaleReadingIsNotFresh()
    {
        var hw = HardwareSensors.Summarize([], false, Now);
        Assert.True(hw.IsFresh(Now.AddSeconds(10)));
        Assert.False(hw.IsFresh(Now.AddSeconds(20)));
        Assert.False(hw.IsFresh(Now.AddSeconds(-10))); // relógio do leitor adiantado
    }

    [Fact]
    public void ReadingRoundTripsThroughThePipeJson()
    {
        var hw = HardwareSensors.Summarize(Laptop(), true, Now);
        string json = System.Text.Json.JsonSerializer.Serialize(hw, HardwareReadingJson.Default.HardwareReading);
        var back = System.Text.Json.JsonSerializer.Deserialize(json, HardwareReadingJson.Default.HardwareReading)!;
        Assert.Equal(hw.CpuAverageGHz, back.CpuAverageGHz);
        Assert.Equal(hw.Gpus.Count, back.Gpus.Count);
        Assert.False(string.IsNullOrEmpty(HardwareSensorPipe.Name));
    }

    [Fact]
    public void TwoIdenticalGpusStaySeparateAndAreAmbiguous()
    {
        var hw = HardwareSensors.Summarize(
        [
            new("GpuNvidia", "NVIDIA RTX A2000", "Load", "GPU Core", 10, "/gpu-nvidia/0"),
            new("GpuNvidia", "NVIDIA RTX A2000", "Load", "GPU Core", 90, "/gpu-nvidia/1"),
        ], false, Now);
        Assert.Equal(2, hw.Gpus.Count);
        Assert.All(hw.Gpus, g => Assert.Equal("NVIDIA RTX A2000", g.Name));
        var adapter = new GpuAdapterInfo("NVIDIA RTX A2000", 0x10DE, 1, 6UL << 30, 0, false);
        Assert.Null(HardwareSensors.Match(hw, adapter)); // sem como saber qual é qual: fica com o Windows
    }
}
