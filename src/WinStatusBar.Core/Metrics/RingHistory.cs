namespace WinStatusBar.Core.Metrics;

/// <summary>Histórico curto e thread-safe de uma métrica (padrão: 60 amostras), para os gráficos dos painéis.</summary>
public sealed class RingHistory(int capacity = 60)
{
    private readonly Queue<double> _values = new(capacity + 1);
    private readonly Lock _gate = new();
    public int Capacity { get; } = capacity;

    public void Add(double value)
    {
        lock (_gate)
        {
            _values.Enqueue(double.IsFinite(value) ? value : 0);
            while (_values.Count > Capacity) _values.Dequeue();
        }
    }

    public void Clear() { lock (_gate) _values.Clear(); }

    /// <summary>Valores do mais antigo ao mais recente.</summary>
    public double[] Snapshot() { lock (_gate) return _values.ToArray(); }
}
