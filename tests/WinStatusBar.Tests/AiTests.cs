using System.Text.Json;
using System.Text.Json.Nodes;
using WinStatusBar.Core.Ai;
using WinStatusBar.Core.Ai.Claude;
using WinStatusBar.Core.Ai.Codex;
using WinStatusBar.Core.Integration;
using WinStatusBar.Core.Ipc;
using Xunit;

namespace WinStatusBar.Tests;

public class CodexTests
{
    // Resposta real (estrutura) de account/rateLimits/read obtida do codex-cli 0.166.
    private const string RateLimitsResult = """
    {"ordinaryUsageAllowed":true,"rateLimits":{"limitId":"codex","primary":{"usedPercent":3,"windowDurationMins":300,"resetsAt":1793823088},
     "secondary":{"usedPercent":14,"windowDurationMins":10080,"resetsAt":1794333894},"credits":{"hasCredits":false,"unlimited":false,"balance":"0"},
     "planType":"team","rateLimitReachedType":null}}
    """;

    [Fact]
    public void ParsesAppServerRateLimits()
    {
        using var doc = JsonDocument.Parse(RateLimitsResult);
        var q = CodexParsers.ParseRateLimits(doc.RootElement, "test")!;
        Assert.Equal("5h", q.Primary!.Label);
        Assert.Equal(3, q.Primary.UsedPercent);
        Assert.Equal(97, q.Primary.RemainingPercent);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1793823088), q.Primary.ResetsAt);
        Assert.Equal("7d", q.Secondary!.Label);
        Assert.Equal(14, q.Secondary.UsedPercent);
        Assert.Equal("team", q.PlanType);
    }

    [Fact]
    public void ParsesRolloutTokenCountRateLimits_SnakeCase()
    {
        var s = new AgentSession { Kind = AgentKind.Codex, Id = "t" };
        string line = """{"timestamp":"2026-11-04T15:52:00.153Z","type":"event_msg","payload":{"type":"token_count","rate_limits":{"limit_id":"codex","primary":{"used_percent":3.0,"window_minutes":300,"resets_at":1793823088},"secondary":{"used_percent":14.0,"window_minutes":10080,"resets_at":1794333894}}}}""";
        var q = CodexParsers.ApplyRolloutLine(s, line)!;
        Assert.Equal(3, q.Primary!.UsedPercent);
        Assert.Equal(14, q.Secondary!.UsedPercent);
    }

    [Fact]
    public void RolloutLifecycle_RunningToolsCompleted()
    {
        var s = new AgentSession { Kind = AgentKind.Codex, Id = "t" };
        CodexParsers.ApplyRolloutLine(s, """{"timestamp":"2026-11-04T15:45:58.665Z","type":"session_meta","payload":{"id":"t","cwd":"C:\\proj\\Obras"}}""");
        Assert.Equal("Obras", s.ProjectName);
        CodexParsers.ApplyRolloutLine(s, """{"timestamp":"2026-11-04T15:46:00Z","type":"event_msg","payload":{"type":"task_started","turn_id":"x"}}""");
        Assert.Equal(AgentState.Running, s.State);
        CodexParsers.ApplyRolloutLine(s, """{"timestamp":"2026-11-04T15:46:01Z","type":"event_msg","payload":{"type":"item_completed","item":{"type":"CommandExecution"}}}""");
        Assert.Equal("Executando comandos…", s.Caption);
        CodexParsers.ApplyRolloutLine(s, """{"timestamp":"2026-11-04T15:46:02Z","type":"response_item","payload":{"type":"function_call","name":"update_plan","arguments":"{\"plan\":[{\"step\":\"a\",\"status\":\"completed\"},{\"step\":\"b\",\"status\":\"in_progress\"},{\"step\":\"c\",\"status\":\"pending\"},{\"step\":\"d\",\"status\":\"completed\"}]}"}}""");
        Assert.Equal(50, s.ProgressPercent);
        CodexParsers.ApplyRolloutLine(s, """{"timestamp":"2026-11-04T15:46:03Z","type":"event_msg","payload":{"type":"exec_approval_request"}}""");
        Assert.Equal(AgentState.WaitingApproval, s.State);
        CodexParsers.ApplyRolloutLine(s, """{"timestamp":"2026-11-04T15:46:04Z","type":"event_msg","payload":{"type":"task_complete","turn_id":"x"}}""");
        Assert.Equal(AgentState.Completed, s.State);
        Assert.Equal(50, s.ProgressPercent); // fim da resposta não conclui etapas pendentes
    }

    [Fact]
    public void Rollout_IgnoresGarbageAndPartialLines()
    {
        var s = new AgentSession { Kind = AgentKind.Codex, Id = "t" };
        Assert.Null(CodexParsers.ApplyRolloutLine(s, "{\"timestamp\":\"2026-11-04T15:46"));
        Assert.Null(CodexParsers.ApplyRolloutLine(s, "not json"));
        Assert.Equal(AgentState.Idle, s.State);
    }

    [Fact]
    public void ThreadIdFromRolloutFileName()
    {
        Assert.Equal("01a1a73d-0bd3-7871-90f9-01112e1583fa",
            CodexParsers.ThreadIdFromRolloutPath(@"C:\x\rollout-2026-11-04T12-45-58-01a1a73d-0bd3-7871-90f9-01112e1583fa.jsonl"));
        Assert.Null(CodexParsers.ThreadIdFromRolloutPath(@"C:\x\other.jsonl"));
    }
}

