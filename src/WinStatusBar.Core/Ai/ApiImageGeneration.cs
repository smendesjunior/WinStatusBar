using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace WinStatusBar.Core.Ai;

/// <summary>
/// Geração de imagem pelas APIs que têm endpoint oficial de imagens (OpenAI e xAI/Grok, /images/generations).
/// Usada quando o pedido da conversa é claramente de imagem; o arquivo fica na pasta de mídia da barra.
/// </summary>
public static class ApiImageGeneration
{
    private static readonly Regex Request = new(
        @"\b(gere|gera|crie|cria|criar|gerar|desenhe|desenha|desenhar|fa[cç]a|fazer|ilustre|generate|create|draw|make|paint|pinte)\b[^.?!\n]{0,60}\b(imagem|imagens|image|images|ilustra[cç][aã]o|desenho|foto|picture|logo|logotipo|arte|pintura|wallpaper|papel de parede)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static bool IsImageRequest(string prompt) => Request.IsMatch(prompt);

    /// <summary>Modelo de imagem do provedor (null quando a API não tem geração de imagem conhecida).</summary>
    public static string? ImageModel(string baseUrl)
        => baseUrl.Contains("api.openai.com", StringComparison.OrdinalIgnoreCase) ? "gpt-image-1"
         : baseUrl.Contains("api.x.ai", StringComparison.OrdinalIgnoreCase) ? "grok-2-image"
         : null;

    public static async Task<string?> GenerateAsync(HttpClient http, string baseUrl, string key, string prompt, CancellationToken ct)
    {
        string? model = ImageModel(baseUrl);
        if (model is null) return null;
        var body = new JsonObject { ["model"] = model, ["prompt"] = prompt, ["n"] = 1 };
        if (model.StartsWith("grok", StringComparison.Ordinal)) body["response_format"] = "b64_json";
        using var req = new HttpRequestMessage(HttpMethod.Post, baseUrl.TrimEnd('/') + "/images/generations")
        { Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json") };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        using var resp = await http.SendAsync(req, ct);
        string text = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException("A API recusou a geração de imagem (HTTP " + (int)resp.StatusCode + "): " + Shorten(text));
        using var doc = JsonDocument.Parse(text);
        var item = doc.RootElement.GetProperty("data")[0];
        byte[] bytes;
        if (item.TryGetProperty("b64_json", out var b) && b.GetString() is { Length: > 0 } b64) bytes = Convert.FromBase64String(b64);
        else if (item.TryGetProperty("url", out var u) && u.GetString() is { Length: > 0 } url) bytes = await http.GetByteArrayAsync(url, ct);
        else return null;
        Directory.CreateDirectory(GeneratedImage.MediaFolder);
        string path = Path.Combine(GeneratedImage.MediaFolder, $"api-{DateTime.Now:yyyyMMdd-HHmmss}.png");
        await File.WriteAllBytesAsync(path, bytes, ct);
        return GeneratedImage.Markdown(path);
    }

    private static string Shorten(string s) => s.Length > 300 ? s[..300] + "…" : s;
}
