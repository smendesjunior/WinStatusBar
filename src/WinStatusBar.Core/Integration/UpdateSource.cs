using System.Text.Json;

namespace WinStatusBar.Core.Integration;

public enum UpdateSourceKind { GitHub, Local }

/// <summary>
/// De onde esta instalação se atualiza, lido de %LOCALAPPDATA%\WinStatusBar\update-source.json (exclusivo do PC).
/// Só <c>"Mode": "Local"</c> mantém uma pasta local (PC do desenvolvedor). O formato antigo, só com LocalDirectory
/// (gravado pelo BAT do OneDrive), arquivo ausente ou ilegível usam o GitHub.
/// </summary>
public sealed record UpdateSource(UpdateSourceKind Kind, string? LocalDirectory)
{
    public static readonly UpdateSource GitHub = new(UpdateSourceKind.GitHub, null);

    public static UpdateSource Resolve(string? fileContent)
    {
        if (string.IsNullOrWhiteSpace(fileContent)) return GitHub;
        string? mode, folder;
        try
        {
            using var doc = JsonDocument.Parse(fileContent.TrimStart('﻿'));
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return GitHub;
            mode = Text(doc.RootElement, "Mode");
            folder = Text(doc.RootElement, "LocalDirectory");
        }
        catch (JsonException) { return GitHub; }
        if (!string.Equals(mode, "Local", StringComparison.OrdinalIgnoreCase)) return GitHub;
        // Pasta local inválida desliga a atualização (como antes): nunca cai em outra origem sem querer.
        if (string.IsNullOrWhiteSpace(folder) || !Path.IsPathFullyQualified(folder) || folder.StartsWith(@"\\", StringComparison.Ordinal))
            throw new InvalidDataException("A pasta local de atualização é inválida; a atualização automática fica desligada neste PC.");
        return new UpdateSource(UpdateSourceKind.Local, folder);
    }

    private static string? Text(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
