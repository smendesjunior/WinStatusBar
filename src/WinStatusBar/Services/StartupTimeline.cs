using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;

namespace WinStatusBar.Services;

/// <summary>
/// Cronômetro da abertura: marca cada etapa desde o nascimento do processo e grava uma linha só no log
/// ("Abertura em X s: …"), para saber onde está cada segundo depois do logon. Também abre a barra com prioridade
/// acima do normal (no logon ela disputa a CPU com dezenas de programas) e volta ao normal quando os serviços ligam.
/// </summary>
internal static class StartupTimeline
{
    private static readonly Lock Gate = new();
    private static readonly StringBuilder Steps = new();
    private static DateTime? _processStart;
    private static TimeSpan _last;
    private static bool _done, _boosted;

    /// <summary>Primeira coisa que roda do código da barra (antes do Main): separa o .NET subindo do WinUI subindo.</summary>
    [ModuleInitializer]
    internal static void OnModuleLoaded()
    {
        Mark(".NET");
        try
        {
            using var self = Process.GetCurrentProcess();
            if (self.PriorityClass == ProcessPriorityClass.Normal)
            {
                self.PriorityClass = ProcessPriorityClass.AboveNormal;
                _boosted = true;
            }
        }
        catch { }
    }

    /// <summary>Volta à prioridade normal (a barra roda o dia todo; acima do normal só durante a abertura).</summary>
    public static void EndBoost()
    {
        if (!_boosted) return;
        _boosted = false;
        try { using var self = Process.GetCurrentProcess(); self.PriorityClass = ProcessPriorityClass.Normal; } catch { }
    }

    private static TimeSpan SinceProcessStart()
    {
        try { _processStart ??= Process.GetCurrentProcess().StartTime; }
        catch { _processStart = DateTime.Now; }
        return DateTime.Now - _processStart.Value;
    }

    /// <summary>Fim de uma etapa: guarda quanto ela levou desde a marca anterior.</summary>
    public static void Mark(string step)
    {
        lock (Gate)
        {
            if (_done) return;
            var now = SinceProcessStart();
            if (Steps.Length > 0) Steps.Append(", ");
            Steps.Append(step).Append(' ').Append(Format(now - _last));
            _last = now;
        }
    }

    /// <summary>Grava a linha do log uma vez; marcas depois disso são ignoradas.</summary>
    public static void Finish(string step)
    {
        string line;
        lock (Gate)
        {
            if (_done) return;
            Mark(step);
            _done = true;
            line = $"Abertura em {Format(_last)} desde o processo: {Steps}.";
        }
        App.Log(line);
        // Bibliotecas .NET carregadas até a barra aparecer: o que estiver aqui sem precisar atrasa a abertura.
        var names = AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetName().Name ?? "?").Order(StringComparer.OrdinalIgnoreCase).ToList();
        App.Log($"Abertura: {names.Count} bibliotecas .NET carregadas antes da barra: {string.Join(", ", names)}.");
    }

    private static string Format(TimeSpan t) => t.TotalSeconds >= 1 ? $"{t.TotalSeconds:0.0} s" : $"{t.TotalMilliseconds:0} ms";
}
