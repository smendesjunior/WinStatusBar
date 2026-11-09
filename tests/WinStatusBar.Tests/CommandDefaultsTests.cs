using System.Text.Json;
using WinStatusBar.Core.Settings;
using Xunit;

namespace WinStatusBar.Tests;

public class CommandDefaultsTests
{
    [Fact]
    public void ExistingSettingsDoNotEnableFileEdits()
    {
        var settings = JsonSerializer.Deserialize<AppSettings>("{\"CommandWebDefault\":false}")!;
        Assert.False(settings.CommandAllowEditsDefault);
        Assert.False(settings.CommandWebDefault);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void CommandPreferencesSurviveSaving(bool edits, bool detect)
    {
        var saved = JsonSerializer.Serialize(new AppSettings
            { CommandAllowEditsDefault = edits, CommandDetectActiveFile = detect });
        var restored = JsonSerializer.Deserialize<AppSettings>(saved)!;
        restored.Normalize();
        Assert.Equal(edits, restored.CommandAllowEditsDefault);
        Assert.Equal(detect, restored.CommandDetectActiveFile);
    }
}
