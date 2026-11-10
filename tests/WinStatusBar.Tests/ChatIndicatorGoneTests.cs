using WinStatusBar.Services;
using Xunit;

namespace WinStatusBar.Tests;

public class ChatIndicatorGoneTests
{
    private static readonly string?[] CodexTitles = ["Responder saudação", CodexQuotaStopsTests.Title, null, ""];

    [Fact]
    public void GluedTitleIsTheTrackedConversation() =>
        Assert.True(ChatProbeSchedule.MatchesThread(CodexQuotaStopsTests.GluedTitle, CodexTitles));

    [Theory]
    [InlineData("analisar processo de fulano de tal", true)]
    [InlineData("  Responder saudação  ", true)]
    [InlineData(CodexQuotaStopsTests.Title + "2", false)]
    [InlineData("Analisar cota por IA", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void MatchesOnlyTheSameTitleOrTheTitleFollowedByASpace(string? window, bool expected) =>
        Assert.Equal(expected, ChatProbeSchedule.MatchesThread(window, CodexTitles));

    [Fact]
    public void ConversationWithoutRecordWhileBlockedIsInterruptedWithTheQuotaText() =>
        Assert.Equal(IndicatorGoneAction.BlockedByQuota, ChatProbeSchedule.OnIndicatorGone(false, "créditos do workspace esgotados"));

    [Fact]
    public void TrackedConversationIsLeftToTheCodexRecord() =>
        Assert.Equal(IndicatorGoneAction.Remove, ChatProbeSchedule.OnIndicatorGone(true, "créditos do workspace esgotados"));

    [Fact]
    public void WithoutABlockTheBehaviourIsTodays()
    {
        Assert.Equal(IndicatorGoneAction.Unconfirmed, ChatProbeSchedule.OnIndicatorGone(false, null));
        Assert.Equal(IndicatorGoneAction.Unconfirmed, ChatProbeSchedule.OnIndicatorGone(false, ""));
    }

    [Fact]
    public void OnlyTheChatGptWindowUsesTheCodexDecision()
    {
        string source = RepoSource.Read("src", "WinStatusBar", "Services", "ChatActivityProvider.cs");
        Assert.Contains("ChatProbeSchedule.MatchesThread(title, skip)", source);
        Assert.Contains("var action = _app == App.ChatGpt", source);
        Assert.Contains("ChatProbeSchedule.OnIndicatorGone(", source);
        Assert.Contains("CodexQuotaText.ChatRefused(", source);
    }
}
