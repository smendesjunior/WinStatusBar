using System.Text.Json;
using System.Text.RegularExpressions;

namespace CodexIAs.Providers;

/// <summary>Linha curta de andamento para cada uso de ferramenta do Claude Code (enviada ao Codex como resumo de raciocínio).</summary>
internal static class ClaudeToolStatus
{
    public const int MaxLength = 140;

    public static string Describe(string tool, JsonElement input)
    {
        string? Str(string name) => input.ValueKind == JsonValueKind.Object && input.TryGetProperty(name, out var v)
            && v.ValueKind == JsonValueKind.String && v.GetString() is { Length: > 0 } s ? s : null;
        string? FileName(string? path) => path is null ? null : Path.GetFileName(path.TrimEnd('\\', '/')) is { Length: > 0 } n ? n : path;

        string? line = tool switch
        {
            "Bash" or "PowerShell" => Str("command") is { } c ? "Executando: " + Regex.Replace(c, @"\s+", " ").Trim() : null,
            "Read" => FileName(Str("file_path")) is { } f ? "Lendo " + f : null,
            "Write" => FileName(Str("file_path")) is { } f ? "Criando " + f : null,
            "Edit" => FileName(Str("file_path")) is { } f ? "Editando " + f : null,
            "Grep" or "Glob" => "Procurando…",
            "WebFetch" => Str("url") is { } u ? "Abrindo " + (Uri.TryCreate(u, UriKind.Absolute, out var uri) && uri.Host.Length > 0 ? uri.Host : u) : null,
            "WebSearch" => Str("query") is { } q ? "Pesquisando: " + q : null,
            _ => null,
        };
        return Cut(line ?? "Usando " + tool);
    }

    public static string Denied(string tool) => Cut("Não liberado: " + tool);

    private static string Cut(string line) => line.Length <= MaxLength ? line : line[..(MaxLength - 1)] + "…";
}
