using System.Text.Json;
using System.Text.Json.Nodes;
using WinStatusBar.Core.Ai;
using WinStatusBar.Core.Ai.Codex;
using Xunit;

namespace WinStatusBar.Tests;

public class CodexStopReasonTests
{
    internal const string Thread = "01aa0000-0000-7000-8000-000000000001";
    internal const string QuotaMessage = "Your workspace is out of credits. Add credits to continue.";

    internal static string Line(string ts, string type, JsonObject payload) =>
        new JsonObject { ["timestamp"] = ts, ["type"] = type, ["payload"] = payload }.ToJsonString();

    internal static string Started(string turn, string trigger, string ts = "2026-11-10T03:00:00.000Z") => Line(ts, "event_msg", new JsonObject
    {
        ["type"] = "task_started",
        ["turn_attribution"] = new JsonObject { ["turn_id"] = turn, ["turn_trigger"] = trigger, ["parent_turn_id"] = null, ["root_turn_id"] = turn },
        ["turn_id"] = turn, ["root_turn_id"] = turn, ["started_at"] = 1794292800, ["model_context_window"] = 475000, ["collaboration_mode_kind"] = "default",
    });

    internal static string StartedExec(string turn = "t2") => Started(turn, "exec", "2026-11-10T03:40:00.000Z");

    internal static string Reached => Line("2026-11-10T03:31:00.371Z", "event_msg", new JsonObject
    {
        ["type"] = "token_count", ["info"] = null, ["rate_limits"] = JsonNode.Parse(CodexQuotaBlockTests.RolloutBlock),
    });

    internal static string End(string turn, string? errorJson, bool omitError = false, string ts = "2026-11-10T03:31:01.000Z")
    {
        var p = new JsonObject { ["type"] = "task_complete", ["root_turn_id"] = turn, ["turn_id"] = turn, ["last_agent_message"] = null };
        if (!omitError) p["error"] = errorJson is null ? null : JsonNode.Parse(errorJson);
        p["started_at"] = 1794292800; p["completed_at"] = 1794294661; p["duration_ms"] = 1861000;
        return Line(ts, "event_msg", p);
    }

    internal static string QuotaEnd(string turn = "t1") =>
        End(turn, $$"""{"message":"{{QuotaMessage}}","codex_error_info":"usage_limit_exceeded"}""");

    internal static string TokenRecord(string turn, string thread = Thread) => Line("2026-11-10T03:30:50.000Z", "token_usage_record", new JsonObject
    {
        ["thread_id"] = thread, ["turn_id"] = turn, ["session_id"] = thread, ["root_turn_id"] = turn, ["response_id"] = "resp_x",
        ["usage"] = new JsonObject { ["input_tokens"] = 37000, ["cached_input_tokens"] = 36000, ["cache_write_input_tokens"] = 0, ["output_tokens"] = 40, ["reasoning_output_tokens"] = 0, ["total_tokens"] = 37040 },
        ["turn_token_usage"] = new JsonObject { ["input_tokens"] = 59825, ["cached_input_tokens"] = 34944, ["cache_write_input_tokens"] = 0, ["output_tokens"] = 415, ["reasoning_output_tokens"] = 0, ["total_tokens"] = 60240 },
        ["thread_token_usage"] = new JsonObject { ["input_tokens"] = 99999, ["cached_input_tokens"] = 1, ["cache_write_input_tokens"] = 0, ["output_tokens"] = 1, ["reasoning_output_tokens"] = 1, ["total_tokens"] = 100000 },
    });

    internal static AgentSession NewSession() => new() { Id = Thread, Kind = AgentKind.Codex };

    private static AgentSession Apply(params string[] lines)
    {
        var s = NewSession();
        foreach (var l in lines) CodexParsers.ApplyRolloutLine(s, l);
        return s;
    }

