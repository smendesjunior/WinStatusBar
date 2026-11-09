using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WinStatusBar.Core.Metrics;
using WinStatusBar.Core.Settings;
using WinStatusBar.Services;

namespace WinStatusBar.Views.Panels;

/// <summary>
/// Painel da GPU no formato do Gerenciador de Tarefas: escolha da placa, quatro gráficos de motores
/// (3D, Copy, Video…), memória dedicada e compartilhada, leituras e dados do driver/DirectX.
/// </summary>
internal sealed class GpuDetailsPanel : IDetailsPanel
{
    private static readonly string[] EngineOrder = ["3D", "Copy", "VideoEncode", "VideoDecode", "VideoProcessing", "Compute_0", "Compute_1", "Graphics_1"];
    private readonly MonitoringHub _hub;
    private readonly SettingsService _settings;
    private readonly TextBlock _subtitle = PanelKit.Caption(string.Empty, 12);
    private readonly ComboBox _adapterBox = new() { MinWidth = 150, MaxWidth = 190, FontSize = 12 };
    private readonly Grid _engines = new() { ColumnSpacing = 8, RowSpacing = 8, Margin = new Thickness(0, 10, 0, 0) };
    private readonly List<(string Key, AreaChart Chart, TextBlock Value)> _engineCharts = new();
    private readonly AreaChart _dedicated = new(36, compact: true), _shared = new(36, compact: true);
    private readonly TextBlock _dedicatedValue = AreaChart.Text(secondary: true, size: 12), _sharedValue = AreaChart.Text(secondary: true, size: 12);
    private readonly Dictionary<string, TextBlock> _fields = new();
    private readonly StackPanel _dedicatedField;
    private readonly Grid _specs = new();
    private long _specsFor;
    private bool _open, _disposed, _filling;
    private IReadOnlyList<GpuAdapterInfo> _listedAdapters = [];
    public FrameworkElement Content { get; }

    public GpuDetailsPanel(MonitoringHub hub, SettingsService settings)
    {
        _hub = hub; _settings = settings;
        var body = new StackPanel { Spacing = 4 };
        body.Children.Add(PanelKit.Header(global::WinStatusBar.Services.Localization.T("GPU"), _subtitle, _adapterBox));
        _adapterBox.SelectionChanged += OnAdapterChanged;
        ToolTipService.SetToolTip(_adapterBox, global::WinStatusBar.Services.Localization.T("Placa acompanhada na barra"));

        body.Children.Add(_engines);

        _dedicated.Root.Margin = new Thickness(0, 8, 0, 0);
        _dedicated.Left.Children.Add(AreaChart.Text(global::WinStatusBar.Services.Localization.T("Memória dedicada"), size: 12)); _dedicated.Right.Children.Add(_dedicatedValue);
        _shared.Root.Margin = new Thickness(0, 6, 0, 0);
        _shared.Left.Children.Add(AreaChart.Text(global::WinStatusBar.Services.Localization.T("Memória compartilhada"), size: 12)); _shared.Right.Children.Add(_sharedValue);
        body.Children.Add(_dedicated.Root); body.Children.Add(_shared.Root);

        var live = new StackPanel { Spacing = 8 };
        live.Children.Add(PanelKit.FieldRow(_fields, global::WinStatusBar.Services.Localization.T("Utilização"), global::WinStatusBar.Services.Localization.T("Temperatura")));
        live.Children.Add(PanelKit.FieldRow(_fields, global::WinStatusBar.Services.Localization.T("Memória da GPU")));
        _dedicatedField = PanelKit.FieldRow(_fields, global::WinStatusBar.Services.Localization.T("Dedicada"));
        live.Children.Add(_dedicatedField);
        live.Children.Add(PanelKit.FieldRow(_fields, global::WinStatusBar.Services.Localization.T("Compartilhada")));
        body.Children.Add(PanelKit.Summary(live, _specs));

        Content = PanelKit.Body(body);
    }