public class ClaudeTests
{
    [Fact]
    public void StatusLine_ExtractsQuotaContextAndModel()
    {
        string json = """
        {"session_id":"s1","session_name":"Implementando autenticação","model":{"id":"claude-opus-5-5","display_name":"Opus 5.5"},
         "workspace":{"current_dir":"C:\\app","project_dir":"C:\\app"},"context_window":{"used_percentage":23.4},
         "rate_limits":{"five_hour":{"used_percentage":12.5,"resets_at":1793823088},"seven_day":{"used_percentage":40,"resets_at":"2026-11-08T10:00:00Z"}}}
        """;
        var m = ClaudeEventExtractor.FromStatusLine(json)!;
        Assert.Equal("Opus 5.5", m.Model);
        Assert.Equal(23.4, m.ContextUsedPercent);
        Assert.Equal(12.5, m.FiveHourUsed);
        Assert.Equal(1793823088, m.FiveHourResetsAt);
        Assert.Equal(40, m.SevenDayUsed);
        Assert.Equal(DateTimeOffset.Parse("2026-11-08T10:00:00Z").ToUnixTimeSeconds(), m.SevenDayResetsAt);
        var q = ClaudeCodeProvider.ToQuota(m, DateTimeOffset.Now)!;
        Assert.Equal(87.5, q.Primary!.RemainingPercent);
    }

    [Fact]
    public void StatusLine_WithoutRateLimits_IsUnavailableNotZero()
    {
        var m = ClaudeEventExtractor.FromStatusLine("""{"session_id":"s1","context_window":{"used_percentage":80}}""")!;
        Assert.Null(ClaudeCodeProvider.ToQuota(m, DateTimeOffset.Now)); // contexto ≠ cota: não fabrica percentuais
        Assert.Equal("Indisponível", QuotaService.Text(null, true, DateTimeOffset.Now));
    }

    [Fact]
    public void Hook_DescribesToolsWithoutLeakingContent()
    {
        var m = ClaudeEventExtractor.FromHook("""{"hook_event_name":"PreToolUse","session_id":"s","tool_name":"Edit","tool_input":{"file_path":"C:\\src\\Auth\\TokenService.cs","old_string":"SEGREDO","new_string":"x"}}""")!;
        Assert.Equal("Editando TokenService.cs…", m.Detail);
        string serialized = JsonSerializer.Serialize(m, EventMessageContext.Default.EventMessage);
        Assert.DoesNotContain("SEGREDO", serialized);

        var bash = ClaudeEventExtractor.FromHook("""{"hook_event_name":"PreToolUse","session_id":"s","tool_name":"Bash","tool_input":{"command":"dotnet test --filter x","description":"Run unit tests"}}""")!;
        Assert.Equal("Executando: Run unit tests", bash.Detail);
        Assert.DoesNotContain("dotnet test", JsonSerializer.Serialize(bash, EventMessageContext.Default.EventMessage));
    }

