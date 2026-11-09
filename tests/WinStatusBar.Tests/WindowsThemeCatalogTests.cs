using WinStatusBar.Core.Metrics;
using Xunit;

namespace WinStatusBar.Tests;

public class WindowsThemeCatalogTests
{
    [Theory]
    [InlineData("[Theme]\nDisplayName=The Animal Kingdom\n", @"C:\T\Themes\The Anima\The Anima.theme", "The Animal Kingdom")]
    [InlineData("[Theme]\nDisplayName=@%SystemRoot%\\x.dll,-1\n", @"C:\T\Themes\Bing Fall\Bing Fall.theme", "Bing Fall")]
    [InlineData("[Theme]\n", @"C:\T\Themes\Bing Fall\Bing Fall.theme", "Bing Fall")]
    [InlineData("[Theme]\nDisplayName=Tema N\uFFFDo Salvo\n", @"C:\T\Themes\config (2)\config.theme", "Personalizado")]
    [InlineData("[Theme]\nDisplayName=Tema N+AOM-o Salvo\n", @"C:\T\Themes\Custom.theme", "Personalizado")]
    public void UserThemeNames(string text, string file, string expected)
        => Assert.Equal(expected, WindowsThemeCatalog.DisplayName(text, file));

    [Fact]
    public void InstalledUserThemesAreListedOnce()
    {
        string user = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "Windows", "Themes");
        var names = WindowsThemeCatalog.Read().Select(t => t.Name).ToList();
        Assert.Equal(names.Count, names.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        if (Directory.Exists(Path.Combine(user, "The Anima"))) Assert.Contains("The Animal Kingdom", names);
    }
}
