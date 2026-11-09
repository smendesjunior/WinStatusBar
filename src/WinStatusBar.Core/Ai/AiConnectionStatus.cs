using System.Text;
using System.Text.Json.Nodes;

namespace WinStatusBar.Core.Ai;

/// <summary>Read-only local evidence, never a claim that a remote credential is valid.</summary>
public sealed record AiConnectionStatus(bool HasSession, bool HasApiKey, string? Email)
{
    /// <summary>Nome exibido pela conta (perfil local), quando o provedor o grava.</summary>
    public string? Name { get; init; }
    /// <summary>Plano da assinatura legível ("Claude Pro", "ChatGPT Plus"), quando gravado localmente.</summary>
    public string? Plan { get; init; }

    private static string? Label(string? text) => text is { Length: > 0 and <= 120 } && !text.Any(char.IsControl) ? text.Trim() : null;

    /// <summary>"claude_pro" → "Claude Pro"; "self_serve_business_prolite" → "ChatGPT Business".</summary>
    public static string? PlanName(string provider, string? raw)
    {
        if (Label(raw) is not { } r) return null;
        string k = r.ToLowerInvariant();
        if (provider == "claude")
            return k.Contains("max") ? "Claude Max" : k.Contains("pro") ? "Claude Pro" : k.Contains("team") ? "Claude Team"
                : k.Contains("enterprise") ? "Claude Enterprise" : k.Contains("free") ? "Claude Free" : null;
        return k.Contains("enterprise") ? "ChatGPT Enterprise" : k.Contains("business") || k.Contains("team") ? "ChatGPT Business"
            : k.Contains("edu") ? "ChatGPT Edu" : k.Contains("pro") ? "ChatGPT Pro" : k.Contains("plus") ? "ChatGPT Plus"
            : k is "go" ? "ChatGPT Go" : k.Contains("free") ? "ChatGPT Free" : null;
    }

    private static JsonNode? Parse(string? json)
    {
        try { return json is null ? null : JsonNode.Parse(json) as JsonObject; }
        catch (System.Text.Json.JsonException) { return null; }
    }
    private static string? Text(JsonNode? node) => node is JsonValue v && v.TryGetValue<string>(out var text) && !string.IsNullOrWhiteSpace(text) ? text : null;
    private static string? EmailOf(JsonNode? node)
    {
        string? email = Text(node);
        return email is { Length: <= 254 } && email.Contains('@') && !email.Any(char.IsControl) ? email : null;
    }

    public static AiConnectionStatus Claude(string? profileJson, string? credentialsJson, bool environmentApiKey)
    {
        var credentials = Parse(credentialsJson);
        bool session = Text((credentials?["claudeAiOauth"] as JsonObject)?["accessToken"]) is not null;
        var profile = Parse(profileJson);
        var account = profile?["oauthAccount"] as JsonObject;
        var oauth = credentials?["claudeAiOauth"] as JsonObject;
        return new(session, environmentApiKey, session ? EmailOf(account?["emailAddress"]) : null)
        {
            Name = session ? Label(Text(account?["displayName"]) ?? Text(account?["fullName"])) : null,
            Plan = session ? PlanName("claude", Text(oauth?["subscriptionType"]) ?? Text(account?["organizationType"])) : null,
        };
    }

    public static AiConnectionStatus Codex(string? authJson, bool environmentApiKey)
    {
        var auth = Parse(authJson);
        var tokens = auth?["tokens"] as JsonObject;
        bool session = Text(tokens?["access_token"]) is not null;
        string? email = null, name = null, plan = null;
        // Only a display hint from local claims; no signature verification or authentication decision.
        if (session && Text(tokens?["id_token"]) is { } token)
        {
            try
            {
                var pieces = token.Split('.');
                if (pieces.Length == 3 && pieces[1].Length < 64 * 1024)
                {
                    string encoded = pieces[1].Replace('-', '+').Replace('_', '/');
                    encoded = encoded.PadRight((encoded.Length + 3) / 4 * 4, '=');
                    var claims = Parse(Encoding.UTF8.GetString(Convert.FromBase64String(encoded)));
                    email = EmailOf(claims?["email"]);
                    name = Label(Text(claims?["name"]));
                    plan = PlanName("codex", Text((claims?["https://api.openai.com/auth"] as JsonObject)?["chatgpt_plan_type"]));
                }
            }
            catch (FormatException) { }
        }
        return new(session, environmentApiKey || Text(auth?["OPENAI_API_KEY"]) is not null, email) { Name = name, Plan = plan };
    }

    public static AiConnectionStatus Gemini(string? accountsJson, bool hasCredentials, bool hasApiKey)
        => new(hasCredentials, hasApiKey, hasCredentials ? EmailOf(Parse(accountsJson)?["active"]) : null);
}
