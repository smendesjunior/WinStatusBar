using Xunit;

namespace WinStatusBar.Tests;

/// <summary>Todo texto novo da continuação pela API tem tradução nos seis idiomas.</summary>
public class ApiResumeTranslationsTests
{
    internal static IEnumerable<string[]> Files()
    {
        string ar = Path.Combine(RepoSource.Root(), "src", "WinStatusBar.Core", "Ai", "Codex", "ApiResume");
        foreach (var file in Directory.EnumerateFiles(ar, "*.cs").OrderBy(f => f, StringComparer.Ordinal))
            yield return ["src", "WinStatusBar.Core", "Ai", "Codex", "ApiResume", Path.GetFileName(file)];
        yield return ["src", "WinStatusBar", "Services", "CodexApiResumeService.cs"];
        yield return ["src", "WinStatusBar", "Views", "CodexApiResumeView.cs"];
        yield return ["src", "WinStatusBar", "Views", "ContinueViaApiDialog.cs"];
        yield return ["src", "WinStatusBar", "Views", "BarWindow.Activity.cs"];
    }

    [Theory]
    [InlineData("pt-BR")]
    [InlineData("en-US")]
    [InlineData("es")]
    [InlineData("fr-FR")]
    [InlineData("ja-JP")]
    [InlineData("zh-Hans")]
    public void EveryNewTextIsTranslated(string language)
    {
        var sources = CodexQuotaTranslationsTests.Sources(Files());
        Assert.True(sources.Count >= 60, $"só {sources.Count} textos");
        CodexQuotaTranslationsTests.AssertTranslated(sources, language);
    }

    [Fact]
    public void NoUnqualifiedTranslationCalls()
    {
        foreach (var file in Files())
        {
            string text = RepoSource.Read(file);
            Assert.DoesNotContain("using static WinStatusBar.Core.LocalizedText", text);
            Assert.DoesNotContain("using static WinStatusBar.Services.Localization", text);
        }
    }
}
