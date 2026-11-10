using System.Text.RegularExpressions;
using WinStatusBar.Core.Integration;
using Xunit;

namespace WinStatusBar.Tests;

/// <summary>A versão do projeto e o topo dos dois changelogs andam juntos, no formato que o script de publicação preenche.</summary>
public class ReleaseVersionTests
{
    private static string ProjectVersion() =>
        Regex.Match(RepoSource.Read("Directory.Build.props"), @"<Version>([^<]+)</Version>").Groups[1].Value;

    [Theory]
    [InlineData("CHANGELOG.md")]
    [InlineData("CHANGELOG.en.md")]
    public void TopChangelogVersionIsTheProjectVersion(string file) =>
        Assert.Equal(ProjectVersion(), ChangelogParser.Parse(RepoSource.Read(file))[0].Version);

    [Theory]
    [InlineData("CHANGELOG.md", "compilação")]
    [InlineData("CHANGELOG.en.md", "build")]
    public void TopHeaderKeepsTheFormatTheReleaseScriptFills(string file, string word)
    {
        string header = RepoSource.Read(file).Split('\n').First(l => l.StartsWith("## [", StringComparison.Ordinal)).TrimEnd('\r');
        Assert.Matches($@"^## \[{Regex.Escape(ProjectVersion())}\] - \d{{4}}-\d{{2}}-\d{{2}} · {word} (BUILD|\d{{14}})$", header);
    }
}
