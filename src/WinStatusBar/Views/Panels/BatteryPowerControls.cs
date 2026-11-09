using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WinStatusBar.Core.Metrics;
using WinStatusBar.Services;

namespace WinStatusBar.Views.Panels;

internal sealed class BatteryPowerControls
{
    public StackPanel Content { get; } = new() { Spacing = 6, Margin = new Thickness(0, 6, 0, 0) };
    private readonly ComboBox _ac = new(), _dc = new(), _threshold = new();
    private readonly ToggleSwitch _dim = new();
    private readonly TextBlock _feedback = PanelKit.Caption("");
    private bool _loading;
    private uint _dimValue = 70;

    public BatteryPowerControls()
    {
        var columns = new Grid { ColumnSpacing = 18 };
        columns.ColumnDefinitions.Add(new ColumnDefinition());
        columns.ColumnDefinitions.Add(new ColumnDefinition());
        var modes = new StackPanel { Spacing = 4 };
        var saver = new StackPanel { Spacing = 6 };
        modes.Children.Add(PanelKit.Title(Localization.T("Modo de energia")));
        saver.Children.Add(PanelKit.Title(Localization.T("Economia de energia")));
        foreach (var box in new[] { _ac, _dc })
        {
            box.FontSize = 12; box.MinHeight = 28; box.HorizontalAlignment = HorizontalAlignment.Stretch;
            foreach (string name in new[] { "Melhor eficiência energética", "Equilibrado", "Desempenho máximo" }) box.Items.Add(Localization.T(name));
        }
        modes.Children.Add(PanelKit.Caption(Localization.T("Conectado"), 12)); modes.Children.Add(_ac);
        modes.Children.Add(PanelKit.Caption(Localization.T("Na bateria"), 12)); modes.Children.Add(_dc);
        var always = new HyperlinkButton { Content = Localization.T("Sempre usar economia ↗"), FontSize = 12, Padding = new Thickness(0), MinHeight = 24 };
        ToolTipService.SetToolTip(always, Localization.T("Configurar no Windows"));
        always.Click += async (_, _) => await Windows.System.Launcher.LaunchUriAsync(new Uri("ms-settings:batterysaver-settings"));
        saver.Children.Add(always);
        foreach (int value in new[] { 0, 10, 20, 30, 40, 50, 100 })
            _threshold.Items.Add(new ComboBoxItem { Content = Localization.T(value == 0 ? "Nunca" : value == 100 ? "Sempre" : value + "%"), Tag = (uint)value });
        _threshold.FontSize = 12; _threshold.MinHeight = 28;
        saver.Children.Add(Row("Ativar em", _threshold));
        _dim.MinWidth = 0; _dim.OnContent = ""; _dim.OffContent = "";
        saver.Children.Add(Row("Reduzir brilho", _dim));
        columns.Children.Add(modes); Grid.SetColumn(saver, 1); columns.Children.Add(saver);
        Content.Children.Add(columns);
        _feedback.Visibility = Visibility.Collapsed;
        Content.Children.Add(_feedback);
        _ac.SelectionChanged += async (_, _) => { if (!_loading && _ac.SelectedIndex >= 0) await ChangeAsync(() => WindowsPowerPreferences.SetMode(true, _ac.SelectedIndex)); };
        _dc.SelectionChanged += async (_, _) => { if (!_loading && _dc.SelectedIndex >= 0) await ChangeAsync(() => WindowsPowerPreferences.SetMode(false, _dc.SelectedIndex)); };
        _threshold.SelectionChanged += async (_, _) =>
        {
            if (!_loading && _threshold.SelectedItem is ComboBoxItem { Tag: uint value }) await ChangeAsync(() => WindowsPowerPreferences.SetThreshold(value));
        };
        _dim.Toggled += async (_, _) => { if (!_loading) await ChangeAsync(() => WindowsPowerPreferences.SetDim(_dim.IsOn, _dimValue)); };
    }

    public async Task RefreshAsync()
    {
        _loading = true;
        try
        {
            var p = await Task.Run(WindowsPowerPreferences.Read);
            _ac.SelectedIndex = p.AcMode is Guid ac ? Array.IndexOf(WindowsPowerPreferences.Modes, ac) : -1;
            _dc.SelectedIndex = p.DcMode is Guid dc ? Array.IndexOf(WindowsPowerPreferences.Modes, dc) : -1;
            _ac.IsEnabled = _ac.SelectedIndex >= 0;
            _dc.IsEnabled = _dc.SelectedIndex >= 0;
            _threshold.IsEnabled = p.Threshold is not null;
            if (p.Threshold is uint threshold)
            {
                var item = _threshold.Items.Cast<ComboBoxItem>().FirstOrDefault(i => (uint)i.Tag == threshold);
                if (item is null) { item = new ComboBoxItem { Content = threshold + "%", Tag = threshold }; _threshold.Items.Add(item); }
                _threshold.SelectedItem = item;
            }
            _dim.IsEnabled = p.Brightness is not null;
            _dim.IsOn = p.Brightness is < 100;
            if (p.Brightness is > 0 and < 100) _dimValue = p.Brightness.Value;
        }
        catch (Exception ex) { _feedback.Text = Localization.T("Use as configurações do Windows neste computador."); App.Log("Preferências de energia: " + ex.Message); }
        finally { _loading = false; }
    }

    private async Task ChangeAsync(Action change)
    {
        try { change(); _feedback.Text = Localization.T("Alteração aplicada no Windows."); }
        catch (Exception ex) { _feedback.Text = Localization.T("O Windows não permitiu esta alteração. Use as configurações do Windows."); App.Log("Alterar energia: " + ex.Message); }
        _feedback.Visibility = Visibility.Visible;
        await RefreshAsync();
    }

    private static Grid Row(string title, FrameworkElement control)
    {
        var row = new Grid { ColumnSpacing = 12 };
        row.ColumnDefinitions.Add(new ColumnDefinition());
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var text = PanelKit.Caption(Localization.T(title), 12); text.VerticalAlignment = VerticalAlignment.Center;
        row.Children.Add(text); Grid.SetColumn(control, 1); row.Children.Add(control);
        return row;
    }
}
