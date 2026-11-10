using WinStatusBar.Core.Interop;
using static WinStatusBar.Core.Interop.NativeMethods;

namespace WinStatusBar.Core.Metrics;

/// <summary>
/// Adaptadores via DXGI (IDXGIFactory1/IDXGIAdapter1::GetDesc1). VRAM total = DedicatedVideoMemory.
/// VRAM em uso (global, todos os processos) = contador "GPU Adapter Memory(luid_*)\Dedicated Usage" do mesmo LUID —
/// a mesma fonte do Gerenciador de Tarefas. Não usa QueryVideoMemoryInfo.CurrentUsage (que é por processo).
/// Utilização 3D/compute = motor mais ocupado, pelo tempo de execução de cada motor no kernel gráfico
/// (<see cref="GpuKernelStats"/>); reserva: maior soma por motor de "GPU Engine(*_luid_*_eng_N_*)\Utilization Percentage".
/// </summary>
public sealed class GpuMetricsProvider : ISystemMetricsProvider<GpuSample>
{
    private const string Dedicated = @"\GPU Adapter Memory(*)\Dedicated Usage";
    private const string Shared = @"\GPU Adapter Memory(*)\Shared Usage";
    private const string Engine = @"\GPU Engine(*)\Utilization Percentage";
    private const string EngineRunningTime = @"\GPU Engine(*)\Running Time";

    private readonly PdhQuery _memory = new();
    private PdhQuery? _engine;
    /// <summary>Uso dos motores pelo kernel gráfico; o PDH "GPU Engine(*)" fica só como reserva (adaptador sem estatísticas).</summary>
    private readonly GpuKernelStats _kernel = new(ReadEngineInstanceNames);
    private List<GpuAdapterInfo> _adapters = new();
    private DateTime _adaptersReadAt;
    private readonly Func<List<GpuAdapterInfo>> _enumerate;
    private readonly Func<bool>? _topologyChanged;
    private IntPtr _factory;
    private int _refreshRequested;

    /// <summary>Nome (ou parte) do adaptador escolhido nas configurações; vazio = preferir GPU dedicada.</summary>
    public string? PreferredAdapter { get; set; }
    public bool MeasureUtilization { get; set; } = true;

    /// <summary>
    /// Última leitura do uso dos motores no formato das instâncias do PDH: pelo kernel gráfico, uma por motor do
    /// adaptador escolhido ("luid_…_eng_N_engtype_X"); pelo PDH (reserva), todas as instâncias, com PID no nome.
    /// </summary>
    public IReadOnlyList<KeyValuePair<string, double>>? LastEngineValues { get; private set; }

    public GpuMetricsProvider() : this(EnumerateAdapters, null) { }

    internal GpuMetricsProvider(Func<List<GpuAdapterInfo>> enumerate, Func<bool>? topologyChanged)
    {
        _enumerate = enumerate;
        _topologyChanged = topologyChanged;
        _memory.AddCounter(Dedicated);
        _memory.AddCounter(Shared);
        _memory.Collect();
        RefreshAdapters();
    }

    public IReadOnlyList<GpuAdapterInfo> Adapters => _adapters;

    public void RefreshAdapters()
    {
        if (_topologyChanged is null)
        {
            Release(_factory);
            _factory = IntPtr.Zero;
            CreateDXGIFactory1(IID_IDXGIFactory1, out _factory);
        }
        _driverSizes = null;
        var adapters = _enumerate();
        if (!_adapters.SequenceEqual(adapters)) { _adapters = adapters; _kernel.Reset(); }
        _adaptersReadAt = DateTime.UtcNow;
    }

    /// <summary>Nomes das instâncias do contador "GPU Engine" (com o tipo de cada motor), lidos de uma vez só.</summary>
    private static IReadOnlyList<KeyValuePair<string, double>> ReadEngineInstanceNames()
    {
        using var query = new PdhQuery();
        // "Running Time" é um valor bruto: vale já na primeira coleta (o percentual precisa de duas).
        return query.AddCounter(EngineRunningTime) && query.Collect() ? query.GetArray(EngineRunningTime) : [];
    }

    /// <summary>
    /// Uso da GPU pelos processos indicados (o servidor de IA local) em qualquer adaptador: soma dos processos por
    /// motor e o motor mais ocupado. Pelo kernel gráfico quando disponível; senão, pela última leitura do PDH.
    /// </summary>
    public double? ProcessUsage(HashSet<uint> pids)
    {
        var adapters = _adapters;
        if (adapters.Any(a => _kernel.Supports(a.Luid))) return _kernel.ProcessUtilization(adapters.Select(a => a.Luid).ToList(), pids);
        return LastEngineValues is { } engines ? Ai.Local.LocalAiProvider.GpuForPids(engines, pids) : null;
    }

