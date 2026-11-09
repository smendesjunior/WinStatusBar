using System.Text.Json;
using WinStatusBar.Core.Ai;
namespace WinStatusBar.Services;

internal static class OpenAiDashboardSession
{
    private static readonly string Cache = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WinStatusBar", "OpenAiDashboard", "metrics.json");
    public static DashboardSnapshot? Current { get; private set; } = Read();
    public static event Action? Changed;
    private static IReadOnlyList<ApiUsageDay> _usage = [], _costs = [];
    private static bool _hasUsage, _hasCosts, _hasBalance;
    private static decimal? _balance;
    private static string? _account;
    private static DateTimeOffset? _expiration;
    public static void Begin() { _hasUsage = _hasCosts = _hasBalance = false; _usage = []; _costs = []; _balance = null; _expiration = null; _account = null; }
    public static void Clear()
    {
        Begin(); Current = null;
        if (File.Exists(Cache)) File.Delete(Cache);
        Changed?.Invoke();
    }
    private static DashboardSnapshot? Read() { try { return JsonSerializer.Deserialize<DashboardSnapshot>(File.ReadAllText(Cache)); } catch { return null; } }
    public static bool Accepts(Uri uri) => uri.Scheme == "https" && (uri.Host is "api.openai.com" or "platform.openai.com")
        && (uri.AbsolutePath == "/v1/dashboard/organization/usage/completions" || uri.AbsolutePath == "/v1/dashboard/organization/costs" || uri.AbsolutePath == "/v1/dashboard/billing/credit_grants");
    public static void Observe(Uri uri, string json)
    {
        if (!Accepts(uri)) return;
        if (uri.AbsolutePath.EndsWith("credit_grants")) { (_balance, _expiration) = DashboardMetrics.Credits(json); _hasBalance = true; }
        else if (uri.AbsolutePath.EndsWith("costs"))
        {
            _costs = DashboardMetrics.Days(json); _hasCosts = true;
            var root = System.Text.Json.Nodes.JsonNode.Parse(json);
            _account = (root?["data"] as System.Text.Json.Nodes.JsonArray)?.SelectMany(b => b?["results"] as System.Text.Json.Nodes.JsonArray ?? []).Select(r => r?["organization_name"]?.GetValue<string>()).FirstOrDefault(n => !string.IsNullOrWhiteSpace(n));
        }
        else { _usage = DashboardMetrics.Days(json); _hasUsage = true; }
        // Keep the last complete snapshot visible until all three responses arrive.
        if (!_hasUsage || !_hasCosts || !_hasBalance) return;
        var dates = _usage.Select(d => d.Date).Union(_costs.Select(d => d.Date)).Order().ToList();
        var days = dates.Select(date =>
        {
            var usage = _usage.FirstOrDefault(d => d.Date == date);
            var cost = _costs.FirstOrDefault(d => d.Date == date);
            return new ApiUsageDay(date, usage?.Input ?? 0, usage?.Output ?? 0, cost?.Cost ?? 0, usage?.Requests ?? 0);
        }).ToList();
        Current = new(days, _balance, _expiration, DateTimeOffset.Now, _account);
        Directory.CreateDirectory(Path.GetDirectoryName(Cache)!);
        File.WriteAllText(Cache, JsonSerializer.Serialize(Current));
        Changed?.Invoke();
    }
}