    [Fact]
    public void QuotaStopBecomesInterruptedWithItsReason()
    {
        var s = Apply(Started("t1", "resume_interrupted_task"), Reached, QuotaEnd());
        Assert.Equal(AgentState.Interrupted, s.State);
        Assert.True(s.ExecutionEnded);
        Assert.Equal(CodexParsers.UsageLimitExceeded, s.StopReason);
        Assert.Equal("workspace_owner_credits_depleted", s.LastReachedType);
        Assert.Equal("Parada pela cota do Codex · créditos do workspace esgotados", s.Caption);
        Assert.Equal("resume_interrupted_task", s.TurnTrigger);
    }

    [Fact]
    public void RolloutQuotaCarriesTheEventTime()
    {
        var s = Apply(Started("t1", "user"));
        var q = CodexParsers.ApplyRolloutLine(s, Reached)!;
        var at = DateTimeOffset.Parse("2026-11-10T03:31:00.371Z");
        Assert.Equal(at, q.ObservedAt);
        Assert.Equal(at, q.Access!.ObservedAt);
    }

    [Fact]
    public void OtherErrorBecomesErrorWithTheExplainerLabel()
    {
        const string msg = "stream disconnected before completion: Internal server error";
        var s = Apply(Started("t1", "user"), End("t1", $$"""{"message":"{{msg}}","codex_error_info":"internal_server_error"}"""));
        Assert.Equal(AgentState.Error, s.State);
        Assert.True(s.ExecutionEnded);
        Assert.Equal("internal_server_error", s.StopReason);
        Assert.Equal("Falha na execução · " + AiErrorExplainer.Explain(AgentPrompt.Target.Codex, msg).Short, s.Caption);

        var http = Apply(Started("t1", "user"), End("t1", """{"message":"bad gateway","codex_error_info":{"http_connection_failed":{"http_status_code":502}}}"""));
        Assert.Equal(AgentState.Error, http.State);
        Assert.Equal("http_connection_failed", http.StopReason);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void EndWithoutErrorStaysCompleted(bool omitError)
    {
        var s = Apply(Started("t1", "user"), End("t1", null, omitError));
        Assert.Equal((AgentState.Completed, "Resposta encerrada", (string?)null), (s.State, s.Caption, s.StopReason));
    }

    [Fact]
    public void TurnAbortedStaysAsToday()
    {
        var aborted = Line("2026-11-10T03:05:00.000Z", "event_msg", new JsonObject { ["type"] = "turn_aborted", ["turn_id"] = "t1", ["reason"] = "interrupted" });
        var s = Apply(Started("t1", "user"), aborted);
        Assert.Equal((AgentState.Interrupted, "Execução interrompida", (string?)null), (s.State, s.Caption, s.StopReason));
    }

    [Fact]
    public void TaskStartedClearsTheStopAndKeepsTheTrigger()
    {
        var s = Apply(Started("t1", "user"), Reached, QuotaEnd(), TokenRecord("t1"));
        Assert.NotNull(s.TurnTokens);
        CodexParsers.ApplyRolloutLine(s, StartedExec());
        Assert.Equal(AgentState.Running, s.State);
        Assert.Equal("exec", s.TurnTrigger);
        Assert.Null(s.StopReason);
        Assert.Null(s.LastReachedType);
        Assert.Null(s.TurnTokens);
    }

    [Fact]
    public void TokenUsageRecordFillsTheCurrentTurnTokens()
    {
        var s = Apply(Started("t1", "user"));
        CodexParsers.ApplyRolloutLine(s, TokenRecord("t0"));
        Assert.Null(s.TurnTokens);
        CodexParsers.ApplyRolloutLine(s, TokenRecord("t1", thread: "01aa0000-0000-7000-8000-000000000009"));
        Assert.Null(s.TurnTokens);
        CodexParsers.ApplyRolloutLine(s, TokenRecord("t1"));
        Assert.Equal(new TokenUsage(59825, 34944, 415, 0), s.TurnTokens);
    }
}
