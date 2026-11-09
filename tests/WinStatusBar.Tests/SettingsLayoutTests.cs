using System.Xml.Linq;
using Xunit;
namespace WinStatusBar.Tests;
public class SettingsLayoutTests
{
    private static XDocument ReadLayout()
    {
        using var file = typeof(SettingsLayoutTests).Assembly.GetManifestResourceStream("SettingsWindow.xaml")!;
        return XDocument.Load(file);
    }
    [Fact] public void ExplanationsAreNeverSelectableOptions()
    {
        XNamespace ui = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        var boxes = ReadLayout().Descendants(ui + "ComboBox").ToList();
        Assert.NotEmpty(boxes);
        foreach (var box in boxes)
        {
            Assert.All(box.Elements(), child => Assert.Equal(ui + "ComboBoxItem", child.Name));
            Assert.All(box.Elements(), child => Assert.False(string.IsNullOrWhiteSpace((string?)child.Attribute("Tag"))));
        }
    }
    [Fact] public void EssentialChoicesRetainEverySupportedValue()
    {
        XNamespace ui = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        var expected = new Dictionary<string, string[]> {
            ["MonitorBox"] = ["Primary", "All"], ["ThemeBox"] = ["System", "Light", "Dark"],
            ["FullscreenBox"] = ["HideBehind", "StayOnTop"], ["QuotaModeBox"] = ["Remaining", "Used"],
            ["GpuPercentBox"] = ["Vram", "Utilization"] };
        var doc = ReadLayout();
        foreach (var (name, values) in expected)
        {
            var box = doc.Descendants(ui + "ComboBox").Single(b => (string?)b.Attribute(x + "Name") == name);
            Assert.Equal(values, box.Elements(ui + "ComboBoxItem").Select(i => (string)i.Attribute("Tag")!));
        }
    }
}
