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
                ["settings"] = new JsonObject { ["model"] = model, ["reasoning_effort"] = collaborationEffort, ["developer_instructions"] = "…" },
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

    // ---- CodexQuotaStops (A5) ----

    internal static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-11-10T14:00:00Z");
    private const string ScanLog = "Conversas paradas pela cota: 1 (excluídas: interrompidas por você=1, exec/scripts=1, subagentes=1, barra=1)";

    internal static JsonObject Item(string id, string? title = null, string? path = null, DateTimeOffset? updatedAt = null, JsonNode? source = null,
        string originator = "Codex Desktop", string? parent = null, string? role = null, string? threadSource = null, bool ephemeral = false,
        string? model = "gpt-5.6-sol", string? effort = "medium") => new()
    {
        ["id"] = id, ["name"] = title, ["path"] = path, ["updatedAt"] = (updatedAt ?? Now.AddHours(-1)).ToUnixTimeSeconds(),
        ["source"] = source ?? "vscode", ["originator"] = originator, ["parentThreadId"] = parent, ["agentRole"] = role,
        ["threadSource"] = threadSource, ["ephemeral"] = ephemeral, ["model"] = model, ["reasoningEffort"] = effort,
        ["cwd"] = Cwd, ["status"] = new JsonObject { ["type"] = "notLoaded" },
    };

    internal static JsonElement Page(IEnumerable<JsonObject> items, string? next = null)
    {
        var data = new JsonArray();
        foreach (var i in items) data.Add(i);
        return JsonDocument.Parse(new JsonObject { ["data"] = data, ["nextCursor"] = next, ["backwardsCursor"] = null }.ToJsonString()).RootElement.Clone();
    }

    internal static string SessionsDir(string home)
    {
        string dir = Path.Combine(home, "sessions", "2026", "11", "10");
        Directory.CreateDirectory(dir);
        return dir;
    }

    private sealed class FakeList
    {
        public readonly List<JsonObject> Requests = [];
        public Func<int, JsonElement> Respond = _ => Page([]);
        public Task<JsonElement> Call(JsonObject request, CancellationToken ct)
        {
            Requests.Add((JsonObject)request.DeepClone());
            return Task.FromResult(Respond(Requests.Count));
        }
    }

    [Theory]
    [InlineData("vscode", "codex_work_desktop", null, null, null, false, CodexThreadKind.User)]
    [InlineData("vscode", "Codex Desktop", null, null, "user", false, CodexThreadKind.User)]
    [InlineData("cli", "codex_cli_rs", null, null, null, false, CodexThreadKind.User)]
    [InlineData("vscode", "codex_vscode", null, null, "chatgpt_handoff", false, CodexThreadKind.User)]
    [InlineData("vscode", "winstatusbar", null, null, null, false, CodexThreadKind.Bar)]
    [InlineData("exec", "codex_exec", null, null, null, false, CodexThreadKind.ExecOrScript)]
    [InlineData("exec", "Codex Desktop", null, null, null, false, CodexThreadKind.ExecOrScript)]
    [InlineData("vscode", "Codex Desktop", "01aa0000-0000-7000-8000-000000000003", null, null, false, CodexThreadKind.Subagent)]
    [InlineData("vscode", "Codex Desktop", null, null, "agent_created_thread", false, CodexThreadKind.Subagent)]
    [InlineData("vscode", "Codex Desktop", null, "worker", null, false, CodexThreadKind.Subagent)]
    [InlineData("subagent", "Codex Desktop", null, null, null, false, CodexThreadKind.Subagent)]
    [InlineData("vscode", "Codex Desktop", null, null, null, true, CodexThreadKind.Ephemeral)]
    [InlineData("vscode", "outro_app", null, null, null, false, CodexThreadKind.UnknownOrigin)]
    [InlineData("appServer", "Codex Desktop", null, null, null, false, CodexThreadKind.UnknownSource)]
    public void ClassifiesTheRealSourceAndOriginPairs(string source, string originator, string? parent, string? role, string? threadSource, bool ephemeral, CodexThreadKind kind)
    {
        var t = new CodexThreadInfo(Id, Title, null, Now, source, originator, parent, role, threadSource, ephemeral, null, null);
        Assert.Equal(kind, CodexQuotaStops.Classify(t));
        Assert.Equal(kind == CodexThreadKind.User, CodexQuotaStops.IsUserThread(t));
    }

    [Fact]
    public void ReadsThreadListItemsWithObjectSources()
    {
        var item = Item(Id, Title, source: JsonNode.Parse("""{"subAgent":{"thread_spawn":{"parent_thread_id":"p"}}}"""), parent: "p",
            model: "gpt-5.6-sol", effort: "medium");
        var t = CodexThreadInfo.FromThreadList(JsonDocument.Parse(item.ToJsonString()).RootElement);
        Assert.Equal(("subagent", "p", "medium", "gpt-5.6-sol"), (t.Source, t.ParentThreadId, t.Effort, t.Model));
        Assert.Equal(Now.AddHours(-1).ToUnixTimeSeconds(), t.UpdatedAt!.Value.ToUnixTimeSeconds());

        var custom = CodexThreadInfo.FromThreadList(JsonDocument.Parse(Item(Id, source: JsonNode.Parse("""{"custom":"x"}""")).ToJsonString()).RootElement);
        Assert.Equal("custom", custom.Source);
    }

    [Theory]
    [InlineData(@"\\?\C:\Users\x\.codex\sessions\a.jsonl", @"C:\Users\x\.codex\sessions\a.jsonl")]
    [InlineData(@"\\?\UNC\srv\share\a.jsonl", @"\\srv\share\a.jsonl")]
    [InlineData(@"C:\a.jsonl", @"C:\a.jsonl")]
    [InlineData(null, null)]
    public void StripsTheLongPathPrefix(string? raw, string? expected) => Assert.Equal(expected, CodexQuotaStops.NormalizePath(raw));

    [Fact]
    public async Task ScanFindsTheStopThroughThreadList()
    {
        string home = TempDir();
        try
        {
            string sessions = SessionsDir(home);
            string stopPath = WriteRollout(sessions, Id, StoppedLines());
            string otherPath = WriteRollout(sessions, OtherId, Meta(OtherId), Started("t1"), Context("t1"), Aborted("t1", "2026-11-10T03:05:00.000Z"));
            var list = new FakeList
            {
                Respond = _ => Page([
                    Item(Id, Title, @"\\?\" + stopPath),
                    Item(OtherId, "Responder saudação", otherPath),
                    Item("01aa0000-0000-7000-8000-000000000004", "Chat da barra", originator: "winstatusbar"),
                    Item("01aa0000-0000-7000-8000-000000000005", "Script", source: "exec", originator: "codex_exec"),
                    Item("01aa0000-0000-7000-8000-000000000006", "Subagente", source: JsonNode.Parse("""{"subAgent":{}}"""), parent: "01aa0000-0000-7000-8000-000000000003"),
                ]),
            };
            var log = new List<string>();
            var scan = await CodexQuotaStops.ScanAsync(home, list.Call, Now, CancellationToken.None, log.Add);

            var stop = Assert.Single(scan.Stops);
            Assert.Equal((Id, Title, stopPath), (stop.ThreadId, stop.Title, stop.RolloutPath));
            Assert.Equal(("gpt-5.6-sol", "low", "workspace-write", "on-request"), (stop.Model, stop.Effort, stop.SandboxMode, stop.ApprovalPolicy));
            Assert.Equal(("t1", DateTimeOffset.Parse(StopAt)), (stop.StoppedTurnId, stop.StoppedAt));
            Assert.True(stop.Remarked);
            Assert.Null(stop.Unavailable);
            Assert.Equal([@"Z:\"], stop.ExtraRoots);
            Assert.Equal(79123, stop.ContextTokens);
            Assert.Equal(new FileInfo(stopPath).Length, stop.RolloutOffset);
            Assert.Equal(new QuotaStopExclusions(1, 1, 1, 1, 0), scan.Excluded);
            Assert.False(scan.UsedFallback);
            Assert.Equal(ScanLog, scan.LogLine);
            Assert.Contains(ScanLog, log);
            var request = Assert.Single(list.Requests);
            Assert.Equal("""{"archived":false,"sortKey":"updated_at","useStateDbOnly":true,"limit":50}""", request.ToJsonString());
        }
        finally { Directory.Delete(home, true); }
    }

    [Fact]
    public async Task ThreadListStopsAfterFourPagesOrWhenTwoWeeksOld()
    {
        string home = TempDir();
        try
        {
            var many = new FakeList { Respond = n => Page([Item($"01aa0000-0000-7000-8000-0000000001{n:00}")], "c" + n) };
            await CodexQuotaStops.ScanAsync(home, many.Call, Now, CancellationToken.None);
            Assert.Equal(new[] { "", "c1", "c2", "c3" }, many.Requests.Select(r => (string?)r["cursor"] ?? "").ToArray());

            var old = new FakeList { Respond = n => Page([Item($"01aa0000-0000-7000-8000-0000000002{n:00}", updatedAt: Now.AddDays(-20))], "c" + n) };
            await CodexQuotaStops.ScanAsync(home, old.Call, Now, CancellationToken.None);
            Assert.Single(old.Requests);
        }
        finally { Directory.Delete(home, true); }
    }

    [Fact]
    public async Task StopOlderThanTheWindowIsNotListed()
    {
        string home = TempDir();
        try
        {
            string old = Now.AddDays(-15).ToString("yyyy-MM-ddTHH:mm:ss.fffZ");
            string path = WriteRollout(SessionsDir(home), Id, Meta(), Started("t1", Now.AddDays(-15).AddMinutes(-5).ToString("yyyy-MM-ddTHH:mm:ss.fffZ")),
                Context("t1", ts: Now.AddDays(-15).AddMinutes(-4).ToString("yyyy-MM-ddTHH:mm:ss.fffZ")), QuotaEnd("t1", old));
            var list = new FakeList { Respond = _ => Page([Item(Id, Title, path, updatedAt: Now)]) };
            var scan = await CodexQuotaStops.ScanAsync(home, list.Call, Now, CancellationToken.None);
            Assert.Empty(scan.Stops);
        }
        finally { Directory.Delete(home, true); }
    }

    [Fact]
    public async Task FallsBackToLocalRecordsWhenThreadListFails()
    {
        string home = TempDir();
        try
        {
            string path = WriteRollout(SessionsDir(home), Id, StoppedLines());
            File.SetLastWriteTimeUtc(path, Now.UtcDateTime.AddHours(-1));
            string archived = Path.Combine(home, "archived_sessions");
            Directory.CreateDirectory(archived);
            WriteRollout(archived, OtherId, Meta(OtherId), Started("t1"), Context("t1"), QuotaEnd());
            File.WriteAllText(Path.Combine(home, "session_index.jsonl"),
                new JsonObject { ["id"] = Id, ["thread_name"] = Title, ["updated_at"] = "2026-11-10T03:31:00Z" }.ToJsonString(new JsonSerializerOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }) + "\n");

            var log = new List<string>();
            var scan = await CodexQuotaStops.ScanAsync(home, (_, _) => throw new IOException("boom"), Now, CancellationToken.None, log.Add);
            Assert.True(scan.UsedFallback);
            var stop = Assert.Single(scan.Stops);
            Assert.Equal((Id, Title), (stop.ThreadId, stop.Title));
            Assert.Contains("Conversas paradas pela cota: lista oficial indisponível (boom); usando os registros locais", log);
            Assert.Contains("Conversas paradas pela cota: no modo local não dá para excluir as conversas arquivadas", log);
        }
        finally { Directory.Delete(home, true); }
    }

    [Fact]
    public async Task FindsTheRolloutByIdWhenTheListHasNoPath()
    {
        string home = TempDir();
        try
        {
            WriteRollout(SessionsDir(home), Id, StoppedLines());
            var list = new FakeList { Respond = _ => Page([Item(Id, title: null, path: null)]) };
            var stop = Assert.Single((await CodexQuotaStops.ScanAsync(home, list.Call, Now, CancellationToken.None)).Stops);
            Assert.Equal("Conversa sem título", stop.Title);
            Assert.True(stop.Remarked);
        }
        finally { Directory.Delete(home, true); }
    }

    [Fact]
    public async Task StopWithoutAModelIsUnavailable()
    {
        string home = TempDir();
        try
        {
            string path = WriteRollout(SessionsDir(home), Id, Meta(), Started("t1"), Context("t1", model: null, effort: null, collaborationEffort: null), QuotaEnd());
            var list = new FakeList { Respond = _ => Page([Item(Id, Title, path)]) };
            var stop = Assert.Single((await CodexQuotaStops.ScanAsync(home, list.Call, Now, CancellationToken.None)).Stops);
            Assert.Equal(CodexQuotaText.MissingTurnData, stop.Unavailable);
        }
        finally { Directory.Delete(home, true); }
    }

    [Fact]
    public async Task FindAsyncJoinsTheRunningScanAndCachesForMaxAge()
    {
        string home = TempDir();
        try
        {
            int opens = 0;
            var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var clock = Now;
            var finder = new QuotaStopFinder(_ => Task.CompletedTask, _ =>
            {
                Interlocked.Increment(ref opens);
                return Task.FromResult(new ThreadListConnection(async (_, _) => { await gate.Task; return Page([]); }, null));
            }, () => clock, TimeSpan.FromSeconds(30));

            var a = finder.FindAsync(home, TimeSpan.FromSeconds(60), CancellationToken.None);
            var b = finder.FindAsync(home, TimeSpan.FromSeconds(60), CancellationToken.None);
            gate.SetResult();
            var first = await a;
            Assert.Same(first, await b);
            Assert.Equal(1, opens);

            clock = Now.AddSeconds(30);
            Assert.Same(first, await finder.FindAsync(home, TimeSpan.FromSeconds(60), CancellationToken.None));
            Assert.Equal(1, opens);

            clock = Now.AddSeconds(61);
            Assert.NotSame(first, await finder.FindAsync(home, TimeSpan.FromSeconds(60), CancellationToken.None));
            Assert.Equal(2, opens);

            await finder.FindAsync(home, TimeSpan.Zero, CancellationToken.None);
            Assert.Equal(3, opens);
        }
        finally { Directory.Delete(home, true); }
    }

    [Fact]
    public async Task FindAsyncWaitsForTheScreenWithoutTimingOut()
    {
        string home = TempDir();
        try
        {
            int opens = 0;
            var visible = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var finder = new QuotaStopFinder(_ => visible.Task, _ =>
            {
                Interlocked.Increment(ref opens);
                return Task.FromResult(new ThreadListConnection((_, _) => Task.FromResult(Page([])), null));
            }, () => Now, TimeSpan.FromMilliseconds(100));

            var scan = finder.FindAsync(home, TimeSpan.FromSeconds(60), CancellationToken.None);
            await Task.Delay(300);
            Assert.False(scan.IsCompleted);
            Assert.Equal(0, opens);
            visible.SetResult();
            Assert.False((await scan).UsedFallback);
            Assert.Equal(1, opens);
        }
        finally { Directory.Delete(home, true); }
    }

    [Fact]
    public async Task FindAsyncFallsBackWhenTheConnectionFails()
    {
        string home = TempDir();
        try
        {
            var finder = new QuotaStopFinder(_ => Task.CompletedTask, _ => throw new InvalidOperationException("Codex não encontrado."),
                () => Now, TimeSpan.FromSeconds(30));
            var log = new List<string>();
            var scan = await finder.FindAsync(home, TimeSpan.FromSeconds(60), CancellationToken.None, log.Add);
            Assert.True(scan.UsedFallback);
            Assert.Contains("Conversas paradas pela cota: lista oficial indisponível (Codex não encontrado.); usando os registros locais", log);
        }
        finally { Directory.Delete(home, true); }
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
