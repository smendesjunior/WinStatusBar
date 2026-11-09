using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WinStatusBar.Core.Metrics;
using WinStatusBar.Services;

namespace WinStatusBar.Views.Panels;

internal sealed class WindowsAppearanceControls
{
    public StackPanel Colors { get; } = new() { Spacing = 8, Margin = new Thickness(0, 8, 0, 8) };
    public StackPanel Themes { get; } = new() { Spacing = 8, Margin = new Thickness(0, 8, 0, 8) };
    private readonly ToggleSwitch _transparency = Switch(), _borders = Switch(), _taskbar = Switch();
    private readonly ComboBox _apps = Modes(), _windows = Modes(), _mode = Modes();
    private readonly ComboBox _accent = new() { MinWidth = 130, FontSize = 12 };
    private readonly StackPanel _recent = new() { Orientation = Orientation.Horizontal, Spacing = 6 };
    private readonly Grid _customModes = new() { ColumnSpacing = 8, Visibility = Visibility.Collapsed };
    private readonly TextBlock _colorFeedback = PanelKit.Caption("", 11), _themeFeedback = PanelKit.Caption("", 11);
    private readonly TextBlock _themeDescription = PanelKit.Caption("", 11);
    private bool _loading, _changing;

    public WindowsAppearanceControls()
    {
        _colorFeedback.Visibility = _themeFeedback.Visibility = Visibility.Collapsed;
        _mode.Items.Add(Localization.T("Personalizado"));
        Colors.Children.Add(PanelKit.Title("Aparência"));
        Colors.Children.Add(Row("Escolher seu modo", _mode, "\uE790"));
        _customModes.ColumnDefinitions.Add(new ColumnDefinition());
        _customModes.ColumnDefinitions.Add(new ColumnDefinition());
        var windowsMode = Row("Windows", _windows);
        var appsMode = Row("Apps", _apps);
        Grid.SetColumn(appsMode, 1);
        _customModes.Children.Add(windowsMode); _customModes.Children.Add(appsMode);
        Colors.Children.Add(_customModes);
        Colors.Children.Add(Row("Efeitos de transparência", _transparency, "\uE7B3"));
        _accent.Items.Add(Localization.T("Automático")); _accent.Items.Add(Localization.T("Manual"));
        Colors.Children.Add(Row("Cor em destaque", _accent, "\uE790"));
        Colors.Children.Add(PanelKit.Caption("Cores recentes", 12));
        Colors.Children.Add(_recent);
        var moreColors = new StackPanel { Spacing = 8 };
        moreColors.Children.Add(PanelKit.Caption("Cores do Windows", 12));
        moreColors.Children.Add(BuildPalette());
        var picker = new ColorPicker { IsAlphaEnabled = false, IsColorSpectrumVisible = true, IsColorSliderVisible = true, IsHexInputVisible = true, IsMoreButtonVisible = false, MinWidth = 0, MaxWidth = 400 };
        var custom = new StackPanel { Spacing = 6 };
        custom.Children.Add(picker);
        custom.Children.Add(SettingsDetailsPanel.ActionButton("\uE73E", "Aplicar cor", () =>
        {
            var color = picker.Color;
            uint argb = 0xff000000u | ((uint)color.R << 16) | ((uint)color.G << 8) | color.B;
            _ = ChangeAsync(() => WindowsThemeCatalog.ApplyAccent(false, argb), true);
        }));
        moreColors.Children.Add(new Expander { Header = "Cor personalizada", Content = custom, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch });
        moreColors.Children.Add(Row("Iniciar e barra de tarefas", _taskbar));
        moreColors.Children.Add(Row("Bordas e títulos", _borders));
        Colors.Children.Add(new Expander { Header = "Escolher outra cor…", Content = moreColors, IsExpanded = false, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch });
        _mode.SelectionChanged += (_, _) =>
        {
            _customModes.Visibility = _mode.SelectedIndex == 2 ? Visibility.Visible : Visibility.Collapsed;
            if (!_loading && _mode.SelectedIndex < 2 && _mode.SelectedIndex >= 0) { bool light = _mode.SelectedIndex == 0; _ = ChangeAsync(() => WindowsAppearancePreferences.SetMode(light, light), true); }
        };
        _accent.SelectionChanged += (_, _) => { if (!_loading && _accent.SelectedIndex >= 0) { bool automatic = _accent.SelectedIndex == 0; _ = ChangeAsync(() => WindowsThemeCatalog.ApplyAccent(automatic), true); } };

        Colors.Children.Add(_colorFeedback);
        Themes.Children.Add(PanelKit.Title("Temas"));
        Themes.Children.Add(BuildThemes());
        Themes.Children.Add(_themeDescription);
        Themes.Children.Add(_themeFeedback);
        _transparency.Toggled += (_, _) => { if (!_loading) _ = ChangeAsync(() => WindowsAppearancePreferences.SetTransparency(_transparency.IsOn), true); };
        _borders.Toggled += (_, _) => { if (!_loading) _ = ChangeAsync(() => WindowsAppearancePreferences.SetAccentOnBorders(_borders.IsOn), true); };
        _taskbar.Toggled += (_, _) => { if (!_loading) _ = ChangeAsync(() => WindowsAppearancePreferences.SetAccentOnTaskbar(_taskbar.IsOn), true); };
        _apps.SelectionChanged += (_, _) => { if (!_loading && _apps.SelectedIndex >= 0) { bool light = _apps.SelectedIndex == 0; _ = ChangeAsync(() => WindowsAppearancePreferences.SetMode(light, null), false); } };
        _windows.SelectionChanged += (_, _) => { if (!_loading && _windows.SelectedIndex >= 0) { bool light = _windows.SelectedIndex == 0; _ = ChangeAsync(() => WindowsAppearancePreferences.SetMode(null, light), false); } };
    }

