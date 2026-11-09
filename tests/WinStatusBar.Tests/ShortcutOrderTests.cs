using System.Text.Json;
using WinStatusBar.Core.Settings;
using Xunit;

namespace WinStatusBar.Tests;

public class ShortcutOrderTests
{
    [Fact]
    public void LegacySettingsGainAllShortcutsWithoutChangingTheirVisibility()
    {
        var settings = JsonSerializer.Deserialize<AppSettings>("{\"ShortcutStore\":false,\"ShortcutDocuments\":false}")!;
        settings.Normalize();
        Assert.Equal(AppSettings.DefaultShortcutOrder, settings.ShortcutOrder);
        Assert.False(settings.ShortcutStore);
        Assert.False(settings.ShortcutDocuments);
    }

    [Fact]
    public void DraggedOrderAndSummaryPreferencesSurviveRealSerializer()
    {
        var settings = new AppSettings { ShortcutOrder = ["documents", "clipboard", "store"],
            ShortcutDownloads = false, TaskBriefingEnabled = false, TaskBriefingIntervalSeconds = 120 };
        settings.Normalize();
        string json = JsonSerializer.Serialize(settings, AppSettingsContext.Default.AppSettings);
        var restored = JsonSerializer.Deserialize(json, AppSettingsContext.Default.AppSettings)!;
        restored.Normalize();
        Assert.Equal(settings.ShortcutOrder, restored.ShortcutOrder);
        Assert.False(restored.ShortcutDownloads);
        Assert.False(restored.TaskBriefingEnabled);
        Assert.Equal(120, restored.TaskBriefingIntervalSeconds);
    }

    [Fact]
    public void InvalidKeysAndDuplicatesDoNotLoseValidOrderOrCreateButtons()
    {
        var settings = new AppSettings { ShortcutOrder = ["downloads", "unknown", "clipboard", "downloads"] };
        settings.Normalize();
        Assert.Equal("downloads", settings.ShortcutOrder[0]);
        Assert.Equal("clipboard", settings.ShortcutOrder[1]);
        Assert.Equal(8, settings.ShortcutOrder.Count);
        Assert.DoesNotContain("unknown", settings.ShortcutOrder);
    }
}
