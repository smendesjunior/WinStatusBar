using WinStatusBar.Core.Ai;
using Xunit;
namespace WinStatusBar.Tests;
public class CopilotResponseStateTests
{
    [Fact]
    public void GenerationMustEndAndTextMustRemainStable()
    {
        var state = new CopilotResponseState();
        Assert.False(state.Observe("4", true, TimeSpan.Zero));
        Assert.False(state.Observe("4", true, TimeSpan.FromSeconds(10)));
        Assert.False(state.Observe("4", false, TimeSpan.FromSeconds(11)));
        Assert.False(state.Observe("4", false, TimeSpan.FromSeconds(13)));
        Assert.True(state.Observe("4", false, TimeSpan.FromSeconds(15)));
    }
    [Fact]
    public void ChangingTextResetsCompletionAndEmptyTextNeverCompletes()
    {
        var state = new CopilotResponseState();
        Assert.False(state.Observe("", false, TimeSpan.Zero));
        Assert.False(state.Observe("", false, TimeSpan.FromSeconds(10)));
        Assert.False(state.Observe("a", false, TimeSpan.FromSeconds(11)));
        Assert.False(state.Observe("ab", false, TimeSpan.FromSeconds(14)));
        Assert.False(state.Observe("ab", false, TimeSpan.FromSeconds(15)));
        Assert.True(state.Observe("ab", false, TimeSpan.FromSeconds(18)));
    }
}
