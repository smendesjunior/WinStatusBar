using System.Text.RegularExpressions;
using WinStatusBar.Core.Ai;
using WinStatusBar.Core.Ai.Codex;
using WinStatusBar.Core.Ai.Codex.ApiResume;
using Xunit;
using static WinStatusBar.Tests.CodexResumeCommandTests;

namespace WinStatusBar.Tests;

internal sealed class FakeHost : IApiResumeHost
{
    public static readonly string FakeKey = "sk-" + new string('k', 30);
    public DateTimeOffset Now { get; set; } = CodexQuotaBlockTests.Seen;
    public bool ScreenOn { get; set; } = true;
    public QuotaSnapshot? CodexQuota { get; set; } =
        CodexQuotaBlockTests.Snapshot(CodexQuotaBlockTests.Seen, CodexQuotaBlockTests.Official(CodexQuotaBlockTests.Seen, false, "workspace_owner_credits_depleted"));
    public DashboardSnapshot? Dashboard { get; set; }
    public string? Exe { get; set; } = @"C:\fake\codex.exe";
    public string? Key { get; set; } = FakeKey;
    public string? Config { get; set; }
    public ModelCheckResult ModelResult { get; set; } = ModelCheckResult.Ok;
    public List<(string Model, bool Fresh)> ModelChecks { get; } = [];
    public CodexModelLevels.Levels? LevelsValue { get; set; }
    public Func<string, bool> Dirs { get; set; } = _ => true;
    public RolloutTail? Tail { get; set; } = Supervisor.Tail(RolloutVerdict.QuotaStop, "turn-parado-1");
    public ThreadLockState Lock { get; set; } = ThreadLockState.Free;
    public int Probes { get; private set; }
    public bool ChatGpt { get; set; }
    public TaskCompletionSource ChatGptExit { get; set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TokenUsage? TurnTokens { get; set; }
    public int Refreshes { get; private set; }
    public InterruptOutcome InterruptResult { get; set; } = InterruptOutcome.Sent;
    public List<int> Interrupts { get; } = [];
    public Action? OnInterrupt { get; set; }
    public List<(string ThreadId, string? Note, DateTimeOffset? KeepUntil)> Notes { get; } = [];
    public List<ApiResumeNotice> Notices { get; } = [];
    public List<string> Logs { get; } = [];

    public string? FindCodexExecutable() => Exe;
    public string? LoadKey(out string sourceName) { sourceName = "teste"; return Key; }
    public string? ReadCodexConfig() => Config;
    public Task<ModelCheck> CheckModelAsync(string model, string apiKey, bool fresh, CancellationToken ct)
    {
        ModelChecks.Add((model, fresh));
        return Task.FromResult(new ModelCheck(ModelResult, "org", ModelResult == ModelCheckResult.Ok ? null : "HTTP 500: x", Now));
    }
    public CodexModelLevels.Levels? Levels(string model) => LevelsValue;
    public bool DirectoryExists(string path) => Dirs(path);
    public RolloutTail? ReadTail(string rolloutPath) => Tail;
    public ThreadLockState ProbeLock(string threadId) { Probes++; return Lock; }
    public bool ChatGptAppRunning() => ChatGpt;
    public Task WaitChatGptExitAsync(CancellationToken ct) => ChatGptExit.Task;
    public TokenUsage? ExecTurnTokens(string threadId, DateTimeOffset startedAt) => TurnTokens;
    public void RequestDashboardRefresh() => Refreshes++;
    public Task<InterruptOutcome> SendInterruptAsync(int pid, DateTimeOffset createdAt)
    {
        OnInterrupt?.Invoke();
        Interrupts.Add(pid);
        return Task.FromResult(InterruptResult);
    }
    public void SetNote(string threadId, string? note, DateTimeOffset? keepUntil) => Notes.Add((threadId, note, keepUntil));
    public void Notify(ApiResumeNotice notice) => Notices.Add(notice);
    public void Log(string line) => Logs.Add(line);
}

internal sealed class FakeLauncher(FakeHost host) : IApiResumeLauncher
{
    public sealed record Started(int Pid, string Exe, IReadOnlyList<string> Args, string Cwd, string Stdout, string Stderr, IReadOnlyDictionary<string, string?> Env, DateTimeOffset CreatedAt);
    public List<Started> Starts { get; } = [];
    public HashSet<int> Alive { get; } = [];
    public Dictionary<int, DateTimeOffset> Created { get; } = [];
    public int Kills { get; private set; }
    private int _next = 100;

