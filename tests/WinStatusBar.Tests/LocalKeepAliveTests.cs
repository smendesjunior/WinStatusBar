using WinStatusBar.Core.Ai;
using WinStatusBar.Core.Settings;
using Xunit;

namespace WinStatusBar.Tests;

public class LocalKeepAliveTests
{
    [Theory]
    [InlineData("5m", 300.0)]
    [InlineData("30s", 30.0)]
    [InlineData("1h", 3600.0)]
    [InlineData("1h30m", 5400.0)]
    [InlineData("90", 90.0)]          // número puro = segundos, como no Ollama
    [InlineData("0", 0.0)]
    [InlineData("", 300.0)]           // vazio = padrão de 5 min
    [InlineData("abc", 300.0)]        // inválido = padrão
    public void ParsesTheOllamaFormat(string value, double seconds) => Assert.Equal(TimeSpan.FromSeconds(seconds), LocalKeepAlive.Parse(value));

    [Theory]
    [InlineData("-1")]
    [InlineData("-5m")]
    public void NegativeKeepsTheModelLoaded(string value) => Assert.Null(LocalKeepAlive.Parse(value));

    [Fact]
    public void SettingIsNormalized()
    {
        Assert.Equal("15m", LocalKeepAlive.Normalize(" 15M "));
        Assert.Equal("5m", LocalKeepAlive.Normalize("depois"));
        var settings = new AppSettings { LocalModelKeepAlive = "nunca" };
        settings.Normalize();
        Assert.Equal("5m", settings.LocalModelKeepAlive);
        Assert.Equal("5m", new AppSettings().LocalModelKeepAlive);
    }

    [Fact]
    public void OllamaReceivesSecondsOrDuration()
    {
        Assert.Equal(-1.0, LocalKeepAlive.OllamaValue("-1"));
        Assert.Equal(0.0, LocalKeepAlive.OllamaValue("0"));
        Assert.Equal("5m", LocalKeepAlive.OllamaValue("5m"));
        Assert.Equal("5m", LocalKeepAlive.OllamaValue("lixo"));
        Assert.Equal("Libera logo após responder", LocalKeepAlive.Describe("0"));
        Assert.Equal("Sempre carregado", LocalKeepAlive.Describe("-1"));
        Assert.Equal("5 min sem uso", LocalKeepAlive.Describe("5m"));
        Assert.Equal("1 h sem uso", LocalKeepAlive.Describe("1h"));
    }

    [Fact]
    public void LoadedModelsAreDescribedLikeOllamaPs()
    {
        var now = DateTimeOffset.Parse("2026-11-08T05:00:00Z");
        var list = WinStatusBar.Core.Ai.Local.OllamaModels.ParsePs("""{"models":[{"name":"phi4-mini:latest","size":3000000000,"size_vram":0,"expires_at":"2026-11-08T05:05:00Z"},{"name":"gemma4:26b","size":2147483648,"size_vram":2147483648,"expires_at":"2318-01-01T00:00:00Z"}]}""");
        Assert.Equal(2, list.Count);
        string text = WinStatusBar.Core.Ai.Local.OllamaModels.StatusText(list, now);
        Assert.Contains("phi4-mini:latest · CPU · libera às ", text);
        Assert.Contains("gemma4:26b · 2.0 GB na GPU · sempre carregado".Replace("2.0", (2.0).ToString("0.0")), text);
        Assert.Equal("Ollama fechado", WinStatusBar.Core.Ai.Local.OllamaModels.StatusText(null, now));
        Assert.StartsWith("Nenhum modelo carregado", WinStatusBar.Core.Ai.Local.OllamaModels.StatusText([], now));
    }
}
