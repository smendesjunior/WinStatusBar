using WinStatusBar.Core.Ai;
using Xunit;

namespace WinStatusBar.Tests;

/// <summary>Vez do Qira compartilhada com o Codex IAs (Aplicativos IA\Codex IAs).</summary>
public class QiraSharedTurnTests
{
    // Nome próprio de teste: nunca disputa a vez real com o Codex IAs em uso.
    private static readonly string TestLock = @"Local\WinStatusBar.Teste." + Guid.NewGuid().ToString("N");

    [Fact]
    public void Nome_da_vez_e_o_mesmo_do_Codex_IAs()
        => Assert.Equal(@"Local\AplicativosIA.Qira.Vez", QiraSharedTurn.LockName);

    [Fact]
    public async Task Um_por_vez_e_a_vez_volta_ao_soltar()
    {
        var first = await QiraSharedTurn.AcquireAsync(TestLock, TimeSpan.FromSeconds(2), CancellationToken.None);
        Assert.NotNull(first);
        Assert.Null(await QiraSharedTurn.AcquireAsync(TestLock, TimeSpan.Zero, CancellationToken.None));
        first!.Dispose();
        var again = await QiraSharedTurn.AcquireAsync(TestLock, TimeSpan.FromSeconds(2), CancellationToken.None);
        Assert.NotNull(again);
        again!.Dispose();
    }

    [Fact]
    public async Task Cancelar_a_espera_nao_trava()
    {
        var holder = await QiraSharedTurn.AcquireAsync(TestLock, TimeSpan.FromSeconds(2), CancellationToken.None);
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        Assert.Null(await QiraSharedTurn.AcquireAsync(TestLock, TimeSpan.FromSeconds(30), cts.Token));
        holder!.Dispose();
    }

    // ---- partes do Qira: uma lista só (barra, IAs no Codex e executor) e o reinício completo só mexe no que é do Qira
    [Fact]
    public void Lista_de_partes_do_Qira_e_a_mesma_de_antes()
        => Assert.Equal(new[]
        {
            "QuantumAI.Hub", "nextmove-bridge", "ProcessHub", "LenovoQiraCore", "mcpMgmtService", "Lenovo Qira.Launcher", "Ludp.Listener",
            "Notification.Listener", "lenovo.pfm.pipe", "LocalAgent", "CreatorIMG.MCPServer", "CatchMeUpMcpServer", "CzCloudMcpServer", "file-search-service",
        }, QiraSharedTurn.ProcessNames.ToArray());

    [Fact]
    public void So_e_parte_do_Qira_o_que_roda_da_pasta_do_Qira()
    {
        const string root = @"C:\Program Files\Lenovo\Lenovo Qira";
        Assert.True(QiraSharedTurn.IsQiraImage(root + @"\QTServer\ProcessHub.exe", root));
        Assert.True(QiraSharedTurn.IsQiraImage(@"c:\program files\lenovo\lenovo qira\Lenovo.McpMgmtService\mcp\LocalAgentMCP\LocalAgent.exe", root));
        Assert.True(QiraSharedTurn.IsQiraImage(root + @"\Lenovo Qira.Launcher.exe", root + @"\"));
        // Programas de outros fabricantes com o mesmo nome de processo ficam de fora.
        Assert.False(QiraSharedTurn.IsQiraImage(@"C:\Ferramentas\Backup\LocalAgent.exe", root));
        Assert.False(QiraSharedTurn.IsQiraImage(@"C:\Program Files\Lenovo\Lenovo Qira Outro\ProcessHub.exe", root));
        Assert.False(QiraSharedTurn.IsQiraImage(null, root));
        Assert.False(QiraSharedTurn.IsQiraImage("", root));
        Assert.EndsWith(@"\Lenovo\Lenovo Qira", QiraSharedTurn.InstallRoot);
        using var me = System.Diagnostics.Process.GetCurrentProcess();
        Assert.False(QiraSharedTurn.IsQiraProcess(me));
    }

    [Theory]
    [InlineData("src", "WinStatusBar", "Services", "QiraHubSession.cs")]
    [InlineData("src", "WinStatusBar.CodexIAs", "Providers", "QiraProvider.cs")]
    public void Reinicio_completo_usa_a_lista_comum_e_confere_a_pasta(params string[] file)
    {
        string source = RepoSource.Read(file);
        int restart = source.IndexOf("private static async Task RestartQiraAsync(", StringComparison.Ordinal);
        Assert.True(restart > 0);
        string body = source[restart..source.IndexOf("Process.Start(", restart, StringComparison.Ordinal)];
        Assert.Contains("QiraSharedTurn.ProcessNames", body);
        Assert.Contains("QiraSharedTurn.IsQiraProcess(p)", body);
        Assert.DoesNotContain("string[] QiraProcesses", source); // sem cópia própria da lista
    }

    [Fact]
    public void Ativar_a_ponte_so_reinicia_o_Qira_com_a_vez_na_mao()
    {
        string source = RepoSource.Read("src", "WinStatusBar", "Services", "QiraHubSession.cs");
        int start = source.IndexOf("public static async Task<string> EnableBridgeAsync()", StringComparison.Ordinal);
        Assert.True(start > 0);
        string body = source[start..source.IndexOf("private sealed record State", start, StringComparison.Ordinal)];
        int turn = body.IndexOf("QiraSharedTurn.AcquireAsync", StringComparison.Ordinal);
        int restart = body.IndexOf("RestartQiraAsync", StringComparison.Ordinal);
        Assert.True(turn > 0 && turn < restart);
        Assert.Contains("turn?.Dispose()", body);
    }
}