    public void Start()
    {
        if (_disposed || _open) return;
        _open = true;
        FillAdapters();
        _hub.CpuSampled += Render;
        Render();
    }

    public void Stop()
    {
        if (!_open) return;
        _open = false;
        _hub.CpuSampled -= Render;
    }

    private void FillAdapters()
    {
        _filling = true;
        _adapterBox.Items.Clear();
        _adapterBox.Items.Add(new ComboBoxItem { Content = global::WinStatusBar.Services.Localization.T("Automático"), Tag = "" });
        _listedAdapters = _hub.LastGpu?.Adapters ?? _hub.Gpu.Adapters;
        foreach (var a in _listedAdapters)
            _adapterBox.Items.Add(new ComboBoxItem { Content = $"{(a.IsIntegrated ? "iGPU" : "dGPU")} · {a.Name}", Tag = a.Name });
        string preferred = _settings.Current.GpuAdapter ?? string.Empty;
        if (preferred.Length > 0 && !_listedAdapters.Any(a => a.Name.Equals(preferred, StringComparison.OrdinalIgnoreCase)))
            _adapterBox.Items.Add(new ComboBoxItem { Content = preferred + " — indisponível", Tag = preferred, IsEnabled = false });
        _adapterBox.SelectedItem = _adapterBox.Items.OfType<ComboBoxItem>().FirstOrDefault(i => string.Equals((string)i.Tag, preferred, StringComparison.OrdinalIgnoreCase))
                                   ?? _adapterBox.Items[0];
        _filling = false;
    }

