using System.Text.RegularExpressions;
using WinStatusBar.Core.Ai.Codex;
using Xunit;

namespace WinStatusBar.Tests;

/// <summary>O script da instalação de teste encerra o encaminhador do Codex (que roda sem a barra) antes de copiar.</summary>
public class UpdateInstalledScriptTests
{
    private static string Script() => RepoSource.Read("tools", "update-installed.ps1");

    [Fact]
    public void StopsTheCodexGatewayBeforeCopying()
    {
        string script = Script();
        int quit = script.IndexOf(CodexIntegration.QuitEventName, StringComparison.Ordinal);
        int copy = script.IndexOf("Copy-Item", StringComparison.Ordinal);
        Assert.True(quit >= 0 && quit < copy, $"evento={quit} cópia={copy}");
    }

    [Fact]
    public void ForceStopsOnlyTheInstalledGateway() =>
        Assert.Matches(new Regex(@"Get-Process WinStatusBar\.CodexIAs[^\r\n]*\$dst"), Script());
}
