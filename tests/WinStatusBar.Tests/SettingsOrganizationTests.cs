using System.Text.RegularExpressions;
using System.Xml.Linq;
using WinStatusBar.Core.Settings;
using Xunit;

namespace WinStatusBar.Tests;

/// <summary>
/// Organização das Configurações de 11/11/2026 (13 páginas viraram 10): os nomes das páginas antigas continuam levando a
/// algum lugar, cada grupo montado em código tem o seu painel com nome no XAML e os textos da janela têm tradução.
/// </summary>
public class SettingsOrganizationTests
{
    private static readonly XNamespace Ui = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";
    private static readonly string[] Languages = ["pt-BR", "en-US", "es", "fr-FR", "ja-JP", "zh-Hans"];

    private static XDocument Layout()
    {
        using var file = typeof(SettingsOrganizationTests).Assembly.GetManifestResourceStream("SettingsWindow.xaml")!;
        return XDocument.Load(file);
    }

    private static string[] Partials() => Directory.GetFiles(Path.Combine(RepoSource.Root(), "src", "WinStatusBar", "Views"), "SettingsWindow*.cs");

    [Fact]
    public void MenuHasNinePagesAndTheFooter()
    {
        Assert.Equal(["system", "appearance", "accounts", "ai", "command", "models", "gpu", "connections", "clip"], SettingsNavigation.Menu);
        Assert.Equal("about", SettingsNavigation.Footer);
    }

    [Theory]
    [InlineData("shortcuts", "appearance")]
    [InlineData("battery", "connections")]
    [InlineData("updates", "about")]
    [InlineData("diagnostics", "about")]
    [InlineData("about", "about")]
    [InlineData("models", "models")]
    [InlineData("accounts", "accounts")]
    [InlineData("nao-existe", "system")]
    [InlineData(null, "system")]
    public void OldPageNamesStillLeadSomewhere(string? asked, string shown) => Assert.Equal(shown, SettingsNavigation.Resolve(asked));

    [Fact]
    public void EveryMenuPageExistsInTheWindow()
    {
        var declared = Layout().Descendants(Ui + "NavigationViewItem").Select(i => (string)i.Attribute("Tag")!).ToHashSet();
        string code = string.Concat(Partials().Select(File.ReadAllText));
        var built = Regex.Matches(code, @"AddPage\(""([a-z]+)""").Select(m => m.Groups[1].Value).ToHashSet();
        foreach (string tag in SettingsNavigation.Menu.Append(SettingsNavigation.Footer))
            Assert.True(declared.Contains(tag) || built.Contains(tag), "página sem item no menu: " + tag);
        // Nenhuma página fora da lista: ficaria sem lugar no menu (o código só recoloca as de SettingsNavigation.Menu).
        Assert.Empty(declared.Concat(built).Except(SettingsNavigation.Menu.Append(SettingsNavigation.Footer)));
    }

    [Theory]
    [InlineData("SystemExtras")]
    [InlineData("ShortcutsHost")]
    [InlineData("QiraTrackingHost")]
    [InlineData("ApiTrackingHost")]
    [InlineData("AiOrderHost")]
    [InlineData("TaskFollowHost")]
    [InlineData("DetailPanelsHost")]
    [InlineData("NetworkGroup")]
    [InlineData("NetworkRows")]
    [InlineData("SensorDriversHost")]
    [InlineData("DiagnosticsPage")]
    public void CodeBuiltGroupsHaveANamedPlace(string name)
    {
        Assert.Single(Layout().Descendants(), e => (string?)e.Attribute(X + "Name") == name);
        Assert.Contains(name, string.Concat(Partials().Select(File.ReadAllText)));
    }

    [Fact]
    public void GroupsAreNotFoundByPositionOrTitleText()
    {
        string code = string.Concat(Partials().Select(File.ReadAllText));
        Assert.DoesNotContain("FindIndex(e => e is TextBlock", code);
        Assert.DoesNotContain("Page.Children[", code);
        Assert.DoesNotContain("Page.Children.Insert(", code);
    }

    [Fact]
    public void RemovedDuplicatesStayRemoved()
    {
        var doc = Layout();
        // Linha morta do intervalo do Claude e o segundo botão da pasta de dados.
        Assert.DoesNotContain(doc.Descendants(), e => (string?)e.Attribute(X + "Name") is "ClaudeQuotaRow" or "ClaudeIntervalBox");
        Assert.DoesNotContain(doc.Descendants(Ui + "Button"), b => (string?)b.Attribute("Click") == "OnOpenData");
        // A bateria é ligada e desligada só em "Itens da barra".
        Assert.DoesNotContain("Widget de bateria", string.Concat(Partials().Select(File.ReadAllText)));
    }

    [Theory]
    [MemberData(nameof(AllLanguages))]
    public void EveryTranslatableSettingsTextIsTranslated(string language)
    {
        var files = Partials().Select(p => Path.GetRelativePath(RepoSource.Root(), p).Split(Path.DirectorySeparatorChar));
        CodexQuotaTranslationsTests.AssertTranslated(CodexQuotaTranslationsTests.Sources(files), language);
    }

    [Theory]
    [MemberData(nameof(AllLanguages))]
    public void EveryLayoutTextHasItsResource(string language)
    {
        var resw = CodexQuotaTranslationsTests.Resw(language);
        var missing = new List<string>();
        foreach (var element in Layout().Descendants().Where(e => e.Attribute(X + "Uid") is not null))
        {
            string uid = (string)element.Attribute(X + "Uid")!;
            foreach (string property in new[] { "Text", "Content", "Header", "OnContent", "OffContent", "PlaceholderText" })
                if (element.Attribute(property) is not null && !resw.ContainsKey(uid + "." + property)) missing.Add(uid + "." + property);
        }
        Assert.Empty(missing);
    }

    public static IEnumerable<object[]> AllLanguages() => Languages.Select(l => new object[] { l });
}
