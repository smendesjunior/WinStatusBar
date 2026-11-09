using Microsoft.Win32;

namespace WinStatusBar.Core.Settings;

/// <summary>
/// Pasta do OneDrive pessoal (nunca o da empresa). Lê a variável de ambiente e, se ela ainda não existir no processo
/// (OneDrive configurado depois de a barra abrir, comum logo após formatar), o registro do usuário.
/// </summary>
public static class OneDrivePaths
{
    public static string? Personal
    {
        get
        {
            foreach (var candidate in Candidates())
                if (!string.IsNullOrWhiteSpace(candidate) && Directory.Exists(candidate)) return candidate;
            return null;
        }
    }

    private static IEnumerable<string?> Candidates()
    {
        yield return Environment.GetEnvironmentVariable("OneDriveConsumer");
        string? fromEnvKey = null, fromAccount = null;
        try { fromEnvKey = Registry.CurrentUser.OpenSubKey("Environment")?.GetValue("OneDriveConsumer") as string; } catch { }
        yield return fromEnvKey;
        try { fromAccount = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\OneDrive\Accounts\Personal")?.GetValue("UserFolder") as string; } catch { }
        yield return fromAccount;
    }
}
