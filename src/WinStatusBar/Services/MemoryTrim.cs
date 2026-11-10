using System.Runtime;
using Microsoft.UI.Dispatching;

namespace WinStatusBar.Services;

/// <summary>
/// Devolve ao Windows a memória que o .NET segura depois de picos: uma coleta compacta e agressiva
/// (<see cref="GCCollectionMode.Aggressive"/>, que também compacta o heap de objetos grandes e libera as regiões vazias)
/// 1 min depois de abrir e quando a tela apaga ou a sessão é bloqueada, no máximo a cada 10 min. A leitura inicial das
/// conversas, dos catálogos e dos ícones deixa dezenas de MB livres e fragmentados que o .NET não devolve sozinho num PC
/// com memória sobrando (medido em 10/11/2026: 100 MB reservados pelo GC, 22 MB deles só em fragmentos de objetos
/// grandes). A coleta leva dezenas de milissegundos e roda fora da thread da interface.
/// </summary>
internal static class MemoryTrim
{
    private static readonly TimeSpan MinimumGap = TimeSpan.FromMinutes(10);
    private static long _last;

    public static void Start(DispatcherQueue ui)
    {
        var timer = ui.CreateTimer();
        timer.Interval = TimeSpan.FromMinutes(1);
        timer.IsRepeating = false;
        // A da abertura vale sempre: a leitura inicial ainda pode estar em andamento quando a tela já está apagada.
        timer.Tick += (_, _) => Run("abertura", always: true);
        timer.Start();
        Core.Presence.Changed += away => { if (away) Run("tela apagada"); };
    }

    public static void Run(string reason, bool always = false)
    {
        long now = Environment.TickCount64;
        long last = Interlocked.Read(ref _last);
        if (!always && last != 0 && now - last < (long)MinimumGap.TotalMilliseconds) return;
        if (Interlocked.CompareExchange(ref _last, now, last) != last) return;
        Task.Run(() =>
        {
            try
            {
                long before = GC.GetGCMemoryInfo().TotalCommittedBytes;
                GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
                GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
                long after = GC.GetGCMemoryInfo().TotalCommittedBytes;
                App.Log($"Memória do .NET ({reason}): {before / 1048576.0:0} → {after / 1048576.0:0} MB reservados.");
            }
            catch (Exception ex) { App.Log("Memória do .NET: " + ex.Message); }
        });
    }
}