    [Fact]
    public void Hook_TodoProgressIsRealRatio()
    {
        var m = ClaudeEventExtractor.FromHook("""{"hook_event_name":"PreToolUse","session_id":"s","tool_name":"TodoWrite","tool_input":{"todos":[{"content":"a","status":"completed"},{"content":"b","status":"in_progress"},{"content":"c","status":"pending"}]}}""")!;
        Assert.Equal(100.0 / 3, m.Progress!.Value, 6);
    }

    [Fact]
    public void Hook_PromptIsTruncated()
    {
        string longPrompt = new string('x', 500);
        var m = ClaudeEventExtractor.FromHook($$"""{"hook_event_name":"UserPromptSubmit","session_id":"s","prompt":"{{longPrompt}}"}""")!;
        Assert.True(m.PromptSnippet!.Length <= 90);
    }

    [Fact]
    public void StateMachine_FullLifecycle()
    {
        var s = new AgentSession { Kind = AgentKind.Claude, Id = "s" };
        var now = DateTimeOffset.Now;
        ClaudeStateMachine.Apply(s, new EventMessage { Event = "UserPromptSubmit" }, now);
        Assert.Equal(AgentState.Running, s.State);
        ClaudeStateMachine.Apply(s, new EventMessage { Event = "PreToolUse", Detail = "Editando a.cs…" }, now);
        Assert.Equal("Editando a.cs…", s.Caption);
        ClaudeStateMachine.Apply(s, new EventMessage { Event = "Notification", NotificationType = "permission_prompt" }, now);
        Assert.Equal(AgentState.WaitingApproval, s.State);
        ClaudeStateMachine.Apply(s, new EventMessage { Event = "PostToolUse" }, now);
        Assert.Equal(AgentState.Running, s.State);
        ClaudeStateMachine.Apply(s, new EventMessage { Event = "SubagentStart", Detail = "Explore" }, now);
        Assert.Equal(1, s.ActiveSubagents);
        ClaudeStateMachine.Apply(s, new EventMessage { Event = "SubagentStop" }, now);
        Assert.Equal(0, s.ActiveSubagents);
        ClaudeStateMachine.Apply(s, new EventMessage { Event = "Stop" }, now);
        Assert.Equal(AgentState.Completed, s.State);
        Assert.True(s.ExecutionEnded);
        ClaudeStateMachine.Apply(s, new EventMessage { Event = "UserPromptSubmit" }, now);
        Assert.False(s.ExecutionEnded);
        ClaudeStateMachine.Apply(s, new EventMessage { Event = "StopFailure" }, now);
        Assert.Equal(AgentState.Error, s.State);
    }

    [Fact]
    public void Provider_SessionEndRemovesSessionAndStaleRunningExpires()
    {
        var p = new ClaudeCodeProvider();
        p.Apply(new EventMessage { Event = "UserPromptSubmit", SessionId = "a", Cwd = @"C:\x\proj", Timestamp = DateTimeOffset.UtcNow.AddMinutes(-30).ToUnixTimeMilliseconds() });
        p.Apply(new EventMessage { Event = "UserPromptSubmit", SessionId = "b", Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() });
        Assert.Equal(2, p.Sessions.Count);
        p.Expire(DateTimeOffset.Now);
        Assert.Equal(AgentState.Running, p.Sessions.Single(x => x.Id == "a").State);
        Assert.True(p.Sessions.Single(x => x.Id == "a").ObservationStale);
        p.Apply(new EventMessage { Event = "SessionEnd", SessionId = "b" });
        var ended = p.Sessions.Single(x => x.Id == "b");
        Assert.Equal(AgentState.Unknown, ended.State); // fim da sessão não comprova sucesso
        Assert.Equal("Sessão encerrada", ended.Caption);
    }

