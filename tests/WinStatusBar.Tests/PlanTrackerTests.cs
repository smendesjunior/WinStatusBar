using System.Text.Json;
using WinStatusBar.Core.Ai;
using WinStatusBar.Core.Ai.Claude;
using WinStatusBar.Core.Ipc;
using Xunit;

namespace WinStatusBar.Tests;

public class PlanTrackerTests
{
    private static string? Op(string json) => PlanTracker.OperationOf(JsonDocument.Parse(json).RootElement);

    [Fact]
    public void FullListBecomesSteps()
    {
        Assert.Equal("set:cip", Op("""{"items":[{"content":"a","status":"completed"},{"content":"b","status":"in_progress"},{"content":"c","status":"pending"}]}"""));
        Assert.Equal("set:cp", Op("""{"todos":[{"content":"a","status":"completed"},{"content":"b","status":"pending"}]}"""));
    }

    [Fact]
    public void PartialUpdatesAreRead()
    {
        Assert.Equal("advance", Op("""{"advance":true}"""));
        Assert.Equal("done:1,2;start:4", Op("""{"done":[1,2],"start":4}"""));
        Assert.Null(Op("""{"recap":"x"}"""));
    }

    [Fact]
    public void AdvanceCompletesCurrentAndStartsNext()
    {
        string? s = PlanTracker.Apply(null, "set:ipp");
        Assert.Equal(0, PlanTracker.Percent(s));
        s = PlanTracker.Apply(s, "advance");
        Assert.Equal("cip", s);
        Assert.Equal(33.3, PlanTracker.Percent(s));
        s = PlanTracker.Apply(s, "done:2,3");
        Assert.Equal(100, PlanTracker.Percent(s));
    }

    [Fact]
    public void StartMovesTheCurrentStep()
    {
        Assert.Equal("cpi", PlanTracker.Apply("cip", "start:3"));
    }

    [Fact]
    public void PartialUpdateWithoutKnownPlanKeepsNothing()
    {
        Assert.Null(PlanTracker.Apply(null, "advance"));
        Assert.Null(PlanTracker.Percent(null));
    }

    [Fact]
    public void AdvanceCountsOnlyOnceAcrossPreAndPostToolUse()
    {
        var s = new AgentSession { Kind = AgentKind.Claude, Id = "teste" };
        var now = DateTimeOffset.Now;
        ClaudeStateMachine.Apply(s, new EventMessage { Event = "UserPromptSubmit" }, now);
        ClaudeStateMachine.Apply(s, new EventMessage { Event = "PostToolUse", Plan = "set:ipp" }, now);
        ClaudeStateMachine.Apply(s, new EventMessage { Event = "PreToolUse", Plan = "advance" }, now);
        ClaudeStateMachine.Apply(s, new EventMessage { Event = "PostToolUse", Plan = "advance" }, now);
        Assert.Equal("cip", s.PlanSteps);
        Assert.Equal(33.3, s.ProgressPercent);
    }

    [Fact]
    public void EndOfResponsePreservesIncompletePlan()
    {
        var s = new AgentSession { Kind = AgentKind.Claude, Id = "teste" };
        var now = DateTimeOffset.Now;
        ClaudeStateMachine.Apply(s, new EventMessage { Event = "UserPromptSubmit" }, now);
        ClaudeStateMachine.Apply(s, new EventMessage { Event = "PostToolUse", Plan = "set:cip" }, now);
        ClaudeStateMachine.Apply(s, new EventMessage { Event = "Stop" }, now);
        Assert.Equal(33.3, s.ProgressPercent);

        var noPlan = new AgentSession { Kind = AgentKind.Claude, Id = "sem-plano" };
        ClaudeStateMachine.Apply(noPlan, new EventMessage { Event = "UserPromptSubmit" }, now);
        ClaudeStateMachine.Apply(noPlan, new EventMessage { Event = "Stop" }, now);
        Assert.Null(noPlan.ProgressPercent);
    }
}
