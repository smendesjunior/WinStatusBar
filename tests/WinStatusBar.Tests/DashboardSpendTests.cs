using System.Text.Json;
using WinStatusBar.Core.Ai;
using WinStatusBar.Core.Settings;
using Xunit;

namespace WinStatusBar.Tests;

public class DashboardSpendTests
{
    private static readonly DateTimeOffset Start = new(2026, 11, 10, 10, 31, 0, TimeSpan.FromHours(-3));
    private static ApiUsageDay Day(int d, decimal cost) => new(new DateTimeOffset(2026, 11, d, 0, 0, 0, TimeSpan.Zero), 0, 0, cost);
    private static DashboardSnapshot Snap(decimal? balance, params ApiUsageDay[] days) => new(days, balance, null, Start);

    [Fact]
    public void SpendFromBalanceDrop() =>
        Assert.Equal(0.62m, DashboardMetrics.SpendSince(Snap(10.00m), Snap(9.38m), Start));

    [Fact]
    public void RechargeUsesCostDifference() =>
        Assert.Equal(0.21m, DashboardMetrics.SpendSince(Snap(1.00m, Day(10, 2.00m)), Snap(11.00m, Day(9, 5m), Day(10, 2.21m)), Start));

    [Fact]
    public void CostDifferenceNeverGoesNegative() =>
        Assert.Equal(0m, DashboardMetrics.SpendSince(Snap(null, Day(10, 2.00m)), Snap(null, Day(10, 1.50m)), Start));

    [Fact]
    public void NoDashboardNoValue()
    {
        Assert.Null(DashboardMetrics.SpendSince(null, Snap(9m), Start));
        Assert.Null(DashboardMetrics.SpendSince(Snap(10m), null, Start));
        Assert.Null(DashboardMetrics.SpendSince(Snap(null), Snap(null), Start));
    }

    [Fact]
    public void ParallelDefaultsToQueue()
    {
        Assert.False(new AppSettings().CodexApiParallel);
        var json = JsonSerializer.Serialize(new AppSettings { CodexApiParallel = true }, AppSettingsContext.Default.AppSettings);
        Assert.True(JsonSerializer.Deserialize(json, AppSettingsContext.Default.AppSettings)!.CodexApiParallel);
    }
}
