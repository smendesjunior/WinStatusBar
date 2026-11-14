using System.Collections.Concurrent;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Session;
using WinStatusBar.Core.Ipc;

/// <summary>
/// Bytes enviados e recebidos por processo, lidos dos eventos "Microsoft-Windows-Kernel-Network" do Windows (TCP e
/// UDP, IPv4 e IPv6) — a mesma fonte do Gerenciador de Tarefas. Só tamanhos e números de processo; nenhum conteúdo,
/// endereço ou porta é guardado. A taxa é a média dos últimos 3 s.
/// </summary>
internal sealed class NetworkTrace : IDisposable
{
    private const string SessionName = "WinStatusBar-Rede";
    private static readonly TimeSpan Window = TimeSpan.FromSeconds(3);
    private readonly ConcurrentQueue<(long At, uint Pid, int Bytes, bool Recv)> _events = new();
    private TraceEventSession? _session;

    public void Start()
    {
        _session = new TraceEventSession(SessionName) { StopOnDispose = true };
        _session.Source.Dynamic.All += OnEvent;
        _session.EnableProvider("Microsoft-Windows-Kernel-Network");
        var thread = new Thread(() => { try { _session.Source.Process(); } catch { } }) { IsBackground = true, Name = "NetworkTrace" };
        thread.Start();
    }

    private void OnEvent(TraceEvent e)
    {
        string name = e.EventName ?? "";
        bool recv = name.Contains("recv", StringComparison.OrdinalIgnoreCase) || name.Contains("received", StringComparison.OrdinalIgnoreCase);
        bool send = name.Contains("sent", StringComparison.OrdinalIgnoreCase) || name.Contains("send", StringComparison.OrdinalIgnoreCase);
        if (!recv && !send) return;
        int pidIndex = e.PayloadIndex("PID"), sizeIndex = e.PayloadIndex("size");
        if (pidIndex < 0 || sizeIndex < 0) return;
        try
        {
            uint pid = Convert.ToUInt32(e.PayloadValue(pidIndex));
            int size = Convert.ToInt32(e.PayloadValue(sizeIndex));
            if (pid == 0 || size <= 0) return;
            _events.Enqueue((Environment.TickCount64, pid, size, recv));
        }
        catch { }
    }

    /// <summary>Os processos que mais usam a rede agora (até 20), em bytes por segundo.</summary>
    public NetUsageReading Read()
    {
        long now = Environment.TickCount64, from = now - (long)Window.TotalMilliseconds;
        while (_events.TryPeek(out var old) && old.At < from) _events.TryDequeue(out _);
        var totals = new Dictionary<uint, (long Recv, long Send)>();
        foreach (var ev in _events)
        {
            if (ev.At < from) continue;
            var t = totals.GetValueOrDefault(ev.Pid);
            totals[ev.Pid] = ev.Recv ? (t.Recv + ev.Bytes, t.Send) : (t.Recv, t.Send + ev.Bytes);
        }
        double seconds = Window.TotalSeconds;
        var top = totals.OrderByDescending(kv => kv.Value.Recv + kv.Value.Send).Take(20)
            .Select(kv => new NetUsageEntry(kv.Key, (long)(kv.Value.Recv / seconds), (long)(kv.Value.Send / seconds))).ToList();
        return new(top, DateTimeOffset.UtcNow);
    }

    public void Dispose() { try { _session?.Dispose(); } catch { } }
}
