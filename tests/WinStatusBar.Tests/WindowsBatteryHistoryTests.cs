using WinStatusBar.Core.Metrics;
using Xunit;

namespace WinStatusBar.Tests;

public class WindowsBatteryHistoryTests
{
    private const string Xml = """
        <BatteryReport xmlns="http://schemas.microsoft.com/battery/2012">
          <ReportInformation><ScanTime>2026-11-06T12:00:00Z</ScanTime></ReportInformation>
          <RecentUsage>
            <UsageEntry Timestamp="2026-11-06T10:00:00Z" Duration="36000000000" Ac="1" EntryType="Active" ChargeCapacity="40000" FullChargeCapacity="50000" Discharge="-5000" />
            <UsageEntry Timestamp="2026-11-06T11:00:00Z" Duration="36000000000" Ac="0" EntryType="Suspend" ChargeCapacity="45000" FullChargeCapacity="50000" Discharge="1000" />
            <UsageEntry Timestamp="2026-11-06T12:00:00Z" Duration="18446744073709526528" EntryType="ReportGenerated" ChargeCapacity="44000" FullChargeCapacity="50000" />
          </RecentUsage>
        </BatteryReport>
        """;

    [Fact] public void NativeTicksCapacityAndChargingAreDecodedWithoutSyntheticSamples()
    {
        var report = WindowsBatteryReport.Parse(Xml);
        Assert.Equal(2, report.Spans.Length);
        Assert.Equal(80, report.Spans[0].Percent);
        Assert.Equal(90, report.Spans[0].EndPercent);
        Assert.True(report.Spans[0].Charging);
        Assert.Equal(TimeSpan.FromHours(1), report.Spans[0].End - report.Spans[0].Start);
        Assert.Equal(85, report.Spans[0].LevelAt(report.Spans[0].Start.AddMinutes(30)));
    }

    [Fact] public void TotalsAreClippedToSelectedPeriodAndSuspendIsNotScreenOff()
    {
        var report = WindowsBatteryReport.Parse(Xml);
        Assert.Equal(TimeSpan.FromMinutes(30), report.Duration("Active", DateTimeOffset.Parse("2026-11-06T10:30:00Z"), report.Generated));
        Assert.Equal(TimeSpan.Zero, report.Duration("ScreenOff", report.Generated.AddDays(-1), report.Generated));
        Assert.Equal(TimeSpan.FromHours(1), report.Duration("Suspend", report.Generated.AddDays(-1), report.Generated));
    }

    [Fact] public async Task ReadsActualWindowsHistoryWithoutElevation()
    {
        if (new BatteryMetricsProvider().Sample().Present != true) return;
        var report = await WindowsBatteryHistory.ReadAsync();
        Assert.InRange(report.Generated, DateTimeOffset.UtcNow.AddMinutes(-2), DateTimeOffset.UtcNow.AddMinutes(1));
        Assert.All(report.Spans, s =>
        {
            Assert.True(s.End > s.Start);
            Assert.InRange(s.Percent, 0, 100);
            Assert.InRange(s.EndPercent, 0, 100);
            Assert.True(s.End <= report.Generated);
        });
    }
}