    public DetachedStart Start(string exe, IReadOnlyList<string> args, string cwd, string stdoutFile, string stderrFile, IReadOnlyDictionary<string, string?> env)
    {
        int pid = _next++;
        Directory.CreateDirectory(Path.GetDirectoryName(stdoutFile)!);
        File.WriteAllText(stdoutFile, "");
        File.WriteAllText(stderrFile, "");
        Starts.Add(new(pid, exe, args, cwd, stdoutFile, stderrFile, new Dictionary<string, string?>(env), host.Now));
        Alive.Add(pid);
        Created[pid] = host.Now;
        return new DetachedStart(pid, host.Now);
    }
    public bool IsSame(int pid, DateTimeOffset createdAt) => Alive.Contains(pid) && Created.TryGetValue(pid, out var c) && c == createdAt;
    public Task WaitForExitAsync(int pid, DateTimeOffset createdAt, CancellationToken ct) => Task.CompletedTask;
    public bool KillTree(int pid, DateTimeOffset createdAt)
    {
        if (!IsSame(pid, createdAt)) return false;
        Kills++;
        Alive.Remove(pid);
        return true;
    }
    public Started Last => Starts[^1];
    public void Emit(string line, Started? process = null) => File.AppendAllText((process ?? Last).Stdout, line + "\n");
    public void Error(string text, Started? process = null) => File.AppendAllText((process ?? Last).Stderr, text + "\n");
    public void Exit(Started? process = null) => Alive.Remove((process ?? Last).Pid);
}

internal sealed class Supervisor : IDisposable
{
    public readonly string Root = Path.Combine(Path.GetTempPath(), "wsb-sup-" + Guid.NewGuid().ToString("N"));
    public readonly FakeHost Host = new();
    public readonly FakeLauncher Launcher;
    public readonly ApiResumeStore Store;
    public readonly ApiResumeSupervisor Sup;

    public Supervisor()
    {
        Directory.CreateDirectory(Root);
        Launcher = new FakeLauncher(Host);
        Store = new ApiResumeStore(Root);
        Sup = new ApiResumeSupervisor(Host, Launcher, Store) { ManualTicks = true };
    }

    public static RolloutTail Tail(RolloutVerdict verdict, string turn, DateTimeOffset? at = null) =>
        new(verdict, turn, at ?? At, null, null, null, null, null, Id1, "vscode", "Codex Desktop", "user", [], 1234);

    public Task Tick(TimeSpan? advance = null)
    {
        if (advance is { } a) Host.Now += a;
        return Sup.TickAsync();
    }

    public async Task Ticks(int n, TimeSpan step) { for (int i = 0; i < n; i++) await Tick(step); }
    public ApiResumeJob Job(int i = 0) => Sup.Jobs.OrderBy(j => j.CreatedAt).ThenBy(j => j.Title).ElementAt(i);
    public ApiResumeJob JobOf(string thread) => Sup.Jobs.Single(j => j.ThreadId == thread);
    public void Dispose() { try { Directory.Delete(Root, true); } catch (IOException) { } }

    public static QuotaStop StopB(string id = Id2, string title = "Responder saudação", int minutesBefore = 10) =>
        Stop(id: id, title: title) with { StoppedAt = At.AddMinutes(-minutesBefore) };
}

public class ApiResumeSupervisorTests
{
    private static readonly TimeSpan S1 = TimeSpan.FromSeconds(1), S5 = TimeSpan.FromSeconds(5), M1 = TimeSpan.FromMinutes(1);

    private static async Task<Supervisor> Running()
    {
        var t = new Supervisor();
        Assert.Equal(1, t.Sup.Enqueue([Stop()], parallel: false));
        await t.Tick();
        Assert.Equal(ApiResumeState.Running, t.Job().State);
        return t;
    }

    // ---- regra 1 ----
    [Fact]
    public async Task StoppingIsSavedBeforeCtrlC()
    {
        using var t = await Running();
        string? saved = null;
        t.Host.OnInterrupt = () => saved = File.ReadAllText(t.Store.JobsFile);
        t.Sup.Stop(t.Job().Id);
        await t.Tick();
        Assert.Contains("\"State\": \"Stopping\"", saved);
        Assert.Single(t.Host.Interrupts);
    }

    [Fact]
    public void PollIntervalFollowsScreen()
    {
        Assert.Equal(TimeSpan.FromSeconds(2), ApiResumeSupervisor.PollInterval(true));
        Assert.Equal(TimeSpan.FromSeconds(10), ApiResumeSupervisor.PollInterval(false));
    }

    // ---- regra 2 ----
    [Fact]
    public async Task RepeatedRequestIsIgnored()
    {
        using var t = new Supervisor();
        Assert.Equal(1, t.Sup.Enqueue([Stop()], false));
        Assert.Equal(0, t.Sup.Enqueue([Stop()], false));
        await t.Tick();
        Assert.Single(t.Sup.Jobs);
        Assert.Single(t.Host.Logs, l => Regex.IsMatch(l, @"^Continuar pela API: fila \+1 conversa=\S+ modelo=\S+ nível=\S+ acesso=\S+ aprovação=\S+$"));
    }

    [Fact]
    public void EnqueueBlocksAnInvalidTurnAndOrdersNewestFirst()
    {
        using var t = new Supervisor();
        Assert.Equal(2, t.Sup.Enqueue([Supervisor.StopB(minutesBefore: 30), Stop(model: "codexias-claude-opus")], false));
        Assert.Equal(ApiResumeState.Blocked, t.JobOf(Id1).State);
        Assert.Equal("modelo da barra", t.JobOf(Id1).Error!.Short);
        Assert.Contains(t.Host.Logs, l => l == $"Continuar pela API: bloqueada conversa={Id1} motivo=modelo da barra");
        Assert.True(t.JobOf(Id1).CreatedAt <= t.JobOf(Id2).CreatedAt);
    }

