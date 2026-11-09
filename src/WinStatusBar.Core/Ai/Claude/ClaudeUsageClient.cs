using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using WinStatusBar.Core.Integration;
using WinStatusBar.Core.Ipc;

namespace WinStatusBar.Core.Ai.Claude;

public sealed record ClaudeUsageResult(EventMessage? Message, string? Error, TimeSpan RetryDelay, bool RateLimited = false)
{
    /// <summary>Janelas adicionais informadas pela conta (ex.: seven_day_opus, seven_day_sonnet).</summary>
    public IReadOnlyList<WinStatusBar.Core.Ai.QuotaWindow> Extras { get; init; } = [];
    /// <summary>Uso extra pago (extra_usage), quando habilitado.</summary>
    public string? ExtraUsage { get; init; }
    /// <summary>Uso desta semana por produto (seven_day_breakdown), como na página de uso do claude.ai.</summary>
    public IReadOnlyList<ProductShare> Breakdown { get; init; } = [];
}

/// <summary>Parcela do uso semanal de um produto (Claude Code, Conversas, Cowork, Outro).</summary>
public sealed record ProductShare(string Name, double Percent);

/// <summary>
/// Consulta os limites da conta usando o login já existente do Claude Code (~/.claude/.credentials.json).
/// Quando o login vence, renova pelo mesmo processo oficial do Claude Code (refresh token em
/// platform.claude.com, client_id público do Claude Code) e grava o login novo no mesmo arquivo, de forma atômica,
/// preservando os demais campos — autorizado pelo usuário em 04/11/2026. Tokens nunca vão para logs, IPC ou outro arquivo,
/// e redirecionamentos não são seguidos.
/// </summary>
public sealed class ClaudeUsageClient : IDisposable
{
    private readonly HttpClient _http;
    private readonly string _credentialsPath;
    private readonly bool _allowRefresh;
    internal const string TokenUrl = "https://platform.claude.com/v1/oauth/token";
    internal const string ClaudeCodeClientId = "9d1c250a-e61b-44d9-88ed-5944d1962f5e";
    private static readonly SemaphoreSlim RefreshGate = new(1, 1);

    public ClaudeUsageClient() : this(new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
        { Timeout = TimeSpan.FromSeconds(12) }, Path.Combine(ClaudeIntegrationInstaller.ClaudeConfigDir(), ".credentials.json"), allowRefresh: true) { }
    internal ClaudeUsageClient(HttpClient http, string credentialsPath, bool allowRefresh = false)
    { _http = http; _credentialsPath = credentialsPath; _allowRefresh = allowRefresh; }

