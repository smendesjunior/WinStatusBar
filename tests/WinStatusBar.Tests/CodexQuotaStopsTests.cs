using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using WinStatusBar.Core.Ai.Codex;
using Xunit;

namespace WinStatusBar.Tests;

public class CodexQuotaStopsTests
{
    // Dados sintéticos no formato real (o repositório é público).
    internal const string Id = "01aa0000-0000-7000-8000-000000000001";
    internal const string OtherId = "01aa0000-0000-7000-8000-000000000002";
    internal const string Title = "Analisar processo de Fulano de Tal";
    internal const string GluedTitle = Title + " voce consegue me dizer";
    internal const string Cwd = @"C:\Users\x\OneDrive\PROJEÇÃO";
    internal const string StopAt = "2026-11-10T03:31:00.389Z";

    private static readonly JsonSerializerOptions Relaxed = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    internal static string Line(string ts, string type, JsonObject payload) =>
        new JsonObject { ["timestamp"] = ts, ["type"] = type, ["payload"] = payload }.ToJsonString(Relaxed);

    internal static string Meta(string id = Id, JsonNode? source = null, string originator = "Codex Desktop", string threadSource = "user") =>
        Line("2026-11-10T02:50:00.000Z", "session_meta", new JsonObject
        {
            ["session_id"] = id, ["id"] = id, ["timestamp"] = "2026-11-10T02:50:00.000Z", ["cwd"] = Cwd,
            ["runtime_workspace_roots"] = new JsonArray(Cwd, @"Z:\"), ["originator"] = originator, ["cli_version"] = "0.166.0",
            ["source"] = source ?? "vscode", ["thread_source"] = threadSource, ["model_provider"] = "openai",
        });

    internal static string Started(string turn, string ts = "2026-11-10T03:00:00.000Z", string trigger = "composer") =>
        Line(ts, "event_msg", new JsonObject
        {
            ["type"] = "task_started",
            ["turn_attribution"] = new JsonObject { ["turn_id"] = turn, ["turn_trigger"] = trigger, ["parent_turn_id"] = null, ["root_turn_id"] = turn },
            ["turn_id"] = turn, ["root_turn_id"] = turn, ["model_context_window"] = 475000, ["collaboration_mode_kind"] = "default",
        });

    internal static string Context(string turn, string? model = "gpt-5.6-sol", string? effort = "low", string? collaborationEffort = "low", string ts = "2026-11-10T03:00:01.000Z") =>
        Line(ts, "turn_context", new JsonObject
        {
            ["turn_id"] = turn, ["root_turn_id"] = turn, ["cwd"] = Cwd, ["approval_policy"] = "on-request", ["approvals_reviewer"] = "user",
            ["sandbox_policy"] = new JsonObject
            {
                ["type"] = "workspace-write", ["writable_roots"] = new JsonArray(@"C:\Users\x\Temp"), ["network_access"] = false,
                ["exclude_tmpdir_env_var"] = false, ["exclude_slash_tmp"] = false,
            },
            ["model"] = model, ["effort"] = effort, ["summary"] = "none",
            ["collaboration_mode"] = new JsonObject
            {
                ["mode"] = "default",
                ["settings"] = new JsonObject { ["model"] = model ?? "gpt-5.6-sol", ["reasoning_effort"] = collaborationEffort, ["developer_instructions"] = "…" },
            },
        });

    internal static string TokenCount(string ts = "2026-11-10T03:31:00.371Z") => Line(ts, "event_msg", new JsonObject
    {
        ["type"] = "token_count",
        ["info"] = new JsonObject
        {
            ["total_token_usage"] = new JsonObject { ["input_tokens"] = 900000, ["cached_input_tokens"] = 800000, ["output_tokens"] = 5000, ["reasoning_output_tokens"] = 100, ["total_tokens"] = 905000 },
            ["last_token_usage"] = new JsonObject { ["input_tokens"] = 79123, ["cached_input_tokens"] = 70000, ["output_tokens"] = 12, ["reasoning_output_tokens"] = 0, ["total_tokens"] = 79135 },
            ["model_context_window"] = 475000,
        },
        ["rate_limits"] = JsonNode.Parse(CodexQuotaBlockTests.RolloutBlock),
    });

    internal static string QuotaEnd(string turn = "t1", string ts = StopAt) => Line(ts, "event_msg", new JsonObject
    {
        ["type"] = "task_complete", ["root_turn_id"] = turn, ["turn_id"] = turn, ["last_agent_message"] = null,
        ["error"] = new JsonObject { ["message"] = "Your workspace is out of credits. Add credits to continue.", ["codex_error_info"] = "usage_limit_exceeded" },
    });

    internal static string Done(string turn, string ts) => Line(ts, "event_msg", new JsonObject
    {
        ["type"] = "task_complete", ["root_turn_id"] = turn, ["turn_id"] = turn, ["last_agent_message"] = "pronto",
    });

    internal static string Aborted(string turn, string ts) => Line(ts, "event_msg", new JsonObject
    {
        ["type"] = "turn_aborted", ["turn_id"] = turn, ["reason"] = "interrupted",
    });

    internal static string Settings(string model, string effort, string ts = "2026-11-10T03:40:00.000Z") => Line(ts, "event_msg", new JsonObject
    {
        ["type"] = "thread_settings_applied", ["thread_id"] = Id,
        ["thread_settings"] = new JsonObject { ["model"] = model, ["model_provider_id"] = "openai", ["reasoning_effort"] = effort, ["cwd"] = Cwd },
    });

    internal static string UserMessage(string text, string ts) => Line(ts, "event_msg", new JsonObject { ["type"] = "user_message", ["message"] = text });

    internal static string Filler(int i) => Line($"2026-11-10T03:{10 + i / 4:00}:{i % 60:00}.000Z", "response_item", new JsonObject
    {
        ["type"] = "message", ["role"] = "assistant",
        ["content"] = new JsonArray(new JsonObject { ["type"] = "output_text", ["text"] = $"{i} " + new string('x', 40_000) + " ação" }),
    });

    internal static string[] StoppedLines()
    {
        var lines = new List<string> { Meta(), Settings("gpt-5.6-sol", "low", "2026-11-10T02:59:59.000Z"), Started("t1"), Context("t1") };
        for (int i = 0; i < 40; i++) lines.Add(Filler(i));
        lines.Add(TokenCount());
        lines.Add(QuotaEnd());
        lines.Add(Settings("gpt-5.6-sol", "medium"));
        return [.. lines];
    }

    internal static string TempDir()
    {
        string dir = Path.Combine(Path.GetTempPath(), "wsb-quota-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    internal static string WriteRollout(string dir, string id, params string[] lines)
    {
        string path = Path.Combine(dir, $"rollout-2026-11-10T02-50-00-{id}.jsonl");
        File.WriteAllText(path, string.Join("\n", lines) + "\n", new UTF8Encoding(false));
        return path;
    }

    private static RolloutTail Analyze(params string[] lines) => CodexRolloutTail.Analyze(lines.Reverse(), Meta(), 0);

    [Fact]
    public void FindsTheStopAndTheStoppedTurnAcrossManyBlocks()
    {
        string dir = TempDir();
        try
        {
            string path = WriteRollout(dir, Id, StoppedLines());
            long size = new FileInfo(path).Length;
            Assert.True(size > 1_500_000);

            var t = CodexRolloutTail.Read(path)!;
            Assert.Equal(RolloutVerdict.QuotaStop, t.Verdict);
            Assert.Equal("t1", t.LastTurnId);
            Assert.Equal(DateTimeOffset.Parse(StopAt), t.LastLifecycleAt);
            var turn = t.StoppedTurn!;
            Assert.Equal(("gpt-5.6-sol", "low", false), (turn.Model, turn.Effort, turn.EffortFromCollaborationMode));
            Assert.Equal(("workspace-write", (bool?)false, "on-request"), (turn.SandboxMode, turn.NetworkAccess, turn.ApprovalPolicy));
            Assert.Equal([@"C:\Users\x\Temp"], turn.WritableRoots);
            Assert.Equal(Cwd, turn.Cwd);
            Assert.Equal(("gpt-5.6-sol", "medium"), (t.CurrentModel, t.CurrentEffort));
            Assert.Equal(79123, t.ContextTokens);
            Assert.Equal("workspace_owner_credits_depleted", t.ReachedType);
            Assert.Equal([Cwd, @"Z:\"], t.WorkspaceRoots);
            Assert.Equal((Id, "vscode", "Codex Desktop", "user"), (t.ThreadId, t.Source, t.Originator, t.ThreadSource));
            Assert.Equal(size, t.CompleteLength);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void LateEventsAfterTheStopDoNotHideIt()
    {
        var t = Analyze(Started("t1"), Context("t1"), TokenCount(), QuotaEnd(),
            UserMessage("voce consegue me dizer", "2026-11-10T03:35:00.000Z"),
            Settings("codexias-claude-opus-5-5", "high", "2026-11-10T03:36:00.000Z"),
            Settings("gpt-5.6-sol", "medium", "2026-11-10T03:37:00.000Z"));
        Assert.Equal(RolloutVerdict.QuotaStop, t.Verdict);
        Assert.Equal(("gpt-5.6-sol", "medium"), (t.CurrentModel, t.CurrentEffort));
        Assert.Equal(("gpt-5.6-sol", "low"), (t.StoppedTurn!.Model, t.StoppedTurn.Effort));
    }

    [Fact]
    public void AbortedContinuedOrRunningConversationsAreNotStops()
    {
        Assert.Equal(RolloutVerdict.Aborted, Analyze(Started("t1"), Context("t1"), Aborted("t1", "2026-11-10T03:05:00.000Z")).Verdict);

        var continued = Analyze(Started("t1"), Context("t1"), QuotaEnd(), Started("t2", "2026-11-10T04:00:00.000Z"), Context("t2", ts: "2026-11-10T04:00:01.000Z"),
            Done("t2", "2026-11-10T04:01:00.000Z"));
        Assert.Equal((RolloutVerdict.Completed, "t2"), (continued.Verdict, continued.LastTurnId));
        Assert.Null(continued.StoppedTurn);

        var running = Analyze(Started("t1"), Context("t1"), QuotaEnd(), Started("t2", "2026-11-10T04:00:00.000Z"), Context("t2", ts: "2026-11-10T04:00:01.000Z"));
        Assert.Equal(RolloutVerdict.Running, running.Verdict);

        var failed = Analyze(Started("t1"), Context("t1"), Line("2026-11-10T03:05:00.000Z", "event_msg", new JsonObject
        {
            ["type"] = "task_complete", ["turn_id"] = "t1", ["error"] = new JsonObject { ["message"] = "boom", ["codex_error_info"] = "other" },
        }));
        Assert.Equal(RolloutVerdict.Failed, failed.Verdict);
        Assert.Equal(RolloutVerdict.Empty, Analyze(Settings("gpt-5.6-sol", "low")).Verdict);
    }

    [Fact]
    public void EffortFallsBackToTheCollaborationMode()
    {
        var t = Analyze(Started("t1"), Context("t1", effort: null, collaborationEffort: "high"), QuotaEnd());
        Assert.Equal(("high", true), (t.StoppedTurn!.Effort, t.StoppedTurn.EffortFromCollaborationMode));
    }

    [Fact]
    public void StopWithoutItsTurnContextHasNoStoppedTurn()
    {
        var t = Analyze(Started("t0"), Context("t0"), Done("t0", "2026-11-10T03:00:30.000Z"), Started("t1", "2026-11-10T03:01:00.000Z"), QuotaEnd());
        Assert.Equal(RolloutVerdict.QuotaStop, t.Verdict);
        Assert.Null(t.StoppedTurn);
    }

    [Fact]
    public void SubagentSourceObjectBecomesSubagent()
    {
        var meta = Meta(source: JsonNode.Parse("""{"subagent":{"thread_spawn":{"parent_thread_id":"p"}}}"""), threadSource: "subagent");
        var t = CodexRolloutTail.Analyze(new[] { QuotaEnd(), Context("t1"), Started("t1") }, meta, 0);
        Assert.Equal("subagent", t.Source);
    }

    [Fact]
    public void ReadsBackwardsWithAccentsCrLfAndAPartialLastLine()
    {
        var bytes = Encoding.UTF8.GetBytes("primeira\n{\"a\":\"ação\"}\n{\"incompl");
        using (var ms = new MemoryStream(bytes))
            Assert.Equal(["{\"incompl", "{\"a\":\"ação\"}", "primeira"], CodexRolloutTail.LinesFromEnd(ms, bytes.Length, blockSize: 3).ToArray());
        using (var ms = new MemoryStream(bytes))
            Assert.Equal(["{\"incompl"], CodexRolloutTail.LinesFromEnd(ms, bytes.Length, maxBytes: 11, blockSize: 3).ToArray());

        string dir = TempDir();
        try
        {
            string path = Path.Combine(dir, $"rollout-2026-11-10T02-50-00-{Id}.jsonl");
            string complete = string.Join("\r\n", StoppedLines()) + "\r\n";
            File.WriteAllText(path, complete + "{\"timestamp\":\"2026-11-10T03:4", new UTF8Encoding(false));
            var t = CodexRolloutTail.Read(path)!;
            Assert.Equal(RolloutVerdict.QuotaStop, t.Verdict);
            Assert.Equal(Cwd, t.StoppedTurn!.Cwd);
            Assert.Equal(Encoding.UTF8.GetByteCount(complete), t.CompleteLength);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void CachedResultFollowsTheFile()
    {
        string dir = TempDir();
        try
        {
            string path = WriteRollout(dir, Id, Meta(), Started("t1"), Context("t1"), TokenCount(), QuotaEnd());
            Assert.Equal(RolloutVerdict.QuotaStop, CodexRolloutTail.Read(path)!.Verdict);
            File.AppendAllText(path, Started("t2", "2026-11-10T04:00:00.000Z") + "\n");
            Assert.Equal(RolloutVerdict.Running, CodexRolloutTail.Read(path)!.Verdict);
            Assert.Null(CodexRolloutTail.Read(Path.Combine(dir, "missing.jsonl")));
        }
        finally { Directory.Delete(dir, true); }
    }
}
