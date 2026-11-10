using System.Diagnostics;
using WinStatusBar.Core.Ai.Codex.ApiResume;
using Xunit;

namespace WinStatusBar.Tests;

public class ConsoleInterruptTests
{
    [Fact]
    public void HelperArgsRoundTrip()
    {
        var created = DateTimeOffset.FromFileTime(DateTimeOffset.Parse("2026-11-10T03:31:00.389Z").ToFileTime());
        Assert.True(ConsoleInterrupt.TryParse(ConsoleInterrupt.HelperArgs(4242, created), out int pid, out var at, out string image));
        Assert.Equal((4242, created, "codex.exe"), (pid, at, image));
        Assert.True(ConsoleInterrupt.TryParse(ConsoleInterrupt.HelperArgs(7, created, "PING.EXE"), out _, out _, out string ping));
        Assert.Equal("PING.EXE", ping);
        Assert.False(ConsoleInterrupt.TryParse(["--parar-codex", "x", "1"], out _, out _, out _));
        Assert.False(ConsoleInterrupt.TryParse(["--parar-codex", "1"], out _, out _, out _));
        Assert.False(ConsoleInterrupt.TryParse(["outro", "1", "2"], out _, out _, out _));
    }

    [Fact]
    public void ExitCodesMapBothWays()
    {
        foreach (var o in new[] { InterruptOutcome.Sent, InterruptOutcome.NotSame, InterruptOutcome.AttachFailed, InterruptOutcome.GenerateFailed })
            Assert.Equal(o, ConsoleInterrupt.FromExitCode(ConsoleInterrupt.ExitCode(o)));
        Assert.Equal((0, 3, 4, 5), (ConsoleInterrupt.ExitCode(InterruptOutcome.Sent), ConsoleInterrupt.ExitCode(InterruptOutcome.NotSame),
            ConsoleInterrupt.ExitCode(InterruptOutcome.AttachFailed), ConsoleInterrupt.ExitCode(InterruptOutcome.GenerateFailed)));
        Assert.Equal(InterruptOutcome.HelperFailed, ConsoleInterrupt.FromExitCode(2));
        Assert.Equal(InterruptOutcome.HelperFailed, ConsoleInterrupt.FromExitCode(99));
    }

    private static string? FindHelper()
    {
        string bin = Path.Combine(RepoSource.Root(), "src", "WinStatusBar.Automation", "bin");
        if (!Directory.Exists(bin)) return null;
        return Directory.EnumerateFiles(bin, "WinStatusBar.Automation.exe", SearchOption.AllDirectories)
            .OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault();
    }

    private static int RunHelper(string helper, string[] args)
    {
        var psi = new ProcessStartInfo(helper) { UseShellExecute = false, CreateNoWindow = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        Assert.True(p.WaitForExit(15000), "o auxiliar não terminou");
        return p.ExitCode;
    }

    [Fact]
    public async Task HelperStopsAPingStartedDetached()
    {
        string? helper = FindHelper();
        if (helper is null)
        {
            if (Environment.GetEnvironmentVariable("WSB_REQUIRE_HELPER") == "1") Assert.Fail("auxiliar não compilado");
            return; // o xUnit 2.9 não tem skip dinâmico; o D1 roda com WSB_REQUIRE_HELPER=1
        }
        string dir = Path.Combine(Path.GetTempPath(), "wsb-ctrlc-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var ping = DetachedProcess.Start(Path.Combine(Environment.SystemDirectory, "PING.EXE"), ["-n", "30", "127.0.0.1"], dir,
            Path.Combine(dir, "out.txt"), Path.Combine(dir, "err.txt"), new Dictionary<string, string?>());
        try
        {
            await Task.Delay(1000);
            Assert.Equal(3, RunHelper(helper, ConsoleInterrupt.HelperArgs(ping.Pid, ping.CreatedAt.AddHours(-1), "PING.EXE")));
            Assert.True(DetachedProcess.IsSame(ping.Pid, ping.CreatedAt, "PING.EXE"));

            Assert.Equal(0, RunHelper(helper, ConsoleInterrupt.HelperArgs(ping.Pid, ping.CreatedAt, "PING.EXE")));
            for (int i = 0; i < 50 && DetachedProcess.IsSame(ping.Pid, ping.CreatedAt, "PING.EXE"); i++) await Task.Delay(100);
            Assert.False(DetachedProcess.IsSame(ping.Pid, ping.CreatedAt, "PING.EXE"));
        }
        finally
        {
            DetachedProcess.KillTree(ping.Pid, ping.CreatedAt, "PING.EXE");
            try { Directory.Delete(dir, true); } catch (IOException) { }
        }
    }
}
