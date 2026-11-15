using System.Text.Json;
using WinStatusBar.Core.Ai.Codex;
using Xunit;

namespace WinStatusBar.Tests;

public class ChatGptTitleRenamerTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 15, 22, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData("15/11/2026 - Vivo - Ativação de eSIM", true)]
    [InlineData("Automatizar nomes de conversas", false)]
    [InlineData("15/11/2026 - Só tema", false)]
    [InlineData(null, false)]
    public void Formatted(string? title, bool expected) => Assert.Equal(expected, ChatGptTitleRenamer.IsFormatted(title));

    [Fact]
    public void RenamesOnlyRecentSettledChats()
    {
        Assert.True(ChatGptTitleRenamer.ShouldRename("New chat", Now.AddMinutes(-10), Now));
        Assert.False(ChatGptTitleRenamer.ShouldRename("New chat", Now.AddMinutes(-1), Now));   // ChatGPT ainda vai dar o título dele
        Assert.False(ChatGptTitleRenamer.ShouldRename("New chat", Now.AddDays(-8), Now));      // antigos ficam como estão
        Assert.False(ChatGptTitleRenamer.ShouldRename("15/11/2026 - A - B", Now.AddHours(-1), Now));
        Assert.False(ChatGptTitleRenamer.ShouldRename("New chat", null, Now));
    }

    [Theory]
    [InlineData("\"Automação - Padronização de títulos\"", "Automação - Padronização de títulos")]
    [InlineData("Título: Vivo — Portabilidade do chip.", "Vivo - Portabilidade do chip")]
    [InlineData("15/11/2026 - PC - Desempenho\nmais texto", "PC - Desempenho")]
    [InlineData("Sem separador", null)]
    [InlineData("  ", null)]
    public void CleansSubject(string raw, string? expected) => Assert.Equal(expected, ChatGptTitleRenamer.CleanSubject(raw));

    [Fact]
    public void ComposesWithLocalDate() =>
        Assert.Matches(@"^\d{2}/\d{2}/2026 - A - B$", ChatGptTitleRenamer.Compose(Now, "A - B"));

    [Fact]
    public void ExcerptKeepsConversationOrder()
    {
        const string json = """
        {"mapping":{
          "b":{"message":{"author":{"role":"assistant"},"create_time":2,"content":{"parts":["resposta"]}}},
          "s":{"message":{"author":{"role":"system"},"create_time":0,"content":{"parts":["oculto"]}}},
          "a":{"message":{"author":{"role":"user"},"create_time":1,"content":{"parts":["pergunta"]}}},
          "r":{"message":null}}}
        """;
        using var doc = JsonDocument.Parse(json);
        Assert.Equal("user: pergunta\nassistant: resposta", ChatGptTitleRenamer.Excerpt(doc.RootElement));
    }

    [Fact]
    public void AutoTitlesOnByDefaultAndMigratedOnce()
    {
        Assert.True(new WinStatusBar.Core.Settings.AppSettings().ChatGptAutoTitles);
        var old = new WinStatusBar.Core.Settings.AppSettings { SchemaVersion = 4, ChatGptAutoTitles = false };
        old.Normalize();
        Assert.True(old.ChatGptAutoTitles);
        var chosen = new WinStatusBar.Core.Settings.AppSettings { SchemaVersion = 5, ChatGptAutoTitles = false };
        chosen.Normalize();
        Assert.False(chosen.ChatGptAutoTitles); // quem desligou depois continua desligado
    }

    [Fact]
    public void PicksLightestModel()
    {
        Assert.Equal("gpt-6-luna", ChatGptTitleRenamer.PickModel("""{"models":[{"slug":"gpt-6.1-sol"},{"slug":"gpt-6-luna"}]}""", null));
        Assert.Equal("gpt-6.1-sol", ChatGptTitleRenamer.PickModel(null, "model = \"gpt-6.1-sol\"\n[x]\nmodel = \"y\""));
        Assert.Null(ChatGptTitleRenamer.PickModel(null, null));
    }
}
