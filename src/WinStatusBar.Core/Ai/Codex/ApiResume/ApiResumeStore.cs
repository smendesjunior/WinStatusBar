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

    public List<ApiResumeJob> Load(Action<string>? log = null) => Load(out _, log);

    /// <summary>
    /// Lê o registro. <paramref name="corrupt"/>: o arquivo existia mas não era um registro válido (foi posto de lado), então
    /// a lista vazia não quer dizer "nenhuma continuação". Arquivo em uso ou sem permissão: tenta mais duas vezes e, se não
    /// der, lança (IOException ou UnauthorizedAccessException) em vez de fingir que o registro está vazio.
    /// </summary>
    public List<ApiResumeJob> Load(out bool corrupt, Action<string>? log = null)
    {
        corrupt = false;
        if (ReadJobsFile() is not { } text) return [];
        try
        {
            return JsonSerializer.Deserialize<List<ApiResumeJob>>(text, Options)?.Where(j => j is not null).ToList() ?? [];
        }
        catch (JsonException)
        {
            corrupt = true;
            string aside = Path.Combine(root, $"jobs.corrompido-{DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture)}.json");
            try { File.Move(JobsFile, aside, overwrite: true); } catch (IOException) { }
            log?.Invoke($"Continuar pela API: jobs.json corrompido; posto de lado em {Path.GetFileName(aside)}");
            return [];
        }
    }

    internal const int ReadAttempts = 3;
    internal static TimeSpan ReadRetryDelay { get; set; } = TimeSpan.FromMilliseconds(200);

    /// <summary>Texto do jobs.json; null quando ele não existe. Antivírus, backup ou sincronização seguram o arquivo por instantes.</summary>
    private string? ReadJobsFile()
    {
        for (int attempt = 1; ; attempt++)
        {
            if (!File.Exists(JobsFile)) return null;
            try { return File.ReadAllText(JobsFile); }
            catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException) { return null; }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException && attempt < ReadAttempts) { Thread.Sleep(ReadRetryDelay); }
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
