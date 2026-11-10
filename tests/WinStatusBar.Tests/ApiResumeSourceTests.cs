using System.Text.RegularExpressions;
using Xunit;

namespace WinStatusBar.Tests;

/// <summary>Partes da continuação pela API que dependem do WinUI ou do Windows: confere o código-fonte.</summary>
public class ApiResumeSourceTests
{
    internal static string Body(string src, string sig)
    {
        var match = Regex.Match(src, Regex.Escape(sig) + @"[^{]*\{(?<b>.*?)\n    \}", RegexOptions.Singleline);
        Assert.True(match.Success, $"não achei: {sig}");
        return match.Groups["b"].Value;
    }

    private static string Src(params string[] path) => RepoSource.Read(["src", "WinStatusBar", .. path]);

    [Fact]
    public void BalloonClickComesFromNinBalloonUserClick()
    {
        string tray = Src("Services", "TrayIconService.cs");
        Assert.Contains("NIN_BALLOONUSERCLICK = WM_USER + 5", tray);
        Assert.Contains("0x405", tray);
        string callback = tray[tray.IndexOf("if (msg == CallbackMsg)", StringComparison.Ordinal)..];
        callback = callback[..callback.IndexOf("return IntPtr.Zero;", StringComparison.Ordinal)];
        Assert.Contains("ev == NIN_BALLOONUSERCLICK", callback);
        Assert.Contains("BalloonClicked?.Invoke()", callback);
    }

    [Fact]
    public void CodexThreadOpensTheCodexLink()
    {
        string launcher = Src("Services", "WindowsLauncherService.cs");
        Assert.Contains("codex://threads/", launcher);
        string body = Body(launcher, "public static void CodexThread(string threadId)");
        Assert.Contains("Shell(\"codex://threads/\" + threadId)", body);
        Assert.Contains("^[0-9A-Za-z-]{8,64}$", body);
    }

    private static string Svc() => Src("Services", "CodexApiResumeService.cs");

    [Fact]
    public void KeepsThePcAwakeButNotTheScreen()
    {
        string svc = Svc();
        Assert.Contains("ES_CONTINUOUS | ES_SYSTEM_REQUIRED", svc);
        Assert.Contains("0x80000000", svc);
        Assert.Contains("0x00000001", svc);
        Assert.DoesNotContain("ES_DISPLAY_REQUIRED", svc);
        Assert.DoesNotContain("0x00000002", svc);
    }

    [Fact]
    public void ScreenOffOnlyChangesTheRhythm()
    {
        string svc = Svc();
        Assert.Contains("ScreenOn => !Presence.Away", svc);
        Assert.DoesNotContain("WhenVisibleAsync", svc);
        Assert.DoesNotContain("WaitVisible", svc);
        var refreshes = svc.Split('\n').Where(l => l.Contains("OpenAiDashboardView.Refresh()")).ToList();
        Assert.NotEmpty(refreshes);
        Assert.All(refreshes, l => Assert.True(l.Contains("OpenAiDashboardView.Enabled") && l.Contains("!Presence.Away"), l));
    }

    [Fact]
    public void KeyNeverReachesLogsOrTheBarEnvironment()
    {
        string svc = Svc();
        Assert.DoesNotContain("SetEnvironmentVariable", svc);
        Assert.DoesNotMatch(new Regex(@"Log\([^;]*\b(apiKey|key)\b"), svc);
    }

    [Fact]
    public void KeyPresenceDoesNotLoadTheKey()
    {
        string body = Body(Svc(), "public bool HasKey");
        Assert.Contains("OpenAiApiKey.Exists(", body);
        Assert.Contains("ApiKeyStore.Exists(", body);
        Assert.DoesNotContain("ApiKeyStore.Get(", body);
        Assert.DoesNotContain("OpenAiApiKey.Read(", body);
    }

    [Fact]
    public void StoreBuildAndHelper()
    {
        string svc = Svc();
        Assert.Contains("PackageContext.IsPackaged", svc);
        Assert.Contains("ConsoleInterrupt.HelperArgs(", svc);
    }

    [Fact]
    public void ShutdownNeverWaitsNorKills()
    {
        string body = Body(Svc(), "public void Shutdown()");
        Assert.Contains("Supervisor.ShutdownAsync()", body);
        Assert.DoesNotContain(".Wait(", body);
        Assert.DoesNotContain("await ", body);
        Assert.DoesNotContain("KillTree", body);
    }

    [Fact]
    public void TheBarNeverAttachesToAConsole()
    {
        string root = Path.Combine(RepoSource.Root(), "src", "WinStatusBar");
        foreach (var file in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
                     .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")))
        {
            string text = File.ReadAllText(file);
            Assert.False(text.Contains("ConsoleInterrupt.Send(") || text.Contains("AttachConsole"), file);
        }
    }

    [Fact]
    public void VaultPresenceNeverReadsThePassword()
    {
        string body = Body(Src("Services", "ApiKeyStore.cs"), "public static bool Exists(string resource)");
        Assert.Contains("FindAllByResource", body);
        Assert.DoesNotContain("RetrievePassword", body);
    }
}
