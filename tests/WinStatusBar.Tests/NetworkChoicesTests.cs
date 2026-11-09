using WinStatusBar.Core.Metrics;
using Xunit;
namespace WinStatusBar.Tests;
public class NetworkChoicesTests
{
    [Fact] public void PresentsRealAdaptersIncludingDisconnectedWifiWithoutWindowsFilters()
    {
        NetworkInterfaceInfo[] all = [
            new(1, "Ethernet", "Realtek", 6, true, true),
            new(2, "Ethernet-WFP", "Windows filter", 6, true, true, true),
            new(3, "Ethernet-QoS", "Windows filter", 6, false, true, true),
            new(4, "WAN Miniport", "WAN", 6, false, true),
            new(5, "Wi-Fi", "Intel", 71, true, false),
            new(6, "Loopback", "Loopback", 24, false, true),
            new(7, "vEthernet", "Virtual", 6, false, true) ];
        Assert.Equal(new[] { "Ethernet", "Wi-Fi" }, NetworkMetricsProvider.SelectableInterfaces(all).Select(n => n.Alias));
    }
    [Fact] public void RealMachineChoicesContainOnlyPhysicalAdapters()
    {
        using var network = new NetworkMetricsProvider();
        var choices = NetworkMetricsProvider.SelectableInterfaces(network.Sample().AllInterfaces);
        Assert.All(choices, n => { Assert.True(n.IsHardware); Assert.False(n.IsFilter); Assert.True(n.ConnectorPresent); });
        Assert.DoesNotContain(choices, n => n.Alias.Contains("WFP") || n.Alias.Contains("QoS"));
    }
}
