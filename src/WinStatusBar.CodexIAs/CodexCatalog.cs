using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace CodexIAs;

/// <summary>
/// Catálogo do seletor de modelos do Codex (model_catalog_json do config.toml), mantido pela verificação da barra:
/// - entradas codexias-* (as IAs configuradas na barra) no lugar das antigas winstatusbar-*;
/// - modelos novos da OpenAI (OpenAiCatalog);
/// - ordem do seletor (o Codex ordena por "priority"): modelos da OpenAI primeiro (os da conta na ordem da própria
///   OpenAI, depois as opções "OpenAI — minha API") e todo o resto em ordem alfabética.
/// As entradas da conta não são alteradas, exceto a posição no arquivo.
/// </summary>
public static class CodexCatalog
{
    public const string Prefix = "codexias-";
    private const string OldPrefix = "winstatusbar-";

    private static readonly JsonSerializerOptions Pretty = new() { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>Caminho do catálogo: model_catalog_json do config.toml; sem ele, null (o Codex usa o catálogo próprio).</summary>
    public static string? CatalogPath()
    {
        string cfg = Path.Combine(Paths.CodexHome, "config.toml");
        if (!File.Exists(cfg)) return null;
        string top = File.ReadAllText(cfg).Split("\n[", 2)[0];
        var m = Regex.Match(top, @"(?m)^\s*model_catalog_json\s*=\s*(?:""((?:[^""\\]|\\.)*)""|'([^']*)')");
        if (!m.Success) return null;
        return m.Groups[1].Success ? m.Groups[1].Value.Replace(@"\\", @"\") : m.Groups[2].Value;
    }

    private static readonly Dictionary<string, string> EffortText = new()
    {
        ["none"] = "Definido pela própria IA", ["minimal"] = "Mínimo", ["low"] = "Baixo", ["medium"] = "Médio", ["high"] = "Alto",
        ["xhigh"] = "Muito alto", ["max"] = "Máximo", ["ultra"] = "Ultra",
    };

    /// <summary>
    /// Janela de contexto declarada ao Codex para as IAs da barra. Elas nunca recebem a conversa inteira: o encaminhador
    /// manda só o pacote de passagem (ConversationReader.WithTranscript, de 8 mil a 40 mil caracteres). Com 128 mil, trocar
    /// para uma delas no meio de uma conversa longa fazia o Codex compactar antes de enviar ("model_downshift"), primeiro
    /// com o modelo anterior (que podia estar sem cota) e depois com o novo, e o turno falhava. Com uma janela grande, a
    /// conversa segue inteira no Codex e o encaminhador continua decidindo o que vai para a IA.
    /// </summary>
    internal const int ContextWindow = 10_000_000;

    /// <summary>Entrada do catálogo no mesmo formato das demais (base: a entrada que a ponte da barra usava).</summary>
    public static JsonObject Entry(AiModel m)
    {
        var levels = new JsonArray();
        var efforts = m.Efforts.Count > 0 ? m.Efforts : ["none"];
        foreach (var e in efforts) levels.Add(new JsonObject { ["effort"] = e, ["description"] = EffortText.GetValueOrDefault(e, e) });
        string defaultEffort = m.Efforts.Count > 0 ? m.DefaultEffort ?? m.Efforts[0] : "none";
        return new JsonObject
        {
            ["additional_speed_tiers"] = new JsonArray(),
            ["apply_patch_tool_type"] = null,
            ["auto_compact_token_limit"] = null,
            ["availability_nux"] = null,
            ["base_instructions"] = "Responda ao pedido da pessoa em texto, no idioma dela.",
            ["context_window"] = ContextWindow,
            ["default_reasoning_level"] = defaultEffort,
            ["default_reasoning_summary"] = "auto",
            ["default_service_tier"] = null,
            ["default_verbosity"] = null,
            ["description"] = m.Description,
            ["display_name"] = m.DisplayName,
            ["effective_context_window_percent"] = 95,
            ["experimental_supported_tools"] = new JsonArray(),
            ["include_apps_usage_instructions"] = false,
            ["include_plugin_usage_instructions"] = false,
            ["include_skills_usage_instructions"] = false,
            ["input_modalities"] = new JsonArray("text"),
            ["max_context_window"] = ContextWindow,
            ["model_messages"] = null,
            ["priority"] = m.Priority,
            ["service_tiers"] = new JsonArray(),
            ["shell_type"] = "unified_exec",
            ["slug"] = m.Slug,
            ["support_verbosity"] = false,
            ["supported_in_api"] = true,
            ["supported_reasoning_levels"] = levels,
            ["supports_image_detail_original"] = false,
            ["supports_parallel_tool_calls"] = false,
            ["supports_reasoning_summaries"] = false,
            ["supports_reasoning_summary_parameter"] = false,
            ["supports_search_tool"] = false,
            ["truncation_policy"] = new JsonObject { ["limit"] = 10000, ["mode"] = "tokens" },
            ["upgrade"] = null,
            ["visibility"] = "list",
            ["web_search_tool_type"] = "text",
        };
    }

    internal static string SlugOf(JsonNode? n) => n?["slug"]?.GetValue<string>() ?? "";
    internal static int PriorityOf(JsonNode? n) => n?["priority"] is JsonValue v && v.TryGetValue<int>(out int p) ? p : int.MaxValue;
    private static string NameOf(JsonNode? n) => n?["display_name"]?.GetValue<string>() ?? SlugOf(n);
    private static bool Hidden(JsonNode? n) => n?["visibility"]?.GetValue<string>() == "hide";

    /// <summary>Modelo da OpenAI: os da conta (gpt-*, o3…, codex-*) e as opções "OpenAI — minha API".</summary>
    public static bool IsOpenAi(string slug)
        => !slug.StartsWith(Prefix, StringComparison.Ordinal)
           && (slug.StartsWith("gpt-", StringComparison.Ordinal) || slug.StartsWith("openai-", StringComparison.Ordinal)
               || slug.StartsWith("chatgpt", StringComparison.Ordinal) || slug.StartsWith("codex-", StringComparison.Ordinal) || Regex.IsMatch(slug, @"^o\d"));

    /// <summary>Tira as entradas codexias-* e winstatusbar-* e põe as atuais no lugar (a ordem final vem de Order).</summary>
    internal static void PlaceEntries(JsonArray list, IReadOnlyList<AiModel> models)
    {
        int insertAt = -1;
        for (int i = list.Count - 1; i >= 0; i--)
        {
            string slug = SlugOf(list[i]);
            if (slug.StartsWith(Prefix, StringComparison.Ordinal) || slug.StartsWith(OldPrefix, StringComparison.Ordinal)) { list.RemoveAt(i); insertAt = i; }
        }
        if (insertAt < 0) insertAt = list.Count;
        foreach (var m in models.Reverse()) list.Insert(insertAt, Entry(m));
    }

    private static readonly StringComparer Alphabetical = StringComparer.Create(CultureInfo.GetCultureInfo("pt-BR"), CompareOptions.IgnoreCase);

    /// <summary>
    /// Ordem do seletor: modelos da OpenAI da conta (pela prioridade da própria OpenAI), depois "OpenAI — minha API"
    /// (os dois atalhos fixos antes), depois todo o resto em ordem alfabética do nome. Ajusta "priority" e a posição no
    /// arquivo; entradas ocultas ficam no fim, como estão.
    /// </summary>
    internal static void Order(JsonArray list)
    {
        var nodes = list.Select((n, i) => (Node: n!, Index: i)).ToList();
        var visible = nodes.Where(x => !Hidden(x.Node)).ToList();
        var account = visible.Where(x => IsOpenAi(SlugOf(x.Node)) && !SlugOf(x.Node).StartsWith(OpenAiCatalog.ApiPrefix, StringComparison.Ordinal))
            .OrderBy(x => PriorityOf(x.Node)).ThenBy(x => x.Index).ToList();
        var api = visible.Where(x => SlugOf(x.Node).StartsWith(OpenAiCatalog.ApiPrefix, StringComparison.Ordinal))
            .OrderBy(x => OpenAiCatalog.IsFixedAlias(SlugOf(x.Node)) ? 0 : 1).ThenBy(x => PriorityOf(x.Node)).ThenBy(x => NameOf(x.Node), Alphabetical).ToList();
        var others = visible.Where(x => !IsOpenAi(SlugOf(x.Node))).OrderBy(x => NameOf(x.Node), Alphabetical).ThenBy(x => SlugOf(x.Node), StringComparer.Ordinal).ToList();
        var hidden = nodes.Where(x => Hidden(x.Node)).ToList();

        // Os da conta mantêm a prioridade que a OpenAI deu; os demais vêm logo depois, em sequência.
        int next = account.Count == 0 ? 0 : account.Max(x => PriorityOf(x.Node)) + 1;
        foreach (var x in api) x.Node["priority"] = next++;
        foreach (var x in others) x.Node["priority"] = next++;

        var ordered = account.Concat(api).Concat(others).Concat(hidden).Select(x => x.Node).ToList();
        list.Clear();
        foreach (var n in ordered) list.Add(n);
    }

    /// <summary>Catálogo completo depois de aplicar as entradas da barra, os modelos novos da OpenAI e a ordem.</summary>
    /// <param name="extra">Ajuste a mais na lista antes da ordem (ex.: modelos do Ollama).</param>
    public static string Apply(string json, IReadOnlyList<AiModel> models, JsonArray? openAi, ref OpenAiCatalog.State? state,
        OpenAiCatalog.Changes? changes = null, bool removeObsolete = false, Action<JsonArray>? extra = null)
    {
        var root = JsonNode.Parse(json) as JsonObject ?? throw new InvalidDataException("Catálogo do Codex inválido.");
        if (root["models"] is not JsonArray list) throw new InvalidDataException("Catálogo do Codex sem a lista de modelos.");
        if (openAi is not null) OpenAiCatalog.MergeInto(list, openAi, ref state, changes, removeObsolete);
        extra?.Invoke(list);
        PlaceEntries(list, models);
        Order(list);
        return root.ToJsonString(Pretty);
    }

    /// <summary>Grava o catálogo se algo mudou. Devolve true quando mudou.</summary>
    public static bool Sync(IReadOnlyList<AiModel> models, JsonArray? openAi = null, OpenAiCatalog.Changes? changes = null, bool removeObsolete = false,
        Action<JsonArray>? extra = null)
    {
        string? path = CatalogPath();
        if (path is null || !File.Exists(path)) throw new InvalidOperationException("catálogo do Codex não encontrado (model_catalog_json no config.toml)");
        string json = File.ReadAllText(path);
        var state = openAi is null ? null : OpenAiCatalog.LoadState();
        string result = Apply(json, models, openAi, ref state, changes, removeObsolete, extra);
        if (openAi is not null && state is not null) OpenAiCatalog.SaveState(state);
        if (result == json) return false;
        WriteAtomic(path, result);
        Log.Write($"Catálogo do Codex atualizado: {models.Count} opções da barra, OpenAI primeiro e o resto em ordem alfabética.");
        return true;
    }

    /// <summary>Arquivo temporário + troca, para nunca deixar o catálogo pela metade (o Codex pode lê-lo a qualquer hora).</summary>
    internal static void WriteAtomic(string path, string text)
    {
        string temp = path + ".winstatusbar-tmp";
        File.WriteAllText(temp, text, new System.Text.UTF8Encoding(false));
        File.Move(temp, path, overwrite: true);
    }
}
