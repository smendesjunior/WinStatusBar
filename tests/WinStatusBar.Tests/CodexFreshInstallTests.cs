using System.Text.Json;
using WinStatusBar.Core.Ai.Codex;
using Xunit;

namespace WinStatusBar.Tests;

/// <summary>
/// Ligação automática das IAs no Codex só onde a barra já estava instalada (atualização). Numa instalação nova, o
/// instalador grava a ligação como desligada: a pessoa liga em Configurações › Modelos (decisão de 09/11/2026).
/// </summary>
public class CodexFreshInstallTests
{
    private static string TempState() => Path.Combine(Path.GetTempPath(), "WinStatusBar.Tests", Guid.NewGuid().ToString("N"), "integracao.json");

    [Fact]
    public void FreshInstallRecordsTheLinkAsOff()
    {
        string file = TempState();
        try
        {
            Assert.True(CodexIntegration.PrepareForInstall(file, hadInstallBefore: false));
            var state = JsonSerializer.Deserialize<CodexIntegration.State>(File.ReadAllText(file))!;
            Assert.False(state.Ligado);
        }
        finally { Directory.Delete(Path.GetDirectoryName(file)!, true); }
    }

    [Fact]
    public void UpdateLeavesTheAutomaticLinkAvailable()
    {
        string file = TempState();
        Assert.False(CodexIntegration.PrepareForInstall(file, hadInstallBefore: true));
        Assert.False(File.Exists(file));
    }

    [Fact]
    public void FreshInstallKeepsAnExistingChoice()
    {
        string file = TempState();
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, """{"Ligado":true,"BasePath":"/v1"}""");
        try
        {
            Assert.False(CodexIntegration.PrepareForInstall(file, hadInstallBefore: false));
            Assert.Contains("\"Ligado\":true", File.ReadAllText(file));
        }
        finally { Directory.Delete(Path.GetDirectoryName(file)!, true); }
    }
}
