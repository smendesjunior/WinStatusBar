using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.InteropServices;
using WinStatusBar.Core.Interop;

namespace WinStatusBar.Core.Metrics;

/// <summary>
/// Placa de vídeo lida direto das estatísticas do kernel gráfico (D3DKMT, gdi32), a mesma fonte do Gerenciador de
/// Tarefas e do System Informer: tempo de execução acumulado de cada motor (no total e por processo) e temperatura.
/// Cada leitura são poucas chamadas de sistema. Antes (medido em 10/11/2026): o contador PDH "GPU Engine(*)" tinha
/// ~900 instâncias, uma por processo e motor (3 ms de CPU e centenas de textos a cada 2 s), e a temperatura da NVIDIA
/// vinha de um nvidia-smi.exe aberto a cada 10 s (44 ms de CPU cada, e a consulta pode acordar a placa dedicada).
/// </summary>
internal sealed class GpuKernelStats : IDisposable
{
    // D3DKMT_QUERYSTATISTICS (x64): Type@0, AdapterLuid@4, hProcess@16, QueryResult@24, NodeId/SegmentId@0x320.
    private const int StatisticsSize = 0x328, ResultOffset = 24, QueryIdOffset = 0x320;
    private const int AdapterQuery = 0, NodeQuery = 5, ProcessNodeQuery = 6;
    // D3DKMT_QUERYSTATISTICS_ADAPTER_INFORMATION: NbSegments@0, NodeCount@4.
    private const int NodeCountOffset = ResultOffset + 4;
    // Os tipos PROCESS_* recusam PROCESS_QUERY_LIMITED_INFORMATION (STATUS_INVALID_PARAMETER).
    private const uint ProcessQueryInformation = 0x0400;

    private sealed class AdapterState
    {
        public int Nodes;
        public long[] Last = [];
        public long LastTicks;
        public string[] Types = [];
    }

    private readonly Lock _gate = new();
    private readonly byte[] _buffer = new byte[StatisticsSize];
    private readonly Dictionary<long, AdapterState> _adapters = new();
    private readonly Dictionary<(uint Pid, long Luid, int Node), long> _processLast = new();
    private readonly Dictionary<uint, long> _processTicks = new();
    private readonly Func<IReadOnlyList<KeyValuePair<string, double>>> _engineNames;
    private DateTime _namesReadAt;

    /// <param name="engineNames">Instâncias do contador "GPU Engine" (nome com o tipo de cada motor), lidas uma vez por adaptador.</param>
    public GpuKernelStats(Func<IReadOnlyList<KeyValuePair<string, double>>> engineNames) => _engineNames = engineNames;

    /// <summary>O kernel gráfico responde às estatísticas deste adaptador (falso em máquinas virtuais sem WDDM, por exemplo).</summary>
    public bool Supports(long luid)
    {
        lock (_gate) return State(luid) is not null;
    }

    /// <summary>
    /// Uso de cada motor desde a leitura anterior, no formato das instâncias do PDH sem o "pid_" do início
    /// ("luid_0x…_0x…_phys_0_eng_3_engtype_3D"), para os cálculos de <see cref="GpuMetricsProvider"/> valerem igual.
    /// Nulo na primeira leitura de cada adaptador (ainda não há intervalo) ou se o kernel não responder.
    /// </summary>
    public List<KeyValuePair<string, double>>? Engines(GpuAdapterInfo adapter)
    {
        lock (_gate)
        {
            if (State(adapter.Luid) is not { } state) return null;
            long now = Stopwatch.GetTimestamp();
            double elapsed100ns = (now - state.LastTicks) * 1e7 / Stopwatch.Frequency;
            bool primed = state.LastTicks != 0 && elapsed100ns > 0;
            var result = new List<KeyValuePair<string, double>>(state.Nodes);
            bool missingName = false;
            for (int n = 0; n < state.Nodes; n++)
            {
                if (Query(NodeQuery, adapter.Luid, IntPtr.Zero, n) is not { } running) continue;
                long previous = state.Last[n];
                state.Last[n] = running;
                if (!primed || running < previous) continue;
                double percent = Math.Clamp((running - previous) * 100.0 / elapsed100ns, 0, 100);
                string type = state.Types[n];
                if (type.Length == 0 && percent > 0) missingName = true;
                result.Add(new($"{adapter.LuidKey}_phys_0_eng_{n}_engtype_{type}", percent));
            }
            state.LastTicks = now;
            // Motor que começou a ser usado depois da leitura dos nomes: lê os nomes de novo, no máximo a cada 5 min.
            if (missingName && DateTime.UtcNow - _namesReadAt > TimeSpan.FromMinutes(5)) ReadNames();
            return primed ? result : null;
        }
    }

