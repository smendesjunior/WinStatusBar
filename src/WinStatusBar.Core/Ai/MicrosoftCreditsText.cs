using System.Text.RegularExpressions;

namespace WinStatusBar.Core.Ai;

/// <summary>Leitura da conta Microsoft (pt-BR e inglês): plano ("Gerenciar: Microsoft 365 Family") e "Saldo de crédito de IA".</summary>
public static class MicrosoftCreditsText
{
    public sealed record Credits(int? Remaining, int? Total, int? Used, string? Plan);

    public static Credits? Parse(string text)
    {
        string? plan = Regex.Match(text, @"(?:Gerenciar|Manage)\s*:\s*(Microsoft 365 [^\r\n]{1,40}|Copilot Pro[^\r\n]{0,20})", RegexOptions.IgnoreCase) is { Success: true } pm ? pm.Groups[1].Value.Trim() : null;
        int at = Regex.Match(text, @"(Saldo de crédito de IA|AI credit balance|AI credits)", RegexOptions.IgnoreCase) is { Success: true } h ? h.Index : 0;
        string part = text[at..];
        var m = Regex.Match(part, @"(\d+)\s*/\s*(\d+)\s*(créditos|credits)", RegexOptions.IgnoreCase);
        var used = Regex.Match(part, @"(\d+)\s*(usados?|used)\b", RegexOptions.IgnoreCase);
        if (!m.Success && plan is null) return null;
        return new(m.Success ? int.Parse(m.Groups[1].Value) : null, m.Success ? int.Parse(m.Groups[2].Value) : null,
            m.Success && used.Success ? int.Parse(used.Groups[1].Value) : null, plan);
    }
}
