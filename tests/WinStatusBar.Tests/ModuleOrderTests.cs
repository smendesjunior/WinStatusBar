using System.Text.Json;
using WinStatusBar.Core.Settings;
using Xunit;

namespace WinStatusBar.Tests;

public class ModuleOrderTests
{
    [Fact]
    public void ExistingInstallMigratesOrderWithoutChangingVisibility()
    {
        var settings = new AppSettings { SchemaVersion = 2, ModuleOrder = ["cpu", "agent"], HiddenModules = ["gpu"] };
        settings.Normalize();
        Assert.Equal(new[] { "agent", "quotas", "task", "network", "cpu", "ram", "gpu", "battery", "shortcuts" }, settings.ModuleOrder);
        Assert.Contains("gpu", settings.HiddenModules);
        Assert.Equal(4, settings.SchemaVersion); // v4: acesso total a conectores e plugins
    }

    [Fact]
    public void LaterCustomizationSurvivesRestart()
    {
        var settings = new AppSettings();
        settings.Normalize();
        settings.ModuleOrder.Reverse();
        var restored = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(settings))!;
        restored.Normalize();
        Assert.Equal(settings.ModuleOrder, restored.ModuleOrder);
    }
}