    /// <summary>
    /// Uso da GPU por um conjunto de processos (o servidor de IA local): soma dos processos em cada motor, e o motor
    /// mais ocupado, como fazia a leitura pelo PDH. Zero na primeira leitura de um processo; nulo sem adaptadores.
    /// </summary>
    public double? ProcessUtilization(IReadOnlyCollection<long> luids, HashSet<uint> pids)
    {
        if (luids.Count == 0) return null;
        lock (_gate)
        {
            long now = Stopwatch.GetTimestamp();
            var perEngine = new Dictionary<(long, int), double>();
            foreach (uint pid in pids)
            {
                IntPtr process = NativeMethods.OpenProcess(ProcessQueryInformation, false, pid);
                if (process == IntPtr.Zero) continue;
                try
                {
                    double elapsed100ns = _processTicks.TryGetValue(pid, out long last) ? (now - last) * 1e7 / Stopwatch.Frequency : 0;
                    _processTicks[pid] = now;
                    foreach (long luid in luids)
                    {
                        if (State(luid) is not { } state) continue;
                        for (int n = 0; n < state.Nodes; n++)
                        {
                            if (Query(ProcessNodeQuery, luid, process, n) is not { } running) continue;
                            var key = (pid, luid, n);
                            bool had = _processLast.TryGetValue(key, out long previous);
                            _processLast[key] = running;
                            if (!had || elapsed100ns <= 0 || running < previous) continue;
                            perEngine[(luid, n)] = perEngine.GetValueOrDefault((luid, n)) + (running - previous) * 100.0 / elapsed100ns;
                        }
                    }
                }
                finally { NativeMethods.CloseHandle(process); }
            }
            // Processos que saíram não seguram memória.
            foreach (var gone in _processTicks.Keys.Where(p => !pids.Contains(p)).ToList())
            {
                _processTicks.Remove(gone);
                foreach (var key in _processLast.Keys.Where(k => k.Pid == gone).ToList()) _processLast.Remove(key);
            }
            return Math.Clamp(perEngine.Values.DefaultIfEmpty(0).Max(), 0, 100);
        }
    }

    /// <summary>
    /// Temperatura do adaptador pelo kernel gráfico (D3DKMT_ADAPTER_PERFDATA, WDDM 2.4+), sem abrir programas.
    /// Supported falso: o driver não informa (aí vale outra fonte). Celsius nulo: informa, mas sem leitura agora
    /// (por exemplo, a placa dedicada desligada para economizar energia — não é acordada só para isso).
    /// </summary>
    public static unsafe (bool Supported, double? Celsius) Temperature(long luid)
    {
        // D3DKMT_OPENADAPTERFROMLUID: AdapterLuid@0, hAdapter@8. D3DKMT_CLOSEADAPTER: hAdapter@0.
        long* open = stackalloc long[2];
        open[0] = luid; open[1] = 0;
        if (D3DKMTOpenAdapterFromLuid((byte*)open) != 0) return (false, null);
        uint* close = stackalloc uint[1];
        *close = (uint)open[1];
        // D3DKMT_ADAPTER_PERFDATA: PhysicalAdapterIndex@0, …, FanRPM@48, Power@52, Temperature@56 (décimos de °C).
        byte* perf = stackalloc byte[64];
        new Span<byte>(perf, 64).Clear();
        try
        {
            var query = new QueryAdapterInfo { Adapter = *close, Type = 62, Data = (IntPtr)perf, Size = 64 };
            if (D3DKMTQueryAdapterInfo(&query) != 0) return (false, null);
            double celsius = *(uint*)(perf + 56) / 10.0;
            return (true, celsius is > 0 and < 130 ? celsius : null);
        }
        finally { D3DKMTCloseAdapter((byte*)close); }
    }

