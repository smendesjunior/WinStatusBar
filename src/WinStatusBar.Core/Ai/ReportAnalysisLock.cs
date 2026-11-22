using System.Diagnostics;
using System.Globalization;
using System.Text.Json;

namespace WinStatusBar.Core.Ai;

/// <summary>
/// Análise em andamento no Editor de Relatórios: o Editor cria <c>&lt;processo&gt;\.&lt;kit&gt;-ai\editor\execucao.lock</c>
/// (JSON com <c>pid</c> e <c>inicio</c>) enquanto a análise roda e apaga ao terminar. Enquanto houver uma trava viva, a barra
/// não faz chamadas próprias à IA (roteiro deduzido): elas gastavam a mesma conta da análise (A15, medido em 21/11/2026).
/// A procura é barata: só sobe das pastas das conversas conhecidas, sem varrer a pasta de processos.
/// </summary>
public static class ReportAnalysisLock
{
    /// <summary>Níveis acima da pasta da conversa em que a trava é procurada (a IA pode rodar numa subpasta do processo).</summary>
    public const int MaxLevels = 6;
    private static readonly TimeSpan UnreadableFor = TimeSpan.FromSeconds(30);

    /// <summary>Caminho da primeira trava viva achada a partir das pastas dadas, ou null.</summary>
    public static string? FindAlive(IEnumerable<string?> folders)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string? folder in folders)
        {
            string? dir = folder;
            for (int level = 0; level <= MaxLevels && !string.IsNullOrWhiteSpace(dir); level++)
            {
                try
                {
                    dir = dir.TrimEnd('\\', '/');
                    if (!seen.Add(dir)) break; // já conferida (e os níveis acima dela também)
                    // Pasta oculta "<kit>-ai" do Editor (o nome do kit não fica no código).
                    foreach (string hidden in Directory.EnumerateDirectories(dir, ".*-ai"))
                    {
                        string file = Path.Combine(hidden, "editor", "execucao.lock");
                        if (File.Exists(file) && Alive(file)) return file;
                    }
                    dir = Path.GetDirectoryName(dir);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { break; }
            }
        }
        return null;
    }

    /// <summary>Mesma regra do Editor (TravaExecucao.Viva): o pid da trava ainda vive e não é um pid reaproveitado.</summary>
    public static bool Alive(string file)
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(file));
            var r = doc.RootElement;
            if (r.ValueKind != JsonValueKind.Object || !r.TryGetProperty("pid", out var p) || !p.TryGetInt32(out int pid)) return Recent(file);
            using var process = Process.GetProcessById(pid);
            if (process.HasExited) return false;
            if (r.TryGetProperty("inicio", out var i) && i.ValueKind == JsonValueKind.String
                && DateTimeOffset.TryParse(i.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.None, out var started)
                && new DateTimeOffset(process.StartTime) > started.AddSeconds(5))
                return false;
            return true;
        }
        catch (ArgumentException) { return false; }          // não há processo com esse pid
        catch (InvalidOperationException) { return false; }
        catch (JsonException) { return Recent(file); }
        catch (IOException) { return true; }                 // presa por quem a está gravando
        catch (UnauthorizedAccessException) { return true; }
        catch (System.ComponentModel.Win32Exception) { return true; } // sem permissão para ver o processo: está vivo
    }

    private static bool Recent(string file) => DateTime.UtcNow - File.GetLastWriteTimeUtc(file) < UnreadableFor;
}
