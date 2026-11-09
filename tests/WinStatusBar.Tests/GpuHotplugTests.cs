using WinStatusBar.Core.Metrics;
using Xunit;

namespace WinStatusBar.Tests;

public class GpuHotplugTests
{
    private static readonly GpuAdapterInfo Intel = new("Intel", 0x8086, 1, 128UL << 20, 32UL << 30, true);
    private static readonly GpuAdapterInfo Nvidia = new("NVIDIA", 0x10de, 2, 6UL << 30, 32UL << 30, false);

    [Fact]
    public void Sample_DetectsArrivalAndRemovalWithoutWaitingFiveMinutes()
    {
        List<GpuAdapterInfo> present = [Intel];
        bool changed = false;
        using var gpu = new GpuMetricsProvider(() => { changed = false; return present.ToList(); }, () => changed);
        gpu.MeasureUtilization = false;
        Assert.Equal("Intel", gpu.Sample().Adapter?.Name);
        present.Add(Nvidia); changed = true;
        Assert.Equal("NVIDIA", gpu.Sample().Adapter?.Name);
        Assert.Equal(2, gpu.Adapters.Count);
        present.Remove(Nvidia); changed = true;
        Assert.Equal("Intel", gpu.Sample().Adapter?.Name);
        Assert.Single(gpu.Adapters);
    }

    [Fact]
    public void PreferredGpu_FallsBackWhileAbsentAndReturnsWhenReconnected()
    {
        List<GpuAdapterInfo> present = [Intel, Nvidia];
        bool changed = false;
        using var gpu = new GpuMetricsProvider(() => { changed = false; return present.ToList(); }, () => changed);
        gpu.MeasureUtilization = false;
        gpu.PreferredAdapter = "NVIDIA";
        present.Remove(Nvidia); changed = true;
        Assert.Equal("Intel", gpu.Sample().Adapter?.Name);
        present.Add(Nvidia); changed = true;
        Assert.Equal("NVIDIA", gpu.Sample().Adapter?.Name);
        Assert.Equal("NVIDIA", gpu.PreferredAdapter);
    }

    [Fact]
    public void UnchangedHardware_DoesNotReenumerateOnEverySample()
    {
        int reads = 0;
        using var gpu = new GpuMetricsProvider(() => { reads++; return [Intel]; }, () => false);
        gpu.MeasureUtilization = false;
        gpu.Sample(); gpu.Sample(); gpu.Sample();
        Assert.Equal(1, reads);
        gpu.RequestAdapterRefresh(); gpu.RequestAdapterRefresh();
        gpu.Sample();
        Assert.Equal(2, reads);
    }

    [Fact]
    public void NoGpu_RemovesTheStaleSelectionAndAcceptsNewLuid()
    {
        List<GpuAdapterInfo> present = [Nvidia];
        bool changed = false;
        using var gpu = new GpuMetricsProvider(() => { changed = false; return present.ToList(); }, () => changed);
        gpu.MeasureUtilization = false;
        present.Clear(); changed = true;
        var absent = gpu.Sample();
        Assert.Null(absent.Adapter);
        Assert.Null(absent.DedicatedUsedBytes);
        Assert.Empty(absent.Adapters);
        present.Add(Nvidia with { Luid = 42 }); changed = true;
        Assert.Equal(42, gpu.Sample().Adapter?.Luid);
    }
}