    public void Refresh()
    {
        _loading = true;
        try
        {
            var state = WindowsAppearancePreferences.Read();
            _mode.SelectedIndex = state.AppsLight == state.WindowsLight ? (state.AppsLight ? 0 : 1) : 2;
            _accent.SelectedIndex = WindowsThemeCatalog.AutomaticAccent() ? 0 : 1;
            _recent.Children.Clear();
            foreach (uint color in WindowsThemeCatalog.RecentColors())
            {
                var swatch = new Button { Width = 46, Height = 38, CornerRadius = new CornerRadius(5), Background = Brush(color) };
                string name = "#" + (color & 0xffffff).ToString("X6");
                Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(swatch, "Cor " + name);
                ToolTipService.SetToolTip(swatch, name);
                swatch.Click += (_, _) => _ = ChangeAsync(() => WindowsThemeCatalog.ApplyAccent(false, color), true);
                _recent.Children.Add(swatch);
            }
            if (_recent.Children.Count == 0) _recent.Children.Add(PanelKit.Caption("Nenhuma cor recente registrada."));
            _apps.SelectedIndex = state.AppsLight ? 0 : 1;
            _windows.SelectedIndex = state.WindowsLight ? 0 : 1;
            _transparency.IsOn = state.Transparency;
            _borders.IsOn = state.AccentOnBorders;
            _taskbar.IsOn = state.AccentOnTaskbar;
            _taskbar.IsEnabled = !state.WindowsLight;
            ToolTipService.SetToolTip(_taskbar, state.WindowsLight ? Localization.T("Disponível com o Windows no modo escuro.") : null);
            _themeDescription.Text = Localization.T(state.AppsLight == state.WindowsLight ? (state.AppsLight ? "Modo atual: claro" : "Modo atual: escuro") : "Modo atual: personalizado");
        }
        catch (Exception ex) { _colorFeedback.Visibility = _themeFeedback.Visibility = Visibility.Visible; _colorFeedback.Text = _themeFeedback.Text = Localization.T("Não foi possível ler a personalização."); App.Log("Personalização: " + ex.Message); }
        finally { _loading = false; }
    }

    private async Task ChangeAsync(Action change, bool colors)
    {
        if (_changing) return;
        _changing = true;
        SetEnabled(Colors, false); SetEnabled(Themes, false);
        var feedback = colors ? _colorFeedback : _themeFeedback;
        feedback.Visibility = Visibility.Visible;
        feedback.Text = Localization.T("Aplicando…");
        try { await Task.Run(change); feedback.Text = Localization.T("Aplicado no Windows."); }
        catch (Exception ex) { feedback.Text = Localization.T("O Windows não permitiu esta alteração."); App.Log("Personalização: " + ex.Message); }
        finally { _changing = false; SetEnabled(Colors, true); SetEnabled(Themes, true); Refresh(); }
    }