    // ---- regra 3 ----
    [Fact]
    public void GateNeedsAnOfficialRead()
    {
        var seen = CodexQuotaBlockTests.Seen;
        Assert.Equal(QuotaGate.Unknown, ApiResumeSupervisor.Gate(null, seen));
        Assert.Equal(QuotaGate.Unknown, ApiResumeSupervisor.Gate(CodexQuotaBlockTests.Snapshot(seen, null, used7d: 40), seen));
        Assert.Equal(QuotaGate.Blocked, ApiResumeSupervisor.Gate(CodexQuotaBlockTests.Snapshot(seen, CodexQuotaBlockTests.Official(seen, false)), seen));
        Assert.Equal(QuotaGate.Back, ApiResumeSupervisor.Gate(CodexQuotaBlockTests.Snapshot(seen, CodexQuotaBlockTests.Official(seen, true), used7d: 10), seen));
    }

    [Fact]
    public async Task RestartWithUnknownQuotaKeepsTheQueue()
    {
        using var t = new Supervisor();
        t.Host.CodexQuota = null;
        t.Sup.Enqueue([Stop(), Supervisor.StopB()], false);
        await t.Ticks(2, S5);
        t.Host.CodexQuota = CodexQuotaBlockTests.Snapshot(t.Host.Now, null, used7d: 40);
        await t.Sup.StartAsync(CancellationToken.None);
        await t.Ticks(5, S5);
        Assert.All(t.Sup.Jobs, j => Assert.Equal(ApiResumeState.Queued, j.State));
        Assert.Empty(t.Launcher.Starts);
        Assert.DoesNotContain(t.Host.Notices, n => n.Message.Contains("A cota do Codex voltou"));
        t.Host.CodexQuota = CodexQuotaBlockTests.Snapshot(t.Host.Now, CodexQuotaBlockTests.Official(t.Host.Now, false, "workspace_owner_credits_depleted"));
        await t.Tick(S5);
        Assert.Single(t.Launcher.Starts);
    }

    // ---- regra 4 ----
    [Fact]
    public async Task NoKeyBlocksAndStartsNothing()
    {
        using var t = new Supervisor();
        t.Host.Key = null;
        t.Sup.Enqueue([Stop()], false);
        await t.Tick();
        Assert.Equal((ApiResumeState.Blocked, "sem chave da API"), (t.Job().State, t.Job().Error!.Short));
        Assert.Empty(t.Launcher.Starts);
        Assert.Contains(t.Host.Logs, l => Regex.IsMatch(l, @"^Continuar pela API: bloqueada conversa=\S+ motivo=sem chave da API$"));
    }

    [Fact]
    public async Task MissingCodexOrForcedLoginBlocks()
    {
        using var noExe = new Supervisor();
        noExe.Host.Exe = @"C:\fake\codex.cmd";
        noExe.Sup.Enqueue([Stop()], false);
        await noExe.Tick();
        Assert.Equal("Codex não encontrado", noExe.Job().Error!.Short);

        using var forced = new Supervisor();
        forced.Host.Config = "forced_login_method = \"chatgpt\"\n";
        forced.Sup.Enqueue([Stop()], false);
        await forced.Tick();
        Assert.Equal("login obrigatório pelo ChatGPT", forced.Job().Error!.Short);
        Assert.Empty(forced.Launcher.Starts);
    }

    [Fact]
    public async Task MissingModelBlocksNeverAnotherModel()
    {
        using var t = new Supervisor();
        t.Host.ModelResult = ModelCheckResult.ModelNotFound;
        t.Sup.Enqueue([Stop()], false);
        await t.Tick();
        Assert.Equal((ApiResumeState.Blocked, "modelo fora da API"), (t.Job().State, t.Job().Error!.Short));
        Assert.All(t.Host.ModelChecks, c => Assert.Equal("gpt-5.6-sol", c.Model));
        Assert.Empty(t.Launcher.Starts);
    }

    [Fact]
    public async Task ModelCheckedAgainAtStart()
    {
        using var t = new Supervisor();
        t.Host.Lock = ThreadLockState.Held;
        t.Sup.Enqueue([Stop()], false);
        await t.Tick();
        Assert.Equal(ApiResumeState.WaitingForApp, t.Job().State);
        t.Host.Lock = ThreadLockState.Free;
        await t.Tick(TimeSpan.FromMinutes(30));
        Assert.Equal(ApiResumeState.Running, t.Job().State);
        Assert.Equal(2, t.Host.ModelChecks.Count(c => c.Fresh));
    }

    [Fact]
    public async Task ChangedRolloutBecomesAlreadyContinued()
    {
        using var t = new Supervisor();
        t.Host.Tail = Supervisor.Tail(RolloutVerdict.Completed, "t2", new DateTimeOffset(new DateTime(2026, 11, 10, 9, 0, 0, DateTimeKind.Local)));
        t.Sup.Enqueue([Stop()], false);
        await t.Tick();
        Assert.Equal(ApiResumeState.AlreadyContinued, t.Job().State);
        Assert.Contains("10/11 09:00", t.Job().Error!.Message);
        Assert.Empty(t.Launcher.Starts);
    }