    [Fact]
    public void CustomTitle_LastEntryWins()
    {
        string text = """
        {"type":"user","message":"x"}
        {"type":"custom-title","customTitle":"Primeiro","sessionId":"s"}
        {"type":"assistant"}
        {"type":"custom-title","customTitle":"WinStatusBar — Barra","sessionId":"s"}
        """;
        Assert.Equal("WinStatusBar — Barra", ClaudeCodeProvider.FindLastCustomTitle(text));
    }
}

public class PriorityTests
{
    private static AgentSession S(AgentKind k, string id, AgentState st, int minutesAgo) =>
        new() { Kind = k, Id = id, State = st, LastActivity = DateTimeOffset.Now.AddMinutes(-minutesAgo) };

    [Fact]
    public void WaitingBeatsRunningBeatsCompleted()
    {
        var list = new[] { S(AgentKind.Codex, "a", AgentState.Completed, 0), S(AgentKind.Claude, "b", AgentState.Running, 5), S(AgentKind.Codex, "c", AgentState.WaitingApproval, 10) };
        Assert.Equal("c", TaskActivityService.ChoosePriority(list, null, null)!.Id);
    }

    [Fact]
    public void ForegroundBreaksTies_PinnedOverrides()
    {
        var list = new[] { S(AgentKind.Codex, "a", AgentState.Running, 0), S(AgentKind.Claude, "b", AgentState.Running, 3) };
        Assert.Equal("b", TaskActivityService.ChoosePriority(list, "auto", AgentKind.Claude)!.Id);
        Assert.Equal("a", TaskActivityService.ChoosePriority(list, "auto", null)!.Id);
        Assert.Equal("b", TaskActivityService.ChoosePriority(list, "b", AgentKind.Codex)!.Id);
        Assert.Equal("b", TaskActivityService.ChoosePriority(list, "claude", null)!.Id);
    }

    [Fact]
    public void RunningLocalInference_WinsOverOtherRunning_ButNotOverWaiting()
    {
        var list = new[] { S(AgentKind.Claude, "c", AgentState.Running, 0), S(AgentKind.Local, "l", AgentState.Running, 1) };
        Assert.Equal("l", TaskActivityService.ChoosePriority(list, "auto", AgentKind.Claude)!.Id);
        var withWaiting = list.Append(S(AgentKind.Codex, "w", AgentState.WaitingApproval, 5));
        Assert.Equal("w", TaskActivityService.ChoosePriority(withWaiting, "auto", null)!.Id);
    }

    [Fact]
    public void ExpiredQuotaWindowIsNotShownAsCurrent()
    {
        var w = new QuotaWindow(AgentKind.Codex, "5h", 90, DateTimeOffset.Now.AddMinutes(-1), 300, DateTimeOffset.Now.AddHours(-2), "t");
        Assert.Equal("—", QuotaService.Text(w, true, DateTimeOffset.Now));
        Assert.Null(QuotaService.Value(w, true, DateTimeOffset.Now));
    }
}

public class ClaudeSettingsMergerTests
{
    private const string Collector = @"C:\Users\u\AppData\Local\Programs\WinStatusBar\WinStatusBar.Collector.exe";

    private const string Existing = """
    {
      "agentPushNotifEnabled": true,
      "statusLine": { "type": "command", "command": "\"C:/Windows/System32/cmd.exe\" --statusline", "padding": 0 },
      "hooks": { "Stop": [ { "hooks": [ { "type": "command", "command": "notify.exe done" } ] } ] },
      "permissions": { "allow": ["Bash(git status)"] }
    }
    """;

