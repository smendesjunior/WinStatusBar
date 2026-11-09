using WinStatusBar.Core.Metrics;
using Xunit;

namespace WinStatusBar.Tests;

public class BatteryTests
{
    [Fact]
    public void UnknownAndAbsentBatteryNeverInventLevelOrTime()
    {
        var unknown = BatteryMetricsProvider.Decode(255, 255, 255, uint.MaxValue, false, null, null, null, null);
        Assert.Null(unknown.Present);
        Assert.Null(unknown.Percent);
        Assert.Null(unknown.Remaining);
        Assert.False(BatteryMetricsProvider.Decode(1, 128, 255, uint.MaxValue, false, null, null, null, null).Present);
    }

    [Fact]
    public void ChargingEstimatesTimeFromCapacityAndPositiveRate()
    {
        var sample = BatteryMetricsProvider.Decode(1, 8, 50, uint.MaxValue, false, 50000, 25000, 25000, null);
        Assert.True(sample.Charging);
        // 50% → 80% na taxa medida (0,6 h) + 80% → 100% a 40% dela (1 h).
        Assert.Equal(TimeSpan.FromHours(1.6).TotalMinutes, sample.Remaining!.Value.TotalMinutes, 3);
        Assert.Equal(50, sample.Percent);
    }

    [Fact]
    public void ChargingAboveEightyPercentAccountsForTaper()
        => Assert.Equal(TimeSpan.FromHours(10000 / (25000 * 0.6)).TotalMinutes,
            BatteryMetricsProvider.Decode(1, 8, 80, uint.MaxValue, false, 50000, 40000, 25000, null).Remaining!.Value.TotalMinutes, 3);

    [Fact]
    public void InvalidRateAndPluggedIdleDoNotProduceChargeEstimate()
    {
        Assert.Null(BatteryMetricsProvider.Decode(1, 8, 50, uint.MaxValue, false, 50000, 25000, -25000, null).Remaining);
        Assert.Null(BatteryMetricsProvider.Decode(1, 0, 80, 5000, false, 50000, 40000, 25000, null).Remaining);
    }

    [Fact]
    public void DischargeUsesWindowsEstimateAndRetainsZeroPercent()
    {
        var sample = BatteryMetricsProvider.Decode(0, 4, 0, 120, true, null, null, null, null);
        Assert.Equal(0, sample.Percent);
        Assert.Equal(TimeSpan.FromMinutes(2), sample.Remaining);
        Assert.True(sample.Saver);
    }
}
