using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Xunit;

namespace WinStatusBar.Tests;

/// <summary>Todo texto novo da tela tem tradução nos seis idiomas (a chave é a de Localization.T).</summary>
public class CodexQuotaTranslationsTests
{
    private static readonly Regex Call = new(@"(?:LocalizedText|Localization)\.[TF]\(\s*""((?:[^""\\]|\\.)*)""", RegexOptions.CultureInvariant);

    /// <summary>Primeiros argumentos literais distintos de LocalizedText/Localization.T/F nos arquivos, mais os textos avulsos.</summary>
    internal static IReadOnlyList<string> Sources(IEnumerable<string[]> files, params string[] extra)
    {
        var set = new List<string>();
        foreach (var file in files)
            foreach (Match m in Call.Matches(RepoSource.Read(file)))
                set.Add(Regex.Unescape(m.Groups[1].Value));
        set.AddRange(extra);
        return set.Where(s => s.Length > 0).Distinct(StringComparer.Ordinal).ToList();
    }

    /// <summary>Mesma chave de Localization.T: "S" + 16 primeiros hex do SHA-256 do texto.</summary>
    internal static string Key(string source) => "S" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(source)))[..16];

    internal static IReadOnlyDictionary<string, string> Resw(string language)
    {
        var doc = XDocument.Load(Path.Combine(RepoSource.Root(), "src", "WinStatusBar", "Strings", language, "Resources.resw"));
        return doc.Root!.Elements("data")
            .GroupBy(d => (string)d.Attribute("name")!)
            .ToDictionary(g => g.Key, g => (string?)g.First().Element("value") ?? "");
    }

    internal static void AssertTranslated(IReadOnlyList<string> sources, string language)
    {
        var resw = Resw(language);
        Assert.Empty(sources.Where(s => !resw.ContainsKey(Key(s))).Select(s => $"{Key(s)} {s}"));
        foreach (var s in sources)
        {
            string value = resw[Key(s)];
            if (language == "pt-BR") Assert.Equal(s, value);
            foreach (var ph in new[] { "{0}", "{1}", "{2}" })
                if (s.Contains(ph)) Assert.True(value.Contains(ph), $"{language}: '{value}' perdeu {ph}");
        }
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
        var sources = Sources(
            [["src", "WinStatusBar.Core", "Ai", "Codex", "CodexQuotaText.cs"], ["src", "WinStatusBar.Core", "Ai", "Codex", "CodexQuotaStops.cs"]],
            "Falha na execução · {0}",
            "Há saldo nesta janela, mas o bloqueio da conta vale para todos os modelos",
            "A IA não registrou etapas; a dedução fica parada enquanto a cota do Codex estiver esgotada.");
        Assert.True(sources.Count >= 40, $"só {sources.Count} textos");
        AssertTranslated(sources, language);
    }
}