    // Apenas sinaliza: DXGI/PDH continuam serializados pelo laço de amostragem.
    public void RequestAdapterRefresh() => Interlocked.Exchange(ref _refreshRequested, 1);

    public static List<GpuAdapterInfo> EnumerateAdapters()
    {
        var list = new List<GpuAdapterInfo>();
        if (CreateDXGIFactory1(IID_IDXGIFactory1, out var factory) < 0 || factory == IntPtr.Zero) return list;
        try
        {
            for (uint i = 0; ; i++)
            {
                int hr = EnumAdapters1(factory, i, out var adapter);
                if (hr == DXGI_ERROR_NOT_FOUND || hr < 0) break;
                try
                {
                    if (GetDesc1(adapter, out var d) < 0) continue;
                    if ((d.Flags & DXGI_ADAPTER_FLAG_SOFTWARE) != 0) continue;
                    long luid = ((long)d.LuidHigh << 32) | d.LuidLow;
                    if (list.Any(a => a.Luid == luid)) continue;
                    bool integrated = (ulong)d.DedicatedVideoMemory < 1024UL * 1024 * 1024;
                    string name = d.Description.Trim();
                    // Total físico informado pelo driver (mesma base do Gerenciador de Tarefas); DXGI desconta áreas reservadas.
                    ulong dedicated = d.DedicatedVideoMemory;
                    if (DriverMemorySizes.TryGetValue(name, out var physical) && physical >= dedicated && physical < dedicated * 2) dedicated = physical;
                    list.Add(new GpuAdapterInfo(name, d.VendorId, luid, dedicated, d.SharedSystemMemory, integrated));
                }
                finally { Release(adapter); }
            }
        }
        finally { Release(factory); }
        return list;
    }

    private static Dictionary<string, ulong>? _driverSizes;

