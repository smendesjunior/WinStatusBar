using System.Text.Json;
using WinStatusBar.Core.Ai;
using Xunit;

namespace WinStatusBar.Tests;

public class GeneratedImageTests
{
    [Fact]
    public void CodexSavedImageBecomesInlineMarkdown()
    {
        string file = Path.Combine(Path.GetTempPath(), "wsb test " + Guid.NewGuid().ToString("N")[..6] + ".png");
        File.WriteAllBytes(file, [1, 2, 3]);
        try
        {
            using var doc = JsonDocument.Parse(JsonSerializer.Serialize(new { type = "imageGeneration", id = "1", status = "completed", savedPath = file }));
            string md = GeneratedImage.FromCodexItem(doc.RootElement)!;
            Assert.StartsWith("![", md);
            Assert.Contains(new Uri(file).AbsoluteUri, md);
            Assert.DoesNotContain(" ", md.Split('(')[1].Split(')')[0]); // espaços codificados para o markdown
        }
        finally { File.Delete(file); }
    }

    [Fact]
    public void CodexInlineImageIsSavedToTheMediaFolder()
    {
        string folder = Path.Combine(Path.GetTempPath(), "wsb-media-" + Guid.NewGuid().ToString("N")[..6]);
        string previous = GeneratedImage.MediaFolder;
        GeneratedImage.MediaFolder = folder;
        try
        {
            string b64 = Convert.ToBase64String(new byte[200]);
            using var doc = JsonDocument.Parse(JsonSerializer.Serialize(new { type = "imageGeneration", id = "2", status = "completed", result = b64 }));
            string md = GeneratedImage.FromCodexItem(doc.RootElement)!;
            Assert.Contains("codex-", md);
            Assert.Single(Directory.GetFiles(folder, "*.png"));
        }
        finally { GeneratedImage.MediaFolder = previous; if (Directory.Exists(folder)) Directory.Delete(folder, true); }
    }

    [Theory]
    [InlineData("Gere uma imagem de um farol ao pôr do sol", true)]
    [InlineData("crie um logo para minha empresa", true)]
    [InlineData("Draw a picture of a cat", true)]
    [InlineData("Explique como funciona a geração de imagens", false)]
    [InlineData("Qual o tamanho dessa imagem?", false)]
    public void ImageRequestsAreDetected(string prompt, bool expected)
        => Assert.Equal(expected, ApiImageGeneration.IsImageRequest(prompt));

    [Theory]
    [InlineData("https://api.openai.com/v1", "gpt-image-1")]
    [InlineData("https://api.x.ai/v1", "grok-2-image")]
    [InlineData("https://api.deepseek.com/v1", null)]
    public void OnlyApisWithAnImageEndpointGenerate(string baseUrl, string? model)
        => Assert.Equal(model, ApiImageGeneration.ImageModel(baseUrl));

    [Fact]
    public void VideosBecomeLinks()
        => Assert.StartsWith("Vídeo gerado: [v.mp4](file:///", GeneratedImage.Markdown(@"C:\x\v.mp4"));
}
