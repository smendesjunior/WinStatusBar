using System.Text.Json;
using System.Text.Json.Nodes;
using CodexIAs.Providers;
using WinStatusBar.Core.Ai.Codex;

namespace CodexIAs;

/// <summary>
/// As opções das IAs configuradas na barra: guardadas em modelos.json (o que a última verificação achou) e relidas das
/// fontes só na verificação manual (Configurações › Codex e modelos › Verificar e atualizar). A verificação também põe o seletor
/// do Codex em dia (só com a ligação ligada) e grava o relatório que a barra mostra: a lista das IAs deste PC, cada uma
/// com o seu estado no Codex.
/// </summary>
internal sealed class ModelRegistry
{
    private readonly IProvider[] _fixed;
    private volatile IReadOnlyList<IProvider> _apis;
    private volatile Dictionary<string, AiModel> _bySlug;
    private readonly SemaphoreSlim _checking = new(1, 1);

    private static readonly JsonSerializerOptions Pretty = new() { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public ModelRegistry()
    {
        _fixed = [new ClaudeProvider(), new WebChatProvider(WebSite.Gemini), new WebChatProvider(WebSite.Copilot), new QiraProvider(), new LocalProvider()];
        _apis = ApiProvider.All();
        _bySlug = Load().ToDictionary(m => m.Slug, StringComparer.Ordinal);
    }

    private IEnumerable<IProvider> Providers => _fixed.Concat(_apis);

    /// <summary>
    /// As opções guardadas, sem as de API que o serviço passou a recusar depois da verificação (um pedido recusado tira
    /// o modelo na hora da lista do Office; o seletor do Codex muda na próxima verificação).
    /// </summary>
    public IReadOnlyCollection<AiModel> Models => _bySlug.Values.Where(m => !Refused(m)).ToList();

    private bool Refused(AiModel m) => _apis.OfType<ApiProvider>().FirstOrDefault(p => p.Name == m.Provider) is { } api && api.Refuses(m.Id);

    private static List<AiModel> Load()
    {
        try { return File.Exists(Paths.ModelsFile) ? JsonSerializer.Deserialize<List<AiModel>>(File.ReadAllText(Paths.ModelsFile)) ?? [] : []; }
        catch (Exception ex) { Log.Write("Lista de modelos guardada não lida: " + ex.Message); return []; }
    }

    /// <summary>A IA e a opção de um identificador pedido pelo Codex (null: não está mais configurada neste PC).</summary>
    public (IProvider Provider, AiModel Model)? Resolve(string slug)
    {
        if (!_bySlug.TryGetValue(slug, out var model)) return null;
        return Providers.FirstOrDefault(p => p.Name == model.Provider) is { } provider ? (provider, model) : null;
    }

    /// <summary>
    /// Relê as IAs (todas, ou só a de <paramref name="only"/>), atualiza a lista guardada e o seletor do Codex e grava o
    /// relatório.
    /// </summary>
    public async Task<CodexIntegration.Report> CheckAsync(bool removeObsolete, CancellationToken ct, string? only = null)
    {
        await _checking.WaitAsync(ct);
        try
        {
            _apis = ApiProvider.All();
            var previousReport = CodexIntegration.LoadReport();
            // Primeira verificação neste PC: tudo seria "novo"; a lista de novidades só vale a partir da segunda.
            bool first = previousReport is null;
            // Só uma IA (botão "Tentar de novo" da linha dela): as demais ficam como estavam. Linhas do próprio seletor do
            // Codex (OpenAI, Ollama) são refeitas em toda verificação.
            var target = only is null ? null : Providers.FirstOrDefault(p => p.Name == only);
            if (only is not null && target is null && only is not ("openai" or "openai-api" or "ollama")) only = null;
            Log.Write("Verificação de modelos iniciada" + (target is not null ? $" (só {target.Label})" : "") + (removeObsolete ? " (com remoção dos obsoletos)" : ""));
            var previous = _bySlug;
            var next = new Dictionary<string, AiModel>(StringComparer.Ordinal);
            var services = new List<CodexIntegration.ServiceReport>();
            foreach (var provider in Providers)
            {
                var before = previous.Values.Where(m => m.Provider == provider.Name).ToList();
                if (only is not null && provider != target)
                {
                    foreach (var m in before) next[m.Slug] = m;
                    services.Add(previousReport?.Servicos.FirstOrDefault(s => s.Id == provider.Name)
                                 ?? new(provider.Label, before.Count, null, false, [], [], provider.Name));
                    continue;
                }
                Listing listing;
                try { listing = await provider.ListAsync(ct); }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { listing = Listing.Fail(ex.Message); }
                var now = listing.Failed ? before : listing.Models.ToList();
                foreach (var m in now) next[m.Slug] = m;
                var beforeSlugs = before.Select(m => m.Slug).ToHashSet(StringComparer.Ordinal);
                var nowSlugs = now.Select(m => m.Slug).ToHashSet(StringComparer.Ordinal);
                var report = new CodexIntegration.ServiceReport(provider.Label, now.Count, listing.Issue, listing.Failed,
                    first ? [] : now.Where(m => !beforeSlugs.Contains(m.Slug)).Select(m => m.DisplayName).ToList(),
                    before.Where(m => !nowSlugs.Contains(m.Slug)).Select(m => m.DisplayName).ToList(),
                    provider.Name, listing.NeedsLogin ? provider.Name : null)
                {
                    Recusados = listing.Failed ? [] : listing.Refused.ToList(),
                    SemCota = listing.Failed ? [] : listing.NoQuota.ToList(),
                };
                services.Add(report);
                Log.Write($"{provider.Label}: {now.Count} opções" + (listing.Issue is { } why ? $" ({(listing.Failed ? "falhou" : "fora")}: {why})" : "")
                    + (report.Novos.Count > 0 ? $"; novas: {string.Join(", ", report.Novos)}" : "") + (report.Sairam.Count > 0 ? $"; saíram: {string.Join(", ", report.Sairam)}" : "")
                    + (report.Recusados.Count > 0 ? $"; recusados pelo serviço: {report.Recusados.Count}" : "") + (report.SemCota.Count > 0 ? $"; sem cota agora: {string.Join(", ", report.SemCota)}" : ""));
            }
            // Uma API própria tirada da barra: as opções dela saem.
            var gone = previous.Values.Where(m => Providers.All(p => p.Name != m.Provider)).ToList();
            if (gone.Count > 0)
                services.Add(new("APIs tiradas da barra", 0, "não estão mais cadastradas na barra", false, [], gone.Select(m => m.DisplayName).ToList(), "removidas"));

            _bySlug = next;
            try { CodexIntegration.WriteAtomic(Paths.ModelsFile, JsonSerializer.Serialize(next.Values.ToList(), Pretty)); }
            catch (Exception ex) { Log.Write("Lista de modelos não gravada: " + ex.Message); }

            var (codex, extra) = await UpdateCodexAsync(next.Values.ToList(), removeObsolete, ct);
            // Com o Codex usando o Ollama, a linha "IA local" é a do Ollama no seletor do Codex.
            if (extra.Any(s => s.Id == "ollama")) services.RemoveAll(s => s.Id == "local");
            services.AddRange(extra);
            var result = new CodexIntegration.Report(DateTime.Now, Program.Version, services, codex);
            try { CodexIntegration.SaveReport(result); } catch (Exception ex) { Log.Write("Relatório não gravado: " + ex.Message); }
            Log.Write("Verificação de modelos concluída");
            return result;
        }
        finally { _checking.Release(); }
    }

    /// <summary>
    /// Seletor do Codex (só com a ligação ligada): as opções da barra, os modelos novos da OpenAI e do Ollama e a ordem.
    /// Devolve também as linhas da lista para a OpenAI (conta e minha API) e o Ollama.
    /// </summary>
    private static async Task<(CodexIntegration.CodexReport Codex, List<CodexIntegration.ServiceReport> Extra)> UpdateCodexAsync(
        IReadOnlyList<AiModel> models, bool removeObsolete, CancellationToken ct)
    {
        var extra = new List<CodexIntegration.ServiceReport>();
        if (!CodexIntegration.LoadState().Ligado)
            return (new(false, false, [], [], [], [], CodexConfig.CodexFound ? null : "Codex não encontrado neste PC"), extra);
        var bundled = await OpenAiCatalog.BundledAsync(ct);
        OpenAiCatalog.HasApiKey = OpenAiKey.Get() is not null;
        var changes = new OpenAiCatalog.Changes();
        string? warning = bundled is null ? "a lista de modelos do Codex instalado não foi lida; os modelos da OpenAI não foram conferidos" : null;

        // Modelos do Ollama (com o Codex usando o Ollama): novos entram, apagados saem.
        JsonObject? routing = null;
        IReadOnlyList<OllamaCatalog.Local>? installed = null;
        List<string> ollamaAdded = [], ollamaRemoved = [];
        bool ollamaUnavailable = false;
        if (OllamaCatalog.InUse)
        {
            installed = await OllamaCatalog.InstalledAsync(ct);
            routing = OllamaCatalog.LoadRouting();
            ollamaUnavailable = installed is null;
        }
        string routingBefore = routing?.ToJsonString() ?? "";

        bool updated = false;
        int account = 0, api = 0;
        try
        {
            updated = CodexCatalog.Sync(models, bundled, changes, removeObsolete, list =>
            {
                if (routing is not null && installed is not null) (ollamaAdded, ollamaRemoved) = OllamaCatalog.MergeInto(list, routing, installed);
                foreach (var n in list.Where(n => n?["visibility"]?.GetValue<string>() != "hide"))
                {
                    string slug = CodexCatalog.SlugOf(n);
                    if (slug.StartsWith(OpenAiCatalog.ApiPrefix, StringComparison.Ordinal)) api++;
                    else if (CodexCatalog.IsOpenAi(slug)) account++;
                }
            });
            if (routing is not null && routing.ToJsonString() != routingBefore) OllamaCatalog.SaveRouting(routing);
        }
        catch (Exception ex) { warning = "seletor do Codex não atualizado: " + ex.Message; }

        extra.Add(new("OpenAI (conta do Codex)", account, bundled is null ? "a lista do Codex instalado não foi lida agora" : null, bundled is null,
            changes.Novos, changes.Sairam.Concat(changes.ObsoletosRemovidos).ToList(), "openai"));
        extra.Add(new("OpenAI — minha API", api, OpenAiCatalog.HasApiKey ? (api == 0 ? "nenhuma opção da API no seletor" : null) : "sem chave da API OpenAI cadastrada neste PC",
            false, [], [], "openai-api"));
        if (routing is not null)
            extra.Add(new("Ollama (modelos locais)", OllamaCatalog.Count(routing),
                ollamaUnavailable ? "o Ollama não respondeu (está aberto?); os modelos locais não foram conferidos" : null, ollamaUnavailable,
                ollamaAdded, ollamaRemoved, "ollama"));
        return (new(true, updated, changes.Novos, changes.Sairam, changes.PossiveisObsoletos, changes.ObsoletosRemovidos, warning), extra);
    }
}
