using System.Globalization;
using System.Text.Json.Nodes;
namespace WinStatusBar.Core.Ai;

/// <summary>
/// Uso e gastos da API da Anthropic (Claude API) nos últimos 30 dias, pelos relatórios oficiais da organização
/// (usage_report/messages e cost_report). Exigem uma chave administrativa (sk-ant-admin…); a chave de conversa não
/// tem acesso. A Anthropic não informa o número de requisições: o campo fica em 0.
/// </summary>
public static class AnthropicUsageClient
{
    private const string Api = "https://api.anthropic.com/v1/organizations/";
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };

    public static async Task<IReadOnlyList<ApiUsageDay>> ReadAsync(string adminKey, CancellationToken ct, HttpClient? client = null)
    {
        var end = DateTimeOffset.UtcNow;
        var start = new DateTimeOffset(end.UtcDateTime.Date.AddDays(-29), TimeSpan.Zero);
        var days = Enumerable.Range(0, 30).Select(i => new ApiUsageDay(start.AddDays(i), 0, 0, 0)).ToDictionary(d => d.Date.UtcDateTime.Date);
        foreach (string endpoint in new[] { "usage_report/messages", "cost_report" })
        {
            string? page = null;
            int guard = 0;
            do
            {
                string url = $"{Api}{endpoint}?starting_at={Uri.EscapeDataString(start.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture))}"
                    + $"&ending_at={Uri.EscapeDataString(end.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture))}&bucket_width=1d&limit=31";
                if (page is not null) url += "&page=" + Uri.EscapeDataString(page);
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.TryAddWithoutValidation("x-api-key", adminKey);
                request.Headers.TryAddWithoutValidation("anthropic-version", "2023-06-01");
                using var response = await (client ?? Http).SendAsync(request, ct);
                if (!response.IsSuccessStatusCode)
                    throw new InvalidOperationException(response.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden
                        ? LocalizedText.T("A consulta exige uma chave administrativa da Anthropic (sk-ant-admin…), criada em console.anthropic.com › Settings › Admin keys.")
                        : LocalizedText.F("Consulta à Anthropic indisponível (HTTP {0}).", new object?[] { (int)response.StatusCode }));
                var root = JsonNode.Parse(await response.Content.ReadAsStringAsync(ct));
                Merge(days, root, endpoint == "cost_report");
                page = root?["has_more"]?.GetValue<bool>() == true ? root?["next_page"]?.GetValue<string>() : null;
            } while (page is not null && ++guard < 20);
        }
        return days.Values.OrderBy(d => d.Date).ToList();
    }

    /// <summary>Soma um relatório (uso ou custo) nos dias. Público para os testes.</summary>
    public static void Merge(IDictionary<DateTime, ApiUsageDay> days, JsonNode? root, bool cost)
    {
        foreach (var bucket in root?["data"]?.AsArray() ?? [])
        {
            if (!DateTimeOffset.TryParse(bucket?["starting_at"]?.GetValue<string>(), CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var at)) continue;
            if (!days.TryGetValue(at.UtcDateTime.Date, out var day)) continue;
            foreach (var r in bucket?["results"]?.AsArray() ?? [])
            {
                if (cost)
                {
                    // Valor em centavos de dólar, como texto decimal ("1234.5" = US$ 12,345).
                    decimal cents = decimal.TryParse(r?["amount"]?.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var c) ? c : 0;
                    day = day with { Cost = day.Cost + cents / 100m };
                }
                else
                {
                    long input = Long(r?["uncached_input_tokens"]) + Long(r?["cache_read_input_tokens"])
                        + Long(r?["cache_creation"]?["ephemeral_1h_input_tokens"]) + Long(r?["cache_creation"]?["ephemeral_5m_input_tokens"]);
                    day = day with { Input = day.Input + input, Output = day.Output + Long(r?["output_tokens"]) };
                }
            }
            days[at.UtcDateTime.Date] = day;
        }
    }

    private static long Long(JsonNode? n) => n is JsonValue v && v.TryGetValue<long>(out var l) ? l
        : n is JsonValue d && d.TryGetValue<double>(out var x) ? (long)x : 0;
}
