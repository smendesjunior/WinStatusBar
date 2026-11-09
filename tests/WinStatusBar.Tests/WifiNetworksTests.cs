using System.Text;
using System.Xml.Linq;
using WinStatusBar.Core.Metrics;
using Xunit;
namespace WinStatusBar.Tests;
public class WifiNetworksTests
{
    [Fact] public void TemporaryProfileEscapesSsidAndPasswordWithoutSavingCredentials()
    {
        var network = new WifiNetwork(Guid.NewGuid(), "Office & <wifi>", Encoding.UTF8.GetBytes("Office & <wifi>"), "", 80, false, true, 7, 4, true);
        var xml = XDocument.Parse(WifiNetworks.ProfileXml(network, "a<&secret123"));
        XNamespace ns = "http://www.microsoft.com/networking/WLAN/profile/v1";
        Assert.Equal("a<&secret123", xml.Descendants(ns + "keyMaterial").Single().Value);
        Assert.Equal("WPA2PSK", xml.Descendants(ns + "authentication").Single().Value);
        Assert.Equal(Convert.ToHexString(network.Ssid), xml.Descendants(ns + "hex").Single().Value);
        Assert.Equal("manual", xml.Descendants(ns + "connectionMode").Single().Value);
    }
    [Fact] public void EnterpriseRequiresExistingWindowsProfile()
    {
        var network = new WifiNetwork(Guid.NewGuid(), "Corporate", [1], "", 80, false, true, 6, 4, true);
        Assert.True(WifiNetworks.NeedsWindowsSetup(network));
        Assert.Throws<InvalidOperationException>(() => WifiNetworks.ProfileXml(network, "password"));
        Assert.False(WifiNetworks.NeedsWindowsSetup(network with { Profile = "Saved profile" }));
    }
    [Fact] public void OpenNetworkHasNoKeyMaterial()
    {
        var network = new WifiNetwork(Guid.NewGuid(), "Guest", [1], "", 80, false, false, 1, 0, true);
        Assert.DoesNotContain("sharedKey", WifiNetworks.ProfileXml(network, ""));
    }
}
