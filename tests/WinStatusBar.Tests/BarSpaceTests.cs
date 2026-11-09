using WinStatusBar.Core.Settings;
using Xunit;

namespace WinStatusBar.Tests;

public class BarSpaceTests
{
    [Fact]
    public void KeepsCenterWhenBothSidesFit()
    {
        var result = BarSpace.Allocate(1600, 200, 400, 600);
        Assert.Equal(700, result.Left);
        Assert.Equal(700, result.Right);
    }

    [Fact]
    public void MetricsNeverMoveSearchAwayFromCenter()
    {
        var result = BarSpace.Allocate(1700, 200, 350, 1000);
        Assert.Equal(750, result.Left);
        Assert.Equal(750, result.Right);
    }

    [Fact]
    public void ReorderingNeverMovesSearchAwayFromCenter()
    {
        var result = BarSpace.Allocate(1700, 200, 1000, 350);
        Assert.Equal(750, result.Left);
        Assert.Equal(750, result.Right);
    }

    [Fact]
    public void SmallScreensNeverProduceNegativeColumns()
    {
        var result = BarSpace.Allocate(500, 180, 400, 1000);
        Assert.True(result.Left >= 0 && result.Right >= 0);
        Assert.Equal(320, result.Left + result.Right);
    }
}