    [Fact]
    public async Task QuotaStopInANewTurnAsksToReopen()
    {
        using var t = new Supervisor();
        t.Host.Tail = Supervisor.Tail(RolloutVerdict.QuotaStop, "t-app-2");
        t.Sup.Enqueue([Stop()], false);
        await t.Tick();
        Assert.Equal("parou de novo pela cota", t.Job().Error!.Short);
        Assert.Empty(t.Launcher.Starts);
    }

    // ---- regra 5 ----
    [Fact]
    public async Task HeldLockWaitsAndStartsWhenReleased()
    {
        using var t = new Supervisor();
        t.Host.Lock = ThreadLockState.Held;
        t.Sup.Enqueue([Stop()], false);
        await t.Tick();
        Assert.StartsWith("Outro programa do Codex", t.Job().Detail);
        Assert.Empty(t.Launcher.Starts);
        t.Host.Lock = ThreadLockState.Free;
        await t.Tick(S5);
        Assert.Equal(ApiResumeState.Running, t.Job().State);
        Assert.Contains(t.Host.Logs, l => l == $"Continuar pela API: trava liberada conversa={Id1}");
    }

    [Fact]
    public async Task WaitsForChatGptExitWithoutPolling()
    {
        using var t = new Supervisor();
        t.Host.Lock = ThreadLockState.Held;
        t.Host.ChatGpt = true;
        t.Sup.Enqueue([Stop()], false);
        await t.Ticks(10, M1);
        Assert.Equal(1, t.Host.Probes);
        Assert.StartsWith("Feche o app do Codex por completo", t.Job().Detail);
        t.Host.ChatGpt = false;
        t.Host.Lock = ThreadLockState.Free;
        t.Host.ChatGptExit.SetResult();
        await t.Tick(S1);
        await t.Tick(S1);
        Assert.Equal(ApiResumeState.Running, t.Job().State);
    }

    [Fact]
    public async Task WaitingRemindersAt30MinThenEvery2h()
    {
        using var t = new Supervisor();
        t.Host.Lock = ThreadLockState.Held;
        t.Sup.Enqueue([Stop()], false);
        await t.Tick();
        int Count() => t.Host.Notices.Count;
        Assert.Equal(1, Count());
        await t.Tick(TimeSpan.FromMinutes(29));
        Assert.Equal(1, Count());
        await t.Tick(M1);
        Assert.Equal(2, Count());
        await t.Tick(TimeSpan.FromMinutes(119));
        Assert.Equal(2, Count());
        await t.Tick(M1);
        Assert.Equal(3, Count());
        Assert.Single(t.Host.Logs, l => l.StartsWith("Continuar pela API: aguardando o app do Codex soltar a conversa", StringComparison.Ordinal));
    }

    // ---- regra 6 ----
    [Fact]
    public async Task KeyNeverStoredOrLogged()
    {
        using var t = await Running();
        var env = t.Launcher.Last.Env;
        Assert.Equal(FakeHost.FakeKey, env["CODEX_API_KEY"]);
        Assert.True(env.ContainsKey("GEMINI_API_KEY") && env["GEMINI_API_KEY"] is null);
        Assert.DoesNotContain(FakeHost.FakeKey, File.ReadAllText(t.Store.JobsFile));
        Assert.DoesNotContain(t.Host.Logs, l => l.Contains(FakeHost.FakeKey));
        Assert.DoesNotContain(t.Launcher.Last.Args, a => a.Contains(FakeHost.FakeKey));
        Assert.Equal(["exec", "--json"], t.Launcher.Last.Args.Take(2));
        Assert.Contains(t.Host.Notes, n => n.ThreadId == Id1 && n.Note == "Pela API" && n.KeepUntil is null);
        Assert.Equal(t.Store.EventsFile(t.Job().Id), t.Launcher.Last.Stdout);
    }

    // ---- regra 7 ----
    [Fact]
    public async Task SpendUpdatedEveryThreeMinutes()
    {
        using var t = new Supervisor();
        t.Host.Dashboard = new DashboardSnapshot([], 10.00m, null, t.Host.Now);
        t.Sup.Enqueue([Stop()], false);
        await t.Tick();
        t.Host.Dashboard = new DashboardSnapshot([], 9.79m, null, t.Host.Now);
        await t.Tick(TimeSpan.FromSeconds(179));
        Assert.Null(t.Job().Spend);
        await t.Tick(S1);
        Assert.Equal(0.21m, t.Job().Spend);
    }

