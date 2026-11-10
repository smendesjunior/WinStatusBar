using WinStatusBar.Core.Interop;

namespace WinStatusBar.Core.Metrics;

/// <summary>
/// Histórico contínuo (60 s) por processador lógico e contagens do sistema para o painel da CPU.
/// Amostrado no mesmo laço da barra, com a mesma métrica (% Processor Utility), então o painel já abre
/// com gráficos preenchidos e mostra os mesmos números da barra. Uma única instância para todos os monitores.
/// </summary>
public sealed class CpuCoreHistory : IDisposable
{
    public const int Capacity = 60;
    private const string Cores = @"\Processor Information(*)\% Processor Utility";
    // Processos, threads e handles vêm de GetPerformanceInfo: os contadores "\System\Processes|Threads" e
    // "\Process(_Total)\Handle Count" percorrem todos os processos e threads a cada coleta (~11 ms de CPU por segundo,
    // medido em 10/11/2026 com 520 processos e 8.300 threads), só para o painel mostrar três números.
    private readonly PdhQuery _pdh = new();
    private readonly Lock _gate = new();
    private readonly Dictionary<string, Queue<double>> _history = new();
    private string[] _order = [];
    private readonly Queue<double> _total = new(Capacity + 1);

    /// <summary>Uso total da CPU nos últimos 60 s (mais antigo primeiro).</summary>
    public double[] TotalSnapshot() { lock (_gate) return _total.ToArray(); }

    public double? ProcessCount { get; private set; }
    public double? ThreadCount { get; private set; }
    public double? HandleCount { get; private set; }

    public CpuCoreHistory()
    {
        _pdh.AddCounter(Cores);
        _pdh.Collect();
    }

    public void Sample()
    {
        lock (_gate)
        {
            var info = new NativeMethods.PERFORMANCE_INFORMATION { cb = (uint)System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.PERFORMANCE_INFORMATION>() };
            if (NativeMethods.GetPerformanceInfo(ref info, info.cb))
            {
                ProcessCount = info.ProcessCount; ThreadCount = info.ThreadCount; HandleCount = info.HandleCount;
            }
            if (!_pdh.Collect()) return;
            var all = _pdh.GetArray(Cores);
            // Instâncias "grupo,índice"; ignora totais ("_Total" e "0,_Total").
            var cores = all.Where(x => !x.Key.Contains("_total", StringComparison.OrdinalIgnoreCase) && x.Key.Contains(','))
                .OrderBy(x => Part(x.Key, 0)).ThenBy(x => Part(x.Key, 1)).ToArray();
            // Uso total (mesma instância "_Total" do contador da barra) para o gráfico principal do painel.
            var total = all.FirstOrDefault(x => x.Key.Equals("_Total", StringComparison.OrdinalIgnoreCase));
            if (total.Key is not null)
            {
                _total.Enqueue(Math.Clamp(total.Value, 0, 100));
                while (_total.Count > Capacity) _total.Dequeue();
            }
            if (cores.Length == 0) return;
            foreach (var core in cores)
            {
                if (!_history.TryGetValue(core.Key, out var q)) _history[core.Key] = q = new Queue<double>(Capacity + 1);
                q.Enqueue(Math.Clamp(core.Value, 0, 100));
                while (q.Count > Capacity) q.Dequeue();
            }
            _order = cores.Select(c => c.Key).ToArray();
        }
    }

    /// <summary>Cópia do histórico, na ordem dos processadores lógicos (mais antigo primeiro).</summary>
    public IReadOnlyList<double[]> Snapshot()
    {
        lock (_gate) return _order.Select(k => _history[k].ToArray()).ToArray();
    }

    private static int Part(string key, int index)
    {
        var parts = key.Split(',');
        return index < parts.Length && int.TryParse(parts[index], out int n) ? n : int.MaxValue;
    }

    public void Dispose() { lock (_gate) _pdh.Dispose(); }
}
