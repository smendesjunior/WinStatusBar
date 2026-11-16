using System.Text.Json;
using WinStatusBar.Core.Integration;

namespace WinStatusBar.Core.Ai.Claude;

/// <summary>
/// Estado do login do Claude Code em ~/.claude/.credentials.json, só para decidir se a barra pode chamar o CLI sozinha.
/// Com o acesso vencido, o CLI tenta renovar; renovações concorrentes (barra + Claude Code/app aberto) giram o token e
/// uma delas falha, e o Claude Code apaga o login do PC. Por isso chamadas automáticas esperam o próprio Claude renovar.
/// </summary>
public static class ClaudeCredentials
{
    public static string FilePath => Path.Combine(ClaudeIntegrationInstaller.ClaudeConfigDir(), ".credentials.json");

    /// <summary>True quando há login OAuth cujo acesso venceu (ou vence em 2 min) e só uma renovação o liberaria.</summary>
    public static bool NeedsRefresh() => NeedsRefresh(ReadOrNull(FilePath), DateTimeOffset.UtcNow);

    internal static bool NeedsRefresh(string? json, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(json)) return false; // sem login OAuth (chave de API ou nada): não há o que renovar
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("claudeAiOauth", out var oauth) || oauth.ValueKind != JsonValueKind.Object) return false;
            if (!oauth.TryGetProperty("expiresAt", out var exp) || !exp.TryGetInt64(out long ms)) return false;
            return ms <= now.AddMinutes(2).ToUnixTimeMilliseconds();
        }
        catch (JsonException) { return false; }
    }

    private static string? ReadOrNull(string path)
    {
        try { return File.Exists(path) ? File.ReadAllText(path) : null; } catch { return null; }
    }
}