    public async Task<ClaudeUsageResult> FetchAsync(CancellationToken ct)
    {
        var interval = TimeSpan.FromMinutes(5);
        try
        {
            if (!File.Exists(_credentialsPath)) return new(null, global::WinStatusBar.Core.LocalizedText.T("Entre na sua conta pelo Claude Code para consultar as cotas."), interval);
            using var credentials = JsonDocument.Parse(await File.ReadAllTextAsync(_credentialsPath, ct));
            if (!credentials.RootElement.TryGetProperty("claudeAiOauth", out var oauth)
                || !oauth.TryGetProperty("accessToken", out var access) || access.ValueKind != JsonValueKind.String
                || string.IsNullOrWhiteSpace(access.GetString()))
                return new(null, global::WinStatusBar.Core.LocalizedText.T("O login do Claude Code não está disponível. Entre novamente na sua conta."), interval);
            string token = access.GetString()!;
            // Vencido ou vencendo em até 2 minutos: renova antes de consultar.
            if (oauth.TryGetProperty("expiresAt", out var expiry) && expiry.TryGetInt64(out long epoch)
                && epoch <= DateTimeOffset.UtcNow.AddMinutes(2).ToUnixTimeMilliseconds())
            {
                if (!_allowRefresh) return new(null, global::WinStatusBar.Core.LocalizedText.T("O login do Claude Code expirou. Abra o Claude Code para renovar a sessão."), interval);
                var (renewed, refreshError) = await RefreshAsync(ct);
                if (renewed is null) return new(null, refreshError, interval);
                token = renewed;
            }
            var response = await QueryAsync(token, ct);
            // Login recusado antes do vencimento (revogado/rotacionado): uma única tentativa de renovação.
            if (response.StatusCode == HttpStatusCode.Unauthorized && _allowRefresh)
            {
                response.Dispose();
                var (renewed, refreshError) = await RefreshAsync(ct);
                if (renewed is null) return new(null, refreshError, interval);
                response = await QueryAsync(renewed, ct);
            }
            using var _ = response;
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                var wait = response.Headers.RetryAfter?.Delta
                    ?? (response.Headers.RetryAfter?.Date - DateTimeOffset.UtcNow) ?? TimeSpan.FromMinutes(15);
                if (wait < TimeSpan.FromMinutes(5)) wait = TimeSpan.FromMinutes(5);
                return new(null, global::WinStatusBar.Core.LocalizedText.T("O Claude limitou as consultas. A próxima tentativa será automática."), wait, true);
            }
            if (response.StatusCode == HttpStatusCode.Unauthorized)
                return new(null, global::WinStatusBar.Core.LocalizedText.T("O Claude não aceitou o login atual. Abra o Claude Code e entre novamente."), interval);
            if (response.StatusCode == HttpStatusCode.Forbidden)
                return new(null, global::WinStatusBar.Core.LocalizedText.T("O login atual não permite consultar as cotas. Entre pelo Claude Code com sua conta de assinatura."), interval);
            if (!response.IsSuccessStatusCode)
                return new(null, global::WinStatusBar.Core.LocalizedText.F("Consulta de cotas indisponível (HTTP {0}). Nova tentativa automática em 5 minutos.", new object?[] {(int)response.StatusCode}), interval);
            string body = await response.Content.ReadAsStringAsync(ct);
            var parsed = ParseUsage(body, DateTimeOffset.UtcNow);
            var (extras, extraUsage) = ParseExtras(body, DateTimeOffset.Now);
            return parsed is null ? new(null, global::WinStatusBar.Core.LocalizedText.T("A conta não retornou os limites de uso. Nenhum percentual foi estimado."), interval)
                : new(parsed, null, interval) { Extras = extras, ExtraUsage = extraUsage, Breakdown = ParseBreakdown(body) };
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        { return new(null, global::WinStatusBar.Core.LocalizedText.T("A consulta ao Claude demorou demais. O app tentará novamente automaticamente."), interval); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or HttpRequestException or InvalidOperationException)
        { return new(null, global::WinStatusBar.Core.LocalizedText.T("Não foi possível consultar as cotas do Claude. Verifique a conexão e o login no Claude Code."), interval); }
    }

    private async Task<HttpResponseMessage> QueryAsync(string token, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.anthropic.com/api/oauth/usage");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Add("anthropic-beta", "oauth-2025-04-20");
        request.Headers.UserAgent.ParseAdd("WinStatusBar/1.0");
        return await _http.SendAsync(request, ct);
    }

