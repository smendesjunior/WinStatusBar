using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace WinStatusBar.Core.Metrics;

public sealed record WifiConnection(string Ssid, string Standard, int SignalPercent, int ReceiveMbps, int TransmitMbps);

public sealed record AdapterDetails(string Alias, string Description, bool IsWireless, string? DnsSuffix, string? IPv4, string? IPv6, long LinkBitsPerSecond, WifiConnection? Wifi);

/// <summary>
/// Detalhes do adaptador ativo para o painel de rede (como a página Wi-Fi/Ethernet do Gerenciador de Tarefas):
/// endereços e sufixo DNS (System.Net.NetworkInformation) e, no Wi-Fi, SSID, padrão 802.11 e sinal pela WLAN API nativa.
/// </summary>
public static class NetworkDetails
{
    public static AdapterDetails? Read(string alias)
    {
        try
        {
            var nic = NetworkInterface.GetAllNetworkInterfaces().FirstOrDefault(n => n.Name.Equals(alias, StringComparison.OrdinalIgnoreCase));
            if (nic is null) return null;
            var ip = nic.GetIPProperties();
            string? v4 = ip.UnicastAddresses.FirstOrDefault(a => a.Address.AddressFamily == AddressFamily.InterNetwork)?.Address.ToString();
            // IPv6 global preferido (não temporário/link-local), como o Gerenciador de Tarefas.
            var v6s = ip.UnicastAddresses.Where(a => a.Address.AddressFamily == AddressFamily.InterNetworkV6 && !a.Address.IsIPv6LinkLocal).ToList();
            string? v6 = (v6s.FirstOrDefault(a => a.SuffixOrigin != SuffixOrigin.Random) ?? v6s.FirstOrDefault())?.Address.ToString();
            bool wireless = nic.NetworkInterfaceType == NetworkInterfaceType.Wireless80211;
            WifiConnection? wifi = wireless && Guid.TryParse(nic.Id, out var id) ? Wifi(id) : null;
            long speed = 0; try { speed = nic.Speed; } catch { }
            return new AdapterDetails(nic.Name, nic.Description, wireless, string.IsNullOrWhiteSpace(ip.DnsSuffix) ? null : ip.DnsSuffix, v4, v6, speed, wifi);
        }
        catch { return null; }
    }

    // ------------------------------------------------------------------ WLAN API
    public static WifiConnection? Wifi(Guid interfaceId)
    {
        IntPtr handle = IntPtr.Zero, data = IntPtr.Zero;
        try
        {
            if (WlanOpenHandle(2, IntPtr.Zero, out _, out handle) != 0) return null;
            if (WlanQueryInterface(handle, ref interfaceId, 7 /* wlan_intf_opcode_current_connection */, IntPtr.Zero, out _, out data, IntPtr.Zero) != 0 || data == IntPtr.Zero)
                return null;
            // WLAN_CONNECTION_ATTRIBUTES: isState(4) mode(4) profile WCHAR[256](512) → WLAN_ASSOCIATION_ATTRIBUTES em 520.
            const int assoc = 520;
            int ssidLength = Math.Clamp(Marshal.ReadInt32(data, assoc), 0, 32);
            var ssidBytes = new byte[ssidLength];
            Marshal.Copy(data + assoc + 4, ssidBytes, 0, ssidLength);
            // DOT11_SSID(36) + bssType(4) + bssid(6) + 2 de alinhamento → phyType em +48.
            int phy = Marshal.ReadInt32(data, assoc + 48);
            int signal = Marshal.ReadInt32(data, assoc + 56);
            int rx = Marshal.ReadInt32(data, assoc + 60), tx = Marshal.ReadInt32(data, assoc + 64); // kbps
            string standard = phy switch
            {
                4 => "802.11a", 5 => "802.11b", 6 => "802.11g", 7 => "802.11n", 8 => "802.11ac",
                9 => "802.11ad", 10 => "802.11ax", 11 => "802.11be", _ => "802.11",
            };
            return new WifiConnection(System.Text.Encoding.UTF8.GetString(ssidBytes), standard, Math.Clamp(signal, 0, 100), rx / 1000, tx / 1000);
        }
        catch { return null; }
        finally
        {
            if (data != IntPtr.Zero) WlanFreeMemory(data);
            if (handle != IntPtr.Zero) WlanCloseHandle(handle, IntPtr.Zero);
        }
    }

    [DllImport("wlanapi.dll")] private static extern int WlanOpenHandle(uint clientVersion, IntPtr reserved, out uint negotiatedVersion, out IntPtr handle);
    [DllImport("wlanapi.dll")] private static extern int WlanCloseHandle(IntPtr handle, IntPtr reserved);
    [DllImport("wlanapi.dll")] private static extern void WlanFreeMemory(IntPtr memory);
    [DllImport("wlanapi.dll")]
    private static extern int WlanQueryInterface(IntPtr handle, ref Guid interfaceGuid, int opCode, IntPtr reserved, out int dataSize, out IntPtr data, IntPtr opcodeValueType);
}
