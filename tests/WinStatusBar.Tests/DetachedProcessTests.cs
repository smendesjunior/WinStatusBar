using System.Collections;
using WinStatusBar.Core.Ai.Codex.ApiResume;
using Xunit;

namespace WinStatusBar.Tests;

public class DetachedProcessTests
{
    private static readonly string FakeKey = "sk-" + new string('k', 30);
    private static readonly string Cmd = Path.Combine(Environment.SystemDirectory, "cmd.exe");
    private static readonly string Ping = Path.Combine(Environment.SystemDirectory, "PING.EXE");

    private static string TempDir()
    {
        string dir = Path.Combine(Path.GetTempPath(), "wsb-detached-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static async Task<string> RunToEnd(string dir, IReadOnlyList<string> args, IReadOnlyDictionary<string, string?> env)
    {
        string stdout = Path.Combine(dir, "out.txt"), stderr = Path.Combine(dir, "err.txt");
        var start = DetachedProcess.Start(Cmd, args, dir, stdout, stderr, env);
        for (int i = 0; i < 100 && DetachedProcess.IsSame(start.Pid, start.CreatedAt, "cmd.exe"); i++) await Task.Delay(100);
        Assert.False(DetachedProcess.IsSame(start.Pid, start.CreatedAt, "cmd.exe"));
        return File.ReadAllText(stdout);
    }

    [Fact]
    public async Task ChildSeesTheKeyInItsEnvironment()
    {
        string dir = TempDir();
        try
        {
            string output = await RunToEnd(dir, ["/d", "/c", "echo %CODEX_API_KEY%"], DetachedProcess.ResumeEnvironment(FakeKey));
            Assert.Contains(FakeKey, output);
            Assert.Null(Environment.GetEnvironmentVariable("CODEX_API_KEY"));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void KeyIsNotOnTheCommandLine() =>
        Assert.DoesNotContain(FakeKey, WindowsCommandLine.Join(Cmd, ["/d", "/c", "echo %CODEX_API_KEY%"]));

    [Fact]
    public async Task GeminiKeyDoesNotReachTheChild()
    {
        string dir = TempDir();
        string? before = Environment.GetEnvironmentVariable("GEMINI_API_KEY");
        try
        {
            Environment.SetEnvironmentVariable("GEMINI_API_KEY", "gm-fake-123");
            string output = await RunToEnd(dir, ["/d", "/c", "echo [%GEMINI_API_KEY%]"], DetachedProcess.ResumeEnvironment(FakeKey));
            Assert.Contains("[%GEMINI_API_KEY%]", output);
            Assert.DoesNotContain("gm-fake-123", output);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GEMINI_API_KEY", before);
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public async Task ChildOutlivesTheBarHandles()
    {
        string dir = TempDir();
        try
        {
            var start = DetachedProcess.Start(Ping, ["-n", "30", "127.0.0.1"], dir, Path.Combine(dir, "out.txt"), Path.Combine(dir, "err.txt"),
                new Dictionary<string, string?>());
            try
            {
                await Task.Delay(1000);
                Assert.True(DetachedProcess.IsSame(start.Pid, start.CreatedAt, "PING.EXE"));
                Assert.False(DetachedProcess.IsSame(start.Pid, start.CreatedAt.AddMinutes(-1), "PING.EXE"));
                Assert.False(DetachedProcess.IsSame(start.Pid, start.CreatedAt, "codex.exe"));
                Assert.False(DetachedProcess.KillTree(start.Pid, start.CreatedAt, "codex.exe"));
                Assert.True(DetachedProcess.IsSame(start.Pid, start.CreatedAt, "PING.EXE"));
            }
            finally
            {
                Assert.True(DetachedProcess.KillTree(start.Pid, start.CreatedAt, "PING.EXE"));
            }
            for (int i = 0; i < 50 && DetachedProcess.IsSame(start.Pid, start.CreatedAt, "PING.EXE"); i++) await Task.Delay(100);
            Assert.False(DetachedProcess.IsSame(start.Pid, start.CreatedAt, "PING.EXE"));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void OnlyCodexExeIsAccepted()
    {
        Assert.Null(DetachedProcess.RequireCodexExe(@"C:\x\codex.cmd"));
        Assert.Equal(@"C:\x\codex.exe", DetachedProcess.RequireCodexExe(@"C:\x\codex.exe"));
        Assert.Null(DetachedProcess.RequireCodexExe(null));
    }

    [Fact]
    public void EnvironmentBlockDropsAndAdds()
    {
        IDictionary current = new Hashtable { ["PATH"] = "p", ["GEMINI_API_KEY"] = "g" };
        Assert.Equal("CODEX_API_KEY=k\0PATH=p\0\0", new string(DetachedProcess.EnvironmentBlock(current, DetachedProcess.ResumeEnvironment("k"))));
    }
}
