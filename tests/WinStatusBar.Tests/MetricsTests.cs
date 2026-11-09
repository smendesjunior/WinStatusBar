using WinStatusBar.Core.Metrics;
using Xunit;

namespace WinStatusBar.Tests;

public class MetricsTests
{
    [Fact]
    public void CpuUsage_UsesKernelPlusUserMinusIdle()
    {
        // kernel inclui idle: 1000 kernel (600 idle) + 400 user → ocupado = 800 de 1400
        Assert.Equal(800 * 100.0 / 1400, CpuMetricsProvider.ComputeUsage(600, 1000, 400)!.Value, 6);
        Assert.Null(CpuMetricsProvider.ComputeUsage(0, 0, 0));
        Assert.Equal(0, CpuMetricsProvider.ComputeUsage(1000, 1000, 0));
    }

    [Fact]
    public void Ram_UsedIsTotalMinusAvailable()
    {
        var s = new RamSample(64UL << 30, 48UL << 30, 64UL << 30, 25);
        Assert.Equal(16UL << 30, s.UsedBytes);
        Assert.Equal(25, s.UsedPercent, 6);
    }

    [Fact]
    public void Format_UsedTotal_MatchesVisualPattern()
    {
        ulong gib = 1UL << 30;
        Assert.Equal("10,6 / 64,0 GB (17%)", Formatters.UsedTotal((ulong)(10.6 * gib), 64 * gib));
        Assert.Equal("4,2 / 6,0 GB (70%)", Formatters.UsedTotal((ulong)(4.2 * gib), 6 * gib));
    }

    [Theory]
    [InlineData(0, "0,00 KB/s")]
    [InlineData(174, "0,17 KB/s")]
    [InlineData(3789, "3,70 KB/s")]
    [InlineData(1024 * 150, "150 KB/s")]
    [InlineData(1024 * 1024 * 2.5, "2,50 MB/s")]
    [InlineData(1024d * 1024 * 250, "250 MB/s")]
    public void Format_Speed_AutoUnits(double bytesPerSec, string expected) => Assert.Equal(expected, Formatters.Speed(bytesPerSec));

    [Fact]
    public void Network_Delta_HandlesCounterReset()
    {
        Assert.Equal(500UL, NetworkMetricsProvider.Delta(1000, 1500));
        Assert.Equal(0UL, NetworkMetricsProvider.Delta(1500, 10)); // reinício do contador: amostra descartada
    }

    [Fact]
    public void Network_AutoSelection_ExcludesVirtualFilterAndLoopback()
    {
        NetworkMetricsProvider.Row R(string alias, uint type, bool hw, bool up, bool connector, bool filter) =>
            new(new NetworkInterfaceInfo((ulong)alias.GetHashCode(), alias, alias, type, hw, up), 0, 0, connector, filter);
        var rows = new[]
        {
            R("Wi-Fi", 71, true, true, true, false),
            R("Wi-Fi-Npcap", 71, true, true, true, true),      // filtro sobre a mesma placa → contaria em dobro
            R("vEthernet (WSL)", 6, false, true, false, false),  // virtual
            R("VPN", 53, false, true, false, false),             // VPN (tráfego já contado na física)
            R("Loopback", 24, false, true, false, false),
            R("Ethernet", 6, true, false, true, false),          // desconectada
        };
        var selected = NetworkMetricsProvider.Select(rows, null).Select(r => r.Info.Alias).ToList();
        Assert.Equal(new[] { "Wi-Fi" }, selected);
        Assert.Equal(new[] { "VPN" }, NetworkMetricsProvider.Select(rows, "vpn").Select(r => r.Info.Alias));
    }

    [Fact]
    public void Network_ReadsRealInterfaceTable()
    {
        var rows = NetworkMetricsProvider.ReadTable();
        Assert.NotEmpty(rows);
        Assert.Contains(rows, r => r.Info.Type == 24); // loopback sempre existe
        // Validação cruzada com a API gerenciada (que também usa GetIfEntry2): mesmo conjunto de aliases.
        var managed = System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces().Select(n => n.Name).ToHashSet();
        Assert.Contains(rows, r => managed.Contains(r.Info.Alias));
    }

    [Fact]
    public void Gpu_EngineUtilization_SumsProcessesPerEngineAndTakesMax()
    {
        const string luid = "luid_0x00000000_0x0000ABCD";
        var values = new List<KeyValuePair<string, double>>
        {
            new($"pid_10_{luid}_phys_0_eng_0_engtype_3D", 20),
            new($"pid_20_{luid}_phys_0_eng_0_engtype_3D", 15),
            new($"pid_10_{luid}_phys_0_eng_3_engtype_VideoDecode", 30),
            new("pid_10_luid_0x00000000_0x00001111_phys_0_eng_0_engtype_3D", 90), // outro adaptador
        };
        Assert.Equal(35, GpuMetricsProvider.ComputeEngineUtilization(values, luid));
        Assert.Null(GpuMetricsProvider.ComputeEngineUtilization(values, "luid_0x00000000_0x00009999"));
    }

    [Fact]
    public void Gpu_SumForLuid_DoesNotMixAdapters()
    {
        var values = new List<KeyValuePair<string, double>>
        {
            new("luid_0x00000000_0x23019699_phys_0", 500),
            new("luid_0x00000000_0x000108E5_phys_0", 100),
        };
        Assert.Equal(500UL, GpuMetricsProvider.SumForLuid(values, "luid_0x00000000_0x23019699"));
    }

    [Fact]
    public void Gpu_LuidFormatMatchesPdhInstances()
    {
        long luid = ((long)0 << 32) | 0x23019699;
        Assert.Equal("luid_0x00000000_0x23019699", GpuAdapterInfo.FormatLuid(luid));
    }

    [Fact]
    public void Gpu_Choose_PrefersDedicatedThenSetting()
    {
        var igpu = new GpuAdapterInfo("Intel(R) Iris(R) Xe Graphics", 0x8086, 1, 128UL << 20, 32UL << 30, true);
        var dgpu = new GpuAdapterInfo("NVIDIA GeForce RTX 4050 Laptop GPU", 0x10DE, 2, 6UL << 30, 32UL << 30, false);
        Assert.Same(dgpu, GpuMetricsProvider.Choose([igpu, dgpu], null));
        Assert.Same(igpu, GpuMetricsProvider.Choose([igpu, dgpu], "Iris"));
    }

    [Fact]
    public void Gpu_DxgiEnumeratesRealAdapters()
    {
        var adapters = GpuMetricsProvider.EnumerateAdapters();
        Assert.NotEmpty(adapters);
        Assert.All(adapters, a => Assert.False(string.IsNullOrWhiteSpace(a.Name)));
    }
}
