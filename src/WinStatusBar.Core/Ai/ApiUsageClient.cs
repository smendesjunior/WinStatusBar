using System.Net.Http.Headers;
using System.Text.Json.Nodes;
namespace WinStatusBar.Core.Ai;

public sealed record ApiUsageDay(DateTimeOffset Date, long Input, long Output, decimal Cost, long Requests = 0);
public static class ApiUsageClient
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };
    public static async Task<IReadOnlyList<ApiUsageDay>> ReadAsync(string key, CancellationToken ct, HttpClient? client = null)
    {
        var end = DateTimeOffset.UtcNow;
        var start = new DateTimeOffset(end.UtcDateTime.Date.AddDays(-29), TimeSpan.Zero);
        var days = Enumerable.Range(0, 30).Select(i => new ApiUsageDay(start.AddDays(i), 0, 0, 0)).ToDictionary(d => d.Date.ToUnixTimeSeconds());
        foreach (string endpoint in new[] { "usage/completions", "costs" })
        {
            string? page = null;
            do
            {
                string url = $"https://api.openai.com/v1/organization/{endpoint}?start_time={start.ToUnixTimeSeconds()}&end_time={end.ToUnixTimeSeconds()}&bucket_width=1d&limit=30";
                if (page is not null) url += "&page=" + Uri.EscapeDataString(page);
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
                using var response = await (client ?? Http).SendAsync(request, ct);
                if (!response.IsSuccessStatusCode) throw new InvalidOperationException(response.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden
                    ? "A consulta exige uma chave administrativa com acesso a uso e custos da organização."
                    : $"Consulta indisponível (HTTP {(int)response.StatusCode}).");
                var root = JsonNode.Parse(await response.Content.ReadAsStringAsync(ct));
                foreach (var bucket in root?["data"]?.AsArray() ?? [])
                {
                    long time = bucket?["start_time"]?.GetValue<long>() ?? 0;
                    if (!days.TryGetValue(time, out var day)) continue;
                    foreach (var result in bucket?["results"]?.AsArray() ?? [])
                        day = day with { Input = day.Input + (result?["input_tokens"]?.GetValue<long>() ?? 0), Output = day.Output + (result?["output_tokens"]?.GetValue<long>() ?? 0), Cost = day.Cost + DashboardMetrics.Number(result?["amount"]?["value"]), Requests = day.Requests + (result?["num_model_requests"]?.GetValue<long>() ?? 0) };
                    days[time] = day;
                }
                page = root?["has_more"]?.GetValue<bool>() == true ? root?["next_page"]?.GetValue<string>() : null;
            } while (page is not null);
        }
        return days.Values.OrderBy(d => d.Date).ToList();
    }
}
