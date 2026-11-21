using WinStatusBar.Core.Ai;
using WinStatusBar.Core.Ai.Antigravity;
using WinStatusBar.Core.Ai.Claude;
using Xunit;
namespace WinStatusBar.Tests;

/// <summary>Cotas que paravam de voltar sozinhas (21/11/2026): Claude, Copilot e Antigravity.</summary>
public class QuotaRecoveryTests
{
    [Theory]
    [InlineData(1, 0, 5)]
    [InlineData(2, 0, 10)]
    [InlineData(3, 0, 15)]
    [InlineData(6, 0, 15)]   // antes: 60 min
    [InlineData(1, 120, 15)] // Retry-After longo também fica em 15 min
    [InlineData(1, 7, 7)]
    public void ClaudeRateLimitWaitNeverExceeds15Minutes(int limits, int retryAfterMin, int expectedMin)
        => Assert.Equal(TimeSpan.FromMinutes(expectedMin), ClaudeCodeProvider.RateLimitWait(limits, TimeSpan.FromMinutes(retryAfterMin)));

    [Fact]
    public void ClaudeOldLongWaitIsTrimmedOnReopen()
    {
        string cache = Path.Combine(Path.GetTempPath(), "wsb-claude-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var now = DateTimeOffset.UtcNow;
            File.WriteAllText(cache + ".next", now.AddMinutes(55).ToUnixTimeSeconds().ToString());
            var provider = new ClaudeCodeProvider(cache);
            Assert.True(provider.NextQuotaQuery <= now.AddMinutes(15).AddSeconds(5));
        }
        finally { File.Delete(cache + ".next"); File.Delete(cache); }
    }

    [Fact]
    public void AntigravitySignedOutIsCheckedAgainSoonSoLoginIsRecognized()
    {
        var now = DateTimeOffset.Now;
        var signedOut = new AntigravityQuota.Snapshot(null, null, [], now.AddMinutes(-4), SignedIn: false, Error: "sem login");
        var signedIn = new AntigravityQuota.Snapshot("a@b.com", null, [new("Gemini", "", "Limite", 90, null)], now.AddMinutes(-4));
        Assert.True(AntigravityQuota.IsDue(signedOut, now));
        Assert.False(AntigravityQuota.IsDue(signedIn, now));
        Assert.True(AntigravityQuota.IsDue(signedIn with { Updated = now.AddMinutes(-31) }, now));
        Assert.True(AntigravityQuota.IsDue(null, now));
    }

    [Fact]
    public void CopilotKeepsTryingAfterMicrosoftAsksToSignInAgain()
    {
        var now = DateTimeOffset.Now;
        Assert.True(MicrosoftCreditsText.ShouldRefresh(false, now.AddMinutes(-31), now, TimeSpan.FromMinutes(5), hasProfile: true));
        Assert.False(MicrosoftCreditsText.ShouldRefresh(false, now.AddMinutes(-10), now, TimeSpan.FromMinutes(5), hasProfile: true));
        Assert.False(MicrosoftCreditsText.ShouldRefresh(false, now.AddHours(-2), now, TimeSpan.FromMinutes(5), hasProfile: false));
        Assert.True(MicrosoftCreditsText.ShouldRefresh(true, now.AddMinutes(-6), now, TimeSpan.FromMinutes(5), hasProfile: true));
    }
}

public class CopilotSignInPageTests
{
    [Theory]
    [InlineData("s@outlook.com\nDigite o código gerado pelo aplicativo autenticador\nCódigo\nAvançar", true)]
    [InlineData("Entrar\nE-mail, telefone ou Skype\nAvançar", true)]
    [InlineData("Enter your password\nSign in", true)]
    [InlineData("", false)]
    [InlineData("Aguarde\nRedirecionando…", false)]
    public void TellsARealSignInPromptFromASilentRedirect(string text, bool asks)
        => Assert.Equal(asks, WinStatusBar.Core.Ai.MicrosoftCreditsText.AsksForSignIn(text));
}
