using System.Diagnostics;
using System.Runtime.InteropServices;
using static WinStatusBar.Core.Interop.NativeMethods;

namespace WinStatusBar.Core.Metrics;

/// <summary>
/// Taxas de rede a partir de GetIfTable2 (IP Helper). Modo automático: soma apenas interfaces físicas ativas
/// (HardwareInterface, sem FilterInterface, conector presente), evitando contar duas vezes o tráfego
/// que passa por VPN, Hyper-V, filtros de rede e adaptadores virtuais.
/// </summary>
public sealed class NetworkMetricsProvider : ISystemMetricsProvider<NetworkSample>
{
    private const uint IF_TYPE_SOFTWARE_LOOPBACK = 24;
    private const uint IF_TYPE_TUNNEL = 131;
    private const uint IfOperStatusUp = 1;

    private readonly Dictionary<ulong, (ulong inOct, ulong outOct)> _last = new();
    private readonly Stopwatch _clock = new();
    private double _lastTime;

    /// <summary>Alias da interface escolhida manualmente; vazio = automático.</summary>
    public string? ManualInterface { get; set; }

    internal readonly record struct Row(NetworkInterfaceInfo Info, ulong InOctets, ulong OutOctets, bool ConnectorPresent, bool Filter);

    public NetworkMetricsProvider() { _clock.Start(); Sample(); }

    public NetworkSample Sample()
    {
        var rows = ReadTable();
        double now = _clock.Elapsed.TotalSeconds;
        double dt = now - _lastTime;
        var selected = Select(rows, ManualInterface).ToList();

        double up = 0, down = 0; bool valid = _last.Count > 0 && dt > 0.05;
        var seen = new HashSet<ulong>();
        foreach (var r in selected)
        {
            seen.Add(r.Info.Luid);
            if (_last.TryGetValue(r.Info.Luid, out var prev))
            {
                down += Delta(prev.inOct, r.InOctets);
                up += Delta(prev.outOct, r.OutOctets);
            }
            _last[r.Info.Luid] = (r.InOctets, r.OutOctets);
        }
        foreach (var key in _last.Keys.Where(k => !seen.Contains(k)).ToList()) _last.Remove(key);
        _lastTime = now;

        return new NetworkSample(
            valid ? up / dt : null,
            valid ? down / dt : null,
            selected.Select(r => r.Info.Alias).ToList(),
            rows.Select(r => r.Info).ToList());
    }

    /// <summary>Diferença tolerante a reinício/estouro de contador (nesse caso a amostra é descartada).</summary>
    internal static ulong Delta(ulong previous, ulong current) => current >= previous ? current - previous : 0;

    internal static IEnumerable<Row> Select(IEnumerable<Row> rows, string? manual)
    {
        // Interface escolhida manualmente, se estiver conectada; desconectada (ex.: cabo fora), volta ao automático
        // para não mostrar 0 KB/s e o ícone errado enquanto o tráfego passa pelo Wi-Fi.
        if (!string.IsNullOrWhiteSpace(manual))
        {
            var chosen = rows.Where(r => r.Info.Alias.Equals(manual, StringComparison.OrdinalIgnoreCase)).ToList();
            if (chosen.Any(r => r.Info.IsUp)) return chosen;
        }
        return rows.Where(r => r.Info.IsUp && r.Info.IsHardware && !r.Filter && r.ConnectorPresent
                               && r.Info.Type != IF_TYPE_SOFTWARE_LOOPBACK && r.Info.Type != IF_TYPE_TUNNEL);
    }

    public static IReadOnlyList<NetworkInterfaceInfo> SelectableInterfaces(IEnumerable<NetworkInterfaceInfo> interfaces) =>
        interfaces.Where(n => n.IsHardware && !n.IsFilter && n.ConnectorPresent && n.Type is 6 or 71 or 243 or 244)
            .DistinctBy(n => n.Luid).OrderByDescending(n => n.IsUp).ThenBy(n => n.Alias).ToList();

    internal static List<Row> ReadTable()
    {
        var rows = new List<Row>();
        if (GetIfTable2(out var table) != 0 || table == IntPtr.Zero) return rows;
        try
        {
            int count = Marshal.ReadInt32(table);
            for (int i = 0; i < count; i++)
            {
                IntPtr p = table + IfRow2.TableHeader + i * IfRow2.Size;
                ulong luid = (ulong)Marshal.ReadInt64(p, IfRow2.InterfaceLuid);
                string alias = Marshal.PtrToStringUni(p + IfRow2.Alias) ?? string.Empty;
                string desc = Marshal.PtrToStringUni(p + IfRow2.Description) ?? string.Empty;
                uint type = (uint)Marshal.ReadInt32(p, IfRow2.Type);
                byte flags = Marshal.ReadByte(p, IfRow2.Flags);
                uint oper = (uint)Marshal.ReadInt32(p, IfRow2.OperStatus);
                ulong inOct = (ulong)Marshal.ReadInt64(p, IfRow2.InOctets);
                ulong outOct = (ulong)Marshal.ReadInt64(p, IfRow2.OutOctets);
                bool hardware = (flags & 0x01) != 0;
                bool filter = (flags & 0x02) != 0;
                bool connector = (flags & 0x04) != 0;
                rows.Add(new Row(new NetworkInterfaceInfo(luid, alias, desc, type, hardware, oper == IfOperStatusUp, filter, connector), inOct, outOct, connector, filter));
            }
        }
        finally { FreeMibTable(table); }
        return rows;
    }

    public void Dispose() { }
}
