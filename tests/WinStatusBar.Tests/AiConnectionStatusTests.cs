using WinStatusBar.Core.Ai;
using Xunit;

namespace WinStatusBar.Tests;

public class AiConnectionStatusTests
{
    [Fact]
    public void ClaudeStaleIdentityDoesNotCountAsLogin()
    {
        var state = AiConnectionStatus.Claude("{\"oauthAccount\":{\"emailAddress\":\"person@example.org\"}}", "{}", false);
        Assert.False(state.HasSession);
        Assert.Null(state.Email);
    }
    [Fact]
    public void ClaudeSessionHasEmailWithoutExposingToken()
    {
        var state = AiConnectionStatus.Claude("{\"oauthAccount\":{\"emailAddress\":\"person@example.org\"}}", "{\"claudeAiOauth\":{\"accessToken\":\"SECRET\"}}", false);
        Assert.True(state.HasSession);
        Assert.Equal("person@example.org", state.Email);
        Assert.DoesNotContain("SECRET", state.ToString());
    }
    [Fact]
    public void ApiKeyDoesNotImplyEmail()
    {
        var state = AiConnectionStatus.Codex("{\"OPENAI_API_KEY\":\"SECRET\"}", false);
        Assert.True(state.HasApiKey);
        Assert.False(state.HasSession);
        Assert.Null(state.Email);
        Assert.DoesNotContain("SECRET", state.ToString());
    }
    [Fact]
    public void CodexIdentityIsReadFromLocalTokenClaimsOnly()
    {
        string claims = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("{\"email\":\"person@example.org\"}")).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var state = AiConnectionStatus.Codex("{\"tokens\":{\"access_token\":\"SECRET\",\"id_token\":\"a." + claims + ".z\"}}", false);
        Assert.True(state.HasSession);
        Assert.Equal("person@example.org", state.Email);
    }
    [Theory]
    [InlineData(null)]
    [InlineData("garbage")]
    [InlineData("[]")]
    public void MissingOrMalformedCredentialsAreNotConnected(string? json)
    {
        Assert.False(AiConnectionStatus.Codex(json, false).HasSession);
        Assert.False(AiConnectionStatus.Claude(json, json, false).HasSession);
    }

    [Fact]
    public void ClaudeReadsDisplayNameAndPlan()
    {
        var state = AiConnectionStatus.Claude("{\"oauthAccount\":{\"emailAddress\":\"a@b.org\",\"displayName\":\"Pessoa\",\"organizationType\":\"claude_pro\"}}",
            "{\"claudeAiOauth\":{\"accessToken\":\"X\",\"subscriptionType\":\"max\"}}", false);
        Assert.Equal("Pessoa", state.Name);
        Assert.Equal("Claude Max", state.Plan);
    }
    [Theory]
    [InlineData("self_serve_business_prolite", "ChatGPT Business")]
    [InlineData("plus", "ChatGPT Plus")]
    [InlineData("pro", "ChatGPT Pro")]
    [InlineData("go", "ChatGPT Go")]
    [InlineData("desconhecido", null)]
    public void ChatGptPlanNames(string raw, string? expected) => Assert.Equal(expected, AiConnectionStatus.PlanName("codex", raw));
    [Fact]
    public void GeminiIneligibleTierBecomesActionableMessage()
    {
        string? text = WinStatusBar.Core.Ai.Gemini.GeminiCli.FriendlyError("IneligibleTierError: This client is no longer supported for Gemini Code Assist for individuals. reasonCode: 'UNSUPPORTED_CLIENT'");
        Assert.Contains("chave de API", text);
        Assert.DoesNotContain("IneligibleTierError", text);
    }
}
