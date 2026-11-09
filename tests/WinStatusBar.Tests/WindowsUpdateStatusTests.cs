using WinStatusBar.Core.Metrics;
using Xunit;

namespace WinStatusBar.Tests;

public class WindowsUpdateStatusTests
{
    [Theory]
    [InlineData(true, false, 0, false, WindowsUpdateState.RestartRequired)]
    [InlineData(true, true, 3, true, WindowsUpdateState.RestartRequired)]
    [InlineData(false, true, 0, true, WindowsUpdateState.UpToDate)]
    [InlineData(false, true, 2, true, WindowsUpdateState.UpdatesAvailable)]
    [InlineData(false, true, 2, false, WindowsUpdateState.UpdatesAvailable)]
    [InlineData(false, true, 0, false, WindowsUpdateState.Unknown)]
    [InlineData(false, false, 0, true, WindowsUpdateState.Unknown)]
    [InlineData(null, true, 0, true, WindowsUpdateState.Unknown)]
    public void NeverReportsUpToDateWithoutCompleteEvidence(bool? reboot, bool success, int count, bool dated, WindowsUpdateState expected)
    {
        var result = WindowsUpdateStatus.FromResult(reboot, success, count, dated ? new DateTime(2026, 11, 6) : null);
        Assert.Equal(expected, result.State);
    }
}
