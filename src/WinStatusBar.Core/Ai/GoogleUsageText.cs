using System.Text.RegularExpressions;

namespace WinStatusBar.Core.Ai;

/// <summary>Leitura do texto de gemini.google.com/usage (pt-BR e inglês): plano, uso atual e semanal.</summary>
public static class GoogleUsageText
{
    public sealed record Usage(string? Plan, double? CurrentPercent, string? CurrentReset, double? WeeklyPercent, string? WeeklyReset);

    /// <summary>Extrai plano, uso atual e semanal (porcentagens e horários de renovação) do texto da página.</summary>
    public static Usage? Parse(string text)
    {
        var used = Regex.Matches(text, @"(?:Quantidade usada|Amount used)\s*:?\s*(\d+(?:[.,]\d+)?)\s*%", RegexOptions.IgnoreCase);
        if (used.Count == 0) return null;
        double Pct(Match m) => double.Parse(m.Groups[1].Value.Replace(',', '.'), System.Globalization.CultureInfo.InvariantCulture);
        var resets = Regex.Matches(text, @"([^\n]{1,40}?)\s*:\s*(?:o limite de uso é redefinido|usage limit resets)", RegexOptions.IgnoreCase);
        string? plan = Regex.Match(text, @"(?:Limites de uso|Usage limits)\s*\n?\s*(PLUS|PRO|ULTRA|FREE|GRATUITO)\b", RegexOptions.IgnoreCase) is { Success: true } p ? p.Groups[1].Value.ToUpperInvariant() : null;
        int weeklyIndex = text.IndexOf("Limite semanal", StringComparison.OrdinalIgnoreCase) is >= 0 and var wi ? wi : text.IndexOf("Weekly limit", StringComparison.OrdinalIgnoreCase);
        Match? current = used.FirstOrDefault(m => weeklyIndex < 0 || m.Index < weeklyIndex);
        Match? weekly = weeklyIndex < 0 ? null : used.FirstOrDefault(m => m.Index > weeklyIndex);
        string? currentReset = resets.FirstOrDefault(m => weeklyIndex < 0 || m.Index < weeklyIndex)?.Groups[1].Value.Trim();
        string? weeklyReset = weeklyIndex < 0 ? null : resets.FirstOrDefault(m => m.Index > weeklyIndex)?.Groups[1].Value.Trim();
        return new(plan, current is null ? null : Pct(current), currentReset, weekly is null ? null : Pct(weekly), weeklyReset);
    }
}
