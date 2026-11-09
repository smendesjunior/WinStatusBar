using System.Globalization;
using System.Text.RegularExpressions;
namespace WinStatusBar.Core.Ai;

/// <summary>Limite de um modelo na chave de API do Gemini: usado / limite por minuto (RPM), tokens por minuto (TPM) e por dia (RPD).</summary>
public sealed record AiStudioModelLimit(string Model, long? RpmUsed, long? RpmLimit, long? TpmUsed, long? TpmLimit, long? RpdUsed, long? RpdLimit)
{
    /// <summary>Percentual do limite diário de pedidos já usado (a cota que zera todo dia).</summary>
    public double? DailyPercent => RpdUsed is { } u && RpdLimit is > 0 ? Math.Min(100, 100.0 * u / RpdLimit.Value) : null;
}

/// <param name="Period">Intervalo aplicado pela página ("1 dia", "28 dias"): os valores "usado" são o MÁXIMO por modelo nesse intervalo.</param>
public sealed record AiStudioUsage(string? Tier, IReadOnlyList<AiStudioModelLimit> Models, string? Period = null)
{
    /// <summary>Intervalo de um dia (ou menos): o RPD lido equivale ao uso de hoje.</summary>
    public static bool IsDaily(string? period) => period is not null && Regex.IsMatch(period, @"(?i)^\s*(1\s*(dia|day)|24\s*(h|horas|hours)|hoje|today|\d+\s*(hora|hour|min))");
}

/// <summary>
/// Leitura do texto da página oficial de limites do Google AI Studio (aistudio.google.com, aba de limites de taxa).
/// A tabela traz, por modelo, colunas "usado / limite" (ex.: "3 / 1K"). A ordem das colunas vem do cabeçalho
/// (RPM, TPM, RPD); sem cabeçalho, assume essa ordem. Só números são guardados.
/// </summary>
public static class AiStudioUsageText
{
    private static readonly Regex Fraction = new(@"(?<u>\d[\d.,]*\s*[KMB]?)\s*/\s*(?<l>\d[\d.,]*\s*[KMB]?|Unlimited|Ilimitad[oa]|∞)", RegexOptions.IgnoreCase);
    private static readonly Regex ModelName = new(@"^\s*(?<m>(Gemini|Gemma|Imagen|Veo|Lyria|Text Embedding|Embedding|gemini-|gemma-|imagen-|veo-|text-embedding)[^\t\r\n]*?)\s*(\t|$)", RegexOptions.IgnoreCase);
    private static readonly Regex TierPattern = new(@"\b(Free tier|Nível sem custo|Nível gratuito|Camada sem custo|Tier\s*[1-3]|Nível\s*[1-3])\b", RegexOptions.IgnoreCase);

    public static AiStudioUsage? Parse(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        // Ordem das colunas pelo primeiro cabeçalho que cite as métricas.
        var order = new List<string>();
        foreach (var line in text.Split('\n'))
        {
            var found = Regex.Matches(line, @"\b(RPM|TPM|RPD)\b").Select(m => m.Value.ToUpperInvariant()).Distinct().ToList();
            if (found.Count >= 2) { order = found; break; }
        }
        // Cabeçalho com uma métrica por linha (uma célula por linha no texto da página): ordem de primeira aparição.
        if (order.Count < 2) order = Regex.Matches(text, @"(?m)^\s*(RPM|TPM|RPD)\s*$").Select(m => m.Groups[1].Value.ToUpperInvariant()).Distinct().ToList();
        if (order.Count < 2) order = ["RPM", "TPM", "RPD"];

        var models = new List<AiStudioModelLimit>();
        string? current = null; var values = new List<(long? Used, long? Limit)>();
        void Flush()
        {
            if (current is null || values.Count == 0) { current = null; values.Clear(); return; }
            (long?, long?) Col(string name) { int i = order.IndexOf(name); return i >= 0 && i < values.Count ? values[i] : (null, null); }
            var (rpmU, rpmL) = Col("RPM"); var (tpmU, tpmL) = Col("TPM"); var (rpdU, rpdL) = Col("RPD");
            models.Add(new(current, rpmU, rpmL, tpmU, tpmL, rpdU, rpdL));
            current = null; values.Clear();
        }
        foreach (var raw in text.Replace("\r", "").Split('\n'))
        {
            string line = raw.Trim();
            if (line.Length == 0) continue;
            var name = ModelName.Match(line);
            if (name.Success && !Fraction.IsMatch(name.Groups["m"].Value))
            {
                Flush();
                current = Regex.Replace(name.Groups["m"].Value, @"\s+", " ").Trim();
            }
            if (current is null) continue;
            foreach (Match f in Fraction.Matches(line)) values.Add((Number(f.Groups["u"].Value), Number(f.Groups["l"].Value)));
        }
        Flush();
        if (models.Count == 0) return null;
        var tier = TierPattern.Match(text);
        var period = Regex.Match(text, @"(?im)^\s*(Intervalo de tempo|Time range)[ \t]*\r?\n\s*(?<p>[^\r\n]+)");
        return new(tier.Success ? tier.Value : null, models, period.Success ? period.Groups["p"].Value.Trim() : null);
    }

    /// <summary>"1K" → 1000, "2.5M" → 2 500 000, "1.000" → 1000; "Unlimited"/"∞" → null.</summary>
    public static long? Number(string s)
    {
        s = s.Trim().Replace(" ", "");
        if (s.Length == 0 || !char.IsDigit(s[0])) return null;
        double mult = 1;
        char last = char.ToUpperInvariant(s[^1]);
        if (last is 'K' or 'M' or 'B') { mult = last == 'K' ? 1e3 : last == 'M' ? 1e6 : 1e9; s = s[..^1]; }
        // Separador: com sufixo, "2.5"/"2,5" é decimal; sem sufixo, "1.000"/"1,000" é milhar.
        if (mult > 1) s = s.Replace(',', '.');
        else s = Regex.IsMatch(s, @"^\d{1,3}([.,]\d{3})+$") ? s.Replace(".", "").Replace(",", "") : s.Replace(',', '.');
        return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? (long)Math.Round(v * mult) : null;
    }

    /// <summary>"1.000" e "250 mil" para exibir.</summary>
    public static string Show(long? n) => n is not { } v ? "—" : v >= 1_000_000 ? (v / 1e6).ToString("0.#", CultureInfo.GetCultureInfo("pt-BR")) + " mi"
        : v >= 10_000 ? (v / 1e3).ToString("0.#", CultureInfo.GetCultureInfo("pt-BR")) + " mil" : v.ToString("N0", CultureInfo.GetCultureInfo("pt-BR"));
}
