using System.Runtime.InteropServices;
using static WinStatusBar.Core.Interop.NativeMethods;

namespace WinStatusBar.Core.Metrics;

/// <summary>Memória física via GlobalMemoryStatusEx: Utilizada = ullTotalPhys − ullAvailPhys (igual ao "Em uso" do Gerenciador de Tarefas).</summary>
public sealed class RamMetricsProvider : ISystemMetricsProvider<RamSample>
{
    private readonly ulong _installed;

    public RamMetricsProvider()
    {
        _installed = GetPhysicallyInstalledSystemMemory(out var kb) ? kb * 1024 : 0;
    }

    public RamSample Sample()
    {
        var status = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
        if (!GlobalMemoryStatusEx(ref status)) return new RamSample(0, 0, _installed, 0);
        return new RamSample(status.ullTotalPhys, status.ullAvailPhys, _installed, status.dwMemoryLoad);
    }

    public void Dispose() { }
}
