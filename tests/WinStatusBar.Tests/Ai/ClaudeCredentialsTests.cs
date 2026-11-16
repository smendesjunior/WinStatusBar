using WinStatusBar.Core.Ai.Claude;
using Xunit;

namespace WinStatusBar.Tests.Ai;

public class ClaudeCredentialsTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.FromUnixTimeMilliseconds(1_800_000_000_000);

    [Theory]
    [InlineData("""{"claudeAiOauth":{"accessToken":"a","expiresAt":0}}""", true)]
    [InlineData("""{"claudeAiOauth":{"accessToken":"a","expiresAt":1800000060000}}""", true)]
    [InlineData("""{"claudeAiOauth":{"accessToken":"a","expiresAt":1800003600000}}""", false)]
    [InlineData("""{"claudeAiOauth":{"accessToken":"a"}}""", false)]
    [InlineData("{}", false)]
    [InlineData("not json", false)]
    [InlineData(null, false)]
    public void NeedsRefresh_only_for_expired_oauth(string? json, bool expected)
        => Assert.Equal(expected, ClaudeCredentials.NeedsRefresh(json, Now));
}
