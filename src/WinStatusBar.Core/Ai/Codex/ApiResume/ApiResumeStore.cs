using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace WinStatusBar.Core.Ai.Codex.ApiResume;

/// <summary>
/// %LOCALAPPDATA%\WinStatusBar\CodexApiResume: jobs.json (gravado de forma atômica) e uma pasta por continuação
/// (events.jsonl, stderr.txt). Um jobs.json corrompido é posto de lado e registrado no log.
/// </summary>
public sealed class ApiResumeStore(string root)
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static string DefaultRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WinStatusBar", "CodexApiResume");

    public string JobsFile => Path.Combine(root, "jobs.json");
    public string FolderOf(string id) => Path.Combine(root, id);
    public string EventsFile(string id) => Path.Combine(FolderOf(id), "events.jsonl");
    public string StderrFile(string id) => Path.Combine(FolderOf(id), "stderr.txt");

    public List<ApiResumeJob> Load(Action<string>? log = null)
    {
        if (!File.Exists(JobsFile)) return [];
        try
        {
            return JsonSerializer.Deserialize<List<ApiResumeJob>>(File.ReadAllText(JobsFile), Options)?.Where(j => j is not null).ToList() ?? [];
        }
        catch (JsonException)
        {
            string aside = Path.Combine(root, $"jobs.corrompido-{DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture)}.json");
            try { File.Move(JobsFile, aside, overwrite: true); } catch (IOException) { }
            log?.Invoke($"Continuar pela API: jobs.json corrompido; posto de lado em {Path.GetFileName(aside)}");
            return [];
        }
    }

    public void Save(IEnumerable<ApiResumeJob> jobs) =>
        CodexIntegration.WriteAtomic(JobsFile, JsonSerializer.Serialize(jobs.ToList(), Options));

    /// <summary>Tira os terminados há mais de 7 dias e guarda só os 20 terminados mais novos; os não terminados sempre ficam.</summary>
    public static List<ApiResumeJob> Prune(IEnumerable<ApiResumeJob> jobs, DateTimeOffset now)
    {
        var list = jobs.ToList();
        var finishedKept = list.Where(j => j.IsFinished && now - (j.EndedAt ?? j.UpdatedAt) <= TimeSpan.FromDays(7))
            .OrderByDescending(j => j.EndedAt ?? j.UpdatedAt).Take(20).ToHashSet();
        return list.Where(j => !j.IsFinished || finishedKept.Contains(j)).ToList();
    }

    /// <summary>Apaga as pastas de continuações que não estão mais no registro.</summary>
    public void DeleteOrphanFolders(IEnumerable<ApiResumeJob> kept)
    {
        if (!Directory.Exists(root)) return;
        var ids = kept.Select(j => j.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var dir in Directory.EnumerateDirectories(root))
        {
            if (ids.Contains(Path.GetFileName(dir))) continue;
            try { Directory.Delete(dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }
}