    /// <summary>Rodada de 10/11/2026: ao vivo o gasto era 0; no fim o painel já tinha as requisições, mas ainda sem custo.
    /// O cartão não pode ficar em US$ 0,00: fica "não informado" até o painel trazer o custo.</summary>
    [Fact]
    public async Task FinalSpendStaysUnknownUntilTheDashboardHasTheCost()
    {
        using var t = new Supervisor();
        t.Host.Dashboard = new DashboardSnapshot([new ApiUsageDay(new DateTimeOffset(t.Host.Now.UtcDateTime.Date, TimeSpan.Zero), 0, 0, 0m, 17)], 71.87m, null, t.Host.Now);
        t.Sup.Enqueue([Stop()], false);
        await t.Tick();
        await t.Tick(TimeSpan.FromMinutes(3));
        Assert.Equal(0m, t.Job().Spend);
        t.Host.Dashboard = new DashboardSnapshot([new ApiUsageDay(new DateTimeOffset(t.Host.Now.UtcDateTime.Date, TimeSpan.Zero), 0, 0, 0m, 20)], 71.87m, null, t.Host.Now);
        t.Launcher.Emit("""{"type":"turn.completed","usage":{"input_tokens":1,"cached_input_tokens":0,"output_tokens":1}}""");
        t.Launcher.Exit();
        await t.Tick(S1);
        Assert.Null(t.Job().Spend);
        t.Host.Dashboard = new DashboardSnapshot([new ApiUsageDay(new DateTimeOffset(t.Host.Now.UtcDateTime.Date, TimeSpan.Zero), 0, 0, 0.40m, 20)], 71.87m, null, t.Host.Now);
        await t.Tick(TimeSpan.FromMinutes(60));
        Assert.Equal(0.40m, t.Job().Spend);
    }

    [Fact]
    public async Task EventsAndTokensFollowTheRun()
    {
        using var t = await Running();
        t.Launcher.Emit("""{"type":"turn.started"}""");
        t.Launcher.Emit("""{"type":"item.started","item":{"type":"command_execution","command":"dir"}}""");
        t.Host.TurnTokens = new TokenUsage(1000, 900, 10, 0);
        await t.Tick(TimeSpan.FromSeconds(2));
        Assert.True(t.Job().TurnStarted);
        Assert.Equal("Executando: dir", t.Job().Detail);
        Assert.Equal(new TokenUsage(1000, 900, 10, 0), t.Job().Tokens);
    }

    // ---- regra 8 ----
    [Fact]
    public async Task CompletedTurnEndsWithNoticeNoteAndLog()
    {
        using var t = await Running();
        t.Launcher.Emit("""{"type":"turn.started"}""");
        t.Launcher.Emit("""{"type":"item.completed","item":{"type":"agent_message","text":"pronto"}}""");
        t.Launcher.Emit("""{"type":"turn.completed","usage":{"input_tokens":5000,"cached_input_tokens":4000,"output_tokens":50}}""");
        t.Launcher.Exit();
        t.Host.Tail = Supervisor.Tail(RolloutVerdict.Completed, "turn-api-1", t.Host.Now);
        await t.Tick(TimeSpan.FromMinutes(14));
        var job = t.Job();
        Assert.Equal((ApiResumeState.Completed, "turn-api-1", "pronto"), (job.State, job.ApiTurnId, job.Summary));
        Assert.Equal(new TokenUsage(5000, 4000, 50, 0), job.Tokens);
        Assert.Contains(t.Host.Notes, n => n.Note == "Pela API" && n.KeepUntil == job.EndedAt!.Value.AddMinutes(15));
        Assert.Contains(t.Host.Notices, n => n.ThreadId == Id1 && n.Message.StartsWith("Concluída pela API em 14 min"));
    }

    [Fact]
    public async Task WriterConflictBeforeTurnStartedWaitsAgain()
    {
        using var t = await Running();
        for (int i = 0; i < 2; i++)
        {
            t.Launcher.Error("Error: thread x already has an active writer (code -32600)");
            t.Launcher.Exit();
            await t.Tick(S5);
            Assert.Equal(ApiResumeState.WaitingForApp, t.Job().State);
            Assert.Null(t.Job().Error);
            await t.Tick(S5);
        }
        Assert.Equal(3, t.Launcher.Starts.Count);
        Assert.Contains(t.Host.Logs, l => l == "Continuar pela API: o app abriu a conversa antes do início (-32600); aguardando");
        Assert.Single(t.Host.Notices); // um balão por espera, não um por tentativa
    }

    [Fact]
    public async Task RepeatedWriterConflictsBackOff()
    {
        using var t = await Running();
        for (int i = 0; i < 3; i++)
        {
            t.Launcher.Error("Error: thread x already has an active writer (code -32600)");
            t.Launcher.Exit();
            await t.Tick(S5);
            if (i < 2) await t.Tick(S5);
        }
        Assert.Equal(3, t.Launcher.Starts.Count);
        await t.Ticks(5, S5); // 25 s: ainda esperando (recuo de 30 s depois de 3 conflitos seguidos)
        Assert.Equal(3, t.Launcher.Starts.Count);
        Assert.StartsWith("Outro programa do Codex", t.Job().Detail);
        await t.Tick(S5);
        Assert.Equal(4, t.Launcher.Starts.Count);
        t.Launcher.Error("Error: thread x already has an active writer (code -32600)");
        t.Launcher.Exit();
        await t.Tick(S5);
        await t.Tick(TimeSpan.FromSeconds(60));
        Assert.Equal(4, t.Launcher.Starts.Count); // 4º conflito: 2 min
        await t.Tick(TimeSpan.FromSeconds(60));
        Assert.Equal(5, t.Launcher.Starts.Count);
    }

