using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CodexIAs;

/// <summary>
/// Modelos do Ollama no seletor do Codex quando o Codex já usa o Ollama ("ollama launch codex"): o próprio Ollama atende
/// esses modelos como agentes completos do Codex (com ferramentas). O "ollama launch" grava a lista uma vez só; aqui, na
/// verificação manual, os modelos novos do Ollama entram do mesmo jeito (catálogo + roteamento do Ollama) e os apagados
/// do Ollama saem. Só mexe nas entradas que estão no roteamento do Ollama.
/// </summary>
internal static class OllamaCatalog
{
    public static string RoutingFile => Path.Combine(Paths.CodexHome, "ollama-launch-codex-routing.json");
    /// <summary>O Codex usa o Ollama ("ollama launch codex" gravou o roteamento).</summary>
    public static bool InUse => File.Exists(RoutingFile);

    private static readonly HttpClient Http = new() { BaseAddress = new Uri("http://127.0.0.1:11434"), Timeout = TimeSpan.FromSeconds(10) };
    private static readonly JsonSerializerOptions Pretty = new() { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>Modelo de conversa instalado no Ollama: nome, se pensa, se lê imagem e o contexto máximo.</summary>
    public sealed record Local(string Name, bool Thinking, bool Vision, int Context);

    /// <summary>Modelos de conversa instalados no Ollama (sem os de embedding); null quando o Ollama não respondeu.</summary>
    public static async Task<IReadOnlyList<Local>?> InstalledAsync(CancellationToken ct)
    {
        JsonObject? tags;
        try { tags = await Http.GetFromJsonAsync<JsonObject>("/api/tags", ct); }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        { Log.Write("Ollama: lista de modelos não lida: " + ex.Message); return null; }
        var list = new List<Local>();
        foreach (var m in tags?["models"] as JsonArray ?? [])
        {
            string? name = m?["name"]?.GetValue<string>() ?? m?["model"]?.GetValue<string>();
            if (string.IsNullOrEmpty(name)) continue;
            bool chat = true, thinking = false, vision = false;
            int context = 32768;
            try
            {
                using var body = new StringContent(JsonSerializer.Serialize(new { model = name }), System.Text.Encoding.UTF8, "application/json");
                using var response = await Http.PostAsync("/api/show", body, ct);
                var show = await response.Content.ReadFromJsonAsync<JsonObject>(ct);
                var caps = (show?["capabilities"] as JsonArray)?.Select(c => c?.GetValue<string>()).OfType<string>().ToList();
                if (caps is { Count: > 0 }) { chat = caps.Contains("completion"); thinking = caps.Contains("thinking"); vision = caps.Contains("vision"); }
                if (show?["model_info"] is JsonObject info)
                    foreach (var (key, value) in info)
                        if (key.EndsWith(".context_length", StringComparison.Ordinal) && value is JsonValue v && v.TryGetValue<long>(out long c) && c > 0)
                        { context = (int)Math.Min(c, int.MaxValue); break; }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch { }
            if (chat) list.Add(new(name, thinking, vision, context));
        }
        return list;
    }

    public static JsonObject LoadRouting()
    {
        try { if (JsonNode.Parse(File.ReadAllText(RoutingFile)) is JsonObject r) return r; } catch { }
        return new JsonObject { ["models"] = new JsonArray() };
    }

    public static void SaveRouting(JsonObject routing) => CodexCatalog.WriteAtomic(RoutingFile, routing.ToJsonString(Pretty));

    private static string NameOf(JsonNode? n) => n?["display_name"]?.GetValue<string>() ?? CodexCatalog.SlugOf(n);

    /// <summary>
    /// Pensamento do modelo no roteamento do Ollama (mesmo formato que o "ollama launch" grava). Nos modelos locais ele é
    /// só ligado/desligado, e o Codex não mostra o nível "none" (os modelos dele começam em "low"): com "none" e "high" o
    /// seletor ficava só com "Alto". O desligado vai como "low" (Baixo) e o ligado como "high" (Alto); "none" continua
    /// valendo desligado para pedidos antigos.
    /// </summary>
    private static JsonObject ThinkingOf(bool thinking) => thinking
        ? new JsonObject
        {
            ["supported"] = true, ["levels"] = new JsonArray("low", "high"),
            ["values"] = new JsonObject { ["none"] = false, ["low"] = false, ["high"] = true },
            ["controls"] = new JsonObject { ["values"] = new JsonArray(false, true), ["default"] = true },
        }
        : new JsonObject
        {
            ["supported"] = true, ["levels"] = new JsonArray("none"),
            ["values"] = new JsonObject { ["none"] = false },
            ["controls"] = new JsonObject { ["values"] = new JsonArray(false), ["default"] = false },
        };

    /// <summary>Níveis do modelo no catálogo do Codex, os mesmos do roteamento.</summary>
    private static JsonArray LevelsOf(bool thinking) => thinking
        ? new JsonArray(new JsonObject { ["effort"] = "low", ["description"] = "Sem raciocínio, mais rápido" },
            new JsonObject { ["effort"] = "high", ["description"] = "Pensa antes de responder" })
        : new JsonArray(new JsonObject { ["effort"] = "none", ["description"] = "Pensamento desligado" });

    /// <summary>Modelos que pensam com "none" e "high" (como o "ollama launch" grava) passam a Baixo e Alto.</summary>
    private static void UpgradeThinkingLevels(JsonArray catalog, JsonArray routes)
    {
        foreach (var route in routes.OfType<JsonObject>())
        {
            if (route["thinking"]?["levels"] is not JsonArray levels
                || !levels.Select(l => l?.GetValue<string>()).SequenceEqual(["none", "high"])) continue;
            string? slug = route["slug"]?.GetValue<string>();
            route["thinking"] = ThinkingOf(true);
            foreach (var entry in catalog.OfType<JsonObject>().Where(n => CodexCatalog.SlugOf(n) == slug))
            {
                entry["supported_reasoning_levels"] = LevelsOf(true);
                if (entry["default_reasoning_level"]?.GetValue<string>() != "high") entry["default_reasoning_level"] = "low";
            }
            Log.Write($"Ollama: {slug} com os níveis Baixo e Alto no seletor do Codex");
        }
    }

    /// <summary>
    /// Acrescenta os modelos novos do Ollama ao catálogo e ao roteamento e tira os que foram apagados do Ollama.
    /// Devolve os nomes que entraram e os que saíram.
    /// </summary>
    public static (List<string> Added, List<string> Removed) MergeInto(JsonArray catalog, JsonObject routing, IReadOnlyList<Local> installed)
    {
        if (routing["models"] is not JsonArray routes) routing["models"] = routes = new JsonArray();
        UpgradeThinkingLevels(catalog, routes);
        var names = installed.Select(i => i.Name).ToHashSet(StringComparer.Ordinal);
        var added = new List<string>();
        var removed = new List<string>();

        // Apagados do Ollama: estavam no roteamento (eram do Ollama) e não estão mais instalados.
        for (int i = routes.Count - 1; i >= 0; i--)
        {
            string? slug = routes[i]?["slug"]?.GetValue<string>();
            if (slug is null || names.Contains(slug)) continue;
            routes.RemoveAt(i);
            for (int j = catalog.Count - 1; j >= 0; j--)
                if (CodexCatalog.SlugOf(catalog[j]) == slug) { removed.Insert(0, NameOf(catalog[j])); catalog.RemoveAt(j); }
            Log.Write($"Ollama: {slug} saiu do seletor do Codex (não está mais no Ollama)");
        }

        var routed = routes.Select(r => r?["slug"]?.GetValue<string>()).OfType<string>().ToHashSet(StringComparer.Ordinal);
        // Base de uma entrada nova: outra do Ollama (mesmas instruções e ferramentas do Codex) ou, sem nenhuma, uma da conta.
        var template = catalog.FirstOrDefault(n => routed.Contains(CodexCatalog.SlugOf(n)))
                       ?? catalog.FirstOrDefault(n => CodexCatalog.IsOpenAi(CodexCatalog.SlugOf(n)) && n?["visibility"]?.GetValue<string>() != "hide");
        foreach (var local in installed.Where(l => !routed.Contains(l.Name)))
        {
            if (catalog.Any(n => CodexCatalog.SlugOf(n) == local.Name)) continue; // já está no seletor por outro caminho
            var entry = template?.DeepClone() as JsonObject ?? new JsonObject();
            bool cloud = local.Name.Contains("cloud", StringComparison.OrdinalIgnoreCase);
            entry["slug"] = local.Name;
            entry["display_name"] = local.Name + (cloud ? " — Ollama" : " — local");
            entry["description"] = cloud ? "Ollama (nuvem), pelo próprio Ollama. Acrescentado pela verificação da WinStatusBar."
                : "Ollama: modelo deste PC, pelo próprio Ollama. Acrescentado pela verificação da WinStatusBar.";
            entry["context_window"] = local.Context;
            entry["max_context_window"] = local.Context;
            entry["input_modalities"] = local.Vision ? new JsonArray("text", "image") : new JsonArray("text");
            entry["supported_reasoning_levels"] = LevelsOf(local.Thinking);
            entry["default_reasoning_level"] = local.Thinking ? "high" : "none";
            entry["visibility"] = "list";
            entry["priority"] = 1000;
            // Como as entradas que o "ollama launch" grava: sem recursos próprios da OpenAI.
            entry["apply_patch_tool_type"] = null;
            entry["supports_reasoning_summaries"] = false;
            entry["supports_reasoning_summary_parameter"] = false;
            entry["support_verbosity"] = false;
            entry["default_verbosity"] = null;
            entry["service_tiers"] = new JsonArray();
            entry["additional_speed_tiers"] = new JsonArray();
            entry["default_service_tier"] = null;
            entry["availability_nux"] = null;
            entry["upgrade"] = null;
            catalog.Add(entry);
            routes.Add(new JsonObject { ["slug"] = local.Name, ["thinking"] = ThinkingOf(local.Thinking) });
            added.Add(NameOf(entry));
            Log.Write($"Ollama: {local.Name} entrou no seletor do Codex");
        }
        return (added, removed);
    }

    /// <summary>Quantos modelos do Ollama estão no roteamento (no seletor do Codex).</summary>
    public static int Count(JsonObject routing) => (routing["models"] as JsonArray)?.Count ?? 0;
}
