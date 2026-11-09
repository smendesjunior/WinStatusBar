using WinStatusBar.Core.Ai;
using Xunit;

namespace WinStatusBar.Tests;

public class OllamaTextOnlyImageTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "wsb-ollama-" + Guid.NewGuid().ToString("N"));
    private readonly string _capture;

    public OllamaTextOnlyImageTests()
    {
        _capture = Path.Combine(_dir, "cache", "context", "janela.png");
        Directory.CreateDirectory(Path.GetDirectoryName(_capture)!);
        File.WriteAllBytes(_capture, [0x89, 0x50, 0x4E, 0x47]);
    }

    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    [Fact]
    public async Task TextOnlyModel_GetsTheRecognizedTextInsteadOfTheImage()
    {
        var (user, notice) = await AgentPrompt.OllamaUserMessageAsync("tudo bem?", "phi4-mini:latest", [_capture], vision: false,
            (_, _) => Task.FromResult<string?>("Nova conversa\nClaude"), CancellationToken.None);
        Assert.Null(user["images"]); // o Ollama recusaria com HTTP 400
        string content = user["content"]!.ToString();
        Assert.StartsWith("tudo bem?", content);
        Assert.Contains("Nova conversa\nClaude", content);
        Assert.Contains("a captura da janela", content);
        Assert.Contains("phi4-mini:latest só lê texto", notice);
    }

    [Fact]
    public async Task TextOnlyModel_WithUnreadableImage_TellsModelAndPerson()
    {
        var (user, notice) = await AgentPrompt.OllamaUserMessageAsync("descreva", "phi4-mini:latest", [_capture], vision: false,
            (_, _) => Task.FromResult<string?>(null), CancellationToken.None);
        Assert.Null(user["images"]);
        Assert.Contains("você não lê imagens", user["content"]!.ToString());
        Assert.Contains("escolha um modelo que lê imagens", notice);
    }

    [Fact]
    public async Task VisionModel_StillReceivesTheImage()
    {
        var (user, notice) = await AgentPrompt.OllamaUserMessageAsync("o que tem na tela?", "gemma3", [_capture], vision: true,
            (_, _) => throw new InvalidOperationException("OCR não deve rodar com visão"), CancellationToken.None);
        Assert.Single(user["images"]!.AsArray());
        Assert.Equal("o que tem na tela?", user["content"]!.ToString());
        Assert.Null(notice);
    }

    [Fact]
    public async Task WindowsOcr_ReadsTextFromARealImage()
    {
        // OCR do próprio Windows sobre uma imagem gerada aqui (texto preto em fundo branco).
        string png = Path.Combine(_dir, "ocr.png");
        using (var bmp = new System.Drawing.Bitmap(900, 160))
        using (var g = System.Drawing.Graphics.FromImage(bmp))
        {
            g.Clear(System.Drawing.Color.White);
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
            using var font = new System.Drawing.Font("Segoe UI", 40);
            g.DrawString("Relatorio de obras", font, System.Drawing.Brushes.Black, 20, 40);
            bmp.Save(png, System.Drawing.Imaging.ImageFormat.Png);
        }
        string? text = await ImageText.ReadAsync(png, CancellationToken.None);
        Assert.NotNull(text);
        Assert.Contains("obras", text, StringComparison.OrdinalIgnoreCase);
    }
}
