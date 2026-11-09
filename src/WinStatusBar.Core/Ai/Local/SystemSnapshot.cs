using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using static WinStatusBar.Core.Interop.NativeMethods;

namespace WinStatusBar.Core.Ai.Local;

public readonly record struct ProcInfo(uint Pid, uint ParentPid, string Name);

/// <summary>Conexão TCP com o processo dono (GetExtendedTcpTable, IPv4 e IPv6).</summary>
public readonly record struct TcpConn(uint Pid, int LocalPort, int RemotePort, bool Listening, bool Established, bool Loopback);

/// <summary>Retrato instantâneo dos processos (Toolhelp) e das conexões TCP, usado para atribuir clientes aos servidores locais.</summary>
public sealed class SystemSnapshot
{
    public required IReadOnlyDictionary<uint, ProcInfo> Processes { get; init; }
    public required IReadOnlyList<TcpConn> Connections { get; init; }

    public static SystemSnapshot Capture(bool includeTcp)
        => new() { Processes = ReadProcesses(), Connections = includeTcp ? ReadTcp() : [] };

    public static Dictionary<uint, ProcInfo> ReadProcesses()
    {
        var map = new Dictionary<uint, ProcInfo>();
        IntPtr snap = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
        if (snap == IntPtr.Zero || snap == new IntPtr(-1)) return map;
        try
        {
            var e = new PROCESSENTRY32W { dwSize = (uint)Marshal.SizeOf<PROCESSENTRY32W>() };
            if (!Process32FirstW(snap, ref e)) return map;
            do
            {
                string name = e.szExeFile ?? string.Empty;
                if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) name = name[..^4];
                map[e.th32ProcessID] = new ProcInfo(e.th32ProcessID, e.th32ParentProcessID, name);
            } while (Process32NextW(snap, ref e));
        }
        finally { CloseHandle(snap); }
        return map;
    }

    public static List<TcpConn> ReadTcp()
    {
        var list = new List<TcpConn>();
        ReadTable(AF_INET, list);
        ReadTable(AF_INET6, list);
        return list;
    }

    private static void ReadTable(int af, List<TcpConn> list)
    {
        int size = 0;
        GetExtendedTcpTable(IntPtr.Zero, ref size, false, af, TCP_TABLE_OWNER_PID_ALL, 0);
        if (size <= 0) return;
        IntPtr buf = Marshal.AllocHGlobal(size);
        try
        {
            if (GetExtendedTcpTable(buf, ref size, false, af, TCP_TABLE_OWNER_PID_ALL, 0) != 0) return;
            int count = Marshal.ReadInt32(buf);
            int rowSize = af == AF_INET ? 24 : 56;
            for (int i = 0; i < count; i++)
            {
                IntPtr r = buf + 4 + i * rowSize;
                uint state, pid; int lport, rport; bool loop;
                if (af == AF_INET)
                {
                    // MIB_TCPROW_OWNER_PID: state, localAddr, localPort, remoteAddr, remotePort, pid
                    state = (uint)Marshal.ReadInt32(r, 0);
                    uint laddr = (uint)Marshal.ReadInt32(r, 4);
                    lport = Port(Marshal.ReadInt32(r, 8));
                    uint raddr = (uint)Marshal.ReadInt32(r, 12);
                    rport = Port(Marshal.ReadInt32(r, 16));
                    pid = (uint)Marshal.ReadInt32(r, 20);
                    loop = (laddr & 0xFF) == 127 || laddr == 0 || (raddr & 0xFF) == 127;
                }
                else
                {
                    // MIB_TCP6ROW_OWNER_PID: localAddr[16], scope, localPort, remoteAddr[16], scope, remotePort, state, pid
                    var la = new byte[16]; Marshal.Copy(r, la, 0, 16);
                    lport = Port(Marshal.ReadInt32(r, 20));
                    var ra = new byte[16]; Marshal.Copy(r + 24, ra, 0, 16);
                    rport = Port(Marshal.ReadInt32(r, 44));
                    state = (uint)Marshal.ReadInt32(r, 48);
                    pid = (uint)Marshal.ReadInt32(r, 52);
                    loop = IPAddress.IsLoopback(new IPAddress(la)) || IPAddress.IsLoopback(new IPAddress(ra)) || la.All(b => b == 0);
                }
                list.Add(new TcpConn(pid, lport, rport, state == MIB_TCP_STATE_LISTEN, state == MIB_TCP_STATE_ESTAB, loop));
            }
        }
        finally { Marshal.FreeHGlobal(buf); }
    }

    /// <summary>Porta em ordem de rede (big-endian) nos 16 bits inferiores.</summary>
    internal static int Port(int raw) => ((raw & 0xFF) << 8) | ((raw >> 8) & 0xFF);

    /// <summary>Tempo de CPU acumulado (kernel + usuário, em segundos) de um processo; null se inacessível.</summary>
    public static double? CpuSeconds(uint pid)
    {
        IntPtr h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (h == IntPtr.Zero) return null;
        try { return GetProcessTimes(h, out _, out _, out var k, out var u) ? (k.Value + u.Value) / 1e7 : null; }
        finally { CloseHandle(h); }
    }

    public static string? ImagePath(uint pid)
    {
        IntPtr h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (h == IntPtr.Zero) return null;
        try
        {
            var sb = new StringBuilder(1024); int size = sb.Capacity;
            return QueryFullProcessImageNameW(h, 0, sb, ref size) ? sb.ToString() : null;
        }
        finally { CloseHandle(h); }
    }
}
