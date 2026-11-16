using WinStatusBar.Core.Ai;
using WinStatusBar.Core.Settings;

namespace WinStatusBar.Services;

/// <summary>
/// Chaves de API da OpenAI e da Anthropic, configuradas nos cartões Codex e Claude Code (2.0.0).
/// - Chave de conversa: fica numa entrada da lista de APIs próprias ("OpenAI API" / "Claude API"), criada aqui quando
///   falta; assim a IA aparece no seletor da barra e continua editável em "Outra IA por chave (API própria)".
/// - Chave administrativa: só para ler uso e gastos (OpenAI: WinStatusBar.OpenAiBilling, a mesma de antes;
///   Anthropic: WinStatusBar.AnthropicAdmin).
/// Tudo no Cofre de Credenciais do Windows (ApiKeyStore), nunca no arquivo de configurações.
/// </summary>
internal static class ProviderApiKeys
{
    public sealed record Provider(string Id, string Kind, string Name, string BaseUrl, string Host, string AdminResource, string AdminPrefix, string KeysUrl, string AdminKeysUrl);

    public static readonly Provider OpenAi = new("openai", "openai", "OpenAI API", "https://api.openai.com/v1", "api.openai.com",
        "WinStatusBar.OpenAiBilling", "sk-", "https://platform.openai.com/api-keys", "https://platform.openai.com/settings/organization/admin-keys");
    public static readonly Provider Anthropic = new("anthropic", "anthropic", "Claude API", "https://api.anthropic.com", "api.anthropic.com",
        "WinStatusBar.AnthropicAdmin", "sk-ant-admin", "https://console.anthropic.com/settings/keys", "https://console.anthropic.com/settings/admin-keys");

    /// <summary>A entrada da lista de APIs próprias que fala com este serviço (a primeira, se houver mais de uma).</summary>
    public static CustomApi? Entry(AppSettings s, Provider p) => s.CustomApis.FirstOrDefault(a =>
        string.Equals(a.Kind, p.Kind, StringComparison.OrdinalIgnoreCase)
        && Uri.TryCreate(a.BaseUrl, UriKind.Absolute, out var u) && string.Equals(u.Host, p.Host, StringComparison.OrdinalIgnoreCase));

    public static string? ChatKey(AppSettings s, Provider p) => Entry(s, p) is { } e ? ApiKeyStore.Get(e.Resource) : null;
    public static string? AdminKey(Provider p) => ApiKeyStore.Get(p.AdminResource);

    /// <summary>Confere a chave de conversa listando os modelos; devolve o catálogo ou a explicação do erro.</summary>
    public static async Task<(AgentPrompt.Catalog? Catalog, string? Error)> VerifyChatKeyAsync(Provider p, string key, CancellationToken ct)
    {
        var catalog = await AgentPrompt.CatalogAsync(AgentPrompt.Target.Api, new(p.Kind, p.BaseUrl, key), ct);
        if (catalog.Models.Count == 0) return (null, catalog.Error ?? Localization.T("A chave não listou nenhum modelo."));
        return (catalog, null);
    }

    /// <summary>Grava a chave de conversa, criando a entrada "OpenAI API"/"Claude API" quando falta. Ativa a entrada se a API própria ativa era ela.</summary>
    public static void SaveChatKey(AppSettings s, Provider p, string key, AgentPrompt.Catalog catalog)
    {
        var entry = Entry(s, p);
        if (entry is null)
        {
            entry = new CustomApi { Name = p.Name, Kind = p.Kind, BaseUrl = p.BaseUrl, Resource = "WinStatusBar.CommandApi." + Guid.NewGuid().ToString("N")[..8] };
            s.CustomApis.Add(entry);
        }
        if (string.IsNullOrWhiteSpace(entry.DefaultModel) || catalog.Models.All(o => o.Id != entry.DefaultModel))
            entry.DefaultModel = PreferredModel(p, catalog);
        ApiKeyStore.Set(key, entry.Resource);
        if (s.ApiCredentialResource == entry.Resource) AdditionalAiAccounts.ActivateCustom(s, entry);
    }

    /// <summary>Remove a chave de conversa e a entrada criada para ela (sai do seletor da barra).</summary>
    public static void RemoveChatKey(AppSettings s, Provider p)
    {
        if (Entry(s, p) is not { } entry) return;
        ApiKeyStore.Set(null, entry.Resource);
        s.CustomApis.Remove(entry);
        if (s.ApiCredentialResource == entry.Resource && s.CustomApis.FirstOrDefault() is { } other) AdditionalAiAccounts.ActivateCustom(s, other);
    }

    public static async Task<string?> VerifyAdminKeyAsync(Provider p, string key, CancellationToken ct)
    {
        try
        {
            if (p.Id == "anthropic") await AnthropicUsageClient.ReadAsync(key, ct);
            else await ApiUsageClient.ReadAsync(key, ct);
            return null;
        }
        catch (Exception ex) { return ex.Message; }
    }

    public static void SaveAdminKey(Provider p, string key) => ApiKeyStore.Set(key, p.AdminResource);
    public static void RemoveAdminKey(Provider p) => ApiKeyStore.Set(null, p.AdminResource);

    /// <summary>Modelo padrão ao criar a entrada: o Sonnet mais novo (Anthropic) ou o GPT de uso geral mais novo (OpenAI).</summary>
    public static string PreferredModel(Provider p, AgentPrompt.Catalog catalog)
    {
        var ids = catalog.Models.Select(o => o.Id).ToList();
        string? pick = p.Id == "anthropic"
            ? ids.Where(i => i.Contains("sonnet", StringComparison.OrdinalIgnoreCase)).OrderByDescending(i => i, StringComparer.Ordinal).FirstOrDefault()
            : ids.Where(i => i.StartsWith("gpt-", StringComparison.OrdinalIgnoreCase) && !i.Contains("mini") && !i.Contains("nano") && !i.Contains("audio") && !i.Contains("realtime") && !i.Contains("image") && !i.Contains("search"))
                .OrderByDescending(i => i, StringComparer.Ordinal).FirstOrDefault();
        return pick ?? catalog.AppModel ?? ids[0];
    }
}
