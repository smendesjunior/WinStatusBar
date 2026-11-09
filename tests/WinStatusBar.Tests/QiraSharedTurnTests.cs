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
}
