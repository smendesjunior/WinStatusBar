using System.Text.RegularExpressions;

namespace WinStatusBar.Core.Clip;

/// <summary>
/// Esconde chaves e tokens na pré-visualização da área de transferência (API da OpenAI/Anthropic, GitHub, Google,
/// AWS, Slack e chaves privadas): mostra só o começo e o fim. O conteúdo copiado continua inteiro.
/// </summary>
public static partial class SecretMask
{
    [GeneratedRegex(@"\b(?:sk-(?:proj-|svcacct-|admin-|ant-)?[A-Za-z0-9_\-]{20,}|gh[pousr]_[A-Za-z0-9]{30,}|github_pat_[A-Za-z0-9_]{40,}|AIza[0-9A-Za-z_\-]{35}|AKIA[0-9A-Z]{16}|xox[abprs]-[A-Za-z0-9\-]{10,})")]
    private static partial Regex Token();
    [GeneratedRegex(@"-----BEGIN ([A-Z ]*PRIVATE KEY)-----[\s\S]*?(?:-----END \1-----|$)")]
    private static partial Regex PrivateKey();

    public static string Mask(string text)
    {
        if (text.Length < 16) return text;
        text = PrivateKey().Replace(text, m => $"-----BEGIN {m.Groups[1].Value}----- •••••• (oculta)");
        return Token().Replace(text, m => m.Value[..Math.Min(11, m.Value.Length / 3)] + "••••••" + m.Value[^4..]);
    }
}
