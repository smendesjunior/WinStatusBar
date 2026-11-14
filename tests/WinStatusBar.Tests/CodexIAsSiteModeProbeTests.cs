using CodexIAs.Providers;
using WinStatusBar.Core.Ai;
using Xunit;
using Mode = WinStatusBar.Core.Ai.WebModePicker.Mode;
using Outcome = WinStatusBar.Core.Ai.WebModePicker.Outcome;

namespace WinStatusBar.Tests;

/// <summary>
/// Modos pagos dos sites (caso real de 14/11/2026: Gemini "Código e análise" / 4 Argon aparecia no menu como os outros
/// e só ao escolher abria a oferta de assinatura). A verificação escolhe cada modo e tira os pagos.
/// </summary>
public class CodexIAsSiteModeProbeTests
{
    private static Mode M(string label, bool disabled = false) => new([label], "", disabled);

    [Fact]
    public async Task Modo_pago_sai_e_aparece_como_recusado()
    {
        var modes = new[] { M("Respostas rápidas"), M("Ajuda no dia a dia"), M("Código e análise") };
        var (result, refused) = await WebChatProvider.ProbeModesAsync(modes, (path, _) => Task.FromResult<(Outcome, string?, IReadOnlyList<string>?)>(
            path[0] == "Código e análise" ? (Outcome.Paid, "exige assinatura", null) : (Outcome.Ok, null, null)), default);

        Assert.Equal(new[] { false, false, true }, result.Select(m => m.Disabled));
        Assert.Equal(["Código e análise (exige assinatura do site)"], refused);
    }

    [Fact]
    public async Task Falha_passageira_nao_tira_o_modo()
    {
        var modes = new[] { M("Think Deeper"), M("Automático") };
        var (result, refused) = await WebChatProvider.ProbeModesAsync(modes, (path, _) => Task.FromResult<(Outcome, string?, IReadOnlyList<string>?)>(
            path[0] == "Think Deeper" ? (Outcome.NotConfirmed, "menu não reabriu", null) : (Outcome.Missing, "sumiu", null)), default);

        Assert.All(result, m => Assert.False(m.Disabled));
        Assert.Empty(refused);
    }

    [Fact]
    public async Task Modo_bloqueado_no_menu_nao_e_clicado()
    {
        int calls = 0;
        var (result, refused) = await WebChatProvider.ProbeModesAsync([M("Pro", disabled: true)], (_, _) =>
        {
            calls++;
            return Task.FromResult<(Outcome, string?, IReadOnlyList<string>?)>((Outcome.Ok, null, null));
        }, default);

        Assert.Equal(0, calls);
        Assert.True(result[0].Disabled);
        Assert.Equal(["Pro (bloqueado na sua conta)"], refused);
    }
}
