using WinStatusBar.Core.Ai;
using WinStatusBar.Core.Ai.Codex;
using WinStatusBar.Core.Integration;

namespace WinStatusBar.Services;

internal static class AiAccountReader
{
    private static string? ReadFile(string path)
    {
        try { return new FileInfo(path).Length <= 1024 * 1024 ? File.ReadAllText(path) : null; }
        catch { return null; }
    }
    private static bool Env(string key) => !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(key));
    public static AiConnectionStatus Read(string provider)
    {
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return provider switch
        {
            "claude" => AiConnectionStatus.Claude(ReadFile(Path.Combine(home, ".claude.json")),
                ReadFile(Path.Combine(ClaudeIntegrationInstaller.ClaudeConfigDir(), ".credentials.json")), Env("ANTHROPIC_API_KEY")),
            "codex" => AiConnectionStatus.Codex(ReadFile(Path.Combine(CodexAppServerClient.CodexHome(), "auth.json")), Env("OPENAI_API_KEY") || Env("CODEX_API_KEY")),
            "gemini" => AiConnectionStatus.Gemini(ReadFile(Path.Combine(home, ".gemini", "google_accounts.json")),
                File.Exists(Path.Combine(home, ".gemini", "oauth_creds.json")), ApiKeyStore.Get("WinStatusBar.Ai.gemini") is not null || Env("GEMINI_API_KEY")),
            _ => new(false, false, null)
        };
    }

    public static string Describe(AiConnectionStatus state)
        => (state.HasSession
            ? "Sessão local: " + (state.Email ?? "e-mail não informado pelo provedor") + ". Validade online não verificada."
            : "Nenhuma sessão de conta encontrada nos arquivos locais. O provedor pode usar outro cofre de credenciais.")
           + "\nAPI do CLI: " + (state.HasApiKey ? "chave configurada; validade não verificada. Uma chave não informa o e-mail da conta." : "nenhuma chave detectada no ambiente ou nas credenciais suportadas.")
           + (state.HasSession && state.HasApiKey ? "\nHá sessão e chave; a prioridade de autenticação depende do CLI." : "");
}
