using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using System.Xml.Linq;
namespace WinStatusBar.Core.Metrics;

public sealed record WifiNetwork(Guid Adapter, string Name, byte[] Ssid, string Profile, int Signal, bool Connected, bool Secure, int Auth, int Cipher, bool Connectable);
public static class WifiNetworks
{
    public static IReadOnlyList<WifiNetwork> Read(bool scan)
    {
        Check(WlanOpenHandle(2, IntPtr.Zero, out _, out var handle));
        try
        {
            Check(WlanEnumInterfaces(handle, IntPtr.Zero, out var interfaces));
            try
            {
                var result = new List<WifiNetwork>();
                for (int i = 0; i < Marshal.ReadInt32(interfaces); i++)
                {
                    var id = Marshal.PtrToStructure<Guid>(interfaces + 8 + i * 532);
                    if (scan) { var code = WlanScan(handle, ref id, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero); if (code == 5) Check(code); }
                    Check(WlanGetAvailableNetworkList(handle, ref id, 0, IntPtr.Zero, out var networks));
                    try
                    {
                        for (int j = 0; j < Marshal.ReadInt32(networks); j++)
                        {
                            var n = Marshal.PtrToStructure<Available>(networks + 8 + j * Marshal.SizeOf<Available>());
                            var ssid = n.Ssid.Bytes.Take((int)Math.Min(n.Ssid.Length, 32)).ToArray();
                            if (ssid.Length == 0) continue;
                            result.Add(new(id, Encoding.UTF8.GetString(ssid), ssid, n.Profile, (int)n.Signal, (n.Flags & 1) != 0, n.Security != 0, n.Auth, n.Cipher, n.Connectable != 0));
                        }
                    }
                    finally { WlanFreeMemory(networks); }
                }
                return result.GroupBy(n => (n.Adapter, Convert.ToHexString(n.Ssid), n.Auth)).Select(g => g.OrderByDescending(n => n.Connected).ThenByDescending(n => n.Profile.Length).First()).OrderByDescending(n => n.Connected).ThenByDescending(n => n.Signal).ToList();
            }
            finally { WlanFreeMemory(interfaces); }
        }
        finally { WlanCloseHandle(handle, IntPtr.Zero); }
    }
    public static bool NeedsWindowsSetup(WifiNetwork n) => n.Profile.Length == 0 && n.Secure && (n.Auth is not (4 or 7) || n.Cipher is not (2 or 4));
    public static string ProfileXml(WifiNetwork n, string password)
    {
        if (NeedsWindowsSetup(n)) throw new InvalidOperationException("Configure esta rede corporativa ou este tipo de segurança no Windows uma vez. Depois ela poderá ser selecionada aqui.");
        if (n.Secure && (password.Length < 8 || password.Length > 63)) throw new ArgumentException("A senha deve ter entre 8 e 63 caracteres.");
        XNamespace ns = "http://www.microsoft.com/networking/WLAN/profile/v1";
        XElement E(string name, params object[] content) => new(ns + name, content);
        var security = E("security", E("authEncryption", E("authentication", n.Secure ? n.Auth == 4 ? "WPAPSK" : "WPA2PSK" : "open"), E("encryption", n.Secure ? n.Cipher == 2 ? "TKIP" : "AES" : "none"), E("useOneX", "false")));
        if (n.Secure) security.Add(E("sharedKey", E("keyType", "passPhrase"), E("protected", "false"), E("keyMaterial", password)));
        return E("WLANProfile", E("name", n.Name), E("SSIDConfig", E("SSID", E("hex", Convert.ToHexString(n.Ssid)), E("name", n.Name))), E("connectionType", "ESS"), E("connectionMode", "manual"), E("MSM", security)).ToString(SaveOptions.DisableFormatting);
    }
    public static async Task ConnectAsync(WifiNetwork network, string password, CancellationToken ct)
    {
        Check(WlanOpenHandle(2, IntPtr.Zero, out _, out var handle));
        try
        {
            var id = network.Adapter;
            var parameters = new Connection { Mode = network.Profile.Length > 0 ? 0 : 1, Profile = network.Profile.Length > 0 ? network.Profile : ProfileXml(network, password), BssType = 1 };
            Check(WlanConnect(handle, ref id, ref parameters, IntPtr.Zero));
            // WlanConnect accepts a request asynchronously; only a connected list entry confirms success.
            for (int i = 0; i < 30; i++)
            {
                await Task.Delay(1000, ct);
                if (Read(false).Any(n => n.Adapter == id && n.Connected && n.Ssid.SequenceEqual(network.Ssid))) return;
            }
            throw new InvalidOperationException("O Windows não confirmou a conexão. Confira a senha e o sinal e tente novamente.");
        }
        finally { WlanCloseHandle(handle, IntPtr.Zero); }
    }
    private static void Check(uint code)
    {
        if (code == 5) throw new InvalidOperationException("O Windows negou acesso às redes. Confira a permissão de localização para aplicativos da área de trabalho nas configurações do Windows.");
        if (code != 0) throw new Win32Exception((int)code);
    }
    [StructLayout(LayoutKind.Sequential)] private struct Ssid { public uint Length; [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)] public byte[] Bytes; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct Available
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string Profile;
        public Ssid Ssid; public int BssType; public uint Bssids; public int Connectable; public uint Reason; public uint PhyCount;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 8)] public int[] Phy;
        public int MorePhy; public uint Signal; public int Security, Auth, Cipher; public uint Flags, Reserved;
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct Connection
    {
        public int Mode; [MarshalAs(UnmanagedType.LPWStr)] public string Profile; public IntPtr Ssid, Bssid; public int BssType; public uint Flags;
    }
    [DllImport("wlanapi.dll")] private static extern uint WlanOpenHandle(uint version, IntPtr reserved, out uint negotiated, out IntPtr handle);
    [DllImport("wlanapi.dll")] private static extern uint WlanCloseHandle(IntPtr handle, IntPtr reserved);
    [DllImport("wlanapi.dll")] private static extern void WlanFreeMemory(IntPtr pointer);
    [DllImport("wlanapi.dll")] private static extern uint WlanEnumInterfaces(IntPtr handle, IntPtr reserved, out IntPtr list);
    [DllImport("wlanapi.dll")] private static extern uint WlanScan(IntPtr handle, ref Guid id, IntPtr ssid, IntPtr ie, IntPtr reserved);
    [DllImport("wlanapi.dll")] private static extern uint WlanGetAvailableNetworkList(IntPtr handle, ref Guid id, uint flags, IntPtr reserved, out IntPtr list);
    [DllImport("wlanapi.dll", CharSet = CharSet.Unicode)] private static extern uint WlanConnect(IntPtr handle, ref Guid id, ref Connection parameters, IntPtr reserved);
}
