using System.Text.RegularExpressions;

namespace WinStatusBar.Core.Integration;

public sealed record ChangelogItem(string Text, int Level, bool Bullet);

public sealed record ChangelogSection(string Title, IReadOnlyList<ChangelogItem> Items);

public sealed record ChangelogVersion(string Version, string? DateText, string? Build, IReadOnlyList<ChangelogSection> Sections);

/// <summary>Lê o CHANGELOG.md (Keep a Changelog) que acompanha o app, para a aba Atualizações das Configurações.</summary>
public static partial class ChangelogParser
{
    public static IReadOnlyList<ChangelogVersion> Parse(string markdown)
    {
        var versions = new List<ChangelogVersion>();
        List<ChangelogSection>? sections = null;
        List<ChangelogItem>? items = null;
        foreach (string raw in markdown.Split('\n'))
        {
            string line = raw.TrimEnd('\r');
            if (line.StartsWith("## [", StringComparison.Ordinal))
            {
                int close = line.IndexOf(']');
                if (close < 0) continue;
                string rest = line[(close + 1)..];
                var date = DateRegex().Match(rest);
                var build = BuildRegex().Match(rest);
                sections = [];
                items = null;
                versions.Add(new ChangelogVersion(line[4..close],
                    date.Success ? date.Groups[1].Value + $"{date.Groups[4].Value}/{date.Groups[3].Value}/{date.Groups[2].Value}" : null,
                    build.Success ? build.Groups[1].Value : null, sections));
                continue;
            }
            if (sections is null || string.IsNullOrWhiteSpace(line)) continue; // introdução antes da primeira versão
            if (line.StartsWith("### ", StringComparison.Ordinal))
            {
                items = [];
                sections.Add(new ChangelogSection(line[4..].Trim(), items));
                continue;
            }
            if (items is null) { items = []; sections.Add(new ChangelogSection("", items)); }
            string text = line.TrimStart();
            int level = (line.Length - text.Length) / 2;
            bool bullet = text.StartsWith("- ", StringComparison.Ordinal) || text.StartsWith("* ", StringComparison.Ordinal);
            if (bullet) text = text[2..];
            else if (NumberedRegex().Match(text) is { Success: true } numbered) { bullet = true; text = text[numbered.Length..]; }
            items.Add(new ChangelogItem(Clean(text), level, bullet));
        }
        return versions;
    }

    private static string Clean(string text)
    {
        text = LinkRegex().Replace(text, "$1");
        return text.Replace("**", "").Replace("__", "").Replace("`", "").Trim();
    }

    [GeneratedRegex(@"(até\s+)?(\d{4})-(\d{2})-(\d{2})")] private static partial Regex DateRegex();
    [GeneratedRegex(@"compila\S*\s+(?:até\s+)?(\d{14})")] private static partial Regex BuildRegex();
    [GeneratedRegex(@"^\d+\.\s+")] private static partial Regex NumberedRegex();
    [GeneratedRegex(@"\[([^\]]+)\]\([^)]+\)")] private static partial Regex LinkRegex();
}
