using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CodexIAs;

/// <summary>
/// Modelos novos da OpenAI no seletor do Codex, sem edição manual. O catálogo do Codex (model_catalog_json) foi copiado
/// uma vez da conta pelo "ollama launch" e não muda sozinho; o próprio Codex traz a lista de modelos que conhece
/// ("codex debug models --bundled") e se atualiza sozinho. Quando aparece nessa lista um modelo que nunca esteve lá
/// (ex.: gpt-6.2-sol), ele entra no catálogo, junto com a opção "OpenAI — minha API" dele (pela chave da API).
/// Entradas que já existiam não são trocadas (elas vêm da conta, com prioridade e nível padrão próprios); só as que o
/// app acrescentou saem quando o modelo some da lista do Codex.
/// </summary>
public static partial class OpenAiCatalog
{
    /// <summary>Nome de modelo da conta OpenAI (gpt-6.1-sol, o3, codex-mini…), sem ":" (marca dos modelos do Ollama).</summary>
    [System.Text.RegularExpressions.GeneratedRegex(@"^(gpt-\d|o\d|codex-)[^:]*$")] private static partial System.Text.RegularExpressions.Regex AccountModel();

    public const string ApiPrefix = "openai-minha-api-";
    private static string StateFile => Path.Combine(Paths.DataDir, "openai-modelos.json");

    /// <summary>Registro do que já foi visto e do que o app acrescentou.</summary>
    public sealed class State
    {
        public List<string> Conhecidos { get; set; } = [];
        public List<string> Adicionados { get; set; } = [];
    }

    public static State? LoadState()
    {
        try { return File.Exists(StateFile) ? JsonSerializer.Deserialize<State>(File.ReadAllText(StateFile)) : null; } catch { return null; }
    }

