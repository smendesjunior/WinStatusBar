using Windows.Security.Credentials;

namespace WinStatusBar.Services;

/// <summary>Chave da API própria no Cofre de Credenciais do Windows (nunca no settings.json nem em logs).</summary>
internal static class ApiKeyStore
{
    private const string Resource = "WinStatusBar.CommandApi";
    private const string User = "api-key";

    public static string? Get(string resource = Resource)
    {
        try { var c = new PasswordVault().Retrieve(resource, User); c.RetrievePassword(); return c.Password; }
        catch { return null; }
    }

    /// <summary>Há credencial para o recurso? Não lê a senha (só a lista do Cofre).</summary>
    public static bool Exists(string resource)
    {
        try { return new PasswordVault().FindAllByResource(resource).Count > 0; }
        catch { return false; } // o Cofre lança quando não há nenhuma
    }

    public static bool HasKey => !string.IsNullOrEmpty(Get());

    public static void Set(string? key, string resource = Resource)
    {
        string? previousGemini = resource == "WinStatusBar.Ai.gemini" ? Get(resource) : null;
        var vault = new PasswordVault();
        try { foreach (var c in vault.FindAllByResource(resource)) vault.Remove(c); } catch { }
        if (!string.IsNullOrWhiteSpace(key)) vault.Add(new PasswordCredential(resource, User, key.Trim()));
        // SubmitCommand injects the bar's Gemini key only into this process. Removing it must
        // also stop future CLI calls using that cached copy, while preserving user/system keys.
        if (string.IsNullOrWhiteSpace(key) && previousGemini is not null
            && Environment.GetEnvironmentVariable("GEMINI_API_KEY") == previousGemini)
            Environment.SetEnvironmentVariable("GEMINI_API_KEY", null, EnvironmentVariableTarget.Process);
    }
}
