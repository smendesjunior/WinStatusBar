using System.Diagnostics;
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

    /// <summary>
    /// Margem antes do vencimento em que o Claude Code já renova sozinho ao abrir (5 min no Claude Code 2.1.327), com folga.
    /// Antes era 2 min: entre 5 e 2 min do fim a barra achava o login bom e abria o Claude, que renovava.
    /// </summary>
    internal static readonly TimeSpan RefreshMargin = TimeSpan.FromMinutes(6);

    public enum LoginState
    {
        /// <summary>Sem login OAuth (chave de API ou nada).</summary>
        None,
        /// <summary>Acesso válido além da margem: abrir o Claude Code não renova nada.</summary>
        Valid,
        /// <summary>Acesso vencido ou vencendo, com código de renovação: abrir o Claude Code renova.</summary>
        NeedsRefresh,
        /// <summary>O Claude Code apagou o login (renovação recusada): só entrar de novo resolve.</summary>
        SignedOut,
    }

    public static LoginState State() => State(ReadOrNull(FilePath), DateTimeOffset.UtcNow);

    internal static LoginState State(string? json, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(json)) return LoginState.None;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object
                || !doc.RootElement.TryGetProperty("claudeAiOauth", out var oauth) || oauth.ValueKind != JsonValueKind.Object) return LoginState.None;
            bool access = Filled(oauth, "accessToken"), refresh = Filled(oauth, "refreshToken");
            if (!access && !refresh) return LoginState.SignedOut;
            if (!oauth.TryGetProperty("expiresAt", out var exp) || !exp.TryGetInt64(out long ms)) return LoginState.Valid;
            if (ms > (now + RefreshMargin).ToUnixTimeMilliseconds()) return LoginState.Valid;
            return refresh ? LoginState.NeedsRefresh : LoginState.SignedOut;
        }
        catch (JsonException) { return LoginState.None; }
    }

    /// <summary>
    /// True quando a barra não deve abrir o Claude Code sozinha: acesso vencido (ou vencendo dentro da margem), que o
    /// Claude renovaria ao abrir, ou login já apagado.
    /// </summary>
    public static bool NeedsRefresh() => NeedsRefresh(ReadOrNull(FilePath), DateTimeOffset.UtcNow);

    internal static bool NeedsRefresh(string? json, DateTimeOffset now) => State(json, now) is LoginState.NeedsRefresh or LoginState.SignedOut;

    /// <summary>
    /// Encerra um Claude Code aberto pela barra sem matá-lo no meio de uma renovação do login. Morto à força enquanto
    /// grava o login renovado, o servidor já trocou o código de renovação e o novo não chega ao arquivo; no uso seguinte o
    /// Claude Code recebe "invalid_grant" e apaga o login do PC (medido em 21/11/2026: arquivo
    /// .credentials.json.tmp vazio no mesmo segundo em que o encaminhador do Codex matou o Claude). Fecha a entrada,
    /// espera o processo sair sozinho e, se passar do tempo, deixa-o terminar por conta própria (nunca Kill).
    /// </summary>
    public static async Task LetExitAsync(Process proc, TimeSpan grace)
    {
        try { proc.StandardInput.Close(); } catch { }
        try
        {
            if (proc.HasExited) return;
            using var wait = new CancellationTokenSource(grace);
            await proc.WaitForExitAsync(wait.Token);
        }
        catch (OperationCanceledException) { } // segue sozinho: sem a entrada, o Claude Code sai ao terminar o que fazia
        catch (InvalidOperationException) { }
    }

    private static bool Filled(JsonElement o, string name)
        => o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(v.GetString());

    private static string? ReadOrNull(string path)
    {
        try { return File.Exists(path) ? File.ReadAllText(path) : null; } catch { return null; }
    }
}
