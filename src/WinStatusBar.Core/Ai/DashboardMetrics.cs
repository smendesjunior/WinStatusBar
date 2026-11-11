using System.Globalization;
using System.Text.Json.Nodes;
namespace WinStatusBar.Core.Ai;

public sealed record DashboardSnapshot(IReadOnlyList<ApiUsageDay> Days, decimal? Balance, DateTimeOffset? Expiration, DateTimeOffset Updated, string? Account = null);
public static class DashboardMetrics
{
    public static decimal Number(JsonNode? node)
    {
        string raw = node?.ToString() ?? "0";
        if (decimal.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)) return value;
        if (double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var tiny) && tiny == 0) return 0;
        throw new FormatException("Valor numérico de uso não reconhecido.");
    }
    public static IReadOnlyList<ApiUsageDay> Days(string json)
    {
        var root = JsonNode.Parse(json);
        if (root?["has_more"]?.GetValue<bool>() == true) throw new InvalidOperationException("O painel retornou uma série parcial; selecione um período menor.");
        var data = root?["data"] as JsonArray ?? throw new FormatException("Formato de métricas não reconhecido.");
        return data.Select(bucket =>
        {
            var rows = bucket?["results"] as JsonArray ?? [];
            return new ApiUsageDay(DateTimeOffset.FromUnixTimeSeconds(bucket!["start_time"]!.GetValue<long>()),
                rows.Sum(r => (long)Number(r?["input_tokens"])), rows.Sum(r => (long)Number(r?["output_tokens"])),
                rows.Sum(r => Number(r?["amount"]?["value"])), rows.Sum(r => (long)Number(r?["num_model_requests"])));
        }).OrderBy(d => d.Date).ToList();
    }
    public static (decimal Balance, DateTimeOffset? Expiration) Credits(string json)
    {
        var root = JsonNode.Parse(json);
        if (root?["total_available"] is not { } available) throw new FormatException("Saldo não informado pelo painel.");
        DateTimeOffset? expiration = null;
        foreach (var grant in root?["grants"]?["data"] as JsonArray ?? [])
        {
            decimal remaining = grant?["grant_amount"] is not null ? Number(grant?["grant_amount"]) - Number(grant?["used_amount"]) : Number(grant?["amount"]?["remaining"]);
            if (remaining <= 0 || grant?["expires_at"] is null || grant?["is_scheduled"]?.GetValue<bool>() == true) continue;
            var date = DateTimeOffset.FromUnixTimeSeconds((long)Number(grant?["expires_at"]));
            if (expiration is null || date < expiration) expiration = date;
        }
        return (Number(available), expiration);
    }

    /// <summary>Gasto desde <paramref name="start"/>: a queda do saldo (quando não houve recarga); senão a diferença da soma
    /// dos custos diários a partir do dia (UTC) do início, nunca negativa; sem painel, null. Nunca estima preço.</summary>
    /// <summary>
    /// Gasto desde o início: a queda do saldo; com o saldo parado (o painel o atualiza com atraso) ou recarregado, a diferença
    /// do custo por dia. Requisições novas ainda com custo 0 querem dizer que o painel não informou o custo: null, não US$ 0.
    /// </summary>
    public static decimal? SpendSince(DashboardSnapshot? baseline, DashboardSnapshot? current, DateTimeOffset start)
    {
        if (baseline is null || current is null) return null;
        if (baseline.Balance is { } before && current.Balance is { } now && now < before) return before - now;
        if (baseline.Days.Count > 0 && current.Days.Count > 0)
        {
            var from = start.UtcDateTime.Date;
            decimal Cost(DashboardSnapshot s) => s.Days.Where(d => d.Date.UtcDateTime.Date >= from).Sum(d => d.Cost);
            long Requests(DashboardSnapshot s) => s.Days.Where(d => d.Date.UtcDateTime.Date >= from).Sum(d => d.Requests);
            decimal cost = Math.Max(0m, Cost(current) - Cost(baseline));
            return cost == 0m && Requests(current) > Requests(baseline) ? null : cost;
        }
        if (baseline.Balance is { } b && current.Balance is { } c && c == b) return 0m;
        return null;
    }
}