    /// <summary>
    /// Renova o login como o Claude Code faz (grant_type=refresh_token) e grava accessToken, refreshToken e expiresAt
    /// no mesmo arquivo (gravação atômica, demais campos preservados). Retorna o token novo ou a mensagem de erro.
    /// </summary>
    private async Task<(string? Token, string? Error)> RefreshAsync(CancellationToken ct)
    {
        const string relogin = "Não foi possível renovar o login do Claude. Entre novamente pelo Claude Code.";
        await RefreshGate.WaitAsync(ct);
        try
        {
            var root = System.Text.Json.Nodes.JsonNode.Parse(await File.ReadAllTextAsync(_credentialsPath, ct))?.AsObject();
            if (root?["claudeAiOauth"] is not System.Text.Json.Nodes.JsonObject oauth) return (null, relogin);
            // Outro processo pode ter renovado enquanto esperávamos: usa o login atual se ainda for válido.
            if (oauth["expiresAt"]?.GetValue<long>() is long exp && exp > DateTimeOffset.UtcNow.AddMinutes(2).ToUnixTimeMilliseconds()
                && oauth["accessToken"]?.GetValue<string>() is { Length: > 0 } current)
                return (current, null);
            if (oauth["refreshToken"]?.GetValue<string>() is not { Length: > 0 } refresh) return (null, relogin);
            string? scopes = oauth["scopes"] is System.Text.Json.Nodes.JsonArray arr
                ? string.Join(" ", arr.Select(s => s?.GetValue<string>()).Where(s => !string.IsNullOrEmpty(s))) : null;
            var body = new Dictionary<string, string> { ["grant_type"] = "refresh_token", ["refresh_token"] = refresh, ["client_id"] = ClaudeCodeClientId };
            if (!string.IsNullOrEmpty(scopes)) body["scope"] = scopes;
            using var request = new HttpRequestMessage(HttpMethod.Post, TokenUrl)
            { Content = new StringContent(JsonSerializer.Serialize(body), System.Text.Encoding.UTF8, "application/json") };
            request.Headers.UserAgent.ParseAdd("WinStatusBar/1.0");
            using var response = await _http.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode) return (null, $"{relogin} (HTTP {(int)response.StatusCode})");
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            var r = doc.RootElement;
            if (!r.TryGetProperty("access_token", out var at) || at.GetString() is not { Length: > 0 } newAccess) return (null, relogin);
            string newRefresh = r.TryGetProperty("refresh_token", out var rt) && rt.GetString() is { Length: > 0 } x ? x : refresh;
            long expiresIn = r.TryGetProperty("expires_in", out var ei) && ei.TryGetInt64(out var secs) ? secs : 3600;
            oauth["accessToken"] = newAccess;
            oauth["refreshToken"] = newRefresh;
            oauth["expiresAt"] = DateTimeOffset.UtcNow.AddSeconds(expiresIn).ToUnixTimeMilliseconds();
            // Gravação atômica no mesmo diretório (sem cópia de backup: o token não é duplicado em outro arquivo).
            string tmp = _credentialsPath + ".wsb-tmp";
            await File.WriteAllTextAsync(tmp, root.ToJsonString(), ct);
            File.Move(tmp, _credentialsPath, overwrite: true);
            return (newAccess, null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or HttpRequestException or InvalidOperationException or FormatException)
        { return (null, relogin); }
        finally { RefreshGate.Release(); }
    }

