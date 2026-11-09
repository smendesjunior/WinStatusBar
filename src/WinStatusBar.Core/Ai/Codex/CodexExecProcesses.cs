using System.Management;

namespace WinStatusBar.Core.Ai.Codex;

/// <summary>
/// Processos <c>codex exec</c> em execução. Cada execução do <c>codex exec</c> é um processo único:
/// se ele terminou sem gravar o fim do turno no rollout, a tarefa ficou órfã.
/// </summary>
internal static class CodexExecProcesses
{
    /// <summary>Início de cada processo <c>codex exec</c> vivo; null quando não foi possível consultar.</summary>
    public static IReadOnlyList<DateTimeOffset>? StartTimes()
    {
        try
        {
            var list = new List<DateTimeOffset>();
            using var searcher = new ManagementObjectSearcher(
                "SELECT CommandLine, CreationDate FROM Win32_Process WHERE Name='codex.exe' OR Name='node.exe'");
            using var results = searcher.Get();
            foreach (ManagementBaseObject process in results)
                using (process)
                {
                    if (!IsExecCommandLine(process["CommandLine"] as string)) continue;
                    if (process["CreationDate"] is string created)
                        list.Add(new DateTimeOffset(ManagementDateTimeConverter.ToDateTime(created)));
                }
            return list;
        }
        catch { return null; }
    }

    /// <summary>Linha de comando do subcomando <c>exec</c> do Codex (não confunde com <c>exec-server</c> nem <c>app-server</c>).</summary>
    internal static bool IsExecCommandLine(string? commandLine)
    {
        if (string.IsNullOrWhiteSpace(commandLine) || commandLine.IndexOf("codex", StringComparison.OrdinalIgnoreCase) < 0) return false;
        foreach (var raw in commandLine.Split((char[])[' ', '\t'], StringSplitOptions.RemoveEmptyEntries))
            if (raw.Trim('"').Equals("exec", StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }
}
