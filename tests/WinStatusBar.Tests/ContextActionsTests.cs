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
}
