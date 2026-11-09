using System.Text.RegularExpressions;

namespace WinStatusBar.Core.Metrics;

/// <summary>
/// Deixa um logotipo SVG de uma cor só (como os ícones Fluent da barra): todo preenchimento e contorno visível passa a
/// usar a cor dada, inclusive os que apontavam para degradês. Blocos de definição, máscara e recorte ficam intactos,
/// porque máscaras dependem de branco/preto e não são pintura.
/// </summary>
public static class SvgRecolor
{
    private static readonly Regex Protected = new(@"<(defs|mask|clipPath)\b.*?</\1\s*>", RegexOptions.Singleline | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex Paint = new(@"(?<![\w-])(fill|stroke)\s*=\s*""(?!none"")[^""]*""", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex StylePaint = new(@"(?<![\w-])(fill|stroke)\s*:\s*(?!none\b)[^;""']+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex RootWithoutFill = new(@"<svg\b(?![^>]*\sfill\s*=)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static string Monochrome(string svg, string color)
    {
        var kept = new List<string>();
        string body = Protected.Replace(svg, m => { kept.Add(m.Value); return $"\u0001{kept.Count - 1}\u0001"; });
        body = Paint.Replace(body, m => $"{m.Groups[1].Value}=\"{color}\"");
        body = StylePaint.Replace(body, m => $"{m.Groups[1].Value}:{color}");
        // Formas sem preenchimento declarado herdam da raiz (o padrão do SVG seria preto).
        body = RootWithoutFill.Replace(body, $"<svg fill=\"{color}\"", 1);
        return Regex.Replace(body, "\u0001(\\d+)\u0001", m => kept[int.Parse(m.Groups[1].Value)]);
    }
}
