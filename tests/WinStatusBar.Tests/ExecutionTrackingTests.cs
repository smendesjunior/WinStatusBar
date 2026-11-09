using System.Text.Json;
using WinStatusBar.Core.Ai;
using WinStatusBar.Core.Ai.Codex;
using Xunit;
namespace WinStatusBar.Tests;

public class ExecutionTrackingTests
{
    private static string Event(string type, string turn, int second) => JsonSerializer.Serialize(new { timestamp = $"2026-11-06T12:00:{second:00}Z", type = "event_msg", payload = new { type, turn_id = turn } });
    [Fact] public void PreviousTurnCannotEndNewTurn()
    {
        var s = new AgentSession { Id = "s", Kind = AgentKind.Codex };
        CodexParsers.ApplyRolloutLine(s, Event("task_started", "old", 0));
        CodexParsers.ApplyRolloutLine(s, Event("task_started", "new", 2));
        CodexParsers.ApplyRolloutLine(s, Event("task_complete", "old", 3));
        Assert.Equal(AgentState.Running, s.State);
        CodexParsers.ApplyRolloutLine(s, Event("turn_aborted", "new", 4));
        Assert.Equal(AgentState.Interrupted, s.State);
        Assert.Null(s.ProgressPercent);
    }
    [Fact] public void LargeReplayKeepsStartAndRetriesIncompleteCrLfRecord()
    {
        string path = Path.Combine(Path.GetTempPath(), "rollout-2026-11-06T12-00-00-" + Guid.NewGuid() + ".jsonl");
        try
        {
            File.WriteAllText(path, Event("task_started", "t", 0) + "\r\n" + JsonSerializer.Serialize(new { type = "ignored", text = new string('x', 300000) }) + "\r\n");
            using var p = new CodexProvider();
            Assert.True(p.ReadFile(path, true));
            Assert.Equal(AgentState.Running, Assert.Single(p.Sessions).State);
            string end = Event("task_complete", "t", 4);
            File.AppendAllText(path, end[..20]);
            p.ReadFile(path, false);
            Assert.Equal(AgentState.Running, Assert.Single(p.Sessions).State);
            File.AppendAllText(path, end[20..] + "\r\n");
            p.ReadFile(path, false);
            Assert.Equal(AgentState.Completed, Assert.Single(p.Sessions).State);
            Assert.False(p.ReadFile(path, false));
        }
        finally { File.Delete(path); }
    }
    [Fact] public void PlanWeightsUseCompletedWorkOnly()
    {
        Assert.Equal(25, CodexParsers.PlanProgress("""{"plan":[{"status":"completed","weight":1},{"status":"in_progress","weight":3}]}"""));
        Assert.Null(CodexParsers.PlanProgress("""{"plan":[]}"""));
    }
    [Fact] public void PlanWaitsForMatchingToolResultAndCanGoBackwards()
    {
        var s = new AgentSession { Kind = AgentKind.Codex, Id = "s" };
        CodexParsers.ApplyRolloutLine(s, Event("task_started", "t", 0));
        string call = JsonSerializer.Serialize(new { timestamp = "2026-11-06T12:00:01Z", type = "response_item", payload = new { type = "function_call", name = "functions.update_plan", call_id = "p", arguments = "{\"plan\":[{\"status\":\"completed\"},{\"status\":\"pending\"}]}" } });
        CodexParsers.ApplyRolloutLine(s, call);
        Assert.Null(s.ProgressPercent);
        CodexParsers.ApplyRolloutLine(s, """{"timestamp":"2026-11-06T12:00:02Z","type":"response_item","payload":{"type":"function_call_output","call_id":"p","output":"Plan updated"}}""");
        Assert.Equal(50, s.ProgressPercent);
        CodexParsers.ApplyRolloutLine(s, call.Replace("12:00:01", "12:00:03").Replace("completed", "pending"));
        CodexParsers.ApplyRolloutLine(s, """{"timestamp":"2026-11-06T12:00:04Z","type":"response_item","payload":{"type":"function_call_output","call_id":"p","output":"Plan updated"}}""");
        Assert.Equal(0, s.ProgressPercent);
        Assert.True(s.ScopeChanged);
    }
    [Fact] public void SilenceDoesNotCompleteOrStopRunningTask()
    {
        string path = Path.Combine(Path.GetTempPath(), "rollout-2026-11-06T12-00-00-" + Guid.NewGuid() + ".jsonl");
        try
        {
            File.WriteAllText(path, Event("task_started", "t", 0) + "\n");
            using var p = new CodexProvider();
            p.ReadFile(path, true);
            p.Expire(new DateTimeOffset(2026, 11, 7, 12, 0, 0, TimeSpan.Zero));
            Assert.Equal(AgentState.Running, Assert.Single(p.Sessions).State);
            Assert.True(Assert.Single(p.Sessions).ObservationStale);
        }
        finally { File.Delete(path); }
    }
    [Fact] public void TurnOpenedByAnEarlierBarIsInterrupted()
    {
        string path = Path.Combine(Path.GetTempPath(), "rollout-2026-11-06T12-00-00-" + Guid.NewGuid() + ".jsonl");
        try
        {
            string meta = JsonSerializer.Serialize(new { timestamp = "2026-11-06T12:00:00Z", type = "session_meta", payload = new { id = "s", originator = "winstatusbar", cwd = "C:\\" } });
            File.WriteAllText(path, meta + "\n" + Event("task_started", "t", 1) + "\n");
            using var p = new CodexProvider();
            p.ReadFile(path, true);
            Assert.Equal("winstatusbar", Assert.Single(p.Sessions).Originator);
            p.Expire(new DateTimeOffset(2026, 11, 6, 12, 5, 0, TimeSpan.Zero));
            Assert.Equal(AgentState.Interrupted, Assert.Single(p.Sessions).State);
        }
        finally { File.Delete(path); }
    }
    private static string ExecRollout()
    {
        string path = Path.Combine(Path.GetTempPath(), "rollout-2026-11-06T12-00-00-" + Guid.NewGuid() + ".jsonl");
        string meta = JsonSerializer.Serialize(new { timestamp = "2026-11-06T12:00:00Z", type = "session_meta", payload = new { id = "s", originator = "codex_exec", cwd = "C:\\" } });
        File.WriteAllText(path, meta + "\n" + Event("task_started", "t", 1) + "\n");
        return path;
    }
    [Fact] public void CodexExecTurnWhoseProcessEndedIsInterrupted()
    {
        string path = ExecRollout();
        try
        {
            using var p = new CodexProvider { ExecProcessStarts = () => [] };
            p.ReadFile(path, true);
            p.Expire(new DateTimeOffset(2026, 11, 6, 12, 1, 0, TimeSpan.Zero));
            Assert.Equal(AgentState.Running, Assert.Single(p.Sessions).State); // menos de 2 min de silêncio
            p.Expire(new DateTimeOffset(2026, 11, 6, 16, 0, 0, TimeSpan.Zero));
            Assert.Equal(AgentState.Interrupted, Assert.Single(p.Sessions).State);
        }
        finally { File.Delete(path); }
    }
    [Fact] public void CodexExecTurnStaysRunningWhileItsProcessLivesOrIsUnknown()
    {
        string path = ExecRollout();
        try
        {
            var owner = new DateTimeOffset(2026, 11, 6, 11, 59, 59, TimeSpan.Zero);
            using var p = new CodexProvider { ExecProcessStarts = () => [owner] };
            p.ReadFile(path, true);
            p.Expire(new DateTimeOffset(2026, 11, 6, 16, 0, 0, TimeSpan.Zero));
            Assert.Equal(AgentState.Running, Assert.Single(p.Sessions).State);

            using var unknown = new CodexProvider { ExecProcessStarts = () => null };
            unknown.ReadFile(path, true);
            unknown.Expire(new DateTimeOffset(2026, 11, 6, 16, 0, 0, TimeSpan.Zero));
            Assert.Equal(AgentState.Running, Assert.Single(unknown.Sessions).State);

            // Um `codex exec` iniciado horas depois não é o dono do turno antigo.
            using var later = new CodexProvider { ExecProcessStarts = () => [new DateTimeOffset(2026, 11, 6, 15, 0, 0, TimeSpan.Zero)] };
            later.ReadFile(path, true);
            later.Expire(new DateTimeOffset(2026, 11, 6, 16, 0, 0, TimeSpan.Zero));
            Assert.Equal(AgentState.Interrupted, Assert.Single(later.Sessions).State);
        }
        finally { File.Delete(path); }
    }
    [Theory]
    [InlineData(@"C:\x\codex.exe exec --json ""Responda apenas: ok""", true)]
    [InlineData(@"node C:\npm\codex.js exec hi", true)]
    [InlineData(@"C:\x\codex.exe -c a=1 exec-server --remote https://x", false)]
    [InlineData(@"C:\x\codex.exe app-server --analytics-default-enabled", false)]
    [InlineData(@"node C:\other\tool.js exec", false)]
    public void RecognizesCodexExecCommandLines(string cmd, bool expected) => Assert.Equal(expected, CodexExecProcesses.IsExecCommandLine(cmd));
    [Fact] public void ApiLateCallbacksCannotFinishAnotherRequest()
    {
        var p = new ApiActivityProvider();
        string first = p.Begin("chat", "gemini", "Gemini", "model");
        string next = p.Begin("chat", "grok", "Grok", "model");
        p.End("chat", first, false, null);
        Assert.Equal(AgentState.WaitingResponse, Assert.Single(p.Sessions).State);
        p.Receiving("chat", next);
        Assert.Equal(AgentState.Running, Assert.Single(p.Sessions).State);
        p.End("chat", next, true, null);
        p.Receiving("chat", next);
        Assert.Equal(AgentState.Interrupted, Assert.Single(p.Sessions).State);
        Assert.Null(Assert.Single(p.Sessions).ProgressPercent);
    }
    [Fact] public void ApiConversationsRemainIndependent()
    {
        var p = new ApiActivityProvider();
        string a = p.Begin("a", "gemini", "Gemini", null);
        p.Begin("b", "deepseek", "DeepSeek", null);
        p.End("a", a, false, "error");
        Assert.Equal(AgentState.Error, p.Sessions.Single(s => s.Id == "api:a").State);
        Assert.Equal(AgentState.WaitingResponse, p.Sessions.Single(s => s.Id == "api:b").State);
    }
}
