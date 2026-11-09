using WinStatusBar.Core.Integration;
using Xunit;

namespace WinStatusBar.Tests;

/// <summary>Leitura do CHANGELOG.md para a aba Atualizações das Configurações.</summary>
public class ChangelogParserTests
{
    private const string Sample = """
        # Registro de mudanças do WinStatusBar

        Texto de introdução que não é versão.
        - item solto antes das versões

        ## [1.3.2] - 2026-11-09 · compilação 20261109050307

        ### Corrigido
        - Tarefas do `codex exec`: **sem** "Executando" para sempre.
        - Menu: veja [o guia](https://example.com/guia).

        ## [1.3.0] - 2026-11-08 · compilação 20261109001225

        ### Adicionado
        - Corrigir problemas:
          - o encaminhador parado;
          - a porta ocupada.
          No fim, verifica os modelos.

        ### Alterado
        - Lista do Claude.

        ## [1.0.0] - até 2026-11-08 · compilações até 20261108200604

        ### Adicionado
        - Primeira versão.
        """;

    [Fact]
    public void ParsesVersionsInFileOrder()
    {
        var versions = ChangelogParser.Parse(Sample);
        Assert.Equal(new[] { "1.3.2", "1.3.0", "1.0.0" }, versions.Select(v => v.Version));
    }

    [Fact]
    public void ParsesDateAndBuild()
    {
        var v = ChangelogParser.Parse(Sample)[0];
        Assert.Equal("09/11/2026", v.DateText);
        Assert.Equal("20261109050307", v.Build);
    }

    [Fact]
    public void KeepsUpToPrefixInDate() => Assert.Equal("até 08/11/2026", ChangelogParser.Parse(Sample)[2].DateText);

    [Fact]
    public void PlaceholderBuildIsNull() =>
        Assert.Null(ChangelogParser.Parse("## [1.4.0] - 2026-11-09 · compilação BUILD\n### Adicionado\n- x")[0].Build);

    [Fact]
    public void ParsesSectionsWithItems()
    {
        var v = ChangelogParser.Parse(Sample)[1];
        Assert.Equal(new[] { "Adicionado", "Alterado" }, v.Sections.Select(s => s.Title));
        Assert.Equal("Lista do Claude.", v.Sections[1].Items.Single().Text);
    }

    [Fact]
    public void NestedItemsKeepTheirLevel()
    {
        var items = ChangelogParser.Parse(Sample)[1].Sections[0].Items;
        Assert.Equal(new[] { 0, 1, 1, 1 }, items.Select(i => i.Level));
        Assert.Equal("o encaminhador parado;", items[1].Text);
    }

    [Fact]
    public void IndentedTextWithoutDashIsContinuationWithoutBullet()
    {
        var last = ChangelogParser.Parse(Sample)[1].Sections[0].Items[3];
        Assert.Equal("No fim, verifica os modelos.", last.Text);
        Assert.False(last.Bullet);
        Assert.Equal(1, last.Level);
    }

    [Fact]
    public void StripsInlineMarkdown()
    {
        var items = ChangelogParser.Parse(Sample)[0].Sections[0].Items;
        Assert.Equal("Tarefas do codex exec: sem \"Executando\" para sempre.", items[0].Text);
        Assert.Equal("Menu: veja o guia.", items[1].Text);
    }

    [Fact]
    public void EmptyOrMissingTextGivesNoVersions()
    {
        Assert.Empty(ChangelogParser.Parse(""));
        Assert.Empty(ChangelogParser.Parse("# Só título\n\nNada aqui."));
    }
}
