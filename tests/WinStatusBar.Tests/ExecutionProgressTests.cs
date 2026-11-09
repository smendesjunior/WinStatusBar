using System.Text.Json;
using WinStatusBar.Core.Ai;
using WinStatusBar.Core.Ai.Codex;
using Xunit;

namespace WinStatusBar.Tests;

public class ExecutionProgressTests
{
    private static AgentSession Running() => new() { Id = "s", Kind = AgentKind.Codex, State = AgentState.Running };

    [Fact] public void FinishedResponseDoesNotClaimGoalCompletionOrKeepPhaseEstimate()
    {
        var s = Running();
        ExecutionProgress.Observe(s, "finalizing");
        CodexParsers.ApplyRolloutLine(s, """{"timestamp":"2026-11-06T12:00:00Z","type":"event_msg","payload":{"type":"task_complete"}}""");
        Assert.Equal("Resposta encerrada", s.Caption);
        Assert.Equal("Resposta encerrada", s.State.ToPt());
        Assert.Null(s.ProgressPercent);
        Assert.False(s.ProgressIsPhaseEstimate);
        Assert.DoesNotContain("Executando", s.DisplayTitle);
    }

    [Fact] public void FinishedResponsePreservesPendingPlanWithoutClaimingSuccess()
    {
        var s = Running(); s.PlanSteps = "cp"; s.ProgressPercent = 50;
        CodexParsers.ApplyRolloutLine(s, """{"timestamp":"2026-11-06T12:00:00Z","type":"event_msg","payload":{"type":"task_complete"}}""");
        Assert.Equal("Resposta encerrada · plano pendente", s.Caption);
        Assert.Equal(50, s.ProgressPercent);
    }

    [Fact] public void RepeatedToolsDoNotInventProgress()
    {
        var s = Running();
        for (int i = 0; i < 1000; i++) ExecutionProgress.Tool(s, "exec");
        Assert.Equal(35, s.ProgressPercent);
        Assert.True(s.ProgressIsPhaseEstimate);
    }

    [Fact] public void ExplicitPlanAlwaysWins()
    {
        var s = Running(); s.PlanSteps = "ci"; s.ProgressPercent = 50;
        ExecutionProgress.Observe(s, "finalizing");
        Assert.Equal(50, s.ProgressPercent);
        Assert.False(s.ProgressIsPhaseEstimate);
    }

    [Fact] public void NewWorkAfterCheckingRecalculatesEstimate()
    {
        var s = Running();
        ExecutionProgress.Observe(s, "checking");
        Assert.Equal(80, s.ProgressPercent);
        ExecutionProgress.Observe(s, "editing");
        Assert.Equal(50, s.ProgressPercent);
        Assert.True(s.ScopeChanged);
    }

    [Fact] public void EndErrorAndCancelNeverBecomeHundredPercent()
    {
        foreach (var state in new[] { AgentState.Completed, AgentState.Error, AgentState.Interrupted })
        {
            var s = Running(); ExecutionProgress.Observe(s, "finalizing");
            s.State = state; s.ExecutionEnded = true;
            ExecutionProgress.Tool(s, "exec");
            Assert.Equal(95, s.ProgressPercent);
        }
    }

    [Theory]
    [InlineData(0, 80)]
    [InlineData(1, 50)]
    public void CodexStructuredTestResultsDriveStage(int exitCode, int expected)
    {
        var s = Running();
        ExecutionProgress.Observe(s, "checking");
        var line = JsonSerializer.Serialize(new { timestamp = "2026-11-06T12:00:00Z", type = "event_msg", payload = new { type = "item_completed", item = new { type = "CommandExecution", command = new[] { "dotnet", "test" }, exit_code = exitCode } } });
        CodexParsers.ApplyRolloutLine(s, line);
        Assert.Equal(expected, s.ProgressPercent);
    }

    [Fact] public void CodexWithoutPlanShowsEstimateAndNextTurnResetsIt()
    {
        var s = Running();
        CodexParsers.ApplyRolloutLine(s, """{"timestamp":"2026-11-06T12:00:00Z","type":"response_item","payload":{"type":"custom_tool_call","name":"exec","call_id":"c","input":"code"}}""");
        Assert.Equal(35, s.ProgressPercent);
        CodexParsers.ApplyRolloutLine(s, """{"timestamp":"2026-11-06T12:00:01Z","type":"event_msg","payload":{"type":"task_started","turn_id":"new"}}""");
        Assert.Null(s.ProgressPercent);
        Assert.False(s.ProgressIsPhaseEstimate);
    }
}
