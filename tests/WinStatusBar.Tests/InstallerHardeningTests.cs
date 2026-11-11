using System.Text.RegularExpressions;
using Xunit;

namespace WinStatusBar.Tests;

/// <summary>
/// Ordem das etapas do instalador (o projeto de testes não referencia o WinStatusBar.Setup): uma instalação que falha
/// não pode deixar a barra fechada, abrir uma segunda barra nem desfazer a desinstalação pela metade.
/// </summary>
public class InstallerHardeningTests
{
    private static string Method(string source, string name)
    {
        var match = Regex.Match(source, @"\n    private static [\w<>\[\]\.]+ " + Regex.Escape(name) + @"\([^\n]*\)\s*\{(?<body>.*?)\n    \}", RegexOptions.Singleline);
        Assert.True(match.Success, name + " não encontrado");
        return match.Groups["body"].Value;
    }

    private static string Setup(string name) => Method(RepoSource.Read("src", "WinStatusBar.Setup", "Program.cs"), name);

    private static int At(string body, string text)
    {
        int at = body.IndexOf(text, StringComparison.Ordinal);
        Assert.True(at >= 0, "trecho não encontrado: " + text);
        return at;
    }

    [Fact]
    public void FailedReplaceReopensTheBarOnlyWhenItWasStopped()
    {
        // Pacote inválido: a barra nem foi fechada. Abrir outra faria a que está aberta mostrar as Configurações.
        string install = Setup("Install");
        int stopped = At(install, "stopped = true; StopRunningApp();");
        int reopen = At(install, "if (stopped && File.Exists(AppExe))");
        Assert.True(stopped < reopen);
        Assert.DoesNotContain("            if (File.Exists(AppExe))", install);
    }

    [Fact]
    public void FailureAfterTheFilesWereReplacedStillOpensTheBarOnQuietInstalls()
    {
        string install = Setup("Install");
        int replaced = At(install, "_filesReplaced = true;");
        int uninstaller = At(install, "\"Uninstall.exe\"), true);");
        int emergency = At(install, "if (launch && !started && File.Exists(AppExe))");
        Assert.True(replaced < uninstaller && uninstaller < emergency, "as etapas depois da troca dos arquivos ficam dentro do try");
        foreach (string step in new[] { "CreateShortcut(", "Registry.CurrentUser.CreateSubKey(UninstallKey)", "StartupService.Set(true, AppExe)" })
            Assert.True(At(install, step) < emergency, step + " dentro do try");
        // Na abertura de emergência, a ligação com o Codex já está gravada (instalação nova = desligada) e o erro segue (código 2).
        string tail = install[emergency..];
        int prepare = At(tail, "PrepareCodex();"), start = At(tail, "StartApp();"), rethrow = At(tail, "throw;");
        Assert.True(prepare < start && start < rethrow);
    }

    [Fact]
    public void BarOpensAfterTheClaudeCodeStepAndBeforeTheWebView2Check()
    {
        string install = Setup("Install");
        int codex = At(install, "            PrepareCodex();");
        int startup = At(install, "StartupService.Set(true, AppExe)");
        int claude = At(install, "ClaudeIntegrationInstaller.Install(CollectorExe)");
        int launch = At(install, "if (launch)\n            {\n                StartApp();".Replace("\n", install.Contains("\r\n") ? "\r\n" : "\n"));
        int webView = At(install, "EnsureWebView2();");
        Assert.True(codex < launch, "a ligação com o Codex é gravada antes de a barra abrir");
        Assert.True(startup < claude, "inicialização automática antes do Claude Code");
        Assert.True(claude < launch, "o Claude Code é ligado antes de a barra abrir (a barra também grava as configurações dele)");
        Assert.True(launch < webView, "a barra abre antes da conferência do WebView2, que pode levar minutos");
        // O assistente conta 6 etapas: nenhuma some nem aparece com a troca de ordem.
        Assert.Equal(6, Regex.Matches(install, @"step\?\.Invoke\(").Count);
    }

    [Fact]
    public void UninstallRemovesTheSensorPermissionBeforeChangingAnything()
    {
        // Se a pessoa recusa o UAC, a desinstalação para com a barra aberta e o Codex ainda ligado.
        string uninstall = Setup("Uninstall");
        int permission = At(uninstall, "SensorPermissionTask.IsEnabled()");
        int refused = At(uninstall, "A desinstalação foi interrompida.");
        Assert.True(refused < At(uninstall, "--desligar"));
        Assert.True(refused < At(uninstall, "StopRunningApp();"));
        Assert.True(refused < At(uninstall, "ClaudeIntegrationInstaller.Uninstall()"));
        Assert.True(permission < refused);
    }

    [Fact]
    public void StopOnlyWaitsForAndKillsTheInstalledBarOfThisSession()
    {
        string stop = Setup("StopRunningApp");
        Assert.DoesNotContain("GetProcessesByName", stop);
        Assert.Contains("InstalledAppRunning()", stop);
        Assert.Contains("foreach (var p in InstalledAppProcesses())", stop);
        Assert.Contains("p.Dispose();", stop);
        string filter = Setup("InstalledAppProcesses");
        Assert.Contains("p.SessionId == session", filter);
        Assert.Contains("p.MainModule?.FileName?.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) == true", filter);
        Assert.Contains("else p.Dispose();", filter);
    }

    [Fact]
    public void SetupExitCodesStayTheSame()
    {
        string main = Setup("Main");
        Assert.Contains("if (!ownsMutex) return 3;", main);
        Assert.Contains("code = 2;", main);
        Assert.Contains("code = 0;", main);
        Assert.Contains("return 1;", main);
    }

    [Fact]
    public void SchtasksOutputIsReadWithoutBlockingTheTimeout()
    {
        string schtasks = Method(RepoSource.Read("src", "WinStatusBar.Core", "Integration", "StartupService.cs"), "Schtasks");
        Assert.DoesNotContain("ReadToEnd()", schtasks);
        Assert.Equal(2, Regex.Matches(schtasks, @"\.ReadToEndAsync\(\)").Count);
        Assert.True(schtasks.LastIndexOf(".ReadToEndAsync()", StringComparison.Ordinal) < At(schtasks, "p.WaitForExit(15000)"));
        Assert.Contains("p.Kill(entireProcessTree: true)", schtasks);
        Assert.Contains("return exited ? p.ExitCode : -1;", schtasks);
    }
}
