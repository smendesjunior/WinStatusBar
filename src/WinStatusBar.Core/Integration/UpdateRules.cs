using System.Security.Cryptography;
using System.Text.Json;

namespace WinStatusBar.Core.Integration;

/// <summary>Manifesto publicado junto do instalador (Release do GitHub ou pasta local deste PC).</summary>
/// <remarks>Versões 1.3.x leem o mesmo arquivo só com Build, Sha256 e Size; Version e Url são campos novos.</remarks>
public sealed record UpdateManifest(string? Version, string Build, string Sha256, long Size, string? Url);

/// <summary>Regras da atualização automática: onde procurar, o que aceitar e como conferir o download.</summary>
public static class UpdateRules
{
    public const string ManifestFileName = "WinStatusBar-update.json";
    public const string SetupFileName = "WinStatusBar-Setup.exe";

    /// <summary>Link fixo da Release mais recente (sem token e sem o limite de consultas da API do GitHub).</summary>
    public const string GitHubManifestUrl = "https://github.com/smendesjunior/WinStatusBar/releases/latest/download/" + ManifestFileName;

    /// <summary>Só instaladores das Releases deste repositório são aceitos.</summary>
    public const string GitHubDownloadPrefix = "https://github.com/smendesjunior/WinStatusBar/releases/download/";

    public static readonly TimeSpan FirstCheckDelay = TimeSpan.FromMinutes(1);
    public static readonly TimeSpan CheckInterval = TimeSpan.FromMinutes(15);

    /// <summary>Lê o manifesto; null se não for um manifesto (HTML de erro, JSON quebrado, campos com tipo errado).</summary>
    public static UpdateManifest? Parse(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json.TrimStart('﻿'));
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            string? build = Text(root, "Build"), sha = Text(root, "Sha256");
            if (build is null || sha is null || !root.TryGetProperty("Size", out var size) || size.ValueKind != JsonValueKind.Number) return null;
            return new UpdateManifest(Text(root, "Version"), build, sha, size.GetInt64(), Text(root, "Url"));
        }
        catch (Exception ex) when (ex is JsonException or FormatException or InvalidOperationException) { return null; }
    }

    private static string? Text(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    /// <summary>Compilação mais nova, tamanho e hash bem formados e, no GitHub, instalador deste repositório.</summary>
    public static bool IsInstallable(UpdateManifest m, string currentBuild, UpdateSourceKind kind)
    {
        if (m.Build is not { Length: 14 } || !m.Build.All(char.IsAsciiDigit) || string.CompareOrdinal(m.Build, currentBuild) <= 0) return false;
        if (m.Size <= 0 || m.Sha256 is not { Length: 64 } || !m.Sha256.All(char.IsAsciiHexDigit)) return false;
        return kind == UpdateSourceKind.Local || (m.Url is not null && m.Url.StartsWith(GitHubDownloadPrefix, StringComparison.Ordinal));
    }

    /// <summary>Confere tamanho e SHA-256 do arquivo baixado antes de qualquer execução.</summary>
    public static void VerifyDownload(string file, UpdateManifest m)
    {
        if (new FileInfo(file).Length != m.Size)
            throw new InvalidDataException("O download veio incompleto; a atualização fica para a próxima consulta.");
        using var stream = File.OpenRead(file);
        if (!Convert.ToHexString(SHA256.HashData(stream)).Equals(m.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("A cópia baixada não confere com a publicação; a atualização fica para a próxima consulta.");
    }
}