    private AdapterState? State(long luid)
    {
        if (_adapters.TryGetValue(luid, out var state)) return state.Nodes > 0 ? state : null;
        int nodes = 0;
        if (QueryRaw(AdapterQuery, luid, IntPtr.Zero, 0)) nodes = BinaryPrimitives.ReadInt32LittleEndian(_buffer.AsSpan(NodeCountOffset));
        state = new AdapterState { Nodes = nodes, Last = new long[Math.Max(0, nodes)], Types = Enumerable.Repeat("", Math.Max(0, nodes)).ToArray() };
        _adapters[luid] = state;
        if (nodes > 0) ReadNames();
        return nodes > 0 ? state : null;
    }

    /// <summary>Tipo de cada motor (3D, Copy, VideoDecode…) a partir dos nomes das instâncias do PDH, uma única vez.</summary>
    private void ReadNames()
    {
        _namesReadAt = DateTime.UtcNow;
        IReadOnlyList<KeyValuePair<string, double>> names;
        try { names = _engineNames(); } catch { return; }
        foreach (var (luid, state) in _adapters)
        {
            string key = GpuAdapterInfo.FormatLuid(luid);
            foreach (var kv in names)
            {
                if (!TryParseEngine(kv.Key, key, out int node, out string type) || node >= state.Nodes) continue;
                if (state.Types[node].Length == 0) state.Types[node] = type;
            }
        }
    }

    /// <summary>"pid_1234_luid_0x…_0x…_phys_0_eng_3_engtype_3D" → motor 3, tipo "3D" (só do adaptador pedido).</summary>
    internal static bool TryParseEngine(string instance, string luidKey, out int node, out string type)
    {
        node = -1; type = "";
        int l = instance.IndexOf(luidKey, StringComparison.OrdinalIgnoreCase);
        if (l < 0) return false;
        int e = instance.IndexOf("_eng_", l, StringComparison.OrdinalIgnoreCase);
        int t = instance.IndexOf("_engtype_", l, StringComparison.OrdinalIgnoreCase);
        if (e < 0 || t < e) return false;
        if (!int.TryParse(instance.AsSpan(e + 5, t - e - 5), out node)) return false;
        type = instance[(t + 9)..];
        return true;
    }

    private long? Query(int type, long luid, IntPtr process, int id)
        => QueryRaw(type, luid, process, id) ? BinaryPrimitives.ReadInt64LittleEndian(_buffer.AsSpan(ResultOffset)) : null;

    private unsafe bool QueryRaw(int type, long luid, IntPtr process, int id)
    {
        var b = _buffer.AsSpan();
        b.Clear();
        BinaryPrimitives.WriteInt32LittleEndian(b, type);
        BinaryPrimitives.WriteInt64LittleEndian(b[4..], luid);
        BinaryPrimitives.WriteInt64LittleEndian(b[16..], process.ToInt64());
        BinaryPrimitives.WriteInt32LittleEndian(b[QueryIdOffset..], id);
        fixed (byte* p = _buffer) return D3DKMTQueryStatistics(p) == 0;
    }

    /// <summary>Adaptadores mudaram: lê de novo a quantidade e os nomes dos motores na próxima leitura.</summary>
    public void Reset()
    {
        lock (_gate) { _adapters.Clear(); _processLast.Clear(); _processTicks.Clear(); }
    }

    public void Dispose() => Reset();

    [StructLayout(LayoutKind.Sequential)]
    private struct QueryAdapterInfo { public uint Adapter; public int Type; public IntPtr Data; public uint Size; }

    [DllImport("gdi32.dll")] private static extern unsafe int D3DKMTQueryStatistics(byte* statistics);
    [DllImport("gdi32.dll")] private static extern unsafe int D3DKMTOpenAdapterFromLuid(byte* open);
    [DllImport("gdi32.dll")] private static extern unsafe int D3DKMTQueryAdapterInfo(QueryAdapterInfo* info);
    [DllImport("gdi32.dll")] private static extern unsafe int D3DKMTCloseAdapter(byte* close);
}
