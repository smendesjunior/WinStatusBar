using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
namespace WinStatusBar.Core.Ai;

/// <summary>Um caminho que já deu certo neste PC: o pedido, o app, os passos conferidos e o que falhou antes.</summary>
public sealed record DesktopRecipe(string Request, string? App, string[] Steps, string[] Avoid, int Uses, DateTime LastUsed);

/// <summary>
/// Registro pequeno de experiência do executor, comum a todas as IAs: guarda o que deu certo (e os desvios que falharam)
/// e devolve os casos parecidos no próximo pedido, para a IA reaproveitar o caminho em vez de replanejar do zero.
/// Fica só neste PC (arquivo local), limitado em tamanho, sem senhas nem textos longos.
/// </summary>
public sealed class DesktopExperience
{
    public const int MaxRecipes = 80, MaxRecalled = 3;
    private const double MinSimilarity = 0.34;
    private static readonly JsonSerializerOptions FileJson = new()
    {
        WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };
    private static readonly JsonSerializerOptions StepJson = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull, PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };
    private static readonly Regex Sensitive = new(@"\b(senha|password|passwd|token|api[ _-]?key|chave de api|cart[aã]o|cvv|pin)\b", RegexOptions.IgnoreCase);
    private static readonly HashSet<string> Stop = new(StringComparer.Ordinal)
    {
        "de","do","da","dos","das","para","com","uma","um","que","por","no","na","nos","nas","em","ao","aos","os","as","e","o","a",
        "me","meu","minha","depois","entao","pra","the","and","isso","esse","essa","este","esta","sem","mais","favor","por favor",
    };
    private readonly string _path;
    private readonly object _gate = new();
    private List<DesktopRecipe> _recipes;

    public DesktopExperience(string path)
    {
        _path = path;
        try { _recipes = File.Exists(path) ? JsonSerializer.Deserialize<List<DesktopRecipe>>(File.ReadAllText(path)) ?? [] : []; }
        catch { _recipes = []; } // arquivo corrompido não impede o executor; é reescrito no próximo acerto
    }

    public IReadOnlyList<DesktopRecipe> All { get { lock (_gate) return _recipes.ToList(); } }

    /// <summary>Até 3 casos parecidos (palavras do pedido + mesmo app), do mais parecido para o menos.</summary>
    public IReadOnlyList<DesktopRecipe> Recall(string request, string? app)
    {
        var words = Words(Clean(request));
        if (words.Count == 0) return [];
        lock (_gate)
            return _recipes.Select(r => (r, score: Similarity(words, Words(r.Request)) + (app is not null && string.Equals(app, r.App, StringComparison.OrdinalIgnoreCase) ? 0.1 : 0)))
                .Where(x => x.score >= MinSimilarity).OrderByDescending(x => x.score).ThenByDescending(x => x.r.Uses)
                .Take(MaxRecalled).Select(x => x.r).ToList();
    }

    /// <summary>Grava (ou atualiza o caso parecido) depois de um pedido concluído e conferido.</summary>
    public bool Remember(string request, string? app, IReadOnlyList<DesktopActionRequest> steps, IReadOnlyList<string> avoid)
    {
        request = Clean(request);
        if (Sensitive.IsMatch(request) || steps.Any(s => Sensitive.IsMatch(s.Text ?? ""))) return false;
        // Só os passos que fazem algo (observações e listas são diagnóstico, refeitos a cada vez).
        var useful = steps.Where(s => s.Action is not ("context" or "observe" or "observe_foreground" or "list_windows" or "find" or "wait" or "clipboard_get")).ToList();
        if (useful.Count == 0 || useful.All(s => s.Action is "file" && s.Op is "read" or "info" or "exists" or "list")) return false;
        app = AppFromSteps(useful) ?? app;
        string[] lines = useful.Take(20).Select(Compact).ToArray();
        string[] lessons = avoid.Distinct().Take(5).Select(a => Cut(a, 160)).ToArray();
        string shortRequest = Cut(request.Trim(), 240);
        var words = Words(request);
        lock (_gate)
        {
            int same = _recipes.FindIndex(r => string.Equals(r.App, app, StringComparison.OrdinalIgnoreCase) && Similarity(words, Words(r.Request)) >= 0.75);
            if (same >= 0)
            {
                var old = _recipes[same];
                _recipes[same] = new(shortRequest, app, lines, lessons.Concat(old.Avoid).Distinct().Take(5).ToArray(), old.Uses + 1, DateTime.Now);
            }
            else _recipes.Add(new(shortRequest, app, lines, lessons, 1, DateTime.Now));
            // Tamanho fixo: saem os menos usados e mais antigos.
            if (_recipes.Count > MaxRecipes)
                _recipes = _recipes.OrderByDescending(r => r.Uses).ThenByDescending(r => r.LastUsed).Take(MaxRecipes).ToList();
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                string temp = _path + ".tmp";
                File.WriteAllText(temp, JsonSerializer.Serialize(_recipes, FileJson), new UTF8Encoding(false));
                File.Move(temp, _path, overwrite: true);
            }
            catch { return false; }
        }
        return true;
    }

    /// <summary>Texto para as instruções da 1ª rodada (dado, não ordem: a IA confere se serve ao pedido atual).</summary>
    public static string Format(IReadOnlyList<DesktopRecipe> recalled)
    {
        if (recalled.Count == 0) return "";
        var sb = new StringBuilder("[Caminhos que já deram certo neste PC — use como atalho se servirem ao pedido atual; ajuste caminhos e textos]");
        foreach (var r in recalled)
        {
            sb.Append("\n• Pedido parecido").Append(r.App is null ? "" : $" (app {r.App})").Append($", {r.Uses}× com sucesso: \"{r.Request}\"\n  passos: ")
              .Append(string.Join(" → ", r.Steps));
            if (r.Avoid.Length > 0) sb.Append("\n  evite (falhou antes): ").Append(string.Join("; ", r.Avoid));
        }
        return sb.ToString();
    }

    /// <summary>Processo da janela ativa a partir do retrato do PC ("Janela ativa: WINWORD — ...").</summary>
    public static string? AppFromContext(string? context)
        => context is null ? null : Regex.Match(context, @"Janela (?:ativa|de cima[^:]*): (\S+)") is { Success: true } m ? m.Groups[1].Value : null;

    private static string Compact(DesktopActionRequest a)
    {
        var shortened = a with
        {
            Text = a.Text is null ? null : Cut(a.Text, 80), Values = a.Values is null ? null : [["…"]],
            Observation = a.Action == "system" && a.Op == "recipe" ? "novo GUID" : null, Window = 0,
        };
        string json = JsonSerializer.Serialize(shortened, StepJson);
        return json.Replace(",\"window\":0", "").Replace("\"window\":0,", "");
    }
    /// <summary>O pedido do usuário sem os acréscimos da barra (formato da resposta, contexto anexado: linha em branco + "[").</summary>
    private static string Clean(string request) { int cut = request.IndexOf("\n\n[", StringComparison.Ordinal); return (cut > 0 ? request[..cut] : request).Trim(); }
    /// <summary>App de fato operado: Office, ou o programa aberto/executado; null quando só houve arquivos/comandos.</summary>
    private static string? AppFromSteps(IReadOnlyList<DesktopActionRequest> steps)
    {
        if (steps.FirstOrDefault(s => s.Action == "office")?.Application is { } office) return office.ToLowerInvariant();
        if (steps.FirstOrDefault(s => s.Action is "open" or "launch" && s.Application is not null)?.Application is { } exe) return Path.GetFileNameWithoutExtension(exe).ToLowerInvariant();
        var text = string.Join(" ", steps.Where(s => s.Action is "powershell" or "run").Select(s => (s.Text ?? "") + " " + s.Application));
        return Regex.Match(text, @"(?i)\b(notepad|winword|excel|powerpnt|explorer|msedge|chrome|mspaint|calc)(?:\.exe)?\b") is { Success: true } m ? m.Groups[1].Value.ToLowerInvariant() : null;
    }
    private static string Cut(string s, int n) => s.Length <= n ? s : s[..(n - 1)] + "…";

    private static HashSet<string> Words(string text)
    {
        // Caminhos e endereços não dizem o que a tarefa é: ficam fora da comparação.
        text = Regex.Replace(text, @"[A-Za-z]:\\\S*|\\\\\S*|https?://\S*", " ");
        string plain = new(text.Normalize(NormalizationForm.FormD).Where(c => CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark).ToArray());
        return Regex.Matches(plain.ToLowerInvariant(), @"[a-z][a-z0-9]{2,}")
            .Select(m => m.Value).Where(w => !Stop.Contains(w)).ToHashSet(StringComparer.Ordinal);
    }
    private static double Similarity(HashSet<string> a, HashSet<string> b)
        => a.Count == 0 || b.Count == 0 ? 0 : (double)a.Intersect(b).Count() / a.Union(b).Count();
}
