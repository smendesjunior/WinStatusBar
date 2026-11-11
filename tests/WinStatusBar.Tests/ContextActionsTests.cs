using WinStatusBar.Services;
using Xunit;
namespace WinStatusBar.Tests;
public class ContextActionsTests
{
    [Fact] public void ParsesProposalWithoutClaimingExecution()
    {
        var result = ContextActions.Parse("Vou substituir.\n```winstatusbar-actions\n[{\"Id\":1,\"Action\":\"fill\",\"Text\":\"Olá\",\"Find\":\"Hello\"}]\n```");
        Assert.Equal("Vou substituir.", result.Text);
        Assert.Equal("Hello", Assert.Single(result.Operations).Find);
    }
    [Fact] public void RejectsShellInstructions()
        => Assert.Throws<InvalidOperationException>(() => ContextActions.Parse("```winstatusbar-actions [{\"Id\":1,\"Action\":\"shell\"}]```"));
    [Fact] public void NormalAnswerHasNoActions() => Assert.Empty(ContextActions.Parse("Olá!").Operations);
    [Fact] public void ExpiredContextCannotAct()
    {
        var snapshot = new ContextActions.Snapshot(IntPtr.Zero, 0, "", DateTimeOffset.Now.AddMinutes(-11), []);
        Assert.Contains("expirou", ContextActions.Execute(snapshot, [], CancellationToken.None));
    }

    // ---- número de controle (posicional) mostrado num retrato antigo
    private static ContextActions.Snapshot Shot(params ContextActions.Control[] controls) => new(new IntPtr(42), 7, "Janela", DateTimeOffset.Now, [.. controls]);
    private static ContextActions.Control Button(int id, string runtime, string name) => new(id, runtime, name, "", ["click"]);

    [Fact] public void HeldControlIdFollowsTheSameControlAfterTheScreenChanged()
    {
        var shown = Shot(Button(1, "42.1", "Cancelar"), Button(2, "42.2", "Excluir"));
        // Um controle novo entrou na frente: os números trocaram de dono.
        var fresh = Shot(Button(1, "42.9", "Novo"), Button(2, "42.1", "Cancelar"), Button(3, "42.2", "Excluir"));
        Assert.Equal(2, ContextActions.Rebind(shown, fresh, 1)); // "Cancelar" continua sendo "Cancelar", nunca "Novo"
        Assert.Equal(3, ContextActions.Rebind(shown, fresh, 2));
        Assert.Equal(1, ContextActions.Rebind(shown, shown, 1)); // tela igual: mesmo número
    }

    [Fact] public void HeldControlIdIsRefusedWhenTheControlChangedOrWasNeverShown()
    {
        var shown = Shot(Button(1, "42.1", "Cancelar"), Button(2, "42.2", "Excluir"));
        Assert.Null(ContextActions.Rebind(shown, Shot(Button(1, "42.9", "Novo")), 1));                 // sumiu
        Assert.Null(ContextActions.Rebind(shown, Shot(Button(1, "42.1", "Excluir tudo")), 1));         // mesmo elemento, outro nome
        Assert.Null(ContextActions.Rebind(shown, shown, 9));                                           // número que não foi mostrado
        var otherWindow = new ContextActions.Snapshot(new IntPtr(43), 7, "Outra", DateTimeOffset.Now, shown.Controls);
        Assert.Null(ContextActions.Rebind(shown, otherWindow, 1));
        var otherProcess = shown with { Process = 8 };
        Assert.Null(ContextActions.Rebind(shown, otherProcess, 1));
        // Identidade repetida (provedor sem RuntimeId próprio): só vale o controle que ficou no mesmo número.
        var twins = Shot(Button(1, "", "OK"), Button(2, "", "OK"));
        Assert.Equal(2, ContextActions.Rebind(twins, twins, 2));
        Assert.Null(ContextActions.Rebind(twins, Shot(Button(3, "", "OK"), Button(4, "", "OK")), 2));
    }
}