    [Fact]
    public void Install_PreservesExistingSettingsHooksAndStatusLine()
    {
        var o = (JsonObject)JsonNode.Parse(Existing)!;
        ClaudeSettingsMerger.Install(o, Collector);
        Assert.True(o["agentPushNotifEnabled"]!.GetValue<bool>());
        Assert.Equal("Bash(git status)", o["permissions"]!["allow"]![0]!.GetValue<string>());
        var stop = o["hooks"]!["Stop"]!.AsArray();
        Assert.Equal(2, stop.Count); // o hook do usuário continua lá
        Assert.Contains("notify.exe", stop[0]!.ToJsonString());
        string cmd = o["statusLine"]!["command"]!.GetValue<string>();
        Assert.Contains("WinStatusBar.Collector.exe", cmd);
        Assert.Equal(0, o["statusLine"]!["padding"]!.GetValue<int>());
        var original = ClaudeSettingsMerger.DecodeOriginal(cmd)!;
        Assert.Equal("\"C:/Windows/System32/cmd.exe\" --statusline", original["command"]!.GetValue<string>());
        Assert.True(ClaudeSettingsMerger.IsInstalled(o));
        foreach (var ev in ClaudeSettingsMerger.HookEvents) Assert.NotNull(o["hooks"]![ev]);
    }

    [Fact]
    public void Install_CommandsAreShellNeutral()
    {
        // PowerShell (usado pelo Claude Code quando não há Git Bash) rejeita "C:/x.exe" arg; sem espaços, nada de aspas.
        var o = new JsonObject();
        ClaudeSettingsMerger.Install(o, Collector);
        string hook = o["hooks"]!["PreToolUse"]![0]!["hooks"]![0]!["command"]!.GetValue<string>();
        Assert.Equal("C:/Users/u/AppData/Local/Programs/WinStatusBar/WinStatusBar.Collector.exe", hook);
        Assert.StartsWith("C:/Users/u/AppData/Local/Programs/WinStatusBar/WinStatusBar.Collector.exe statusline", o["statusLine"]!["command"]!.GetValue<string>());
    }

