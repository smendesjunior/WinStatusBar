using System.Globalization;
using System.Text.RegularExpressions;

namespace WinStatusBar.Core.Ai;

/// <summary>
/// Tempo que o modelo da IA local fica carregado depois do último uso, no formato do keep_alive do Ollama, que a barra
/// envia em cada pedido. O próprio Ollama faz o resto: carrega sob demanda, só começa a contar quando a resposta termina,
/// reinicia a contagem a cada uso e libera a GPU (ou a memória) quando o tempo acaba.
///  • "0" libera logo após responder; valor negativo ("-1") mantém carregado; padrão de 5 minutos;
///  • formato: "30s", "5m", "1h", "1h30m"; número puro = segundos.
/// </summary>
public static class LocalKeepAlive
{
    public const string DefaultValue = "5m";
    public static readonly TimeSpan Default = TimeSpan.FromMinutes(5);

    /// <summary>Opções oferecidas nas configurações (valores no formato do Ollama).</summary>
    public static readonly IReadOnlyList<string> Choices = ["0", "1m", "5m", "15m", "30m", "1h", "-1"];

    private static readonly Regex Part = new(@"(\d+(?:\.\d+)?)(ms|s|m|h)", RegexOptions.CultureInvariant);

    /// <summary>Converte no formato do Ollama. null = manter carregado; valor inválido = padrão (5 min).</summary>
    public static TimeSpan? Parse(string? value) => TryParse(value, out var result) ? result : Default;

    /// <summary>Valor guardado nas configurações: válido no formato do Ollama, senão o padrão.</summary>
    public static string Normalize(string? value) => TryParse(value, out _) ? value!.Trim().ToLowerInvariant() : DefaultValue;

    private static bool TryParse(string? value, out TimeSpan? result)
    {
        result = Default;
        string text = (value ?? "").Trim().ToLowerInvariant();
        if (text.Length == 0) return false;
        bool negative = text.StartsWith('-');
        string body = negative ? text[1..] : text;
        TimeSpan span;
        if (double.TryParse(body, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out double seconds)) span = TimeSpan.FromSeconds(seconds);
        else
        {
            var parts = Part.Matches(body);
            if (parts.Count == 0 || string.Concat(parts.Select(m => m.Value)) != body) return false;
            span = TimeSpan.Zero;
            foreach (Match m in parts)
            {
                double n = double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
                span += m.Groups[2].Value switch { "ms" => TimeSpan.FromMilliseconds(n), "s" => TimeSpan.FromSeconds(n), "m" => TimeSpan.FromMinutes(n), _ => TimeSpan.FromHours(n) };
            }
        }
        result = negative && span > TimeSpan.Zero ? null : span;
        return true;
    }

    public static string Describe(string? value) => Parse(value) switch
    {
        null => "Sempre carregado",
        { Ticks: 0 } => "Libera logo após responder",
        var t => Short(t.Value) + " sem uso",
    };

    public static string Short(TimeSpan t) =>
        t.TotalHours >= 1 && t.TotalMinutes % 60 == 0 ? $"{t.TotalHours:0} h"
        : t.TotalMinutes >= 1 && t.TotalSeconds % 60 == 0 ? $"{t.TotalMinutes:0} min"
        : $"{t.TotalSeconds:0} s";

    /// <summary>Valor para o campo keep_alive da API do Ollama: número = segundos; texto = duração ("5m", "-1").</summary>
    public static object OllamaValue(string? value)
    {
        string text = Normalize(value);
        return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double seconds) ? seconds : text;
    }
}
