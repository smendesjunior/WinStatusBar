using System.Text.Json;

namespace WinStatusBar.Core.Ai;

/// <summary>Imagens e vídeos gerados pelas IAs, mostrados no chat da barra como markdown com o arquivo local.</summary>
public static class GeneratedImage
{
    public static string MediaFolder { get; set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WinStatusBar", "Media");

    /// <summary>Markdown de um arquivo local: imagem inline com link para abrir, ou link do vídeo.</summary>
    public static string Markdown(string path)
    {
        string uri = new Uri(path).AbsoluteUri;
        string name = Path.GetFileName(path);
        string ext = Path.GetExtension(path).ToLowerInvariant();
        return ext is ".mp4" or ".webm" or ".mov" ? $"Vídeo gerado: [{name}]({uri})" : $"![{name}]({uri})\n[Abrir imagem]({uri})";
    }

    /// <summary>
    /// Item "imageGeneration" do app-server do Codex: usa o arquivo salvo (savedPath) ou grava a imagem embutida (result,
    /// base64) na pasta de mídia da barra. Falha de geração vira uma linha de aviso.
    /// </summary>
    public static string? FromCodexItem(JsonElement item)
    {
        try
        {
            if (item.TryGetProperty("savedPath", out var saved) && saved.ValueKind == JsonValueKind.String && File.Exists(saved.GetString()))
                return Markdown(saved.GetString()!);
            if (item.TryGetProperty("result", out var result) && result.ValueKind == JsonValueKind.String && result.GetString() is { Length: > 100 } b64)
            {
                int comma = b64.IndexOf(',');
                if (b64.StartsWith("data:", StringComparison.Ordinal) && comma > 0) b64 = b64[(comma + 1)..];
                Directory.CreateDirectory(MediaFolder);
                string path = Path.Combine(MediaFolder, $"codex-{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid().ToString("N")[..6]}.png");
                File.WriteAllBytes(path, Convert.FromBase64String(b64));
                return Markdown(path);
            }
            if (item.TryGetProperty("failure", out var failure) && failure.ValueKind == JsonValueKind.Object)
                return "Não foi possível gerar a imagem: " + (failure.TryGetProperty("message", out var m) ? m.GetString() : failure.ToString());
        }
        catch (FormatException) { }
        catch (IOException) { }
        return null;
    }
}
