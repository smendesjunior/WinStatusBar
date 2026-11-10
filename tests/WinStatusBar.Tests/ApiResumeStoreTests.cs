using WinStatusBar.Core.Ai;
using WinStatusBar.Core.Ai.Codex.ApiResume;
using Xunit;

namespace WinStatusBar.Tests;

public class ApiResumeStoreTests
{
    private static string TempRoot()
    {
        string dir = Path.Combine(Path.GetTempPath(), "wsb-store-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    internal static ApiResumeJob Job(ApiResumeState state = ApiResumeState.Queued, DateTimeOffset? endedAt = null)
    {
        var stop = CodexResumeCommandTests.Stop();
        var job = ApiResumeJob.From(stop, CodexResumeCommand.ToSpec(stop, _ => true, out _), CodexResumeCommandTests.At);
        job.State = state;
        job.EndedAt = endedAt;
        return job;
    }

    [Fact]
    public void SaveLoadRoundTripWithoutTheKey()
    {
        string root = TempRoot();
        try
        {
            var store = new ApiResumeStore(root);
            var job = Job(ApiResumeState.Running);
            job.Pid = 4242; job.ProcessCreatedAt = CodexResumeCommandTests.At.AddSeconds(5); job.Executable = @"C:\codex\codex.exe";
            job.EventsOffset = 120; job.RolloutOffset = 99999; job.TurnStarted = true; job.ApiTurnId = "turn-api-1";
            job.Tokens = new TokenUsage(120000, 98000, 3100, 12);
            job.Baseline = new DashboardSnapshot([new ApiUsageDay(new DateTimeOffset(2026, 11, 10, 0, 0, 0, TimeSpan.Zero), 1, 2, 0.5m, 3)], 10m, null, CodexResumeCommandTests.At, "org-teste");
            job.Spend = 0.21m; job.Summary = "Pronto"; job.Error = new AiErrorExplanation("chave recusada", "msg", "HTTP 401: sk-•••");
            job.AllowWhenQuotaBack = true; job.Interrupts = 2; job.Reminders = 1;
            store.Save([job]);

            var loaded = Assert.Single(store.Load());
            Assert.Equal((job.Id, job.ThreadId, job.Title, job.Model, job.Effort, job.Cwd), (loaded.Id, loaded.ThreadId, loaded.Title, loaded.Model, loaded.Effort, loaded.Cwd));
            Assert.Equal((ApiResumeState.Running, 4242, 120L, 99999L, true, "turn-api-1"), (loaded.State, loaded.Pid!.Value, loaded.EventsOffset, loaded.RolloutOffset, loaded.TurnStarted, loaded.ApiTurnId));
            Assert.Equal(job.Tokens, loaded.Tokens);
            Assert.Equal(10m, loaded.Baseline!.Balance);
            Assert.Equal(0.5m, Assert.Single(loaded.Baseline.Days).Cost);
            Assert.Equal(job.Error, loaded.Error);
            Assert.Equal([@"Z:\"], loaded.ExtraDirs);
            Assert.True(loaded.AllowWhenQuotaBack);
            Assert.Equal(job.ToSpec(), loaded.ToSpec() with { ExtraDirs = job.ExtraDirs });

            string text = File.ReadAllText(store.JobsFile);
            Assert.DoesNotContain("CODEX_API_KEY", text);
            Assert.DoesNotMatch(@"sk-[A-Za-z0-9_\-]{6,}", text);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void CorruptJobsJsonIsSetAside()
    {
        string root = TempRoot();
        try
        {
            var store = new ApiResumeStore(root);
            File.WriteAllText(store.JobsFile, "{not json");
            var log = new List<string>();
            Assert.Empty(store.Load(log.Add));
            Assert.False(File.Exists(store.JobsFile));
            Assert.Single(Directory.GetFiles(root, "jobs.corrompido-*.json"));
            Assert.Contains(log, l => l.StartsWith("Continuar pela API: jobs.json corrompido; posto de lado em ", StringComparison.Ordinal));
            Assert.Empty(new ApiResumeStore(Path.Combine(root, "vazio")).Load());
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void PruneKeepsUnfinishedAndTwentyNewest()
    {
        var now = CodexResumeCommandTests.At.AddDays(9);
        var jobs = Enumerable.Range(0, 25).Select(i => Job(ApiResumeState.Completed, now.AddHours(-i))).ToList();
        var old = Job(ApiResumeState.Failed, now.AddDays(-8));
        var running = Job(ApiResumeState.Running);
        jobs.Add(old); jobs.Add(running);
        var kept = ApiResumeStore.Prune(jobs, now);
        Assert.Equal(21, kept.Count);
        Assert.Contains(running, kept);
        Assert.DoesNotContain(old, kept);
        Assert.Contains(jobs[0], kept);
        Assert.DoesNotContain(jobs[24], kept);
    }

    [Fact]
    public void FoldersFollowTheJob()
    {
        string root = TempRoot();
        try
        {
            var store = new ApiResumeStore(root);
            Assert.Equal(Path.Combine(root, "a1", "events.jsonl"), store.EventsFile("a1"));
            Assert.Equal(Path.Combine(root, "a1", "stderr.txt"), store.StderrFile("a1"));
            Assert.Equal(Path.Combine(root, "jobs.json"), store.JobsFile);
            Directory.CreateDirectory(store.FolderOf("velho"));
            var keep = Job();
            Directory.CreateDirectory(store.FolderOf(keep.Id));
            store.DeleteOrphanFolders([keep]);
            Assert.False(Directory.Exists(store.FolderOf("velho")));
            Assert.True(Directory.Exists(store.FolderOf(keep.Id)));
        }
        finally { Directory.Delete(root, true); }
    }
}
