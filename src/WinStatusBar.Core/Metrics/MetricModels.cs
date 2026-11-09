namespace WinStatusBar.Core.Metrics;

public interface ISystemMetricsProvider<out T> : IDisposable
{
    /// <summary>Amostra imediata. Deve ser chamada periodicamente pelo agendador central (não cria timers próprios).</summary>
    T Sample();
}

public sealed record CpuSample(double? UsagePercent, double? CurrentGHz, string ProcessorName, int LogicalProcessors, string Source);

public sealed record RamSample(ulong TotalBytes, ulong AvailableBytes, ulong InstalledBytes, uint MemoryLoad)
{
    public ulong UsedBytes => TotalBytes >= AvailableBytes ? TotalBytes - AvailableBytes : 0;
    public double UsedPercent => TotalBytes == 0 ? 0 : UsedBytes * 100.0 / TotalBytes;
}

public sealed record GpuAdapterInfo(string Name, uint VendorId, long Luid, ulong DedicatedVideoMemory, ulong SharedSystemMemory, bool IsIntegrated)
{
    public string LuidKey => FormatLuid(Luid);
    public static string FormatLuid(long luid) => $"luid_0x{(uint)(luid >> 32):X8}_0x{(uint)(luid & 0xFFFFFFFF):X8}";
}

public sealed record GpuSample(GpuAdapterInfo? Adapter, ulong? DedicatedUsedBytes, ulong? SharedUsedBytes, double? UtilizationPercent, IReadOnlyList<GpuAdapterInfo> Adapters)
{
    public double? DedicatedPercent => Adapter is null || DedicatedUsedBytes is null || Adapter.DedicatedVideoMemory == 0
        ? null : DedicatedUsedBytes.Value * 100.0 / Adapter.DedicatedVideoMemory;
}

public sealed record NetworkInterfaceInfo(ulong Luid, string Alias, string Description, uint Type, bool IsHardware, bool IsUp, bool IsFilter = false, bool ConnectorPresent = true);

public sealed record NetworkSample(double? UploadBytesPerSec, double? DownloadBytesPerSec, IReadOnlyList<string> ActiveInterfaces, IReadOnlyList<NetworkInterfaceInfo> AllInterfaces);
