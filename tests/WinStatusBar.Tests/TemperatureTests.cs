using WinStatusBar.Core.Metrics;
using Xunit;
namespace WinStatusBar.Tests;
public class TemperatureTests
{
    [Theory]
    [InlineData(84.0, 84.0)]
    [InlineData(0, null)]
    [InlineData(255, null)]
    [InlineData(-1, null)]
    public void LenovoReadingsRejectUnavailableSentinels(double raw, double? expected)
        => Assert.Equal(expected, TemperatureProvider.ValidateTemperature(raw));

    [Fact] public void ProximityWinsEvenWhenMotherboardSensorAndCooler()
    {
        var result = TemperatureProvider.SelectCpu([new("CPU Package", 80, true), new("CPU Proximity", 50, false), new("GPU", 90, false)]);
        Assert.Equal("CPU Proximity", result?.Name);
        Assert.Equal(50, result?.Value);
    }
    [Fact] public void MissingProximityUsesNamedPackageInsteadOfHottestCore()
    {
        Assert.Equal("CPU Package", TemperatureProvider.SelectCpu([new("CPU Core #1", 90, true), new("CPU Package", 70, true)])?.Name);
    }
    [Fact] public void InvalidAndUnrelatedSensorsAreNeverCpuReadings()
    {
        Assert.Null(TemperatureProvider.SelectCpu([new("CPU Proximity", double.NaN, false), new("CPU Package", 0, true), new("GPU", 65, false), new("CPU", 200, true)]));
    }
}

