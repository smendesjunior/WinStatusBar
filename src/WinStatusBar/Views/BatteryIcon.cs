using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using WinStatusBar.Core.Metrics;

namespace WinStatusBar.Views;

/// <summary>
/// Mesmo ícone de bateria da bandeja do Windows 11 (pedido de 15/11/2026): a fonte do sistema "SysBatt Fluent Icons"
/// (Sysbatt.ttf, do próprio Windows, não distribuída com o app), em duas camadas: contorno + preenchimento por nível.
/// Sem a fonte (Windows 10), usa os glifos de bateria da Segoe Fluent Icons / MDL2.
/// </summary>
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
    public bool SmartCharging { get => (bool)GetValue(SmartChargingProperty); set => SetValue(SmartChargingProperty, value); }
    public bool SlowCharging { get => (bool)GetValue(SlowChargingProperty); set => SetValue(SlowChargingProperty, value); }

    private static readonly string SysBattPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), @"SystemResources\Windows.UI.ControlCenter\Assets\Sysbatt.ttf");
    // Cópia local da fonte do sistema (feita ao abrir; nunca vai no instalador), lida por ms-appx como as demais fontes do app.
    private static readonly bool HasSysBatt = CopySysBatt();
    private static bool CopySysBatt()
    {
        try
        {
            if (!File.Exists(SysBattPath)) return false;
            string local = Path.Combine(AppContext.BaseDirectory, "Assets", "SysBatt.ttf");
            if (!File.Exists(local) || new FileInfo(local).Length != new FileInfo(SysBattPath).Length)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(local)!);
                File.Copy(SysBattPath, local, true);
            }
            return true;
        }
        catch (Exception ex) { App.Log("Fonte de bateria do sistema: " + ex.Message); return false; }
    }
    private const double Size = 20;

    private readonly FontIcon _fill = new() { FontSize = Size };
    private readonly FontIcon _outline = new() { FontSize = Size };

    public BatteryIcon()
    {
        IsHitTestVisible = false;
        var family = HasSysBatt
            ? new FontFamily("ms-appx:///Assets/SysBatt.ttf#SysBatt Fluent Icons")
            : (FontFamily)Application.Current.Resources["BarIconFont"];
        _fill.FontFamily = _outline.FontFamily = family;
        if (HasSysBatt) Children.Add(_fill);
        Children.Add(_outline);
        ActualThemeChanged += (_, _) => Update();
        Update();
    }

    private static void Changed(DependencyObject d, DependencyPropertyChangedEventArgs e) => ((BatteryIcon)d).Update();

    /// <summary>Camadas da SysBatt: (contorno, preenchimento). Normal F8D0 + F8D1–F8DA (10 níveis);
    /// carregando F8DB + F8DC–F8E3 (8); na tomada F8E4 + F8E5–F8EC (8); desconhecido F849.</summary>
    public static (char Outline, char? Fill) SysBattGlyphs(BatteryVisualState state, int percent)
    {
        int p = Math.Clamp(percent, 0, 100);
        int Level(int steps) => Math.Clamp((int)Math.Ceiling(p * steps / 100.0), 1, steps) - 1;
        return state switch
        {
            BatteryVisualState.Unknown => ('', null),
            BatteryVisualState.Charging or BatteryVisualState.SmartCharging or BatteryVisualState.SlowCharging => ('', (char)(0xF8DC + Level(8))),
            BatteryVisualState.Plugged or BatteryVisualState.SmartPaused => ('', (char)(0xF8E5 + Level(8))),
            _ => ('', (char)(0xF8D1 + Level(10))),
        };
    }

    /// <summary>Glifo único da Segoe Fluent Icons (Windows 10): Battery0–10 E850, BatteryCharging0–10 E85B, BatterySaver0–10 E867.</summary>
    public static char FallbackGlyph(BatteryVisualState state, int percent)
    {
        int level = Math.Clamp((int)Math.Round(Math.Clamp(percent, 0, 100) / 10.0), 0, 10);
        return state switch
        {
            BatteryVisualState.Unknown => '',
            BatteryVisualState.Charging or BatteryVisualState.SmartCharging or BatteryVisualState.SlowCharging
                or BatteryVisualState.Plugged or BatteryVisualState.SmartPaused => (char)(0xE85B + level),
            BatteryVisualState.EnergySaver => (char)(0xE867 + level),
            _ => (char)(0xE850 + level),
        };
    }

    private void Update()
    {
        var state = BatteryVisualStates.Select(Percent, Charging, Plugged, Saver, SmartCharging, SlowCharging);
        if (!HasSysBatt) { _outline.Glyph = FallbackGlyph(state, Percent).ToString(); return; }
        var (outline, fill) = SysBattGlyphs(state, Percent);
        _outline.Glyph = outline.ToString();
        _fill.Glyph = fill?.ToString() ?? "";
        bool dark = ActualTheme == ElementTheme.Dark;
        // Cores da bandeja: verde na tomada/carregando, vermelho crítico, amarelo na economia; senão a cor do texto.
        SolidColorBrush? color = state switch
        {
            BatteryVisualState.Charging or BatteryVisualState.SmartCharging or BatteryVisualState.SlowCharging
                or BatteryVisualState.Plugged or BatteryVisualState.SmartPaused => Brush(dark ? 0x9FD89F : 0x0F7B0F),
            BatteryVisualState.Critical => Brush(dark ? 0xFF99A4 : 0xC42B1C),
            BatteryVisualState.EnergySaver when Saver => Brush(dark ? 0xFCE100 : 0x9D5D00),
            _ => null,
        };
        if (color is null) _fill.ClearValue(IconElement.ForegroundProperty); else _fill.Foreground = color;
    }

    private static SolidColorBrush Brush(int rgb) => new(ColorHelper.FromArgb(255, (byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb));
}
