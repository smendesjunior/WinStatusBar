using System.Text.Json;
using Windows.Security.Credentials;
using WinStatusBar.Core.Ai;
using WinStatusBar.Core.Settings;

namespace CodexIAs.Providers;

/// <summary>
/// Um serviço com chave da API configurado na barra (DeepSeek, Grok, Google Gemini pela chave ou uma API própria): os
/// modelos dele no Codex, pela mesma chamada que a barra faz. A chave fica no Cofre de Credenciais do Windows, onde a
/// barra a guardou; aqui ela só é lida na hora do pedido, nunca gravada nem registrada.
/// </summary>
internal sealed partial class ApiProvider : IProvider
{
    public sealed record Service(string Id, string Name, string Kind, string BaseUrl, string Resource);

    private readonly Service _service;
    public ApiProvider(Service service) => _service = service;

    public string Name => "api-" + _service.Id;
    public string Label => _service.Name;

    /// <summary>Os mesmos serviços de AdditionalAiAccounts.Providers, na barra (os que usam chave da API).</summary>
    private static readonly Service[] Known =
    [
        new("gemini", "Google Gemini (API)", "openai", "https://generativelanguage.googleapis.com/v1beta/openai", "WinStatusBar.Ai.gemini"),
        new("deepseek", "DeepSeek", "openai", "https://api.deepseek.com", "WinStatusBar.Ai.deepseek"),
        new("grok", "Grok", "openai", "https://api.x.ai/v1", "WinStatusBar.Ai.grok"),
    ];

    internal static string? Key(string resource)
    {
        try { var c = new PasswordVault().Retrieve(resource, "api-key"); c.RetrievePassword(); return c.Password; }
        catch { return null; }
    }

    /// <summary>Os serviços conhecidos e as APIs próprias cadastradas na barra (a da OpenAI fica com "OpenAI — minha API").</summary>
    public static IReadOnlyList<ApiProvider> All()
    {
        var list = Known.Select(s => new ApiProvider(s)).ToList();
        try
        {
            // BarSettings abre o settings.json sem travar a gravação da barra e guarda a leitura enquanto o arquivo não muda.
            foreach (var api in BarSettings.Load().CustomApis)
            {
                if (string.IsNullOrWhiteSpace(api.BaseUrl) || IsOpenAi(api.BaseUrl)) continue;
                string name = string.IsNullOrWhiteSpace(api.Name) ? new Uri(api.BaseUrl).Host : api.Name.Trim();
                string id = "propria-" + Slug.From(name);
                if (list.Any(p => p._service.Id == id)) continue;
                list.Add(new(new(id, name, api.Kind, api.BaseUrl.Trim(), api.Resource)));
            }
        }
        catch (Exception ex) { Log.Write("APIs da barra: configurações não lidas: " + ex.Message); }
        return list;
    }

    /// <summary>
    /// Nome no seletor do Codex: o modelo primeiro e a origem depois ("Gemini 3 Flash Preview · Google API"). O seletor
    /// corta o fim dos nomes longos; com "Google Gemini (API) — …" na frente, o nome do modelo sumia.
    /// </summary>
    internal string MenuName(string modelLabel) => $"{modelLabel} · {Origin}";

    /// <summary>Origem curta: "Google API" para o Gemini; nos demais, o nome do serviço com "API" (sem repetir).</summary>
    internal string Origin => _service.Id == "gemini" ? "Google API"
        : _service.Name.Contains("API", StringComparison.OrdinalIgnoreCase) ? _service.Name : _service.Name + " API";

    private static bool IsOpenAi(string baseUrl)
        => Uri.TryCreate(baseUrl.Trim(), UriKind.Absolute, out var u) && u.Host.Equals("api.openai.com", StringComparison.OrdinalIgnoreCase);

    private AgentPrompt.ApiConfig? Config() => Key(_service.Resource) is { Length: > 0 } key ? new(_service.Kind, _service.BaseUrl, key) : null;

