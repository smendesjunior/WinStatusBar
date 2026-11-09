using WinStatusBar.Core.Ai;
using Xunit;

namespace WinStatusBar.Tests;

public class ActivityProgressTests
{
    [Fact] public void NoPlanDoesNotInventZeroPercent() => Assert.Null(ActivityProgress.From([]).Percent);
    [Fact] public void OnlyCompletedStepsCountAsCompleted()
    {
        var progress = ActivityProgress.From([new("Feito", "completed"), new("Atual", "in_progress"), new("Depois", "pending")]);
        Assert.Equal(1, progress.Completed);
        Assert.Equal(3, progress.Total);
        Assert.Equal(100d / 3, progress.Percent);
    }
    [Fact] public void CompletePlanShowsOneHundredPercent() => Assert.Equal(100d,
        ActivityProgress.From([new("Validado", "completed")]).Percent);
}
