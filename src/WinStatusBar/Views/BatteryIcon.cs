using System.Globalization;
using System.Text;
using System.Xml.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Storage.Streams;
using WinStatusBar.Core.Metrics;

namespace WinStatusBar.Views;

/// <summary>Separate vector assets for each Windows battery state. No font glyphs or nested battery overlays.</summary>
public sealed class BatteryIcon : Grid
{
    public static readonly DependencyProperty PercentProperty = DependencyProperty.Register(nameof(Percent), typeof(int), typeof(BatteryIcon), new PropertyMetadata(-1, Changed));
    public static readonly DependencyProperty ChargingProperty = DependencyProperty.Register(nameof(Charging), typeof(bool), typeof(BatteryIcon), new PropertyMetadata(false, Changed));
    public static readonly DependencyProperty PluggedProperty = DependencyProperty.Register(nameof(Plugged), typeof(bool), typeof(BatteryIcon), new PropertyMetadata(false, Changed));
    public static readonly DependencyProperty SaverProperty = DependencyProperty.Register(nameof(Saver), typeof(bool), typeof(BatteryIcon), new PropertyMetadata(false, Changed));
    public static readonly DependencyProperty SmartChargingProperty = DependencyProperty.Register(nameof(SmartCharging), typeof(bool), typeof(BatteryIcon), new PropertyMetadata(false, Changed));
    public static readonly DependencyProperty SlowChargingProperty = DependencyProperty.Register(nameof(SlowCharging), typeof(bool), typeof(BatteryIcon), new PropertyMetadata(false, Changed));
    public int Percent { get => (int)GetValue(PercentProperty); set => SetValue(PercentProperty, value); }
    public bool Charging { get => (bool)GetValue(ChargingProperty); set => SetValue(ChargingProperty, value); }
    public bool Plugged { get => (bool)GetValue(PluggedProperty); set => SetValue(PluggedProperty, value); }
    public bool Saver { get => (bool)GetValue(SaverProperty); set => SetValue(SaverProperty, value); }
    // These states require an explicit hardware signal; a plugged-in/full battery alone is insufficient.
    public bool SmartCharging { get => (bool)GetValue(SmartChargingProperty); set => SetValue(SmartChargingProperty, value); }
    public bool SlowCharging { get => (bool)GetValue(SlowChargingProperty); set => SetValue(SlowChargingProperty, value); }
    private readonly Image _image = new() { Width = 17, Height = 17 };
    private int _revision;
    private string _rendered = "";
    private XamlRoot? _observedRoot;
    private static readonly Dictionary<string, string> Assets = new();

    public BatteryIcon()
    {
        Width = Height = 17;
        IsHitTestVisible = false;
        Children.Add(_image);
        Loaded += (_, _) =>
        {
            _observedRoot = XamlRoot;
            if (_observedRoot is not null) _observedRoot.Changed += RootChanged;
            _ = DrawAsync();
        };
        Unloaded += (_, _) => { if (_observedRoot is not null) _observedRoot.Changed -= RootChanged; _observedRoot = null; };
        ActualThemeChanged += (_, _) => _ = DrawAsync();
    }
    private void RootChanged(XamlRoot sender, XamlRootChangedEventArgs args) => _ = DrawAsync();
    private static void Changed(DependencyObject d, DependencyPropertyChangedEventArgs e) => _ = ((BatteryIcon)d).DrawAsync();

    private async Task DrawAsync()
    {
        if (!IsLoaded) return;
        var state = BatteryVisualStates.Select(Percent, Charging, Plugged, Saver, SmartCharging, SlowCharging);
        string theme = ActualTheme == ElementTheme.Dark ? "dark" : "light";
        double pixels = Math.Ceiling(17 * (XamlRoot?.RasterizationScale ?? 1));
        string key = $"{state}-{theme}";
        string signature = $"{key}:{Percent}:{pixels}";
        if (_rendered == signature) return;
        int revision = ++_revision;
        try
        {
            if (!Assets.TryGetValue(key, out string? asset))
                Assets[key] = asset = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Assets", "Battery", key + ".svg"));
            var svg = XDocument.Parse(asset);
            var fill = svg.Descendants().FirstOrDefault(e => (string?)e.Attribute("id") == "charge");
            fill?.SetAttributeValue("width", Math.Max(1.1, 12.8 * Math.Clamp(Percent, 0, 100) / 100.0).ToString("0.###", CultureInfo.InvariantCulture));
            using var stream = new InMemoryRandomAccessStream();
            using var writer = new DataWriter(stream);
            writer.WriteBytes(Encoding.UTF8.GetBytes(svg.ToString(SaveOptions.DisableFormatting)));
            await writer.StoreAsync();
            stream.Seek(0);
            var source = new SvgImageSource { RasterizePixelWidth = pixels, RasterizePixelHeight = pixels };
            var status = await source.SetSourceAsync(stream);
            if (status != SvgImageSourceLoadStatus.Success) throw new IOException("SVG de bateria inválido: " + key);
            if (revision != _revision) return;
            _image.Source = source;
            _rendered = signature;
        }
        catch (Exception ex) { App.Log("Ícone de bateria: " + ex.Message); }
    }
}
