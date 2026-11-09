using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WinStatusBar.Core.Metrics;
using WinStatusBar.Services;

namespace WinStatusBar.Views.Panels;

internal sealed class BatteryDetailsPanel : IDetailsPanel
{
    private readonly MonitoringHub _hub;
    private readonly TextBlock _status = PanelKit.Caption("", 12);
    private readonly TextBlock _level = AreaChart.Text();
    private readonly TextBlock _time = PanelKit.Caption("", 13);
    private readonly Grid _specs = new();
    private readonly Dictionary<string, TextBlock> _fields = new();
    private readonly BatteryPowerControls _power = new();
    private readonly BatteryHistoryChart _history;
    private bool _open, _disposed, _loading;
    private DateTime _loaded;
    private List<(string Label, string Value)> _details = [];
    public FrameworkElement Content { get; }

    public BatteryDetailsPanel(MonitoringHub hub)
    {
        _hub = hub;
        _history = new BatteryHistoryChart();
        var body = new StackPanel { Spacing = 8 };
        _level.FontSize = 22;
        body.Children.Add(PanelKit.Header(Localization.T("Bateria"), _status, _level));
        body.Children.Add(_history.Content);
        var live = new StackPanel { Spacing = 6 };
        live.Children.Add(PanelKit.FieldRow(_fields, Localization.T("Potência"), Localization.T("Saúde")));
        live.Children.Add(PanelKit.FieldRow(_fields, Localization.T("Ciclos"), Localization.T("Economia")));
        live.Children.Add(_time);
        body.Children.Add(PanelKit.Summary(live, _specs));
        _power.Content.Visibility = ((App)Application.Current).Settings.Current.BatteryPowerControlsVisible ? Visibility.Visible : Visibility.Collapsed;
        body.Children.Add(_power.Content);
        Content = PanelKit.Body(body);
    }

    public void Start()
    {
        if (_disposed || _open) return;
        _open = true;
        _power.Content.Visibility = ((App)Application.Current).Settings.Current.BatteryPowerControlsVisible ? Visibility.Visible : Visibility.Collapsed;
        _hub.BatterySampled += Render;
        Render();
        _ = _power.RefreshAsync();
        if (!_loading && DateTime.UtcNow - _loaded > TimeSpan.FromMinutes(5)) _ = LoadAsync();
    }

    private async Task LoadAsync()
    {
        _loading = true;
        try
        {
            _details = await Task.Run(BatteryMetricsProvider.ReadDetails);
            _loaded = DateTime.UtcNow;
            if (_open && !_disposed) Render();
        }
        finally { _loading = false; }
    }

    private void Render()
    {
        if (!_open || _disposed || _hub.LastBattery is not { } b) return;
        _status.Text = _hub.ViewModel.BatteryStatus;
        _level.Text = _hub.ViewModel.BatteryText;
        _time.Text = _hub.ViewModel.BatteryTime;

        _fields[Localization.T("Potência")].Text = b.RateMilliwatts is int rate ? $"{Math.Abs((double)rate) / 1000:0.0} W" : "—";
        _fields[Localization.T("Economia")].Text = Localization.T(b.Saver ? "Ativada" : "Desativada");
        _fields[Localization.T("Saúde")].Text = _details.FirstOrDefault(r => r.Label.StartsWith("Saúde")).Value ?? "—";
        _fields[Localization.T("Ciclos")].Text = _details.FirstOrDefault(r => r.Label.StartsWith("Ciclos")).Value ?? "—";
        foreach (string label in new[] { "Saúde", "Ciclos", "Potência" })
            if (_fields[Localization.T(label)].Parent is FrameworkElement field)
                field.Visibility = _fields[Localization.T(label)].Text == "—" ? Visibility.Collapsed : Visibility.Visible;
        var rows = _details.Where(r => !r.Label.StartsWith("Saúde") && !r.Label.StartsWith("Ciclos"))
            .Select(r => (Localization.T(r.Label.Replace("Capacidade de projeto", "Projeto").Replace("Capacidade de carga completa", "Carga completa").Replace("Número de série", "Série")), r.Value));
        PanelKit.FillSpecs(_specs, rows);
        _history.Render();
    }

    public void Stop() { if (!_open) return; _open = false; _hub.BatterySampled -= Render; }
    public void Dispose() { Stop(); _disposed = true; }
}