    private static Microsoft.UI.Xaml.Media.SolidColorBrush Brush(uint argb) => new(Windows.UI.Color.FromArgb(255, (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb));

    private Grid BuildPalette()
    {
        uint[] colors = [0xFFB900,0xFF8C00,0xF7630C,0xCA5010,0xDA3B01,0xEF6950,0xD13438,0xFF4343,0xE74856,
            0xE81123,0xEA005E,0xC30052,0xE3008C,0xBF0077,0xC239B3,0x9A0089,0x0078D7,0x0063B1,
            0x8E8CD8,0x6B69D6,0x8764B8,0x744DA9,0xB146C2,0x881798,0x0099BC,0x2D7D9A,0x00B7C3,
            0x038387,0x00B294,0x018574,0x00CC6A,0x10893E,0x7A7574,0x5D5A58,0x68768A,0x515C6B,
            0x567C73,0x486860,0x498205,0x107C10,0x767676,0x4C4A48,0x69797E,0x4A5459,0x647C64,
            0x525E54,0x847545,0x7E735F];
        var grid = new Grid { RowSpacing = 4, ColumnSpacing = 4 };
        for (int col = 0; col < 9; col++) grid.ColumnDefinitions.Add(new ColumnDefinition());
        for (int i = 0; i < colors.Length; i++)
        {
            if (i % 9 == 0) grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            uint color = 0xff000000u | colors[i];
            var swatch = new Button { Height = 28, MinWidth = 0, MinHeight = 0, Padding = new Thickness(0), Background = Brush(color), CornerRadius = new CornerRadius(4), HorizontalAlignment = HorizontalAlignment.Stretch };
            string label = "#" + colors[i].ToString("X6");
            ToolTipService.SetToolTip(swatch, label);
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(swatch, "Cor " + label);
            swatch.Click += (_, _) => _ = ChangeAsync(() => WindowsThemeCatalog.ApplyAccent(false, color), true);
            Grid.SetColumn(swatch, i % 9); Grid.SetRow(swatch, i / 9); grid.Children.Add(swatch);
        }
        return grid;
    }

    private static Grid BuildThemes()
    {
        var grid = new Grid { ColumnSpacing = 6, RowSpacing = 6 };
        for (int i = 0; i < 4; i++) grid.ColumnDefinitions.Add(new ColumnDefinition());
        var themes = WindowsThemeCatalog.Read();
        for (int i = 0; i < themes.Count; i++)
        {
            if (i % 4 == 0) grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var theme = themes[i];
            var preview = new Grid { Height = 52, Width = 98, Background = Brush(theme.Color), CornerRadius = new CornerRadius(5) };
            if (theme.Wallpaper is not null)
                preview.Children.Add(new Image { Source = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(new Uri(theme.Wallpaper)) { DecodePixelWidth = 196 }, Stretch = Microsoft.UI.Xaml.Media.Stretch.UniformToFill });
            var chip = new Border { Background = Brush(theme.Color), Width = 26, Height = 8, CornerRadius = new CornerRadius(2), Margin = new Thickness(6), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Bottom };
            var sample = new Grid { Width = 34, Height = 28, Background = Brush(theme.Name.Contains("escuro", StringComparison.OrdinalIgnoreCase) ? 0xff252525u : 0xffe8e8e8u), CornerRadius = new CornerRadius(4), Margin = new Thickness(4), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom };
            sample.Children.Add(chip);
            preview.Children.Add(sample);
            var tile = new StackPanel { Spacing = 3 }; tile.Children.Add(preview);
            tile.Children.Add(new TextBlock { Text = theme.Name, FontSize = 10, MaxWidth = 98, TextTrimming = TextTrimming.CharacterEllipsis });
            var button = new Button { Content = tile, Padding = new Thickness(3), HorizontalAlignment = HorizontalAlignment.Stretch };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(button, theme.Name);
            ToolTipService.SetToolTip(button, theme.Name);
            button.Click += (_, _) => WindowsLauncherService.Shell(theme.Path);
            Grid.SetRow(button, i / 4); Grid.SetColumn(button, i % 4); grid.Children.Add(button);
        }
        return grid;
    }

    private static void SetEnabled(DependencyObject root, bool enabled)
    {
        if (root is Control control) control.IsEnabled = enabled;
        for (int i = 0; i < Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(root); i++)
            SetEnabled(Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(root, i), enabled);
    }
    private static ToggleSwitch Switch() => new() { MinWidth = 0, OnContent = "", OffContent = "" };
    private static ComboBox Modes()
    {
        var box = new ComboBox { FontSize = 12, MinHeight = 28, MinWidth = 110 };
        box.Items.Add(Localization.T("Claro")); box.Items.Add(Localization.T("Escuro"));
        return box;
    }
    private static Border Row(string label, FrameworkElement control, string? glyph = null)
    {
        if (control is ComboBox choice)
        {
            choice.HorizontalAlignment = HorizontalAlignment.Right;
            choice.VerticalAlignment = VerticalAlignment.Center;
            choice.BorderThickness = new Thickness(1);
            choice.CornerRadius = new CornerRadius(4);
            choice.Margin = new Thickness(0);
            choice.MinHeight = 32;
            choice.UseLayoutRounding = true;
        }
        var row = new Grid { ColumnSpacing = 10 };
        row.ColumnDefinitions.Add(new ColumnDefinition());
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var text = PanelKit.Caption(Localization.T(label), 12); text.VerticalAlignment = VerticalAlignment.Center;
        var caption = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
        if (glyph is not null) caption.Children.Add(new FontIcon { Glyph = glyph, FontSize = 18 });
        caption.Children.Add(text);
        row.Children.Add(caption); Grid.SetColumn(control, 1); row.Children.Add(control);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(control, Localization.T(label));
        return new Border { Child = row, Padding = new Thickness(10, 6, 10, 6), CornerRadius = new CornerRadius(6), Background = PanelKit.Res("CardBackgroundFillColorDefaultBrush") };
    }
}

