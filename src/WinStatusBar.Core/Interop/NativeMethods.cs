using System.Runtime.InteropServices;

namespace WinStatusBar.Core.Interop;

/// <summary>P/Invoke de kernel32, pdh, iphlpapi e dxgi usados pelos coletores.</summary>
internal static unsafe partial class NativeMethods
{
    // ---------------- kernel32 ----------------
    [StructLayout(LayoutKind.Sequential)]
    internal struct FILETIME { public uint Low; public uint High; public readonly ulong Value => ((ulong)High << 32) | Low; }

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern bool GetSystemTimes(out FILETIME idle, out FILETIME kernel, out FILETIME user);

    [StructLayout(LayoutKind.Sequential)]
    internal struct MEMORYSTATUSEX
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX buffer);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern bool GetPhysicallyInstalledSystemMemory(out ulong totalKilobytes);

    // ---------------- pdh ----------------
    internal const uint PDH_FMT_DOUBLE = 0x00000200;
    internal const uint PDH_FMT_NOCAP100 = 0x00008000;
    internal const uint PDH_MORE_DATA = 0x800007D2;
    internal const uint ERROR_SUCCESS = 0;

    [StructLayout(LayoutKind.Sequential)]
    internal struct PDH_FMT_COUNTERVALUE { public uint CStatus; public double doubleValue; }

    [StructLayout(LayoutKind.Sequential)]
    internal struct PDH_FMT_COUNTERVALUE_ITEM { public IntPtr szName; public PDH_FMT_COUNTERVALUE FmtValue; }

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    internal static extern uint PdhOpenQueryW(string? dataSource, IntPtr userData, out IntPtr query);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    internal static extern uint PdhAddEnglishCounterW(IntPtr query, string path, IntPtr userData, out IntPtr counter);

    [DllImport("pdh.dll")]
    internal static extern uint PdhCollectQueryData(IntPtr query);

    [DllImport("pdh.dll")]
    internal static extern uint PdhGetFormattedCounterValue(IntPtr counter, uint format, out uint type, out PDH_FMT_COUNTERVALUE value);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    internal static extern uint PdhGetFormattedCounterArrayW(IntPtr counter, uint format, ref uint bufferSize, out uint itemCount, IntPtr buffer);

    [DllImport("pdh.dll")]
    internal static extern uint PdhCloseQuery(IntPtr query);

    // ---------------- iphlpapi ----------------
    [DllImport("iphlpapi.dll")]
    internal static extern uint GetIfTable2(out IntPtr table);

    [DllImport("iphlpapi.dll")]
    internal static extern void FreeMibTable(IntPtr table);

    /// <summary>Tamanho e deslocamentos de MIB_IF_ROW2 (x64, netioapi.h).</summary>
    internal static class IfRow2
    {
        public const int Size = 1352;
        public const int InterfaceLuid = 0;
        public const int InterfaceIndex = 8;
        public const int Alias = 28;          // WCHAR[257]
        public const int Description = 542;   // WCHAR[257]
        public const int Type = 1128;
        public const int Flags = 1152;        // InterfaceAndOperStatusFlags (BOOLEAN bitfield)
        public const int OperStatus = 1156;
        public const int MediaConnectState = 1164;
        public const int TransmitLinkSpeed = 1192;
        public const int ReceiveLinkSpeed = 1200;
        public const int InOctets = 1208;
        public const int OutOctets = 1280;
        public const int TableHeader = 8;     // ULONG NumEntries + alinhamento
    }

    // ---------------- TCP (dono de cada conexão) ----------------
    internal const int AF_INET = 2, AF_INET6 = 23;
    internal const int TCP_TABLE_OWNER_PID_ALL = 5;
    internal const uint MIB_TCP_STATE_LISTEN = 2, MIB_TCP_STATE_ESTAB = 5;

    [DllImport("iphlpapi.dll")]
    internal static extern uint GetExtendedTcpTable(IntPtr table, ref int size, bool order, int af, int tableClass, uint reserved);

    // ---------------- Processos ----------------
    internal const uint TH32CS_SNAPPROCESS = 2;
    internal const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct PROCESSENTRY32W
    {
        public uint dwSize, cntUsage, th32ProcessID;
        public IntPtr th32DefaultHeapID;
        public uint th32ModuleID, cntThreads, th32ParentProcessID;
        public int pcPriClassBase;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szExeFile;
    }

    [DllImport("kernel32.dll", SetLastError = true)] internal static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint pid);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] internal static extern bool Process32FirstW(IntPtr snap, ref PROCESSENTRY32W e);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] internal static extern bool Process32NextW(IntPtr snap, ref PROCESSENTRY32W e);
    [DllImport("kernel32.dll")] internal static extern bool CloseHandle(IntPtr h);
    [DllImport("kernel32.dll")] internal static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
    [DllImport("kernel32.dll")] internal static extern bool GetProcessTimes(IntPtr h, out FILETIME creation, out FILETIME exit, out FILETIME kernel, out FILETIME user);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] internal static extern bool QueryFullProcessImageNameW(IntPtr h, uint flags, System.Text.StringBuilder name, ref int size);

    // ---------------- dxgi ----------------
    [DllImport("dxgi.dll")]
    internal static extern int CreateDXGIFactory1(in Guid riid, out IntPtr factory);

    internal static readonly Guid IID_IDXGIFactory1 = new("770aae78-f26f-4dba-a829-253c83d1b387");

    /// <summary>DXGI_ADAPTER_DESC1 (312 bytes em x64).</summary>
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct DXGI_ADAPTER_DESC1
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Description;
        public uint VendorId;
        public uint DeviceId;
        public uint SubSysId;
        public uint Revision;
        public nuint DedicatedVideoMemory;
        public nuint DedicatedSystemMemory;
        public nuint SharedSystemMemory;
        public uint LuidLow;
        public int LuidHigh;
        public uint Flags;
    }

    internal const uint DXGI_ADAPTER_FLAG_SOFTWARE = 2;
    internal const int DXGI_ERROR_NOT_FOUND = unchecked((int)0x887A0002);

    /// <summary>IDXGIFactory1::EnumAdapters1 = vtable 12; IDXGIAdapter1::GetDesc1 = vtable 10; Release = 2.</summary>
    internal static int EnumAdapters1(IntPtr factory, uint index, out IntPtr adapter)
    {
        var vtbl = *(IntPtr**)factory;
        var fn = (delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr*, int>)vtbl[12];
        IntPtr result;
        int hr = fn(factory, index, &result);
        adapter = result;
        return hr;
    }

    internal static bool IsFactoryCurrent(IntPtr factory)
    {
        if (factory == IntPtr.Zero) return false;
        var vtbl = *(IntPtr**)factory;
        var fn = (delegate* unmanaged[Stdcall]<IntPtr, int>)vtbl[13];
        return fn(factory) != 0;
    }

    internal static int GetDesc1(IntPtr adapter, out DXGI_ADAPTER_DESC1 desc)
    {
        var vtbl = *(IntPtr**)adapter;
        var fn = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, int>)vtbl[10];
        IntPtr buffer = Marshal.AllocHGlobal(512);
        try
        {
            int hr = fn(adapter, buffer);
            desc = hr >= 0 ? Marshal.PtrToStructure<DXGI_ADAPTER_DESC1>(buffer) : default;
            return hr;
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    internal static void Release(IntPtr unknown)
    {
        if (unknown == IntPtr.Zero) return;
        var vtbl = *(IntPtr**)unknown;
        var fn = (delegate* unmanaged[Stdcall]<IntPtr, uint>)vtbl[2];
        fn(unknown);
    }
}
