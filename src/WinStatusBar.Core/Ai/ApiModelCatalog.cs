using System.Text.RegularExpressions;
namespace WinStatusBar.Core.Ai;

public static class ApiModelCatalog
{
    private static Version VersionOf(string id)
    {
        var m = Regex.Match(id, @"^gpt-(\d+)(?:\.(\d+))?");
        return m.Success ? new Version(int.Parse(m.Groups[1].Value), m.Groups[2].Success ? int.Parse(m.Groups[2].Value) : 0) : new Version(0, 0);
    }
    /// <summary>
    /// Níveis de pensamento do modelo pela API: os da tabela (conferidos na API) e, para um modelo que a tabela não
    /// conhece (ex.: lançado depois), os da lista do Codex instalado, que se atualiza sozinho. "ultra" fica de fora
    /// porque é um nível do Codex, não confirmado na API.
    /// </summary>
    public static IReadOnlyList<string> Efforts(string id)
    {
        var known = TableEfforts(id);
        if (known.Count > 0) return known;
        return Codex.CodexModelLevels.Get(id) is { } levels ? levels.Efforts.Where(e => e != "ultra").ToList() : [];
    }

    /// <summary>Nível padrão: o do Codex se for um dos níveis do modelo; senão "medium"; senão o primeiro.</summary>
    public static string? DefaultEffort(string id)
    {
        var efforts = Efforts(id);
        if (efforts.Count == 0) return null;
        if (Codex.CodexModelLevels.Get(id)?.Default is { } def && efforts.Contains(def)) return def;
        return efforts.Contains("medium") ? "medium" : efforts[0];
    }

    private static IReadOnlyList<string> TableEfforts(string id) => id switch
    {
        "gpt-6-astra" or "gpt-6.1-sol" => ["low", "medium", "high", "xhigh", "max"],
        "gpt-6.1-terra" or "gpt-6-sol" or "gpt-6-luna" => ["none", "low", "medium", "high", "xhigh", "max"],
        _ when id.StartsWith("gpt-5.6") => ["none", "low", "medium", "high", "xhigh", "max"],
        "gpt-5.5" or "gpt-5.4" or "gpt-5.4-mini" or "gpt-5.4-nano" or "gpt-5.2" => ["none", "low", "medium", "high", "xhigh"],
        "gpt-5.1" => ["none", "low", "medium", "high"],
        "gpt-5" or "gpt-5-mini" or "gpt-5-nano" => ["minimal", "low", "medium", "high"],
        "o3" or "o4-mini" => ["low", "medium", "high"],
        _ => []
    };
    public static List<AgentPrompt.ModelOption> Recent(IEnumerable<AgentPrompt.ModelOption> models)
    {
        var all = models.Where(m => !Regex.IsMatch(m.Id, @"-\d{4}-\d{2}-\d{2}$")
            && !new[] { "image", "audio", "tts", "transcribe", "realtime", "search", "embedding", "moderation", "instruct", "codex" }.Any(x => m.Id.Contains(x, StringComparison.OrdinalIgnoreCase))
            && (Regex.IsMatch(m.Id, @"^gpt-\d") || m.Id == "chat-latest" || Regex.IsMatch(m.Id, @"^o\d"))).ToList();
        var versions = all.Select(m => Regex.Match(m.Id, @"^gpt-(\d+)(?:\.(\d+))?"))
            .Where(m => m.Success).Select(m => new Version(int.Parse(m.Groups[1].Value), m.Groups[2].Success ? int.Parse(m.Groups[2].Value) : 0)).Distinct().OrderDescending().Take(2).ToList();
        return all.Where(m => { var match = Regex.Match(m.Id, @"^gpt-(\d+)(?:\.(\d+))?"); return match.Success && versions.Contains(new Version(int.Parse(match.Groups[1].Value), match.Groups[2].Success ? int.Parse(match.Groups[2].Value) : 0)) || m.Id == "chat-latest"; })
            .OrderByDescending(m => VersionOf(m.Id)).ThenBy(m => m.Id, StringComparer.OrdinalIgnoreCase)
            .Select(m => m with { Efforts = Efforts(m.Id), DefaultEffort = DefaultEffort(m.Id) }).ToList();
    }
}