    /// <summary>HardwareInformation.qwMemorySize dos adaptadores de vídeo (classe de dispositivo Display), somente leitura.</summary>
    private static Dictionary<string, ulong> DriverMemorySizes
    {
        get
        {
            if (_driverSizes is not null) return _driverSizes;
            var map = new Dictionary<string, ulong>(StringComparer.OrdinalIgnoreCase);
            try
            {
                using var cls = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}");
                foreach (var sub in cls?.GetSubKeyNames() ?? [])
                {
                    try
                    {
                        using var k = cls!.OpenSubKey(sub);
                        if (k?.GetValue("DriverDesc") is not string desc) continue;
                        object? v = k.GetValue("HardwareInformation.qwMemorySize");
                        ulong size = v switch { long l => (ulong)l, byte[] b when b.Length >= 8 => BitConverter.ToUInt64(b, 0), _ => 0 };
                        if (size > 0) map[desc.Trim()] = size;
                    }
                    catch { }
                }
            }
            catch { }
            return _driverSizes = map;
        }
    }

    public static GpuAdapterInfo? Choose(IReadOnlyList<GpuAdapterInfo> adapters, string? preferred)
    {
        if (adapters.Count == 0) return null;
        if (!string.IsNullOrWhiteSpace(preferred))
        {
            var match = adapters.FirstOrDefault(a => a.Name.Equals(preferred, StringComparison.OrdinalIgnoreCase))
                        ?? adapters.FirstOrDefault(a => a.Name.Contains(preferred, StringComparison.OrdinalIgnoreCase));
            if (match is not null) return match;
        }
        return adapters.Where(a => !a.IsIntegrated).OrderByDescending(a => a.DedicatedVideoMemory).FirstOrDefault()
               ?? adapters.OrderByDescending(a => a.DedicatedVideoMemory).First();
    }

    public GpuSample Sample()
    {
        bool changed = _topologyChanged?.Invoke() ?? (_factory != IntPtr.Zero && !IsFactoryCurrent(_factory));
        if (Interlocked.Exchange(ref _refreshRequested, 0) != 0 || changed
            || (_topologyChanged is null && _factory == IntPtr.Zero && DateTime.UtcNow - _adaptersReadAt > TimeSpan.FromSeconds(5)))
            RefreshAdapters();
        var adapter = Choose(_adapters, PreferredAdapter);
        if (adapter is null)
        {
            LastEngineValues = null;
            return new GpuSample(null, null, null, null, _adapters);
        }

        ulong? dedicated = null, shared = null;
        if (_memory.Collect())
        {
            dedicated = SumForLuid(_memory.GetArray(Dedicated), adapter.LuidKey);
            shared = SumForLuid(_memory.GetArray(Shared), adapter.LuidKey);
        }

        double? util = null;
        if (MeasureUtilization)
        {
            if (_kernel.Supports(adapter.Luid))
            {
                if (_engine is not null) { _engine.Dispose(); _engine = null; }
                if (_kernel.Engines(adapter) is { } engines)
                {
                    LastEngineValues = engines;
                    util = ComputeEngineUtilization(engines, adapter.LuidKey);
                }
            }
            else if (_engine is null) { _engine = new PdhQuery(); _engine.AddCounter(Engine); _engine.Collect(); }
            else if (_engine.Collect())
            {
                var engines = _engine.GetArray(Engine);
                LastEngineValues = engines; // reaproveitado pelo provedor "Local" (GPU por PID)
                util = ComputeEngineUtilization(engines, adapter.LuidKey);
            }
        }
        else if (_engine is not null) { _engine.Dispose(); _engine = null; }

        return new GpuSample(adapter, dedicated, shared, util, _adapters);
    }

    internal static ulong? SumForLuid(List<KeyValuePair<string, double>> values, string luidKey)
    {
        double sum = 0; bool any = false;
        foreach (var kv in values)
        {
            if (!kv.Key.StartsWith(luidKey, StringComparison.OrdinalIgnoreCase)) continue;
            sum += kv.Value; any = true;
        }
        return any ? (ulong)Math.Max(0, sum) : null;
    }

    /// <summary>Soma a utilização de todos os processos por motor (eng_N) do adaptador e devolve o maior motor (como o Gerenciador de Tarefas).</summary>
    internal static double? ComputeEngineUtilization(IEnumerable<KeyValuePair<string, double>> values, string luidKey)
    {
        var perEngine = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        foreach (var kv in values)
        {
            int l = kv.Key.IndexOf(luidKey, StringComparison.OrdinalIgnoreCase);
            if (l < 0) continue;
            int e = kv.Key.IndexOf("_eng_", l, StringComparison.OrdinalIgnoreCase);
            if (e < 0) continue;
            int end = kv.Key.IndexOf('_', e + 5);
            string engine = end > 0 ? kv.Key[e..end] : kv.Key[e..];
            perEngine[engine] = perEngine.GetValueOrDefault(engine) + kv.Value;
        }
        if (perEngine.Count == 0) return null;
        return Math.Clamp(perEngine.Values.Max(), 0, 100);
    }

    /// <summary>
    /// Utilização por tipo de motor do adaptador (3D, Copy, VideoDecode…), como os gráficos do Gerenciador de Tarefas:
    /// soma de todos os processos por motor físico (eng_N) e, por tipo, o motor mais ocupado.
    /// </summary>
    public static Dictionary<string, double> EngineTypeUtilization(IEnumerable<KeyValuePair<string, double>> values, string luidKey)
    {
        var perEngine = new Dictionary<string, (string Type, double Sum)>(StringComparer.OrdinalIgnoreCase);
        foreach (var kv in values)
        {
            int l = kv.Key.IndexOf(luidKey, StringComparison.OrdinalIgnoreCase);
            if (l < 0) continue;
            int e = kv.Key.IndexOf("_eng_", l, StringComparison.OrdinalIgnoreCase);
            int t = kv.Key.IndexOf("_engtype_", l, StringComparison.OrdinalIgnoreCase);
            if (e < 0 || t < 0) continue;
            int end = kv.Key.IndexOf('_', e + 5);
            string engine = end > 0 ? kv.Key[e..end] : kv.Key[e..];
            string type = kv.Key[(t + 9)..];
            var cur = perEngine.GetValueOrDefault(engine, (type, 0));
            perEngine[engine] = (type, cur.Sum + kv.Value);
        }
        var result = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        foreach (var (type, sum) in perEngine.Values)
            result[type] = Math.Max(result.GetValueOrDefault(type), Math.Clamp(sum, 0, 100));
        return result;
    }

    public void Dispose() { _memory.Dispose(); _engine?.Dispose(); _kernel.Dispose(); Release(_factory); _factory = IntPtr.Zero; }
}
