using System.Globalization;
using System.Text;
using System.Text.Json;

namespace WinStatusBar.Core.Ai.Local;

/// <summary>Modelos carregados no Ollama (equivale a "ollama ps") e liberação imediata (equivale a "ollama stop").</summary>
public static class OllamaModels
{
    public const string Url = "http://127.0.0.1:11434";

    public sealed record Loaded(string Name, long Size, long SizeVram, DateTimeOffset? ExpiresAt);

    public static IReadOnlyList<Loaded> ParsePs(string json)
    {
        var list = new List<Loaded>();
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("models", out var models) || models.ValueKind != JsonValueKind.Array) return list;
            foreach (var m in models.EnumerateArray())
            {
                string name = m.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString() ?? "" : "";
                long Num(string p) => m.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt64() : 0;
                DateTimeOffset? expires = m.TryGetProperty("expires_at", out var e) && e.ValueKind == JsonValueKind.String
                    && DateTimeOffset.TryParse(e.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.None, out var at) ? at : null;
                if (name.Length > 0) list.Add(new(name, Num("size"), Num("size_vram"), expires));
            }
        }
        catch (JsonException) { }
        return list;
    }

    /// <summary>null quando o Ollama não está aberto.</summary>
    public static async Task<IReadOnlyList<Loaded>?> LoadedAsync(HttpClient http, CancellationToken ct)
    {
        try { return ParsePs(await http.GetStringAsync($"{Url}/api/ps", ct)); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return null; }
        catch (HttpRequestException) { return null; }
    }

    /// <summary>Libera o modelo agora (keep_alive 0), como "ollama stop".</summary>
    public static async Task UnloadAsync(HttpClient http, string model, CancellationToken ct)
    {
        string body = JsonSerializer.Serialize(new { model, keep_alive = 0 });
        using var content = new StringContent(body, Encoding.UTF8, "application/json");
        using var response = await http.PostAsync($"{Url}/api/generate", content, ct);
    }

    /// <summary>"phi4-mini · CPU · libera às 02:10", "gemma4 · 4,2 GB na GPU · sempre carregado"…</summary>
    public static string StatusText(IReadOnlyList<Loaded>? loaded, DateTimeOffset now)
    {
        if (loaded is null) return "Ollama fechado";
        if (loaded.Count == 0) return "Nenhum modelo carregado · GPU e memória livres";
        return string.Join("\n", loaded.Select(m =>
        {
            string where = m.SizeVram <= 0 ? "CPU"
                : m.SizeVram >= m.Size ? $"{m.SizeVram / 1073741824.0:0.0} GB na GPU"
                : $"{100.0 * m.SizeVram / Math.Max(1, m.Size):0}% na GPU";
            string until = m.ExpiresAt is not { } at ? ""
                : at - now > TimeSpan.FromDays(365) ? " · sempre carregado"
                : $" · libera às {at.ToLocalTime():HH:mm}";
            return $"{m.Name} · {where}{until}";
        }));
    }
}
