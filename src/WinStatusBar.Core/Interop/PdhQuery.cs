using System.Runtime.InteropServices;
using static WinStatusBar.Core.Interop.NativeMethods;

namespace WinStatusBar.Core.Interop;

/// <summary>Wrapper mínimo de uma consulta PDH (Performance Data Helper) com nomes de contadores em inglês.</summary>
public sealed class PdhQuery : IDisposable
{
    private IntPtr _query;
    private readonly Dictionary<string, IntPtr> _counters = new(StringComparer.OrdinalIgnoreCase);

    public PdhQuery()
    {
        if (PdhOpenQueryW(null, IntPtr.Zero, out _query) != ERROR_SUCCESS) _query = IntPtr.Zero;
    }

    public bool IsValid => _query != IntPtr.Zero;

    public bool AddCounter(string path)
    {
        if (!IsValid) return false;
        if (_counters.ContainsKey(path)) return true;
        if (PdhAddEnglishCounterW(_query, path, IntPtr.Zero, out var counter) != ERROR_SUCCESS) return false;
        _counters[path] = counter;
        return true;
    }

    public bool Has(string path) => _counters.ContainsKey(path);

    public bool Collect() => IsValid && PdhCollectQueryData(_query) == ERROR_SUCCESS;

    public double? GetValue(string path, bool noCap = false)
    {
        if (!_counters.TryGetValue(path, out var c)) return null;
        uint fmt = PDH_FMT_DOUBLE | (noCap ? PDH_FMT_NOCAP100 : 0);
        if (PdhGetFormattedCounterValue(c, fmt, out _, out var v) != ERROR_SUCCESS) return null;
        if (v.CStatus != 0 && v.CStatus != 1) return null; // PDH_CSTATUS_VALID_DATA / NEW_DATA
        return v.doubleValue;
    }

    /// <summary>Lê todas as instâncias de um contador com curinga (*).</summary>
    public List<KeyValuePair<string, double>> GetArray(string path)
    {
        var list = new List<KeyValuePair<string, double>>();
        if (!_counters.TryGetValue(path, out var c)) return list;
        uint size = 0;
        uint status = PdhGetFormattedCounterArrayW(c, PDH_FMT_DOUBLE | PDH_FMT_NOCAP100, ref size, out _, IntPtr.Zero);
        if (status != PDH_MORE_DATA || size == 0) return list;
        IntPtr buffer = Marshal.AllocHGlobal((int)size);
        try
        {
            status = PdhGetFormattedCounterArrayW(c, PDH_FMT_DOUBLE | PDH_FMT_NOCAP100, ref size, out uint count, buffer);
            if (status != ERROR_SUCCESS) return list;
            int itemSize = Marshal.SizeOf<PDH_FMT_COUNTERVALUE_ITEM>();
            for (int i = 0; i < count; i++)
            {
                var item = Marshal.PtrToStructure<PDH_FMT_COUNTERVALUE_ITEM>(buffer + i * itemSize);
                if (item.FmtValue.CStatus != 0 && item.FmtValue.CStatus != 1) continue;
                string name = Marshal.PtrToStringUni(item.szName) ?? string.Empty;
                list.Add(new(name, item.FmtValue.doubleValue));
            }
        }
        finally { Marshal.FreeHGlobal(buffer); }
        return list;
    }

    public void Dispose()
    {
        if (_query != IntPtr.Zero) { PdhCloseQuery(_query); _query = IntPtr.Zero; }
        _counters.Clear();
    }
}
