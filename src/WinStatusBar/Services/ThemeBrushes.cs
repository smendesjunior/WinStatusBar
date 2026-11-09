using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Controls;

namespace WinStatusBar.Services;

/// <summary>Resolve recursos WinUI no tema efetivo e atualiza os pincéis já usados por painéis persistentes.</summary>
internal static class ThemeBrushes
{
    private static readonly Dictionary<string, SolidColorBrush> Brushes = new();
    private static readonly Dictionary<(string Key, bool Dark), SolidColorBrush> FixedBrushes = new();
    public static Brush Get(string key)
    {
        if (!Brushes.TryGetValue(key, out var brush))
        {
            var resolved = Resolve(key);
            if (resolved is not SolidColorBrush solid) return resolved;
            Brushes[key] = brush = new SolidColorBrush(solid.Color);
        }
        return brush;
    }
    private static Brush Resolve(string key, bool? dark = null)
    {
        string theme = (dark ?? App.IsDarkTheme) ? "Dark" : "Light";
        var probe = (Border)XamlReader.Load($"<Border xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' RequestedTheme='{theme}' Background='{{ThemeResource {key}}}' />");
        return probe.Background;
    }
    public static Brush ForTheme(string key, ElementTheme theme)
    {
        bool dark = theme == ElementTheme.Default ? App.IsDarkTheme : theme == ElementTheme.Dark;
        if (FixedBrushes.TryGetValue((key, dark), out var cached)) return cached;
        var resolved = Resolve(key, dark);
        if (resolved is not SolidColorBrush solid) return resolved;
        FixedBrushes[(key, dark)] = cached = new SolidColorBrush(solid.Color);
        return cached;
    }
    public static void VerifyPalette()
    {
        foreach (bool dark in new[] { false, true })
        {
            var primary = (SolidColorBrush)Resolve("TextFillColorPrimaryBrush", dark);
            var secondary = (SolidColorBrush)Resolve("TextFillColorSecondaryBrush", dark);
            App.Log(global::WinStatusBar.Services.Localization.F("Paleta {0}: texto {1}, secundário {2}", new object?[] {(dark ? "escura" : "clara"),primary.Color,secondary.Color}));
            var background = ((SolidColorBrush)Resolve("SolidBackgroundFillColorBaseBrush", dark)).Color;
            var accent = ((SolidColorBrush)Resolve("AccentFillColorDefaultBrush", dark)).Color;
            var onAccent = ((SolidColorBrush)Resolve("TextOnAccentFillColorPrimaryBrush", dark)).Color;
            App.Log($"Auditoria de contraste {(dark ? "escuro" : "claro")}: texto {Contrast(primary.Color, background):0.00}:1 · secundário {Contrast(secondary.Color, background):0.00}:1 · botão ativo {Contrast(onAccent, Composite(accent, background)):0.00}:1");
        }
    }
    private static Windows.UI.Color Composite(Windows.UI.Color color, Windows.UI.Color background)
    {
        double a = color.A / 255.0;
        return Windows.UI.Color.FromArgb(255, (byte)Math.Round(color.R * a + background.R * (1 - a)),
            (byte)Math.Round(color.G * a + background.G * (1 - a)), (byte)Math.Round(color.B * a + background.B * (1 - a)));
    }
    private static double Contrast(Windows.UI.Color foreground, Windows.UI.Color background)
    {
        static double Channel(byte b) { double v = b / 255.0; return v <= 0.04045 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4); }
        static double Luminance(Windows.UI.Color c) => .2126 * Channel(c.R) + .7152 * Channel(c.G) + .0722 * Channel(c.B);
        double first = Luminance(Composite(foreground, background)), second = Luminance(background);
        return (Math.Max(first, second) + .05) / (Math.Min(first, second) + .05);
    }
    public static void Refresh()
    {
        foreach (var pair in Brushes)
            if (Resolve(pair.Key) is SolidColorBrush resolved) pair.Value.Color = resolved.Color;
        foreach (var pair in FixedBrushes)
            if (Resolve(pair.Key.Key, pair.Key.Dark) is SolidColorBrush resolved) pair.Value.Color = resolved.Color;
    }
}