    [Fact]
    public async Task NeverRetriesAfterTurnStarted()
    {
        using var t = await Running();
        t.Launcher.Emit("""{"type":"turn.started"}""");
        t.Launcher.Emit("""{"type":"turn.failed","error":{"message":"stream disconnected before completion"}}""");
        t.Launcher.Exit();
        await t.Ticks(5, S5);
        Assert.Equal((ApiResumeState.Failed, "sem conexão"), (t.Job().State, t.Job().Error!.Short));
        Assert.Single(t.Launcher.Starts);
    }

    [Fact]
    public async Task AbortedAfterStopBecomesStopped()
    {
        using var t = await Running();
        await t.Tick(TimeSpan.FromMinutes(6));
        t.Sup.Stop(t.Job().Id);
        await t.Tick();
        t.Launcher.Exit();
        t.Host.Tail = Supervisor.Tail(RolloutVerdict.Aborted, "turn-api-1", t.Host.Now);
        await t.Tick(S1);
        Assert.Equal(ApiResumeState.Stopped, t.Job().State);
        Assert.Contains(t.Host.Notices, n => n.Message == "Parada por você após 6 min; o que já foi feito ficou na conversa.");
    }

    [Fact]
    public async Task DashboardRefreshedAtFiveFifteenSixtyAndHundredEightyMinutes()
    {
        using var t = await Running();
        t.Launcher.Emit("""{"type":"turn.completed","usage":{"input_tokens":1,"cached_input_tokens":0,"output_tokens":1}}""");
        t.Launcher.Exit();
        await t.Tick(S1);
        var end = t.Job().EndedAt!.Value;
        async Task At(TimeSpan after) { t.Host.Now = end + after; await t.Sup.TickAsync(); }
        await At(new TimeSpan(0, 4, 59)); Assert.Equal(0, t.Host.Refreshes);
        await At(TimeSpan.FromMinutes(5)); Assert.Equal(1, t.Host.Refreshes);
        await At(TimeSpan.FromMinutes(14)); Assert.Equal(1, t.Host.Refreshes);
        await At(TimeSpan.FromMinutes(15)); Assert.Equal(2, t.Host.Refreshes);
        await At(TimeSpan.FromMinutes(59)); Assert.Equal(2, t.Host.Refreshes);
        await At(TimeSpan.FromMinutes(60)); Assert.Equal(3, t.Host.Refreshes);
        await At(TimeSpan.FromMinutes(179)); Assert.Equal(3, t.Host.Refreshes);
        await At(TimeSpan.FromMinutes(180)); Assert.Equal(4, t.Host.Refreshes);
        await At(TimeSpan.FromMinutes(600)); Assert.Equal(4, t.Host.Refreshes);
    }

    [Fact]
    public async Task QueueOneAtATimeAndParallel()
    {
        using var q = new Supervisor();
        q.Sup.Enqueue([Stop(), Supervisor.StopB()], false);
        await q.Tick();
        Assert.Single(q.Launcher.Starts);
        q.Launcher.Emit("""{"type":"turn.completed","usage":{"input_tokens":1,"cached_input_tokens":0,"output_tokens":1}}""");
        q.Launcher.Exit();
        q.Host.Tail = Supervisor.Tail(RolloutVerdict.QuotaStop, "turn-parado-1");
        await q.Tick(S1);
        await q.Tick(S1);
        Assert.Equal(2, q.Launcher.Starts.Count);

        using var p = new Supervisor();
        p.Sup.Enqueue([Stop(), Supervisor.StopB()], true);
        await p.Tick();
        Assert.Equal(2, p.Launcher.Starts.Count);
    }

    // ---- regra 9 ----
    [Fact]
    public async Task SecondInterruptAtFifteenSeconds()
    {
        using var t = await Running();
        t.Sup.Stop(t.Job().Id);
        await t.Tick();
        await t.Tick(TimeSpan.FromSeconds(14));
        Assert.Single(t.Host.Interrupts);
        await t.Tick(S1);
        Assert.Equal(2, t.Host.Interrupts.Count);
        await t.Tick(TimeSpan.FromSeconds(15));
        Assert.True(t.Job().ForceOffered);
        Assert.Equal("não parou", t.Job().Error!.Short);
        Assert.Equal(0, t.Launcher.Kills);
    }

    [Fact]
    public async Task ForceKillIsOnlyOffered()
    {
        using var t = await Running();
        t.Sup.Stop(t.Job().Id);
        await t.Tick();
        t.Sup.ForceKill(t.Job().Id);
        await t.Tick(S1);
        Assert.Equal(0, t.Launcher.Kills);
        await t.Tick(TimeSpan.FromSeconds(30));
        t.Sup.ForceKill(t.Job().Id);
        await t.Tick(S1);
        Assert.Equal(1, t.Launcher.Kills);
    }

    [Fact]
    public async Task MissingHelperOffersForceKillAtOnce()
    {
        using var t = await Running();
        t.Host.InterruptResult = InterruptOutcome.HelperMissing;
        t.Sup.Stop(t.Job().Id);
        await t.Tick();
        Assert.True(t.Job().ForceOffered);
        Assert.Equal("parada segura indisponível", t.Job().Error!.Short);
    }