    internal static EventMessage? ParseUsage(string json, DateTimeOffset observed)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object) return null;
        (double? used, long? resets) Window(string name)
        {
            if (!root.TryGetProperty(name, out var w) || w.ValueKind != JsonValueKind.Object
                || !w.TryGetProperty("utilization", out var used) || used.ValueKind != JsonValueKind.Number || !used.TryGetDouble(out var percentage)
                || !double.IsFinite(percentage) || percentage < 0 || percentage > 100) return (null, null);
            long? reset = null;
            if (w.TryGetProperty("resets_at", out var at))
            {
                if (at.ValueKind == JsonValueKind.String && DateTimeOffset.TryParse(at.GetString(), CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind, out var date)) reset = date.ToUnixTimeSeconds();
                else if (at.ValueKind == JsonValueKind.Number && at.TryGetInt64(out var seconds))
                    reset = seconds > 100_000_000_000 ? seconds / 1000 : seconds;
            }
            if (reset is not null && (reset < 0 || reset > 253402300799)) return (null, null);
            return (percentage, reset);
        }
        var five = Window("five_hour"); var seven = Window("seven_day");
        if (five.used is null && seven.used is null) return null;
        return new EventMessage { Kind = "usage", QuotaSource = global::WinStatusBar.Core.LocalizedText.T("Consulta da conta Claude"), Timestamp = observed.ToUnixTimeMilliseconds(),
            FiveHourUsed = five.used, FiveHourResetsAt = five.resets, SevenDayUsed = seven.used, SevenDayResetsAt = seven.resets };
    }
    /// <summary>
    /// Adaptativo: qualquer outra janela com "utilization" (ex.: seven_day_opus, seven_day_sonnet, seven_day_oauth_apps)
    /// vira uma linha extra, sem depender de uma lista fixa; extra_usage descreve o uso pago adicional.
    /// </summary>
    internal static (List<WinStatusBar.Core.Ai.QuotaWindow> windows, string? extraUsage) ParseExtras(string json, DateTimeOffset observed)
    {
        var list = new List<WinStatusBar.Core.Ai.QuotaWindow>();
        string? extra = null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return (list, null);
            foreach (var p in doc.RootElement.EnumerateObject())
            {
                if (p.Value.ValueKind != JsonValueKind.Object) continue;
                if (p.Name == "extra_usage")
                {
                    var e = p.Value;
                    bool enabled = e.TryGetProperty("is_enabled", out var en) && en.ValueKind == JsonValueKind.True;
                    if (!enabled) { extra = global::WinStatusBar.Core.LocalizedText.T("Uso extra: desativado"); continue; }
                    string used = e.TryGetProperty("used_credits", out var uc) && uc.ValueKind == JsonValueKind.Number ? uc.GetDouble().ToString("0.##", CultureInfo.GetCultureInfo("pt-BR")) : "—";
                    string limit = e.TryGetProperty("monthly_limit", out var ml) && ml.ValueKind == JsonValueKind.Number ? ml.GetDouble().ToString("0.##", CultureInfo.GetCultureInfo("pt-BR")) : "sem limite";
                    extra = global::WinStatusBar.Core.LocalizedText.F("Uso extra: {0} de {1} créditos no mês", new object?[] {used,limit});
                    continue;
                }
                if (p.Name is "five_hour" or "seven_day") continue;
                if (!p.Value.TryGetProperty("utilization", out var u) || u.ValueKind != JsonValueKind.Number) continue;
                double pct = u.GetDouble();
                if (!double.IsFinite(pct) || pct < 0 || pct > 100) continue;
                DateTimeOffset? reset = null;
                if (p.Value.TryGetProperty("resets_at", out var at) && at.ValueKind == JsonValueKind.String
                    && DateTimeOffset.TryParse(at.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var d)) reset = d;
                list.Add(new WinStatusBar.Core.Ai.QuotaWindow(WinStatusBar.Core.Ai.AgentKind.Claude, FriendlyWindow(p.Name), pct, reset,
                    p.Name.StartsWith("seven_day", StringComparison.Ordinal) ? 10080 : p.Name.StartsWith("five_hour", StringComparison.Ordinal) ? 300 : null,
                    observed, global::WinStatusBar.Core.LocalizedText.T("Consulta da conta Claude")));
            }
        }
        catch (JsonException) { }
        return (list, extra);
    }

    /// <summary>seven_day_breakdown.rows → nomes em português; ignora linhas sem percentual válido.</summary>
    internal static List<ProductShare> ParseBreakdown(string json)
    {
        var list = new List<ProductShare>();
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("seven_day_breakdown", out var b) || b.ValueKind != JsonValueKind.Object
                || !b.TryGetProperty("rows", out var rows) || rows.ValueKind != JsonValueKind.Array) return list;
            foreach (var r in rows.EnumerateArray())
            {
                if (!r.TryGetProperty("percent", out var p) || !p.TryGetDouble(out var pct) || !double.IsFinite(pct) || pct < 0 || pct > 100) continue;
                string key = r.TryGetProperty("key", out var k) ? k.GetString() ?? "" : "";
                string display = r.TryGetProperty("display_name", out var d) ? d.GetString() ?? key : key;
                string name = key switch { "claude_code" => global::WinStatusBar.Core.LocalizedText.T("Claude Code"), "chat" => global::WinStatusBar.Core.LocalizedText.T("Conversas"), "cowork" => "Cowork", "other" => global::WinStatusBar.Core.LocalizedText.T("Outro"), _ => display };
                list.Add(new ProductShare(name, pct));
            }
        }
        catch (JsonException) { }
        return list;
    }

    internal static string FriendlyWindow(string key)
    {
        string period = key.StartsWith("seven_day", StringComparison.Ordinal) ? "7d" : key.StartsWith("five_hour", StringComparison.Ordinal) ? "5h" : string.Empty;
        string rest = key.Replace("seven_day", "").Replace("five_hour", "").Trim('_');
        string name = rest switch
        {
            "opus" => "Opus", "sonnet" => "Sonnet", "haiku" => "Haiku", "oauth_apps" => global::WinStatusBar.Core.LocalizedText.T("Apps conectados"), "cowork" => "Cowork",
            "" => global::WinStatusBar.Core.LocalizedText.T("Geral"), _ => string.Join(' ', rest.Split('_').Select(w => w.Length == 0 ? w : char.ToUpperInvariant(w[0]) + w[1..])),
        };
        return period.Length == 0 ? name : $"{name} {period}";
    }

    public void Dispose() => _http.Dispose();
}
