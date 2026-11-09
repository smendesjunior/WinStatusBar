using System.Text.Json;
using WinStatusBar.Core.Settings;
namespace WinStatusBar.Services;

internal static class AdditionalAiAccounts
{
    public sealed record Provider(string Id, string Name, string? Endpoint, string Website, string? KeysUrl)
    {
        public string Resource => "WinStatusBar.Ai." + Id;
    }
    public static readonly Provider[] Providers = [
        new("gemini", "Google Gemini", "https://generativelanguage.googleapis.com/v1beta/openai", "https://gemini.google.com/", "https://aistudio.google.com/apikey"),
        new("deepseek", "DeepSeek", "https://api.deepseek.com", "https://chat.deepseek.com/", "https://platform.deepseek.com/api_keys"),
        new("grok", "Grok", "https://api.x.ai/v1", "https://grok.com/", "https://console.x.ai/"),
        new("copilot", "Microsoft Copilot", null, "https://copilot.microsoft.com/", null)
    ];
    public static Provider Gemini => Providers[0];

    /// <summary>Aplica o método do Gemini escolhido (login ou chave) antes de consultar/rodar o CLI, só neste processo.</summary>
    public static void PrepareGemini(AppSettings settings)
    {
        WinStatusBar.Core.Ai.Gemini.GeminiCli.AuthMode = settings.GeminiAuth;
        WinStatusBar.Core.Ai.Gemini.GeminiCli.CloudProject = settings.GeminiCloudProject;
        if (ApiKeyStore.Get(Gemini.Resource) is { Length: > 0 } key)
            Environment.SetEnvironmentVariable("GEMINI_API_KEY", key, EnvironmentVariableTarget.Process);
    }

    /// <summary>Provedores com chave salva pela barra (configurados), na ordem da lista.</summary>
    public static IEnumerable<Provider> Configured() => Providers.Where(p => p.Endpoint is not null && ApiKeyStore.Get(p.Resource) is not null);

    private static string FilePath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WinStatusBar", "ai-accounts.json");
    public static Dictionary<string, string> Models()
    {
        try { return JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(FilePath)) ?? []; }
        catch { return []; }
    }
    public static void Save(Provider provider, string model)
    {
        var models = Models(); models[provider.Id] = model;
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(models));
    }
    public static void Activate(AppSettings settings, Provider provider)
    {
        if (provider.Endpoint is null) return;
        MigrateCustom(settings);
        SaveCustom(settings); // a API própria ativa fica guardada na lista antes de dar lugar ao serviço escolhido
        settings.ApiName = provider.Name; settings.ApiKind = "openai"; settings.ApiBaseUrl = provider.Endpoint;
        settings.ApiCredentialResource = provider.Resource; settings.ApiCredentialEndpoint = provider.Endpoint;
        settings.ApiDefaultModel = Models().GetValueOrDefault(provider.Id) ?? "";
        settings.CommandModels.Remove("api"); settings.CommandEfforts.Remove("api"); settings.CommandTarget = "api";
    }
    public static bool RestoreCustom(AppSettings settings)
    {
        MigrateCustom(settings);
        if (settings.CustomApis.FirstOrDefault() is not { } first) return false;
        ActivateCustom(settings, first); settings.CommandTarget = "api";
        return true;
    }

    /// <summary>A API ativa é uma API própria (e não um serviço da lista, como DeepSeek ou Grok).</summary>
    public static bool CustomActive(AppSettings s) => (s.ApiCredentialResource ?? "").StartsWith("WinStatusBar.CommandApi", StringComparison.Ordinal);

    /// <summary>Guarda os campos da API própria ativa na lista (cria a entrada na primeira vez).</summary>
    public static void SaveCustom(AppSettings s)
    {
        if (!CustomActive(s)) return;
        var entry = s.CustomApis.FirstOrDefault(a => a.Resource == s.ApiCredentialResource);
        if (entry is null)
        {
            if (string.IsNullOrWhiteSpace(s.ApiBaseUrl) && string.IsNullOrWhiteSpace(s.ApiName)) return;
            entry = new CustomApi { Resource = s.ApiCredentialResource }; s.CustomApis.Add(entry);
        }
        entry.Name = s.ApiName; entry.Kind = s.ApiKind; entry.BaseUrl = s.ApiBaseUrl; entry.DefaultModel = s.ApiDefaultModel;
    }

    /// <summary>Torna ativa uma API própria da lista (a anterior fica guardada).</summary>
    public static void ActivateCustom(AppSettings s, CustomApi api)
    {
        SaveCustom(s);
        s.ApiName = api.Name; s.ApiKind = api.Kind; s.ApiBaseUrl = api.BaseUrl; s.ApiDefaultModel = api.DefaultModel;
        s.ApiCredentialResource = api.Resource; s.ApiCredentialEndpoint = api.BaseUrl;
        s.CommandModels.Remove("api"); s.CommandEfforts.Remove("api");
    }

    /// <summary>Migração: a API própria única de antes (campos ativos ou cópia guardada) vira a primeira da lista.</summary>
    public static void MigrateCustom(AppSettings s)
    {
        if (s.CustomApis.Count > 0) return;
        SaveCustom(s);
        if (s.CustomApis.Count > 0 || !File.Exists(FilePath + ".custom")) return;
        try
        {
            var v = JsonSerializer.Deserialize<string[]>(File.ReadAllText(FilePath + ".custom"));
            if (v is { Length: 4 } && v[2].Length > 0)
                s.CustomApis.Add(new CustomApi { Name = v[0], Kind = v[1], BaseUrl = v[2], DefaultModel = v[3], Resource = "WinStatusBar.CommandApi" });
        }
        catch { }
    }
    public static string? ActiveKey(AppSettings settings)
    {
        string resource = settings.ApiCredentialResource ?? "WinStatusBar.CommandApi";
        if (!resource.StartsWith("WinStatusBar.CommandApi", StringComparison.Ordinal) && !string.Equals(settings.ApiCredentialEndpoint?.TrimEnd('/'), settings.ApiBaseUrl.TrimEnd('/'), StringComparison.OrdinalIgnoreCase)) return null;
        return ApiKeyStore.Get(resource);
    }
}