    // ---- regra 10 ----
    [Fact]
    public async Task QuotaBackStopsWaitingJobs()
    {
        using var t = new Supervisor();
        t.Sup.Enqueue([Stop()], false);
        await t.Tick();
        Assert.Equal(ApiResumeState.Running, t.JobOf(Id1).State);
        t.Sup.Enqueue([Supervisor.StopB(), Supervisor.StopB("01aa0000-0000-7000-8000-000000000003", "Terceira")], false);
        t.Host.Lock = ThreadLockState.Held;
        t.Sup.Enqueue([Supervisor.StopB("01aa0000-0000-7000-8000-000000000004", "Quarta")], true);
        await t.Tick(S1);
        Assert.Equal(ApiResumeState.WaitingForApp, t.JobOf("01aa0000-0000-7000-8000-000000000004").State);

        t.Host.CodexQuota = CodexQuotaBlockTests.Snapshot(t.Host.Now, CodexQuotaBlockTests.Official(t.Host.Now, true), used7d: 10);
        t.Sup.QuotaChanged();
        await t.Tick(S1);
        Assert.Equal(ApiResumeState.Running, t.JobOf(Id1).State);
        foreach (var id in new[] { Id2, "01aa0000-0000-7000-8000-000000000003", "01aa0000-0000-7000-8000-000000000004" })
            Assert.Equal("Cota do Codex liberada: aguardando você", ApiResumeText.State(t.JobOf(id), t.Host.Now));
        Assert.Single(t.Host.Notices, n => n.Message.Contains("3 conversa(s)"));

        t.Host.Lock = ThreadLockState.Free;
        t.Sup.AllowWhenQuotaBack(t.JobOf(Id2).Id);
        t.Sup.RemoveFromQueue(t.JobOf("01aa0000-0000-7000-8000-000000000003").Id);
        await t.Tick(S1);
        Assert.Single(t.Launcher.Starts);
        Assert.Equal(ApiResumeState.Removed, t.JobOf("01aa0000-0000-7000-8000-000000000003").State);
        t.Launcher.Emit("""{"type":"turn.completed","usage":{"input_tokens":1,"cached_input_tokens":0,"output_tokens":1}}""");
        t.Launcher.Exit();
        await t.Tick(S1);
        await t.Tick(S1);
        Assert.Equal(2, t.Launcher.Starts.Count);
        Assert.Equal(Id2, t.Launcher.Starts[1].Args[t.Launcher.Starts[1].Args.ToList().IndexOf("resume") + 1]);
        Assert.Single(t.Host.Notices, n => n.Message.Contains("A cota do Codex voltou"));
    }

    // ---- regra 11 ----
    [Fact]
    public async Task RetryAfterApiTurnFailedStartsAgain()
    {
        using var t = await Running();
        t.Launcher.Emit("""{"type":"turn.started"}""");
        t.Launcher.Emit("""{"type":"turn.failed","error":{"message":"stream disconnected before completion"}}""");
        t.Launcher.Exit();
        t.Host.Tail = Supervisor.Tail(RolloutVerdict.Failed, "turn-api-1");
        await t.Tick(S1);
        Assert.Equal(ApiResumeState.Failed, t.Job().State);
        t.Sup.Retry(t.Job().Id);
        await t.Tick(S1);
        Assert.Equal(2, t.Launcher.Starts.Count);
        Assert.Equal(ApiResumeState.Running, t.Job().State);
    }

    [Fact]
    public async Task ContinueAgainAfterCompletedWhileBlocked()
    {
        using var t = await Running();
        t.Launcher.Emit("""{"type":"turn.started"}""");
        t.Launcher.Emit("""{"type":"turn.completed","usage":{"input_tokens":1,"cached_input_tokens":0,"output_tokens":1}}""");
        t.Launcher.Exit();
        t.Host.Tail = Supervisor.Tail(RolloutVerdict.Completed, "turn-api-1", t.Host.Now);
        await t.Tick(S1);
        t.Sup.Retry(t.Job().Id);
        await t.Tick(S1);
        Assert.Equal(2, t.Launcher.Starts.Count);
    }

    [Fact]
    public async Task RetryAfterAnAppTurnIsAlreadyContinued()
    {
        using var t = await Running();
        t.Launcher.Emit("""{"type":"turn.started"}""");
        t.Launcher.Emit("""{"type":"turn.completed","usage":{"input_tokens":1,"cached_input_tokens":0,"output_tokens":1}}""");
        t.Launcher.Exit();
        t.Host.Tail = Supervisor.Tail(RolloutVerdict.Completed, "turn-api-1", t.Host.Now);
        await t.Tick(S1);
        t.Host.Tail = Supervisor.Tail(RolloutVerdict.Completed, "t-app", t.Host.Now);
        t.Sup.Retry(t.Job().Id);
        await t.Tick(S1);
        Assert.Equal(ApiResumeState.AlreadyContinued, t.Job().State);
        Assert.Single(t.Launcher.Starts);
    }

