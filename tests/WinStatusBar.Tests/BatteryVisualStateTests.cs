using WinStatusBar.Core.Metrics;
using Xunit;

namespace WinStatusBar.Tests;

public class BatteryVisualStateTests
{
    [Theory]
    [InlineData(100, false, true, false, false, false, BatteryVisualState.Plugged)]
    [InlineData(90, false, false, false, false, false, BatteryVisualState.Normal)]
    [InlineData(15, false, false, false, false, false, BatteryVisualState.EnergySaver)]
    [InlineData(50, false, false, true, false, false, BatteryVisualState.EnergySaver)]
    [InlineData(5, false, false, true, false, false, BatteryVisualState.Critical)]
    [InlineData(50, true, true, false, false, false, BatteryVisualState.Charging)]
    [InlineData(70, true, true, false, true, false, BatteryVisualState.SmartCharging)]
    [InlineData(80, false, true, false, true, false, BatteryVisualState.SmartPaused)]
    [InlineData(50, true, true, false, false, true, BatteryVisualState.SlowCharging)]
    [InlineData(-1, false, true, false, false, false, BatteryVisualState.Unknown)]
    public void UsesEachSymbolOnlyForItsReportedState(int percent, bool charging, bool plugged, bool saver, bool smart, bool slow, BatteryVisualState expected)
        => Assert.Equal(expected, BatteryVisualStates.Select(percent, charging, plugged, saver, smart, slow));
}
