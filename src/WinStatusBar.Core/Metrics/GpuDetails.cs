using System.Management;
using System.Runtime.InteropServices;
using static WinStatusBar.Core.Interop.NativeMethods;

namespace WinStatusBar.Core.Metrics;

/// <summary>Dados estáticos de um adaptador para o painel da GPU (lidos uma vez por adaptador e guardados).</summary>
public sealed record GpuStaticInfo(string? DriverVersion, DateTime? DriverDate, string? DirectX, string? Location);

/// <summary>
/// Driver (WMI Win32_VideoController), local físico (Configuration Manager, DEVPKEY_Device_LocationInfo)
/// e nível de recursos do DirectX 12 (D3D12CreateDevice + CheckFeatureSupport), como o Gerenciador de Tarefas.
/// </summary>
public static class GpuDetails
{
    private static readonly Dictionary<long, GpuStaticInfo> Cache = new();

    public static GpuStaticInfo Read(GpuAdapterInfo adapter)
    {
        lock (Cache)
            if (Cache.TryGetValue(adapter.Luid, out var cached)) return cached;
        string? version = null, location = null; DateTime? date = null;
        try
        {
            using var s = new ManagementObjectSearcher("SELECT Name,DriverVersion,DriverDate,PNPDeviceID FROM Win32_VideoController");
            using var found = s.Get();
            foreach (ManagementObject v in found)
                using (v)
                {
                    if (!string.Equals((v["Name"] as string)?.Trim(), adapter.Name, StringComparison.OrdinalIgnoreCase)) continue;
                    version = v["DriverVersion"] as string;
                    if (v["DriverDate"] is string d) try { date = ManagementDateTimeConverter.ToDateTime(d).ToUniversalTime().Date; } catch { } // data do driver é UTC (sem fuso)
                    if (v["PNPDeviceID"] is string id) location = LocationInfo(id);
                }
        }
        catch { }
        var info = new GpuStaticInfo(version, date, DirectXLevel(adapter.Luid), location);
        lock (Cache) Cache[adapter.Luid] = info;
        return info;
    }

    // ------------------------------------------------------------------ local físico
    private static string? LocationInfo(string deviceId)
    {
        try
        {
            if (CM_Locate_DevNodeW(out uint node, deviceId, 0) != 0) return null;
            var key = new DEVPROPKEY { fmtid = new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"), pid = 15 };
            uint size = 0;
            CM_Get_DevNode_PropertyW(node, ref key, out _, null, ref size, 0);
            if (size == 0) return null;
            var buffer = new byte[size];
            if (CM_Get_DevNode_PropertyW(node, ref key, out _, buffer, ref size, 0) != 0) return null;
            return System.Text.Encoding.Unicode.GetString(buffer).TrimEnd('\0');
        }
        catch { return null; }
    }

    [StructLayout(LayoutKind.Sequential)] private struct DEVPROPKEY { public Guid fmtid; public uint pid; }
    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)] private static extern int CM_Locate_DevNodeW(out uint devInst, string deviceId, uint flags);
    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern int CM_Get_DevNode_PropertyW(uint devInst, ref DEVPROPKEY key, out uint type, byte[]? buffer, ref uint size, uint flags);

    // ------------------------------------------------------------------ DirectX
    /// <summary>"12 (FL 12.2)" para o adaptador do LUID; null se o DirectX 12 não estiver disponível.</summary>
    private static string? DirectXLevel(long luid)
    {
        IntPtr factory = IntPtr.Zero, device = IntPtr.Zero;
        try
        {
            if (CreateDXGIFactory1(IID_IDXGIFactory1, out factory) < 0 || factory == IntPtr.Zero) return null;
            for (uint i = 0; ; i++)
            {
                if (EnumAdapters1(factory, i, out var adapter) < 0) return null;
                try
                {
                    if (GetDesc1(adapter, out var d) < 0 || (((long)d.LuidHigh << 32) | d.LuidLow) != luid) continue;
                    var iid = new Guid("189819f1-1db6-4b57-be54-1821339b85f7"); // ID3D12Device
                    if (D3D12CreateDevice(adapter, 0xb000 /* 11_0 */, ref iid, out device) < 0 || device == IntPtr.Zero) return null;
                    int[] requested = [0xc200, 0xc100, 0xc000, 0xb100, 0xb000];
                    var handle = GCHandle.Alloc(requested, GCHandleType.Pinned);
                    try
                    {
                        var levels = new D3D12_FEATURE_DATA_FEATURE_LEVELS { NumFeatureLevels = (uint)requested.Length, pFeatureLevelsRequested = handle.AddrOfPinnedObject() };
                        // ID3D12Device::CheckFeatureSupport = entrada 13 da vtable (IUnknown 3 + ID3D12Object 4 + 6 métodos).
                        var vtable = Marshal.ReadIntPtr(device);
                        var check = Marshal.GetDelegateForFunctionPointer<CheckFeatureSupportFn>(Marshal.ReadIntPtr(vtable, 13 * IntPtr.Size));
                        int size = Marshal.SizeOf<D3D12_FEATURE_DATA_FEATURE_LEVELS>();
                        IntPtr data = Marshal.AllocHGlobal(size);
                        try
                        {
                            Marshal.StructureToPtr(levels, data, false);
                            if (check(device, 2 /* D3D12_FEATURE_FEATURE_LEVELS */, data, size) < 0) return "12";
                            levels = Marshal.PtrToStructure<D3D12_FEATURE_DATA_FEATURE_LEVELS>(data);
                        }
                        finally { Marshal.FreeHGlobal(data); }
                        int fl = levels.MaxSupportedFeatureLevel;
                        return $"12 (FL {fl >> 12}.{(fl >> 8) & 0xF})";
                    }
                    finally { handle.Free(); }
                }
                finally { Release(adapter); }
            }
        }
        catch { return null; }
        finally { if (device != IntPtr.Zero) Release(device); if (factory != IntPtr.Zero) Release(factory); }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct D3D12_FEATURE_DATA_FEATURE_LEVELS
    {
        public uint NumFeatureLevels;
        public IntPtr pFeatureLevelsRequested;
        public int MaxSupportedFeatureLevel;
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int CheckFeatureSupportFn(IntPtr self, int feature, IntPtr data, int size);

    [DllImport("d3d12.dll")] private static extern int D3D12CreateDevice(IntPtr adapter, int minimumFeatureLevel, ref Guid riid, out IntPtr device);
}