    // ---- regra 12 ----
    private static ApiResumeJob SavedRunning(Supervisor t, int pid, DateTimeOffset created)
    {
        var job = ApiResumeStoreTests.Job(ApiResumeState.Running);
        job.Pid = pid; job.ProcessCreatedAt = created; job.StartedAt = created; job.Executable = @"C:\fake\codex.exe";
        t.Store.Save([job]);
        return job;
    }

    [Fact]
    public async Task RestartWithLiveProcessReattachesFromSavedOffset()
    {
        using var t = new Supervisor();
        var created = t.Host.Now.AddMinutes(-3);
        var job = SavedRunning(t, 100, created);
        t.Launcher.Alive.Add(100); t.Launcher.Created[100] = created;
        string events = t.Store.EventsFile(job.Id);
        Directory.CreateDirectory(Path.GetDirectoryName(events)!);
        string first = "{\"type\":\"thread.started\"}\n{\"type\":\"turn.started\"}\n";
        File.WriteAllText(events, first);
        job.EventsOffset = first.Length;
        t.Store.Save([job]);
        File.AppendAllText(events, "{\"type\":\"item.started\",\"item\":{\"type\":\"command_execution\",\"command\":\"dir\"}}\n");

        await t.Sup.StartAsync(CancellationToken.None);
        await t.Tick();
        Assert.Empty(t.Launcher.Starts);
        Assert.Contains(t.Host.Logs, l => Regex.IsMatch(l, @"^Continuar pela API: reconectado pid=100 \(barra reiniciada\)$"));
        Assert.Equal("Executando: dir", t.Job().Detail);
        Assert.Equal(new FileInfo(events).Length, t.Job().EventsOffset);
    }

    [Fact]
    public async Task DeadProcessWithTaskCompleteIsCompleted()
    {
        using var t = new Supervisor();
        var created = t.Host.Now.AddMinutes(-20);
        SavedRunning(t, 100, created);
        t.Host.Tail = Supervisor.Tail(RolloutVerdict.Completed, "api1", created.AddMinutes(1));
        await t.Sup.StartAsync(CancellationToken.None);
        Assert.Equal(ApiResumeState.Completed, t.Job().State);
        Assert.Contains(t.Host.Notices, n => n.Message.StartsWith("Enquanto a barra estava fechada"));
    }

    [Fact]
    public async Task DeadProcessWithNothingFailsExplained()
    {
        using var t = new Supervisor();
        SavedRunning(t, 100, t.Host.Now.AddMinutes(-20));
        await t.Sup.StartAsync(CancellationToken.None);
        Assert.Equal((ApiResumeState.Failed, "parou sem registrar o fim"), (t.Job().State, t.Job().Error!.Short));
    }

    [Fact]
    public async Task ReusedPidIsTreatedAsEnded()
    {
        using var t = new Supervisor();
        var created = t.Host.Now.AddMinutes(-20);
        SavedRunning(t, 100, created);
        t.Launcher.Alive.Add(100); t.Launcher.Created[100] = created.AddMinutes(5); // outro processo com o mesmo pid
        await t.Sup.StartAsync(CancellationToken.None);
        Assert.True(t.Job().IsFinished);
        Assert.Equal(0, t.Launcher.Kills);
    }

    // ---- regras 13 e 14 ----
    [Fact]
    public async Task ShutdownNeverKills()
    {
        using var t = await Running();
        await t.Sup.ShutdownAsync();
        Assert.Equal(0, t.Launcher.Kills);
        Assert.Contains(t.Launcher.Last.Pid, t.Launcher.Alive);
        Assert.Equal(ApiResumeState.Running, Assert.Single(t.Store.Load()).State);
    }

    [Fact]
    public async Task LogLinesFollowTheSpec()
    {
        using var t = new Supervisor();
        t.Sup.Enqueue([Stop(), Supervisor.StopB()], true);
        await t.Tick();
        var first = t.Launcher.Starts.Single(s => s.Args.Contains(Id1));
        t.Sup.Stop(t.JobOf(Id1).Id);
        await t.Tick(S1);
        t.Launcher.Exit(first);
        t.Host.Tail = Supervisor.Tail(RolloutVerdict.Aborted, "turn-api-1", t.Host.Now);
        await t.Tick(S1);
        await t.Sup.ShutdownAsync();

        Assert.Contains(t.Host.Logs, l => Regex.IsMatch(l, @"^Continuar pela API: iniciado pid=\d+ conversa=\S+ exe=.*codex\.exe$"));
        Assert.Contains(t.Host.Logs, l => Regex.IsMatch(l, @"^Continuar pela API: Ctrl\+C enviado pid=\d+ resultado=Sent$"));
        Assert.Contains(t.Host.Logs, l => Regex.IsMatch(l, @"^Continuar pela API: fim conversa=\S+ resultado=Stopped duração=\S+ entrada=\d+ cache=\d+ saída=\d+ US\$=(n/d|[\d.,]+)$"));
        Assert.Contains(t.Host.Logs, l => Regex.IsMatch(l, @"^Continuar pela API: barra encerrando; .* segue rodando \(pid \d+\)$"));
    }
}