    [Fact]
    public void MissingOriginalStatusLine_IsDroppedAndDetected()
    {
        string missing = @"C:/Nao/Existe/TrafficMonitor/cota-codex/CotaStatusLine.exe";
        Assert.True(ClaudeSettingsMerger.CommandTargetMissing($"\"{missing}\" --statusline"));
        Assert.False(ClaudeSettingsMerger.CommandTargetMissing("node statusline.js"));            // sem caminho absoluto
        Assert.False(ClaudeSettingsMerger.CommandTargetMissing($"\"{Environment.ProcessPath}\" x")); // existe

        // Instalação sobre statusLine quebrada: não a encadeia (nem a restauraria depois)
        var o = new JsonObject { ["statusLine"] = new JsonObject { ["type"] = "command", ["command"] = $"\"{missing}\" --statusline" } };
        ClaudeSettingsMerger.Install(o, Collector);
        Assert.DoesNotContain("--orig", o["statusLine"]!["command"]!.GetValue<string>());

        // Integração já instalada encadeando um original que depois sumiu: detectado para reparo
        var orig = new JsonObject { ["type"] = "command", ["command"] = $"\"{missing}\" --statusline" };
        string b64 = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(orig.ToJsonString()));
        var installed = new JsonObject { ["statusLine"] = new JsonObject { ["type"] = "command", ["command"] = $"C:/x/WinStatusBar.Collector.exe statusline --orig {b64}" } };
        Assert.True(ClaudeSettingsMerger.HasBrokenOriginal(installed));
    }

    [Fact]
    public void Install_IsIdempotent()
    {
        var o = (JsonObject)JsonNode.Parse(Existing)!;
        ClaudeSettingsMerger.Install(o, Collector);
        string first = o.ToJsonString();
        ClaudeSettingsMerger.Install(o, Collector);
        Assert.Equal(first, o.ToJsonString());
    }

    [Fact]
    public void Uninstall_RestoresOriginalExactly()
    {
        var o = (JsonObject)JsonNode.Parse(Existing)!;
        string before = o.ToJsonString();
        ClaudeSettingsMerger.Install(o, Collector);
        ClaudeSettingsMerger.Uninstall(o);
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(before), o));
    }

    [Fact]
    public void Uninstall_WithoutPreviousStatusLine_RemovesIt()
    {
        var o = new JsonObject { ["theme"] = "dark" };
        ClaudeSettingsMerger.Install(o, Collector);
        ClaudeSettingsMerger.Uninstall(o);
        Assert.True(JsonNode.DeepEquals(new JsonObject { ["theme"] = "dark" }, o));
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern int GetShortPathNameW(string longPath, System.Text.StringBuilder shortPath, int size);

    /// <summary>Pasta temporária COM ESPAÇO no nome e um coletor de mentira dentro (o caso do perfil "Nome Sobrenome").</summary>
    private static string CollectorInFolderWithSpace(out string dir)
    {
        dir = Path.Combine(Path.GetTempPath(), "wsb merger " + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string collector = Path.Combine(dir, ClaudeSettingsMerger.Marker);
        File.WriteAllText(collector, string.Empty);
        return collector;
    }

    [Fact]
    public void Install_PathWithSpace_KeepsMarkerAndIsIdempotent()
    {
        // Vale com nomes 8.3 ligados (pasta curta + nome longo do arquivo) ou desligados (caminho entre aspas).
        string collector = CollectorInFolderWithSpace(out string dir);
        try
        {
            var o = (JsonObject)JsonNode.Parse(Existing)!;
            string before = o.ToJsonString();
            ClaudeSettingsMerger.Install(o, collector);
            string first = o.ToJsonString();
            string hook = o["hooks"]!["PreToolUse"]![0]!["hooks"]![0]!["command"]!.GetValue<string>();
            Assert.Contains(ClaudeSettingsMerger.Marker, hook);
            Assert.Contains(ClaudeSettingsMerger.Marker, o["statusLine"]!["command"]!.GetValue<string>());
            Assert.True(ClaudeSettingsMerger.IsInstalled(o));
            Assert.False(ClaudeSettingsMerger.HasBrokenOriginal(o));

            ClaudeSettingsMerger.Install(o, collector);
            Assert.Equal(first, o.ToJsonString());
            Assert.Equal(2, o["hooks"]!["Stop"]!.AsArray().Count); // o do usuário + um só do WinStatusBar
            Assert.Single(o["hooks"]!["PreToolUse"]!.AsArray());

            ClaudeSettingsMerger.Uninstall(o);
            Assert.True(JsonNode.DeepEquals(JsonNode.Parse(before), o));
        }
        finally { try { Directory.Delete(dir, true); } catch (IOException) { } }
    }

    /// <summary>Estado deixado pelo defeito: a cada conferência, 14 grupos a mais e a própria statusLine guardada como "original".</summary>
    private static JsonObject Damaged(string command, int cycles)
    {
        var o = (JsonObject)JsonNode.Parse(Existing)!;
        var status = (JsonObject)o["statusLine"]!.DeepClone();
        var hooks = (JsonObject)o["hooks"]!;
        for (int cycle = 0; cycle < cycles; cycle++)
        {
            foreach (var ev in ClaudeSettingsMerger.HookEvents)
            {
                if (hooks[ev] is not JsonArray arr) { arr = new JsonArray(); hooks[ev] = arr; }
                arr.Add(new JsonObject { ["hooks"] = new JsonArray(new JsonObject { ["type"] = "command", ["command"] = command, ["async"] = true, ["timeout"] = 10 }) });
            }
            string b64 = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(status.ToJsonString()));
            status = new JsonObject { ["type"] = "command", ["command"] = $"{command} statusline --orig {b64}", ["padding"] = 0 };
        }
        o["statusLine"] = status;
        return o;
    }

    [Fact]
    public void Uninstall_UnwrapsNestedOwnStatusLines()
    {
        var o = Damaged("C:/x/WinStatusBar.Collector.exe", cycles: 4);
        Assert.Equal(5, o["hooks"]!["Stop"]!.AsArray().Count);
        Assert.True(ClaudeSettingsMerger.HasBrokenOriginal(o)); // o reparo automático da barra reinstala
        ClaudeSettingsMerger.Uninstall(o);
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(Existing), o));
    }

    [Fact]
    public void LegacyShortPathCommands_AreRecognisedAndHealed()
    {
        string collector = CollectorInFolderWithSpace(out string dir);
        try
        {
            var sb = new System.Text.StringBuilder(1024);
            int n = GetShortPathNameW(collector, sb, sb.Capacity);
            string legacy = sb.ToString().Replace('\\', '/');
            // Sem nomes 8.3 neste volume, as versões antigas nunca gravaram o comando sem o marcador: nada a curar.
            if (n <= 0 || n >= sb.Capacity || legacy.Contains(' ') || legacy.Contains(ClaudeSettingsMerger.Marker, StringComparison.OrdinalIgnoreCase)) return;

            // Como as versões antigas gravavam: o caminho inteiro em 8.3 (…/WINSTA~1.EXE), sem o marcador.
            var o = Damaged(legacy, cycles: 3);
            Assert.DoesNotContain(ClaudeSettingsMerger.Marker, o.ToJsonString(), StringComparison.OrdinalIgnoreCase);
            Assert.True(ClaudeSettingsMerger.IsInstalled(o));
            Assert.True(ClaudeSettingsMerger.HasBrokenOriginal(o));
            ClaudeSettingsMerger.Uninstall(o);
            Assert.True(JsonNode.DeepEquals(JsonNode.Parse(Existing), o)); // a statusLine verdadeira e só o hook do usuário

            // Reinstalar por cima do arquivo danificado dá o mesmo que instalar num arquivo limpo.
            var healed = Damaged(legacy, cycles: 3);
            ClaudeSettingsMerger.Install(healed, collector);
            var clean = (JsonObject)JsonNode.Parse(Existing)!;
            ClaudeSettingsMerger.Install(clean, collector);
            Assert.True(JsonNode.DeepEquals(clean, healed));
            Assert.Contains(ClaudeSettingsMerger.Marker, healed["statusLine"]!["command"]!.GetValue<string>());
            Assert.False(ClaudeSettingsMerger.HasBrokenOriginal(healed));
        }
        finally { try { Directory.Delete(dir, true); } catch (IOException) { } }
    }

    [Fact]
    public void PruneBackups_KeepsNewestFiveAndOnlyTouchesOwnBackups()
    {
        string dir = Path.Combine(Path.GetTempPath(), "wsb-merger-bak-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string file = Path.Combine(dir, "settings.json");
            File.WriteAllText(file, "{}");
            for (int i = 1; i <= 8; i++) File.WriteAllText(file + $".winstatusbar-backup-2026110{i}-120000", i.ToString());
            string[] others = ["settings.json.bak", "settings.local.json", "settings.json.backup-20260101-000000", "other.json.winstatusbar-backup-20260101-000000"];
            foreach (string other in others) File.WriteAllText(Path.Combine(dir, other), "x");

            ClaudeIntegrationInstaller.PruneBackups(file, keep: 5);

            var left = Directory.GetFiles(dir, "settings.json.winstatusbar-backup-*").Select(Path.GetFileName).OrderBy(x => x).ToArray();
            Assert.Equal(Enumerable.Range(4, 5).Select(i => $"settings.json.winstatusbar-backup-2026110{i}-120000"), left);
            Assert.True(File.Exists(file));
            foreach (string other in others) Assert.True(File.Exists(Path.Combine(dir, other)), other);
        }
        finally { try { Directory.Delete(dir, true); } catch (IOException) { } }
    }

    [Fact]
    public void PipeMessage_RoundTrip()
    {
        var m = new EventMessage { Event = "PreToolUse", SessionId = "s", Detail = "Editando a.cs…", FiveHourUsed = 10 };
        var json = JsonSerializer.Serialize(m, EventMessageContext.Default.EventMessage);
        Assert.DoesNotContain("\"prompt\"", json); // campos nulos não são enviados
        var back = JsonSerializer.Deserialize(json, EventMessageContext.Default.EventMessage)!;
        Assert.Equal("Editando a.cs…", back.Detail);
        Assert.Equal(10, back.FiveHourUsed);
    }
}