    public static void SaveState(State state)
    {
        // Gravação atômica: uma queda no meio não deixa o registro pela metade (ele seria lido como "primeira vez").
        try { WinStatusBar.Core.Ai.Codex.CodexIntegration.WriteAtomic(StateFile, JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true })); }
        catch (Exception ex) { Log.Write("OpenAI: não gravou o registro de modelos: " + ex.Message); }
    }

    /// <summary>codex.exe mais novo: o app Codex se atualiza em %LOCALAPPDATA%\OpenAI\Codex\bin\&lt;versão&gt;; senão, o do PATH.</summary>
    public static string? FindCodex()
    {
        string bin = Path.Combine(Paths.LocalAppData, "OpenAI", "Codex", "bin");
        if (Directory.Exists(bin))
        {
            var newest = Directory.EnumerateFiles(bin, "codex.exe", SearchOption.AllDirectories)
                .Select(f => new FileInfo(f)).OrderByDescending(f => f.LastWriteTimeUtc).FirstOrDefault();
            if (newest is not null) return newest.FullName;
        }
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries))
            foreach (var name in new[] { "codex.exe", "codex.cmd" })
                if (File.Exists(Path.Combine(dir.Trim(), name))) return Path.Combine(dir.Trim(), name);
        return null;
    }

    /// <summary>Lista de modelos que o Codex instalado conhece ("codex debug models --bundled"); null se não deu.</summary>
    public static async Task<JsonArray?> BundledAsync(CancellationToken ct)
    {
        string? exe = FindCodex();
        if (exe is null) { Log.Write("OpenAI: Codex não encontrado para ler a lista de modelos"); return null; }
        bool viaCmd = exe.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase);
        var psi = new ProcessStartInfo(viaCmd ? Path.Combine(Environment.SystemDirectory, "cmd.exe") : exe)
        {
            RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8, WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        };
        if (viaCmd) { psi.ArgumentList.Add("/d"); psi.ArgumentList.Add("/c"); psi.ArgumentList.Add(exe); }
        foreach (var a in new[] { "debug", "models", "--bundled" }) psi.ArgumentList.Add(a);
        try
        {
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
            limit.CancelAfter(TimeSpan.FromSeconds(60));
            // No fim do prazo o codex é encerrado (antes ficava rodando sem dono).
            if (await WinStatusBar.Core.Ai.Codex.CodexModelLevels.OutputAsync(psi, limit.Token) is not { } output) return null;
            return JsonNode.Parse(output)?["models"] as JsonArray;
        }
        catch (Exception ex) { Log.Write("OpenAI: lista de modelos do Codex falhou: " + ex.Message); return null; }
    }

    private static string SlugOf(JsonNode? n) => CodexCatalog.SlugOf(n);
    private static int PriorityOf(JsonNode? n) => CodexCatalog.PriorityOf(n);

    /// <summary>Geração do modelo pelo nome: gpt-6.1-sol → 6.1; gpt-7 → 7.0; outros nomes → null.</summary>
    public static Version? VersionOf(string slug)
    {
        var m = System.Text.RegularExpressions.Regex.Match(slug, @"^gpt-(\d+)(?:\.(\d+))?");
        return m.Success ? new Version(int.Parse(m.Groups[1].Value), m.Groups[2].Success ? int.Parse(m.Groups[2].Value) : 0) : null;
    }

    /// <summary>Atalhos fixos do encaminhador (modelo e nível definidos lá): openai-minha-api-sol e -astra.</summary>
    public static bool IsFixedAlias(string slug) => slug is ApiPrefix + "sol" or ApiPrefix + "astra";

    /// <summary>Opção "OpenAI — minha API" de um modelo (mesmos níveis; o encaminhador manda pela chave da API).</summary>
    public static JsonObject ApiEntry(JsonNode model)
    {
        var entry = (JsonObject)model.DeepClone();
        string name = model["display_name"]?.GetValue<string>() ?? SlugOf(model);
        entry["slug"] = ApiPrefix + SlugOf(model);
        entry["display_name"] = "OpenAI — minha API — " + name;
        entry["description"] = "Pela sua chave da API OpenAI (cobrança por uso), pelo encaminhador do Codex.";
        entry["priority"] = -8;
        entry["visibility"] = "list";
        entry.Remove("availability_nux");
        entry.Remove("upgrade");
        return entry;
    }

    /// <summary>O que a verificação mudou (ou achou) nos modelos da OpenAI, para o relatório.</summary>
    public sealed class Changes
    {
        public List<string> Novos { get; } = [];
        public List<string> Sairam { get; } = [];
        /// <summary>Modelos da conta que não estão mais na lista do Codex (só saem se a pessoa pedir).</summary>
        public List<string> PossiveisObsoletos { get; } = [];
        public List<string> ObsoletosRemovidos { get; } = [];
    }

    private static string NameOf(JsonNode? n) => n?["display_name"]?.GetValue<string>() ?? SlugOf(n);

    /// <summary>
    /// Aplica a lista do Codex à lista de modelos do catálogo. Primeira vez: só registra o que existe (nada muda).
    /// Depois: acrescenta os modelos novos e tira os que o app acrescentou e sumiram. Os modelos da conta que sumiram
    /// da lista do Codex são só apontados, e saem apenas com <paramref name="removeObsolete"/>. Devolve true se mudou algo.
    /// </summary>
    public static bool MergeInto(JsonArray list, JsonArray bundled, ref State? state, Changes? changes = null, bool removeObsolete = false)
    {
        changes ??= new Changes();
        var listed = bundled.Where(m => m?["visibility"]?.GetValue<string>() == "list" && SlugOf(m).Length > 0).ToList();
        var listedSlugs = listed.Select(SlugOf).ToHashSet(StringComparer.Ordinal);
        var present = list.Select(SlugOf).ToHashSet(StringComparer.Ordinal);
        bool changed = false;

        // Modelos da conta (visíveis) que o Codex instalado não conhece mais: possivelmente aposentados pela OpenAI. Só
        // nomes de modelo da OpenAI (gpt-6…, o3, codex-…); modelos do Ollama ("gpt-oss:20b") têm ":" e ficam de fora.
        // Sem a lista do Codex (vazia), nada é apontado.
        var added = state?.Adicionados.ToHashSet(StringComparer.Ordinal) ?? [];
        for (int i = listedSlugs.Count == 0 ? -1 : list.Count - 1; i >= 0; i--)
        {
            string slug = SlugOf(list[i]);
            if (!AccountModel().IsMatch(slug) || added.Contains(slug)
                || list[i]?["visibility"]?.GetValue<string>() == "hide" || listedSlugs.Contains(slug)) continue;
            if (removeObsolete)
            {
                changes.ObsoletosRemovidos.Insert(0, NameOf(list[i]));
                list.RemoveAt(i);
                changed = true;
                Log.Write($"OpenAI: modelo obsoleto removido do seletor a pedido: {slug}");
            }
            else changes.PossiveisObsoletos.Insert(0, NameOf(list[i]));
        }

        if (state is null)
        {
            state = new State { Conhecidos = listedSlugs.Union(present).OrderBy(s => s, StringComparer.Ordinal).ToList() };
            return changed;
        }
        var known = state.Conhecidos.ToHashSet(StringComparer.Ordinal);

        // Modelo que o app acrescentou e sumiu da lista do Codex (aposentado): sai, com a opção da API.
        foreach (var gone in state.Adicionados.Where(s => !listedSlugs.Contains(s)).ToList())
        {
            for (int i = list.Count - 1; i >= 0; i--)
                if (SlugOf(list[i]) == gone || SlugOf(list[i]) == ApiPrefix + gone)
                {
                    if (SlugOf(list[i]) == gone) changes.Sairam.Add(NameOf(list[i]));
                    list.RemoveAt(i); changed = true;
                }
            state.Adicionados.Remove(gone);
            Log.Write($"OpenAI: modelo retirado do seletor (não está mais no Codex): {gone}");
        }

        // A lista do Codex também traz modelos antigos que a OpenAI não libera para contas do ChatGPT (ex.: gpt-5.5:
        // "not supported when using Codex with a ChatGPT account"). Só entra modelo de geração igual ou mais nova que a
        // mais nova da conta (ex.: gpt-6.2-sol, gpt-7); sem número de versão no nome, entra.
        var newest = list.Select(SlugOf).Where(s => !s.StartsWith(ApiPrefix, StringComparison.Ordinal)).Select(VersionOf).Where(v => v is not null).Max();
        foreach (var model in listed.Where(m => !known.Contains(SlugOf(m)) && !present.Contains(SlugOf(m))))
        {
            string slug = SlugOf(model);
            if (VersionOf(slug) is { } version && newest is not null && version < newest)
            {
                Log.Write($"OpenAI: {slug} ficou de fora (geração mais antiga que a da conta; a OpenAI costuma não liberá-la para contas do ChatGPT)");
                continue;
            }
            int priority = PriorityOf(model);
            // Antes do primeiro modelo da OpenAI com prioridade maior (as entradas da barra e da API ficam onde estão).
            int at = list.Select((n, i) => (n, i)).Where(x => !SlugOf(x.n).StartsWith(CodexCatalog.Prefix, StringComparison.Ordinal)
                    && !SlugOf(x.n).StartsWith(ApiPrefix, StringComparison.Ordinal) && PriorityOf(x.n) > priority && PriorityOf(x.n) != int.MaxValue)
                .Select(x => x.i).DefaultIfEmpty(list.Count).First();
            list.Insert(at, model!.DeepClone());
            if (model["supported_in_api"]?.GetValue<bool>() == true && !present.Contains(ApiPrefix + slug) && HasApiKey) list.Add(ApiEntry(model));
            state.Adicionados.Add(slug);
            changes.Novos.Add(NameOf(model));
            changed = true;
            Log.Write($"OpenAI: modelo novo no seletor do Codex: {NameOf(model)}");
        }
        state.Conhecidos = known.Union(listedSlugs).OrderBy(s => s, StringComparer.Ordinal).ToList();
        return changed;
    }

    /// <summary>Chave da API OpenAI do encaminhador cadastrada (só então a opção "minha API" de um modelo novo entra).</summary>
    public static bool HasApiKey { get; set; } = true;

    /// <summary>
    /// Lista de modelos da OpenAI para um catálogo novo (PC sem catálogo próprio): a da conta, que o Codex guarda em
    /// models_cache.json; sem ela, a lista do Codex instalado.
    /// </summary>
    public static async Task<JsonArray> InitialModelsAsync(CancellationToken ct)
    {
        try
        {
            string cache = Path.Combine(Paths.CodexHome, "models_cache.json");
            if (File.Exists(cache) && JsonNode.Parse(await File.ReadAllTextAsync(cache, ct))?["models"] is JsonArray account && account.Count > 0)
                return (JsonArray)account.DeepClone();
        }
        catch { }
        return await BundledAsync(ct) ?? [];
    }
}
