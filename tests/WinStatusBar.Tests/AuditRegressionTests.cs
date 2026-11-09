using System.Text.Json;
using WinStatusBar.Core.Metrics;
using WinStatusBar.Core.Settings;
using Xunit;

namespace WinStatusBar.Tests;

public class AuditRegressionTests
{
    [Fact]
    public void NullCollectionsFromSettingsAreRepairedWithoutLosingCustomOrder()
    {
        var settings = JsonSerializer.Deserialize<AppSettings>("""
            {"SchemaVersion":3,"ModuleOrder":null,"HiddenModules":null,
             "LocalServerProcesses":null,"DisabledApiActivityProviders":null}
            """);
        settings!.Normalize();
        Assert.Equal(AppSettings.DefaultOrder, settings.ModuleOrder);
        Assert.NotNull(settings.HiddenModules);
        Assert.NotNull(settings.LocalServerProcesses);
        Assert.NotNull(settings.DisabledApiActivityProviders);
    }
}