    /// <summary>O serviço recusou este modelo no último teste ou pedido (fica fora das listas).</summary>
    internal bool Refuses(string modelId) => ApiModelAvailability.IsRefused(_service.BaseUrl, modelId);

    /// <summary>
    /// Modelos de conversa do serviço que respondem de verdade: cada um listado recebe um pedido mínimo ("ok"). Os que o
    /// serviço recusa (aposentado, sem acesso no plano) ficam fora do Codex, da barra e do Office, com o motivo no relatório.
    /// </summary>
    public async Task<Listing> ListAsync(CancellationToken ct)
    {
        if (Config() is not { } config) return Listing.Off("sem chave da API na barra");
        var catalog = await AgentPrompt.CatalogAsync(AgentPrompt.Target.Api, config, ct, includeRefused: true);
        if (catalog.Error is { Length: > 0 } error)
        {
            // Chave recusada: não adianta manter as opções no Codex. Falha de rede: mantém as de antes.
            bool refused = error.StartsWith("HTTP 401", StringComparison.Ordinal) || error.StartsWith("HTTP 403", StringComparison.Ordinal);
            return refused ? Listing.Off("a API recusou a chave (" + error + ")") : Listing.Fail(error);
        }
        var chat = catalog.Models.Where(m => IsChatModel(m.Id, m.Label)).ToList();
        var (usable, refusedModels, noQuota) = await ApiModelAvailability.CheckAsync(_service.BaseUrl, chat, m => m.Id,
            (m, token) => AgentPrompt.ProbeApiModelAsync(config, m.Id, token), ct);
        foreach (var (m, why) in refusedModels) Log.Write($"{_service.Name}: {m.Label} fora ({why})");
        string prefix = CodexCatalog.Prefix + "api-" + Slug.From(_service.Id) + "-";
        var models = usable
            .Select(m => new AiModel(prefix + Slug.From(m.Id), Name, m.Id, MenuName(m.Label),
                $"{_service.Name} pela sua chave da API (cobrança da própria API), pela WinStatusBar. Só texto.",
                m.Efforts, m.DefaultEffort, -2))
            .DistinctBy(m => m.Slug).DistinctBy(m => m.DisplayName, StringComparer.OrdinalIgnoreCase).ToList();
        string? issue = models.Count == 0 && refusedModels.Count > 0 ? "o serviço recusou todos os modelos testados" : null;
        return new(models, issue)
        {
            Refused = refusedModels.Select(r => $"{r.Model.Label} ({r.Reason})").ToList(),
            NoQuota = noQuota.Select(m => m.Label).ToList(),
        };
    }

    /// <summary>
    /// Só modelos de conversa entram no Codex: as APIs também listam voz, transcrição, embeddings, imagem, vídeo, música,
    /// tempo real e agentes próprios do serviço, que não respondem a um chat de texto.
    /// </summary>
    internal static bool IsChatModel(string id, string label)
        => !NotChat().IsMatch(id) && !NotChat().IsMatch(label);

    [System.Text.RegularExpressions.GeneratedRegex(@"(embed|\btts\b|audio|\bimage|imagen|transcri|realtime|\blive\b|whisper|dall-?e|moderation|banana|lyria|\bveo\b|robotics|computer.?use|\baqa\b|attributed question|deep.?research|antigravity|\bomni\b|speech|\bsora\b)",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase)]
    private static partial System.Text.RegularExpressions.Regex NotChat();

    public async Task<Answer> RunAsync(AiModel model, string? effort, Conversation conversation, Action<string> onText, CancellationToken ct)
    {
        if (Config() is not { } config) return new("", $"A chave da API do {_service.Name} não está mais na barra. Cadastre de novo em Configurações › IAs e contas.");
        var result = await AgentPrompt.RunAsync(AgentPrompt.Target.Api, ConversationReader.WithTranscript(conversation, 40000),
            model.Id, effort, config, AgentPrompt.Options.None, onText, ct);
        return new(result.Text, result.Error);
    }
}