    private void OnAdapterChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_filling || _adapterBox.SelectedItem is not ComboBoxItem item) return;
        string? name = string.IsNullOrEmpty((string)item.Tag) ? null : (string)item.Tag;
        if (string.Equals(_settings.Current.GpuAdapter, name, StringComparison.OrdinalIgnoreCase)) return;
        _settings.Current.GpuAdapter = name;
        _settings.Save();
        _hub.Gpu.PreferredAdapter = name;
        _hub.ResampleGpuNow();
    }

    private void Render()
    {
        if (!_open || _disposed) return;
        try
        {
            var gpu = _hub.LastGpu;
            if (gpu is not null && !_listedAdapters.SequenceEqual(gpu.Adapters)) FillAdapters();
            var a = gpu?.Adapter;
            if (a is null) { _specsFor = 0; PanelKit.FillSpecs(_specs, []); }
            _subtitle.Text = a?.Name ?? global::WinStatusBar.Services.Localization.T("Nenhuma GPU detectada");
            RenderEngines();

            bool integrated = a?.IsIntegrated == true;
            _dedicated.Root.Visibility = _dedicatedField.Visibility = integrated ? Visibility.Collapsed : Visibility.Visible;
            double dedTotal = a?.DedicatedVideoMemory ?? 0, shTotal = a?.SharedSystemMemory ?? 0;
            double dedUsed = gpu?.DedicatedUsedBytes ?? 0, shUsed = gpu?.SharedUsedBytes ?? 0;
            string Pair(double used, double total) => $"{used / 1073741824.0:0.0}/{total / 1073741824.0:0.0} GB";
            _dedicatedValue.Text = Pair(dedUsed, dedTotal);
            _sharedValue.Text = Pair(shUsed, shTotal);
            _dedicated.Update(new ChartSeries(_hub.GpuDedicatedHistory.Snapshot(), dedTotal));
            _shared.Update(new ChartSeries(_hub.GpuSharedHistory.Snapshot(), shTotal));

            _fields[global::WinStatusBar.Services.Localization.T("Utilização")].Text = gpu?.UtilizationPercent is double u ? $"{u:0}%" : "—";
            _fields[global::WinStatusBar.Services.Localization.T("Temperatura")].Text = _hub.LastTemperature?.Gpu is double t ? $"{t:0} °C" : "N/D";
            _fields[global::WinStatusBar.Services.Localization.T("Memória da GPU")].Text = Pair(dedUsed + shUsed, dedTotal + shTotal);
            _fields[global::WinStatusBar.Services.Localization.T("Dedicada")].Text = Pair(dedUsed, dedTotal);
            _fields[global::WinStatusBar.Services.Localization.T("Compartilhada")].Text = Pair(shUsed, shTotal);

            if (a is not null && a.Luid != _specsFor) { _specsFor = a.Luid; _ = LoadSpecsAsync(a); }
        }
        catch (Exception ex) { App.Log(global::WinStatusBar.Services.Localization.T("Painel GPU: ") + ex.Message); }
    }

    /// <summary>Até quatro motores, na ordem do Gerenciador de Tarefas (3D, Copy, Video Encode/Decode/Processing).</summary>
    private void RenderEngines()
    {
        var history = _hub.GpuEngineHistory;
        var keys = EngineOrder.Where(history.ContainsKey)
            .Concat(history.Keys.Where(k => !EngineOrder.Contains(k, StringComparer.OrdinalIgnoreCase)).OrderBy(k => k))
            .Take(4).ToList();
        if (!keys.SequenceEqual(_engineCharts.Select(c => c.Key)))
        {
            _engines.Children.Clear(); _engines.ColumnDefinitions.Clear(); _engines.RowDefinitions.Clear(); _engineCharts.Clear();
            _engines.ColumnDefinitions.Add(new ColumnDefinition()); _engines.ColumnDefinitions.Add(new ColumnDefinition());
            for (int i = 0; i < keys.Count; i++)
            {
                if (i % 2 == 0) _engines.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                var chart = new AreaChart(44, compact: true);
                var value = AreaChart.Text(secondary: true, size: 12);
                chart.Left.Children.Add(AreaChart.Text(EngineName(keys[i]), size: 12));
                chart.Right.Children.Add(value);
                Grid.SetRow(chart.Root, i / 2); Grid.SetColumn(chart.Root, i % 2);
                _engines.Children.Add(chart.Root);
                _engineCharts.Add((keys[i], chart, value));
            }
        }
        foreach (var (key, chart, value) in _engineCharts)
        {
            var values = history.TryGetValue(key, out var h) ? h.Snapshot() : [];
            value.Text = values.Length > 0 ? $"{values[^1]:0}%" : "0%";
            chart.Update(new ChartSeries(values, 100));
        }
    }

    private static string EngineName(string key)
    {
        var sb = new System.Text.StringBuilder();
        foreach (char c in key.Replace('_', ' '))
        {
            if (char.IsUpper(c) && sb.Length > 0 && char.IsLower(sb[^1])) sb.Append(' ');
            sb.Append(c);
        }
        return sb.ToString();
    }

    private async Task LoadSpecsAsync(GpuAdapterInfo adapter)
    {
        var info = await Task.Run(() => GpuDetails.Read(adapter));
        if (_disposed || adapter.Luid != _specsFor) return;
        var rows = new List<(string, string)>();
        if (info.DriverVersion is { } v) rows.Add((global::WinStatusBar.Services.Localization.T("Versão do driver"), v));
        if (info.DriverDate is { } d) rows.Add((global::WinStatusBar.Services.Localization.T("Data do driver"), d.ToString("dd/MM/yyyy")));
        if (info.DirectX is { } dx) rows.Add((global::WinStatusBar.Services.Localization.T("Versão do DirectX"), dx));
        if (info.Location is { } l) rows.Add((global::WinStatusBar.Services.Localization.T("Local físico"), l));
        rows.Add((global::WinStatusBar.Services.Localization.T("Tipo"), adapter.IsIntegrated ? global::WinStatusBar.Services.Localization.T("Integrada (iGPU)") : global::WinStatusBar.Services.Localization.T("Dedicada (dGPU)")));
        PanelKit.FillSpecs(_specs, rows);
    }

    public void Dispose() { _disposed = true; Stop(); }
}
