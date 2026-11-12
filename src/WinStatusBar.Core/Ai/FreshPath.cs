using System.Diagnostics;

namespace WinStatusBar.Core.Ai;

/// <summary>
/// PATH do Windows lido agora (máquina e usuário, do registro) para os programas que a barra e o encaminhador do Codex
/// iniciam (Claude Code, Codex, Gemini CLI). Um processo guarda o PATH do momento em que abriu: um programa instalado
/// depois (caso real de 12/11/2026: o adb do Platform Tools, instalado às 23:15 com o encaminhador aberto desde as 22:00)
/// ficava invisível para o Claude, que passava minutos procurando ou tentando instalar o que já estava no PC.
/// </summary>
public static class FreshPath
{
    /// <summary>Máquina, depois usuário (a ordem do Windows) e, no fim, o que só existe no PATH deste processo.</summary>
    public static string Build()
    {
        string? machine = null, user = null;
        try { machine = Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.Machine); } catch { }
        try { user = Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.User); } catch { }
        return Merge(machine, user, Environment.GetEnvironmentVariable("PATH"));
    }

    /// <summary>Junta as listas sem repetir pasta (sem diferenciar maiúsculas nem a barra final).</summary>
    internal static string Merge(string? machine, string? user, string? process)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var dirs = new List<string>();
        foreach (var list in new[] { machine, user, process })
            foreach (var raw in (list ?? string.Empty).Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                string dir = Environment.ExpandEnvironmentVariables(raw.Trim('"'));
                string key = dir.TrimEnd('\\', '/');
                if (key.Length > 0 && seen.Add(key)) dirs.Add(dir);
            }
        return string.Join(';', dirs);
    }

    /// <summary>Põe o PATH atual no processo que vai ser iniciado (falha na leitura: fica o PATH herdado).</summary>
    public static void Apply(ProcessStartInfo psi)
    {
        try { if (Build() is { Length: > 0 } path) psi.Environment["PATH"] = path; } catch { }
    }
}
