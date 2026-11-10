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

    // ---- CodexProvider adota, segura e libera (A7) ----

    private static readonly DateTimeOffset StoppedAt = DateTimeOffset.Parse(CodexQuotaStopsTests.StopAt);
    private const string StoppedCaptionText = "Parada pela cota do Codex · créditos do workspace esgotados";

    private static string StoppedRollout(string dir) => CodexQuotaStopsTests.WriteRollout(dir, CodexQuotaStopsTests.Id,
        CodexQuotaStopsTests.Meta(), CodexQuotaStopsTests.Started("t1"), CodexQuotaStopsTests.Context("t1"),
        CodexQuotaStopsTests.TokenCount(), CodexQuotaStopsTests.QuotaEnd());

    private static QuotaStop Stopped(string path = @"C:\nao\existe.jsonl", long offset = 0) => new(
        CodexQuotaStopsTests.Id, CodexQuotaStopsTests.Title, path, offset, "t1", StoppedAt, "gpt-5.6-sol", "low", false,
        "workspace-write", false, [], "on-request", CodexQuotaStopsTests.Cwd, [], "gpt-5.6-sol", "medium", 79123, "workspace_owner_credits_depleted");

    private static QuotaSnapshot Blocked(DateTimeOffset at) =>
        CodexQuotaBlockTests.Snapshot(at, CodexQuotaBlockTests.Official(at, false, "workspace_owner_credits_depleted"));

    private static QuotaSnapshot Allowed(DateTimeOffset at) =>
        CodexQuotaBlockTests.Snapshot(at, CodexQuotaBlockTests.Official(at, true), used7d: 10);

    [Fact]
    public void AdoptedStopOlderThanSixHoursShowsAndKeepsReading()
    {
        string dir = CodexQuotaStopsTests.TempDir();
        try
        {
            string path = StoppedRollout(dir);
            using var p = new CodexProvider();
            var now = StoppedAt.AddDays(2);
            Assert.Equal(1, p.AdoptQuotaStops([Stopped(path, new FileInfo(path).Length)], now));
            var s = p.SessionById(CodexQuotaStopsTests.Id)!;
            Assert.Equal((AgentState.Interrupted, StoppedCaptionText), (s.State, s.Caption));
            Assert.Equal((CodexQuotaStopsTests.Title, CodexParsers.UsageLimitExceeded, "t1"), (s.Title, s.StopReason, s.TurnId));
            Assert.Contains(CodexQuotaStopsTests.Title, p.KnownTitles);
            Assert.Null(p.SessionById("outra"));
            Assert.Equal(0, p.AdoptQuotaStops([Stopped(path, new FileInfo(path).Length)], now));

            File.AppendAllText(path, CodexQuotaStopsTests.Started("t2", "2026-11-12T10:00:00.000Z", "exec") + "\n");
            p.ReadFile(path, false);
            s = p.SessionById(CodexQuotaStopsTests.Id)!;
            Assert.Equal((AgentState.Running, "t2", "exec", CodexQuotaStopsTests.Title), (s.State, s.TurnId, s.TurnTrigger, s.Title));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void QuotaStopDoesNotAgeWhileBlockedAndTheOfficialReleaseClearsIt()
    {
        using var p = new CodexProvider();
        var log = new List<string>();
        p.Diagnostic += log.Add;
        var t0 = StoppedAt.AddMinutes(1);
        p.ApplyQuota(Blocked(t0), official: true, t0);
        p.AdoptQuotaStops([Stopped()], t0);
        p.Expire(StoppedAt.AddDays(1));
        Assert.Equal(AgentState.Interrupted, p.SessionById(CodexQuotaStopsTests.Id)!.State);

        var released = StoppedAt.AddDays(2);
        p.ApplyQuota(Allowed(released), official: true, released);
        Assert.Equal("Cota liberada · pode continuar no app", p.SessionById(CodexQuotaStopsTests.Id)!.Caption);
        Assert.Contains("Codex cota: liberadas 1 conversa(s) parada(s) pela cota", log);
        p.Expire(released.AddMinutes(20));
        Assert.Equal(AgentState.Idle, p.SessionById(CodexQuotaStopsTests.Id)!.State);
    }

    [Fact]
    public async Task ScanRunsOnTheFirstBlockedOfficialReadAndOnEachNewBlock()
    {
        using var p = new CodexProvider();
        int scans = 0;
        p.FindQuotaStops = _ =>
        {
            scans++;
            return Task.FromResult(new QuotaStopScan([Stopped()], new QuotaStopExclusions(0, 0, 0, 0, 0), false, StoppedAt));
        };
        var t = StoppedAt.AddHours(1);
        p.ApplyQuota(Blocked(t), official: true, t);
        await p.LastQuotaScan!;
        Assert.Equal(1, scans);
        Assert.Equal(AgentState.Interrupted, p.SessionById(CodexQuotaStopsTests.Id)!.State);

        p.ApplyQuota(Blocked(t.AddMinutes(10)), official: true, t.AddMinutes(10));
        p.ApplyQuota(Blocked(t.AddMinutes(11)), official: false, t.AddMinutes(11));
        await p.LastQuotaScan!;
        Assert.Equal(1, scans);

        p.ApplyQuota(Allowed(t.AddMinutes(20)), official: true, t.AddMinutes(20));
        p.ApplyQuota(Blocked(t.AddMinutes(30)), official: true, t.AddMinutes(30));
        await p.LastQuotaScan!;
        Assert.Equal(2, scans);
    }

    [Fact]
    public async Task FailedScanIsRetriedOnTheNextBlockedOfficialRead()
    {
        using var p = new CodexProvider();
        int scans = 0;
        p.FindQuotaStops = _ => ++scans == 1
            ? Task.FromException<QuotaStopScan>(new TimeoutException("tempo esgotado"))
            : Task.FromResult(new QuotaStopScan([Stopped()], new QuotaStopExclusions(0, 0, 0, 0, 0), false, StoppedAt));
        var t = StoppedAt.AddHours(1);
        p.ApplyQuota(Blocked(t), official: true, t);
        await p.LastQuotaScan!;
        Assert.Null(p.SessionById(CodexQuotaStopsTests.Id));
        p.ApplyQuota(Blocked(t.AddMinutes(10)), official: true, t.AddMinutes(10));
        await p.LastQuotaScan!;
        Assert.Equal(2, scans);
        Assert.Equal(AgentState.Interrupted, p.SessionById(CodexQuotaStopsTests.Id)!.State);
    }

    [Fact]
    public void StopOfAWindowOnlyBlockIsHeldUntilTheOfficialRelease()
    {
        using var p = new CodexProvider();
        var t0 = StoppedAt.AddMinutes(1);
        var windowOnly = CodexQuotaBlockTests.Snapshot(t0, CodexQuotaBlockTests.Official(t0, true), used7d: 100, resets7d: StoppedAt.AddHours(5));
        p.ApplyQuota(windowOnly, official: true, t0);
        p.AdoptQuotaStops([Stopped()], t0);
        p.Expire(StoppedAt.AddHours(6)); // a janela venceu, mas a conta ainda não confirmou
        Assert.Equal(AgentState.Interrupted, p.SessionById(CodexQuotaStopsTests.Id)!.State);
        var released = StoppedAt.AddHours(6);
        p.ApplyQuota(Allowed(released), official: true, released);
        Assert.Equal("Cota liberada · pode continuar no app", p.SessionById(CodexQuotaStopsTests.Id)!.Caption);
    }

    [Fact]
    public void StopsTheDetectorDidNotListAreNotHeld()
    {
        string dir = CodexQuotaStopsTests.TempDir();
        try
        {
            string path = CodexQuotaStopsTests.WriteRollout(dir, CodexQuotaStopsTests.Id, CodexQuotaStopsTests.Meta(originator: "codex_exec"),
                CodexQuotaStopsTests.Started("t1"), CodexQuotaStopsTests.Context("t1"), CodexQuotaStopsTests.TokenCount(), CodexQuotaStopsTests.QuotaEnd());
            using var p = new CodexProvider();
            p.ReadFile(path, true);
            p.ApplyQuota(Blocked(StoppedAt.AddMinutes(1)), official: true, StoppedAt.AddMinutes(1));
            Assert.Equal(AgentState.Interrupted, p.SessionById(CodexQuotaStopsTests.Id)!.State);
            p.Expire(StoppedAt.AddMinutes(20));
            Assert.Equal(AgentState.Idle, p.SessionById(CodexQuotaStopsTests.Id)!.State);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void ExecTriggeredTurnWithoutItsProcessIsOrphaned()
    {
        string dir = CodexQuotaStopsTests.TempDir();
        try
        {
            string path = CodexQuotaStopsTests.WriteRollout(dir, CodexQuotaStopsTests.Id, CodexQuotaStopsTests.Meta(),
                CodexQuotaStopsTests.Started("t1", "2026-11-10T03:00:00.000Z", "exec"));
            using var p = new CodexProvider { ExecProcessStarts = () => [] };
            p.ReadFile(path, true);
            Assert.Equal(AgentState.Running, p.SessionById(CodexQuotaStopsTests.Id)!.State);
            p.Expire(DateTimeOffset.Parse("2026-11-10T07:00:00Z"));
            Assert.Equal(AgentState.Interrupted, p.SessionById(CodexQuotaStopsTests.Id)!.State);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void NotePrefixesTheCaptionUntilItExpires()
    {
        using var p = new CodexProvider();
        p.AdoptQuotaStops([Stopped()], StoppedAt.AddMinutes(1));
        p.SetNote(CodexQuotaStopsTests.Id, "Pela API");
        Assert.Equal("Pela API · " + StoppedCaptionText, p.SessionById(CodexQuotaStopsTests.Id)!.Caption);
        Assert.Equal("Pela API · " + StoppedCaptionText, Assert.Single(p.Sessions).Caption);
        p.SetNote(CodexQuotaStopsTests.Id, "Pela API", DateTimeOffset.Now.AddMinutes(-1));
        Assert.Equal(StoppedCaptionText, p.SessionById(CodexQuotaStopsTests.Id)!.Caption);
        p.SetNote(CodexQuotaStopsTests.Id, null);
        Assert.Equal(StoppedCaptionText, p.SessionById(CodexQuotaStopsTests.Id)!.Caption);
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
