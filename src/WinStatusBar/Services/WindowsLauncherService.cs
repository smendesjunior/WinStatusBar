using System.Diagnostics;

namespace WinStatusBar.Services;

/// <summary>Abre aplicativos nativos do Windows pelo Shell (UseShellExecute = true).</summary>
public static class WindowsLauncherService
{
    public const string SettingsAumid = "windows.immersivecontrolpanel_cw5n1h2txyewy!microsoft.windows.immersivecontrolpanel";
    public const string StoreAumid = "Microsoft.WindowsStore_8wekyb3d8bbwe!App";
    public static string TaskManagerPath => Path.Combine(Environment.SystemDirectory, "Taskmgr.exe");

    public static void TaskManager() => Shell(TaskManagerPath);
    public static void Settings() => Shell("ms-settings:");
    public static void Store() => Shell("ms-windows-store:");
    public static void AgentApp(bool codex)
    {
        string family = codex ? "OpenAI.Codex_2p2nqsd0c76g0" : "AnthropicPBC.Claude_fnn82j28hfe8t";
        if (IconService.PackagePath(family) is not null)
            Shell("explorer.exe", @"shell:AppsFolder\" + family + (codex ? "!App" : "!Claude"));
        else Shell(codex ? "codex://" : "claude://");
    }

    /// <summary>Abre uma conversa no app do Codex (codex://threads/&lt;id&gt;). Id fora do formato é ignorado.</summary>
    public static void CodexThread(string threadId)
    {
        if (!System.Text.RegularExpressions.Regex.IsMatch(threadId ?? "", "^[0-9A-Za-z-]{8,64}$"))
        {
            App.Log("Abrir conversa do Codex: id inválido ignorado");
            return;
        }
        Shell("codex://threads/" + threadId);
    }

    public static void Shell(string target, string? args = null)
    {
        try
        {
            var psi = new ProcessStartInfo(target) { UseShellExecute = true };
            if (args is not null) psi.Arguments = args;
            Process.Start(psi);
        }
        catch (Exception ex) { App.Log($"Falha ao abrir {target}: {ex.Message}"); }
    }

    public static void OpenFolder(string path)
    {
        if (Directory.Exists(path)) Shell("explorer.exe", $"\"{path}\"");
    }
}
