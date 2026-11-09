using System.Globalization;

namespace WinStatusBar.Core.Metrics;

public static class Formatters
{
    public static readonly CultureInfo PtBr = CultureInfo.GetCultureInfo("pt-BR");
    public const double GiB = 1024d * 1024 * 1024;

    /// <summary>Converte bytes para GiB, exibidos como "GB" (mesmo padrão do Gerenciador de Tarefas).</summary>
    public static string Gb(ulong bytes) => (bytes / GiB).ToString("0.0", PtBr);

    /// <summary>"10,6 / 64,0 GB (17%)"</summary>
    public static string UsedTotal(ulong used, ulong total)
    {
        double pct = total == 0 ? 0 : used * 100.0 / total;
        return $"{Gb(used)} / {Gb(total)} GB ({Math.Round(pct).ToString("0", PtBr)}%)";
    }

    /// <summary>Velocidade em KB/s ou MB/s (base 1024).</summary>
    public static string Speed(double? bytesPerSec)
    {
        if (bytesPerSec is null) return "— KB/s";
        double v = Math.Max(0, bytesPerSec.Value);
        if (v >= 1024 * 1024 * 1000) return (v / (1024d * 1024 * 1024)).ToString("0.00", PtBr) + " GB/s";
        if (v >= 1024 * 1000) return (v / (1024d * 1024)).ToString(v >= 1024 * 1024 * 100 ? "0" : "0.00", PtBr) + " MB/s";
        return (v / 1024d).ToString(v >= 1024 * 100 ? "0" : "0.00", PtBr) + " KB/s";
    }

    public static string Percent(double? value) => value is null ? "—" : Math.Round(value.Value).ToString("0", PtBr) + " %";

    public static string GHz(double? value) => value is null ? "— GHz" : value.Value.ToString("0.00", PtBr) + " GHz";

    public static string Truncate(string? text, int max)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;
        string t = text.ReplaceLineEndings(" ").Trim();
        while (t.Contains("  ")) t = t.Replace("  ", " ");
        return t.Length <= max ? t : t[..(max - 1)].TrimEnd() + "…";
    }

    public static string ResetTime(DateTimeOffset? resetsAt)
    {
        if (resetsAt is null) return global::WinStatusBar.Core.LocalizedText.T("renovação desconhecida");
        var local = resetsAt.Value.ToLocalTime();
        var delta = resetsAt.Value - DateTimeOffset.Now;
        string when = local.Date == DateTime.Today ? local.ToString("HH:mm", PtBr) : local.ToString("ddd dd/MM HH:mm", PtBr);
        if (delta <= TimeSpan.Zero) return $"renovado ({when})";
        string inText = delta.TotalHours >= 24 ? $"{(int)delta.TotalDays}d {delta.Hours}h" : delta.TotalHours >= 1 ? $"{(int)delta.TotalHours}h {delta.Minutes:00}min" : $"{Math.Max(1, delta.Minutes)}min";
        return $"renova em {inText} ({when})";
    }
}
