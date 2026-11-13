using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;
using Windows.Devices.Enumeration;
using WinStatusBar.Core.Metrics;
using WinStatusBar.Services;

namespace WinStatusBar.Views.Panels;

/// <summary>
/// Painel de rede no formato do Gerenciador de Tarefas (taxa de transferência de envio e recebimento, endereços,
/// SSID, padrão 802.11 e sinal) com o teste de velocidade no estilo das Configurações rápidas do Windows.
/// </summary>
internal sealed class NetworkDetailsPanel : IDetailsPanel
{
    private readonly MonitoringHub _hub;
    private readonly Grid _root = new();
    private readonly StackPanel _main = new() { Spacing = 4 };
    private readonly SpeedTestView _speed;
    private readonly TextBlock _title = new() { Text = global::WinStatusBar.Services.Localization.T("Rede"), FontSize = 18, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold };
    private readonly TextBlock _subtitle = PanelKit.Caption(string.Empty, 12);
    private readonly AreaChart _chart = new(92);
    private readonly TextBlock _recvValue = AreaChart.Text("—"), _sendValue = AreaChart.Text("—");
    private readonly Dictionary<string, TextBlock> _fields = new();
    private readonly Grid _specs = new();
    private string? _alias;
    private int _ticks;
    private bool _open, _disposed;
    public FrameworkElement Content { get; }

    private readonly WinStatusBar.Core.Settings.SettingsService _settings;
    private Button _testButton = null!;
    private readonly Button _ethernetButton = new() { Content = "Ethernet · conectada", FontSize = 12, HorizontalAlignment = HorizontalAlignment.Left, Visibility = Visibility.Collapsed };
    private Button? _wifiButton;
    private TextBlock? _wifiLabel;
    private bool _wifiRefreshing;
    private Button? _bluetoothButton;
    private TextBlock? _bluetoothLabel;
    private Button? _hotspotButton;
    private TextBlock? _hotspotLabel;
    private bool _hotspotRefreshing;
    private DeviceWatcher? _bluetoothWatcher;
    private readonly Dictionary<string, DeviceInformation> _bluetoothDevices = new();

    public NetworkDetailsPanel(MonitoringHub hub, WinStatusBar.Core.Settings.SettingsService settings)
    {
        _hub = hub; _settings = settings;
        _speed = new SpeedTestView(() => ShowMain(true));

        var header = new Grid { ColumnSpacing = 12 };
        header.ColumnDefinitions.Add(new ColumnDefinition());
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var titles = new StackPanel(); titles.Children.Add(_title); titles.Children.Add(_subtitle);
        var testButton = PanelKit.Small(new Button { FontSize = 12, Padding = new Thickness(10, 4, 10, 5), VerticalAlignment = VerticalAlignment.Top });
        var testContent = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        testContent.Children.Add(new FontIcon { Glyph = "", FontSize = 13 });
        testContent.Children.Add(new TextBlock { Text = global::WinStatusBar.Services.Localization.T("Teste de velocidade") });
        testButton.Content = testContent;
        testButton.Click += (_, _) => ShowMain(false);
        _testButton = testButton;
        Grid.SetColumn(testButton, 1);
        header.Children.Add(titles); header.Children.Add(testButton);
        _main.Children.Add(header);
        _ethernetButton.Style = ((Button)Microsoft.UI.Xaml.Markup.XamlReader.Load("<Button xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' Style='{StaticResource AccentButtonStyle}' />")).Style;
        _ethernetButton.Click += async (_, _) => await Windows.System.Launcher.LaunchUriAsync(new Uri("ms-settings:network-ethernet"));
        ToolTipService.SetToolTip(_ethernetButton, "Conexão por cabo ativa · abrir configurações de Ethernet");
        _main.Children.Add(_ethernetButton);
        var connections = new Grid { ColumnSpacing = 8, Margin = new Thickness(0, 8, 0, 0) };
        var connectionStatus = PanelKit.Caption("", 11);
        connectionStatus.Visibility = Visibility.Collapsed;
        var links = new[] { "Wi-Fi", "Bluetooth", "Hotspot móvel" };
        for (int i = 0; i < links.Length; i++)
        {
            connections.ColumnDefinitions.Add(new ColumnDefinition());
            var label = links[i];
            var button = PanelKit.Small(new Button { Content = label, FontSize = 12, HorizontalAlignment = HorizontalAlignment.Stretch, Padding = new Thickness(8, 5, 8, 5) });
            {
                var content = new Grid { ColumnSpacing = 6 };
                content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                content.ColumnDefinitions.Add(new ColumnDefinition());
                content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                var icon = new FontIcon
                {
                    FontFamily = new FontFamily("Segoe Fluent Icons"), FontSize = 14,
                    Glyph = label switch { "Wi-Fi" => "\uE701", "Bluetooth" => "\uE702", _ => "\uEC15" },
                    VerticalAlignment = VerticalAlignment.Center,
                };
                var name = new TextBlock { Text = label, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 130, VerticalAlignment = VerticalAlignment.Center };
                Grid.SetColumn(name, 1);
                content.Children.Add(icon); content.Children.Add(name);
                if (label is "Wi-Fi" or "Bluetooth")
                {
                    var arrow = new FontIcon { Glyph = "\uE70D", FontSize = 10 }; Grid.SetColumn(arrow, 2);
                    content.Children.Add(arrow);
                }
                button.Content = content;
                if (label == "Wi-Fi") { _wifiButton = button; _wifiLabel = name; }
                else if (label == "Bluetooth") { _bluetoothButton = button; _bluetoothLabel = name; }
                else { _hotspotButton = button; _hotspotLabel = name; }
            }
            button.Click += async (_, _) =>
            {
                button.IsEnabled = false;
                try { await ConnectionPicker.ShowAsync(button, label, message => { connectionStatus.Text = message; connectionStatus.Visibility = Visibility.Visible; }); }
                finally { button.IsEnabled = true; }
            };
            Grid.SetColumn(button, i); connections.Children.Add(button);
        }
        connections.Visibility = settings.Current.NetworkConnectionsVisible ? Visibility.Visible : Visibility.Collapsed;
        _main.Children.Add(connections);
        _main.Children.Add(connectionStatus);


        // Recebimento: linha contínua com área; envio: tracejado, como no Gerenciador de Tarefas.
        _chart.Root.Margin = new Thickness(0, 10, 0, 0);
        _chart.Left.Children.Add(AreaChart.Text(global::WinStatusBar.Services.Localization.T("Receber"))); _chart.Left.Children.Add(PanelKit.Dot()); _chart.Left.Children.Add(_recvValue);
        _chart.Right.Children.Add(AreaChart.Text(global::WinStatusBar.Services.Localization.T("Enviar"), secondary: true)); _chart.Right.Children.Add(PanelKit.Dot()); _chart.Right.Children.Add(_sendValue);
        _main.Children.Add(_chart.Root);
        var legend = PanelKit.Caption(global::WinStatusBar.Services.Localization.T("Linha contínua: recebimento · tracejada: envio · últimos 60 segundos"), 11);
        legend.Margin = new Thickness(2, 2, 0, 0);
        _main.Children.Add(legend);

        var live = new StackPanel { Spacing = 8 };
        live.Children.Add(PanelKit.FieldRow(_fields, global::WinStatusBar.Services.Localization.T("Enviar")));
        live.Children.Add(PanelKit.FieldRow(_fields, global::WinStatusBar.Services.Localization.T("Receber")));
        _main.Children.Add(PanelKit.Summary(live, _specs));

        _speed.Root.Visibility = Visibility.Collapsed;
        _root.Children.Add(_main); _root.Children.Add(_speed.Root);
        Content = PanelKit.Body(new StackPanel { Children = { _root } });
    }

    private void ShowMain(bool main)
    {
        _main.Visibility = main ? Visibility.Visible : Visibility.Collapsed;
        _speed.Root.Visibility = main ? Visibility.Collapsed : Visibility.Visible;
    }

    public void Start()
    {
        if (_disposed || _open) return;
        _open = true; _ticks = 0; _alias = null;
        _ = RefreshWifiButtonAsync();
        _ = RefreshHotspotButtonAsync();
        StartBluetoothWatcher();
        _testButton.Visibility = _settings.Current.SpeedTestEnabled ? Visibility.Visible : Visibility.Collapsed;
        _hub.CpuSampled += Render;
        AutoSpeedTest.Changed += OnAutoTestChanged;
        Render();
    }

    public void Stop()
    {
        if (!_open) return;
        _open = false;
        var watcher = _bluetoothWatcher;
        _bluetoothWatcher = null;
        if (watcher?.Status is DeviceWatcherStatus.Started or DeviceWatcherStatus.EnumerationCompleted) watcher.Stop();
        _bluetoothDevices.Clear();
        _hub.CpuSampled -= Render;
        AutoSpeedTest.Changed -= OnAutoTestChanged;
        _speed.Cancel();
        ShowMain(true);
    }

    private void Render()
    {
        if (!_open || _disposed || _hub.LastNetwork is not { } net) return;
        try
        {
            double up = net.UploadBytesPerSec ?? 0, down = net.DownloadBytesPerSec ?? 0;
            _recvValue.Text = PanelKit.Bits(down); _sendValue.Text = PanelKit.Bits(up);
            _fields[global::WinStatusBar.Services.Localization.T("Enviar")].Text = PanelKit.Bits(up); _fields[global::WinStatusBar.Services.Localization.T("Receber")].Text = PanelKit.Bits(down);
            var recv = _hub.NetDownHistory.Snapshot(); var send = _hub.NetUpHistory.Snapshot();
            double max = Math.Max(12_500, Math.Max(recv.DefaultIfEmpty(0).Max(), send.DefaultIfEmpty(0).Max()) * 1.2);
            _chart.Update(new ChartSeries(recv, max), new ChartSeries(send, max, Dashed: true, Fill: false, Opacity: 0.75));

            // Adaptador ativo (Wi-Fi tem prioridade, como o ícone da barra); detalhes a cada 5 s ou quando muda.
            var active = net.AllInterfaces.Where(n => net.ActiveInterfaces.Contains(n.Alias)).OrderByDescending(n => n.Type == 71).ThenByDescending(n => n.Type == 6).FirstOrDefault();
            var ethernet = net.AllInterfaces.FirstOrDefault(n => n.Type == 6 && net.ActiveInterfaces.Contains(n.Alias));
            _ethernetButton.Visibility = ethernet is not null ? Visibility.Visible : Visibility.Collapsed;
            _ethernetButton.Content = (ethernet?.Alias ?? "Ethernet") + " · conectada";
            string? alias = active?.Alias;
            if (alias != _alias || ++_ticks % 5 == 0) { _alias = alias; _ = LoadDetailsAsync(alias); _ = RefreshWifiButtonAsync(); _ = RefreshHotspotButtonAsync(); }
        }
        catch (Exception ex) { App.Log("Painel rede: " + ex.Message); }
    }

    private async Task RefreshWifiButtonAsync()
    {
        if (_wifiRefreshing || _wifiButton is null || _wifiLabel is null) return;
        _wifiRefreshing = true;
        try
        {
            var connected = await Task.Run(() => WifiNetworks.Read(false).FirstOrDefault(n => n.Connected));
            if (!_open || _disposed) return;
            _wifiLabel.Text = connected?.Name ?? "Wi-Fi";
            ToolTipService.SetToolTip(_wifiButton, connected is null ? "Escolher rede Wi-Fi" : connected.Name + " · conectado · escolher rede");
            if (connected is not null) _wifiButton.Style = _ethernetButton.Style;
            else _wifiButton.ClearValue(FrameworkElement.StyleProperty);
        }
        catch (Exception ex) { App.Log("Estado Wi-Fi: " + ex.Message); _wifiLabel.Text = "Wi-Fi"; _wifiButton.ClearValue(FrameworkElement.StyleProperty); }
        finally { _wifiRefreshing = false; }
    }

    /// <summary>Hotspot ligado: botão em destaque (como Wi-Fi e Bluetooth conectados) e o número de dispositivos.</summary>
    private async Task RefreshHotspotButtonAsync()
    {
        if (_hotspotRefreshing || _hotspotButton is null || _hotspotLabel is null) return;
        _hotspotRefreshing = true;
        try
        {
            var state = await Task.Run(HotspotState.Read);
            if (!_open || _disposed) return;
            _hotspotLabel.Text = HotspotState.Label(state);
            ToolTipService.SetToolTip(_hotspotButton, HotspotState.Tooltip(state));
            if (state is { On: true }) _hotspotButton.Style = _ethernetButton.Style;
            else _hotspotButton.ClearValue(FrameworkElement.StyleProperty);
        }
        catch (Exception ex) { App.Log("Estado do hotspot: " + ex.Message); }
        finally { _hotspotRefreshing = false; }
    }

    private void StartBluetoothWatcher()
    {
        _bluetoothDevices.Clear();
        RenderBluetoothButton();
        try
        {
            var watcher = _bluetoothWatcher = BluetoothConnections.CreateWatcher(false);
            void Update(Action action) => _root.DispatcherQueue.TryEnqueue(() =>
            {
                if (!_open || _disposed || _bluetoothWatcher != watcher) return;
                action(); RenderBluetoothButton();
            });
            watcher.Added += (_, device) => Update(() => _bluetoothDevices[device.Id] = device);
            watcher.Updated += (_, change) => Update(() =>
            {
                if (_bluetoothDevices.TryGetValue(change.Id, out var device)) device.Update(change);
            });
            watcher.Removed += (_, change) => Update(() => _bluetoothDevices.Remove(change.Id));
            watcher.Start();
        }
        catch (Exception ex) { App.Log("Estado Bluetooth: " + ex.Message); }
    }

    private void RenderBluetoothButton()
    {
        if (_bluetoothButton is null || _bluetoothLabel is null) return;
        var names = _bluetoothDevices.Values.Where(BluetoothConnections.IsConnected)
            .Select(d => string.IsNullOrWhiteSpace(d.Name) ? "Dispositivo Bluetooth" : d.Name)
            .Distinct(StringComparer.CurrentCultureIgnoreCase).OrderBy(n => n).ToArray();
        _bluetoothLabel.Text = names.Length == 0 ? "Bluetooth" : names[0] + (names.Length > 1 ? $" +{names.Length - 1}" : "");
        ToolTipService.SetToolTip(_bluetoothButton, names.Length == 0 ? "Escolher dispositivo Bluetooth" : "Bluetooth · conectado: " + string.Join(", ", names));
        if (names.Length > 0) _bluetoothButton.Style = _ethernetButton.Style;
        else _bluetoothButton.ClearValue(FrameworkElement.StyleProperty);
    }

    private async Task LoadDetailsAsync(string? alias)
    {
        if (alias is null) { _title.Text = global::WinStatusBar.Services.Localization.T("Rede"); _subtitle.Text = global::WinStatusBar.Services.Localization.T("Sem conexão"); PanelKit.FillSpecs(_specs, []); return; }
        var d = await Task.Run(() => NetworkDetails.Read(alias));
        if (_disposed || alias != _alias || d is null) return;
        _title.Text = d.IsWireless ? "Wi-Fi" : d.Alias;
        _subtitle.Text = d.Description;
        var rows = new List<(string, string)> { (global::WinStatusBar.Services.Localization.T("Nome do adaptador"), d.Alias) };
        if (d.Wifi is { } w)
        {
            rows.Add(("SSID", w.Ssid));
            if (d.DnsSuffix is { } dns) rows.Add((global::WinStatusBar.Services.Localization.T("Nome DNS"), dns));
            rows.Add((global::WinStatusBar.Services.Localization.T("Tipo de conexão"), w.Standard));
        }
        else
        {
            if (d.DnsSuffix is { } dns) rows.Add((global::WinStatusBar.Services.Localization.T("Nome DNS"), dns));
            rows.Add((global::WinStatusBar.Services.Localization.T("Tipo de conexão"), d.IsWireless ? "Wi-Fi" : "Ethernet"));
        }
        if (d.IPv4 is { } v4) rows.Add((global::WinStatusBar.Services.Localization.T("Endereço IPv4"), v4));
        if (d.IPv6 is { } v6) rows.Add((global::WinStatusBar.Services.Localization.T("Endereço IPv6"), v6));
        if (d.Wifi is { } wifi)
        {
            string quality = wifi.SignalPercent >= 80 ? global::WinStatusBar.Services.Localization.T("Excelente") : wifi.SignalPercent >= 60 ? global::WinStatusBar.Services.Localization.T("Boa") : wifi.SignalPercent >= 40 ? global::WinStatusBar.Services.Localization.T("Razoável") : global::WinStatusBar.Services.Localization.T("Fraca");
            rows.Add((global::WinStatusBar.Services.Localization.T("Intensidade do sinal"), $"{wifi.SignalPercent}% ({quality})"));
            rows.Add((global::WinStatusBar.Services.Localization.T("Taxa do link"), $"{wifi.ReceiveMbps} / {wifi.TransmitMbps} Mbps"));
        }
        else if (d.LinkBitsPerSecond > 0)
            rows.Add((global::WinStatusBar.Services.Localization.T("Velocidade do link"), d.LinkBitsPerSecond >= 1_000_000_000 ? $"{d.LinkBitsPerSecond / 1e9:0.#} Gbps" : $"{d.LinkBitsPerSecond / 1e6:0} Mbps"));
        if (_settings.Current.SpeedTestEnabled) rows.Add((global::WinStatusBar.Services.Localization.T("Teste automático"), AutoTestText()));
        PanelKit.FillSpecs(_specs, rows);
    }

    private static string AutoTestText()
    {
        static string Mbps(double? v) => v is double x ? x.ToString(x >= 100 ? "0" : "0.#") : "—";
        if (AutoSpeedTest.Running) return global::WinStatusBar.Services.Localization.T("testando…");
        if (AutoSpeedTest.Paused is { } paused) return paused;
        if (AutoSpeedTest.Last is not { } r) return global::WinStatusBar.Services.Localization.T("primeiro teste em instantes");
        int minutes = (int)(DateTimeOffset.Now - AutoSpeedTest.LastAt).TotalMinutes;
        string ago = minutes < 1 ? global::WinStatusBar.Services.Localization.T("agora") : global::WinStatusBar.Services.Localization.F("há {0} min", minutes);
        string ping = r.LatencyMs is double p ? $" · ping {p:0} ms" : "";
        return $"D {Mbps(r.DownloadMbps)} · U {Mbps(r.UploadMbps)} Mbps{ping} · {ago}";
    }

    private void OnAutoTestChanged() { if (_open && !_disposed) _ = LoadDetailsAsync(_alias); }

    public void Dispose() { _disposed = true; Stop(); }
}

/// <summary>
/// Teste de velocidade com mostrador semicircular (escala 0–1000+ Mbps, não linear), como nas Configurações
/// rápidas do Windows 11. Só executa quando o usuário clica em "Testar".
/// </summary>
internal sealed class SpeedTestView
{
    private static readonly double[] Ticks = [0, 5, 10, 50, 100, 250, 500, 750, 1000];
    private const double Size = 260, Radius = 96, StartAngle = 135, Sweep = 270;
    private readonly Canvas _gauge = new() { Width = Size, Height = 210 };
    private readonly Microsoft.UI.Xaml.Shapes.Path _track = new() { StrokeThickness = 10, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round };
    private const int ColorSegments = 90;
    private readonly List<Microsoft.UI.Xaml.Shapes.Path> _colorArc = [];
    private readonly SolidColorBrush _knobBrush = new();
    private readonly Ellipse _knob = new() { Width = 20, Height = 20 };
    private readonly TextBlock _value = new() { Text = "0", FontSize = 40, HorizontalAlignment = HorizontalAlignment.Center };
    private readonly TextBlock _unit = PanelKit.Caption(global::WinStatusBar.Services.Localization.T("Megabits por segundo"), 12);
    private readonly TextBlock _phase = PanelKit.Caption(string.Empty, 12);
    private readonly TextBlock _down = new() { Text = "—", FontSize = 20, HorizontalAlignment = HorizontalAlignment.Center };
    private readonly TextBlock _up = new() { Text = "—", FontSize = 20, HorizontalAlignment = HorizontalAlignment.Center };
    private readonly TextBlock _ping = new() { Text = "—", FontSize = 20, HorizontalAlignment = HorizontalAlignment.Center };
    private readonly Button _run = new() { Content = global::WinStatusBar.Services.Localization.T("Testar"), MinWidth = 96, HorizontalAlignment = HorizontalAlignment.Center };
    private CancellationTokenSource? _cts;
    public StackPanel Root { get; } = new() { Spacing = 8 };
    private double _displayed, _target;
    private bool _animating;
    private long _frame;
    private readonly TranslateTransform _knobPosition = new();

    public SpeedTestView(Action back)
    {
        var header = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var backButton = new Button { Content = new FontIcon { Glyph = "", FontSize = 13 }, Padding = new Thickness(8, 6, 8, 6), Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent), BorderThickness = new Thickness(0) };
        backButton.Click += (_, _) => { Cancel(); back(); };
        ToolTipService.SetToolTip(backButton, global::WinStatusBar.Services.Localization.T("Voltar"));
        header.Children.Add(backButton);
        header.Children.Add(new TextBlock { Text = global::WinStatusBar.Services.Localization.T("Teste de velocidade"), FontSize = 16, VerticalAlignment = VerticalAlignment.Center });
        Root.Children.Add(header);

        _knob.RenderTransform = _knobPosition;
        Root.Unloaded += (_, _) => Cancel();
        BuildGauge();
        Root.ActualThemeChanged += (_, _) => { ApplyGaugeColors(); DrawGauge(_displayed); };
        var center = new Grid { Width = Size, HorizontalAlignment = HorizontalAlignment.Center };
        center.Children.Add(_gauge);
        var readout = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 72, 0, 0) };
        _unit.HorizontalAlignment = HorizontalAlignment.Center; _phase.HorizontalAlignment = HorizontalAlignment.Center;
        readout.Children.Add(_value); readout.Children.Add(_unit); readout.Children.Add(_phase);
        center.Children.Add(readout);
        Root.Children.Add(center);

        if (Application.Current.Resources.TryGetValue("AccentButtonStyle", out var style)) _run.Style = (Style)style;
        _run.Click += async (_, _) => await RunAsync();
        Root.Children.Add(_run);

        var results = new Grid
        {
            Margin = new Thickness(0, 8, 0, 0), CornerRadius = new CornerRadius(8),
            Background = PanelKit.Res("CardBackgroundFillColorDefaultBrush"), BorderBrush = PanelKit.Res("CardStrokeColorDefaultBrush"), BorderThickness = new Thickness(1),
        };
        for (int i = 0; i < 3; i++) results.ColumnDefinitions.Add(new ColumnDefinition());
        AddResult(results, 0, _down, global::WinStatusBar.Services.Localization.T("Mbps download"));
        AddResult(results, 1, _up, global::WinStatusBar.Services.Localization.T("Mbps upload"));
        AddResult(results, 2, _ping, global::WinStatusBar.Services.Localization.T("ms latência"));
        Root.Children.Add(results);
        var note = PanelKit.Caption(global::WinStatusBar.Services.Localization.T("Usa os servidores públicos de teste da Cloudflare (speed.cloudflare.com). Só dados de teste são trafegados; o teste consome alguns MB."), 11);
        note.Margin = new Thickness(2, 4, 2, 0);
        Root.Children.Add(note);
    }

    private static void AddResult(Grid grid, int column, TextBlock value, string label)
    {
        var cell = new StackPanel { Padding = new Thickness(8, 10, 8, 10), BorderBrush = PanelKit.Res("CardStrokeColorDefaultBrush"), BorderThickness = new Thickness(column > 0 ? 1 : 0, 0, 0, 0) };
        var caption = PanelKit.Caption(label, 12); caption.HorizontalAlignment = HorizontalAlignment.Center;
        cell.Children.Add(value); cell.Children.Add(caption);
        Grid.SetColumn(cell, column); grid.Children.Add(cell);
    }

    private static Point PointAt(double fraction, double radius)
    {
        double angle = (StartAngle + Sweep * fraction) * Math.PI / 180;
        return new Point(Size / 2 + radius * Math.Cos(angle), 110 + radius * Math.Sin(angle));
    }

    private static PathGeometry Arc(double fraction)
    {
        fraction = Math.Clamp(fraction, 0.0005, 1);
        var figure = new PathFigure { StartPoint = PointAt(0, Radius), IsClosed = false, IsFilled = false };
        figure.Segments.Add(new ArcSegment
        {
            Point = PointAt(fraction, Radius), Size = new Size(Radius, Radius),
            IsLargeArc = Sweep * fraction > 180, SweepDirection = SweepDirection.Clockwise,
        });
        var g = new PathGeometry(); g.Figures.Add(figure); return g;
    }

    /// <summary>Posição no mostrador: linear entre as marcas (0, 5, 10, 50, 100, 250, 500, 750, 1000+).</summary>
    private static double Fraction(double mbps)
    {
        if (mbps <= 0) return 0;
        for (int i = 1; i < Ticks.Length; i++)
            if (mbps <= Ticks[i]) return (i - 1 + (mbps - Ticks[i - 1]) / (Ticks[i] - Ticks[i - 1])) / (Ticks.Length - 1);
        return 1;
    }

    private void BuildGauge()
    {
        _track.Stroke = PanelKit.Res("ControlStrongFillColorDisabledBrush");
        _track.Data = Arc(1);
        _gauge.Children.Add(_track);
        // Short vector arcs provide an angular gradient, independent of resolution.
        // Geometry and brushes are retained; only the frontier changes during animation.
        for (int i = 0; i < ColorSegments; i++)
        {
            var geometry = Arc((i + 1.0) / ColorSegments);
            geometry.Figures[0].StartPoint = PointAt((double)i / ColorSegments, Radius);
            ((ArcSegment)geometry.Figures[0].Segments[0]).IsLargeArc = false;
            var part = new Microsoft.UI.Xaml.Shapes.Path
            {
                Data = geometry, StrokeThickness = 10,
                StrokeStartLineCap = i == 0 ? PenLineCap.Round : PenLineCap.Flat,
                StrokeEndLineCap = PenLineCap.Flat, Visibility = Visibility.Collapsed,
            };
            _colorArc.Add(part); _gauge.Children.Add(part);
        }
        _knob.Fill = _knobBrush;
        ApplyGaugeColors();
        _gauge.Children.Add(_knob);
        for (int i = 0; i < Ticks.Length; i++)
        {
            var p = PointAt((double)i / (Ticks.Length - 1), Radius + 22);
            var label = PanelKit.Caption(Ticks[i] >= 1000 ? "1000+" : Ticks[i].ToString("0"), 11);
            label.Measure(new Size(100, 100));
            Canvas.SetLeft(label, p.X - label.DesiredSize.Width / 2);
            Canvas.SetTop(label, p.Y - label.DesiredSize.Height / 2);
            _gauge.Children.Add(label);
        }
        SetGauge(0);
    }

    private void SetGauge(double mbps)
    {
        _target = Math.Max(0, mbps);
        if (!_gauge.IsLoaded || Root.Visibility != Visibility.Visible || !new Windows.UI.ViewManagement.UISettings().AnimationsEnabled)
        { StopAnimation(); DrawGauge(_displayed = _target); return; }
        if (_animating) return;
        _animating = true; _frame = System.Diagnostics.Stopwatch.GetTimestamp();
        CompositionTarget.Rendering += AnimateGauge;
    }
    private void AnimateGauge(object? sender, object e)
    {
        long now = System.Diagnostics.Stopwatch.GetTimestamp();
        double dt = Math.Clamp((now - _frame) / (double)System.Diagnostics.Stopwatch.Frequency, 0, .1); _frame = now;
        _displayed += (_target - _displayed) * (1 - Math.Exp(-dt / .12));
        if (Math.Abs(_target - _displayed) < .03) { _displayed = _target; StopAnimation(); }
        DrawGauge(_displayed);
    }
    private void StopAnimation() { CompositionTarget.Rendering -= AnimateGauge; _animating = false; }
    private void DrawGauge(double mbps)
    {
        double f = Fraction(mbps);
        for (int i = 0; i < _colorArc.Count; i++)
        {
            var part = _colorArc[i];
            bool visible = f > (double)i / ColorSegments;
            var visibility = visible ? Visibility.Visible : Visibility.Collapsed;
            if (part.Visibility != visibility) part.Visibility = visibility;
            if (!visible) continue;
            // Tiny overlap prevents antialias seams between adjacent vector segments.
            var end = PointAt(Math.Min(f, (i + 1.0) / ColorSegments + .0002), Radius);
            var segment = (ArcSegment)((PathGeometry)part.Data).Figures[0].Segments[0];
            if (segment.Point != end) segment.Point = end;
        }
        _knobBrush.Color = GaugeColor(f);
        var p = PointAt(f, Radius);
        _knobPosition.X = p.X - _knob.Width / 2; _knobPosition.Y = p.Y - _knob.Height / 2;
        string text = mbps >= 100 ? mbps.ToString("0") : mbps.ToString("0.#");
        if (_value.Text != text) _value.Text = text;
    }
    public void Cancel() { _cts?.Cancel(); StopAnimation(); }

    private Windows.UI.Color GaugeColor(double fraction)
    {
        // Darker variants retain contrast against light surfaces; neutral labels stay unchanged.
        bool light = Root.ActualTheme == ElementTheme.Light;
        Windows.UI.Color[] colors = light
            ? [Windows.UI.Color.FromArgb(255, 37, 99, 235), Windows.UI.Color.FromArgb(255, 0, 140, 170), Windows.UI.Color.FromArgb(255, 20, 153, 91), Windows.UI.Color.FromArgb(255, 113, 156, 24), Windows.UI.Color.FromArgb(255, 190, 133, 9)]
            : [Windows.UI.Color.FromArgb(255, 67, 132, 255), Windows.UI.Color.FromArgb(255, 23, 198, 222), Windows.UI.Color.FromArgb(255, 43, 213, 139), Windows.UI.Color.FromArgb(255, 161, 215, 66), Windows.UI.Color.FromArgb(255, 246, 195, 65)];
        double scaled = Math.Clamp(fraction, 0, 1) * (colors.Length - 1);
        int index = Math.Min((int)scaled, colors.Length - 2);
        double blend = scaled - index;
        var a = colors[index]; var b = colors[index + 1];
        byte Mix(byte x, byte y) => (byte)Math.Round(x + (y - x) * blend);
        return Windows.UI.Color.FromArgb(255, Mix(a.R, b.R), Mix(a.G, b.G), Mix(a.B, b.B));
    }

    private void ApplyGaugeColors()
    {
        _track.Stroke = PanelKit.Res("ControlStrongFillColorDisabledBrush");
        for (int i = 0; i < _colorArc.Count; i++)
        {
            double start = (double)i / ColorSegments, end = (i + 1.0) / ColorSegments;
            var brush = new LinearGradientBrush
            {
                MappingMode = BrushMappingMode.Absolute,
                StartPoint = PointAt(start, Radius), EndPoint = PointAt(end, Radius),
            };
            brush.GradientStops.Add(new GradientStop { Offset = 0, Color = GaugeColor(start) });
            brush.GradientStops.Add(new GradientStop { Offset = 1, Color = GaugeColor(end) });
            _colorArc[i].Stroke = brush;
        }
    }

    private async Task RunAsync()
    {
        if (_cts is not null) { Cancel(); return; }
        using var run = new CancellationTokenSource();
        _cts = run;
        AutoSpeedTest.ManualRunning = true;
        AutoSpeedTest.CancelCurrent();
        var ui = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
        _run.Content = global::WinStatusBar.Services.Localization.T("Cancelar");
        _down.Text = _up.Text = _ping.Text = "—";
        var test = new SpeedTest();
        test.Progress += (phase, value) => ui.TryEnqueue(() =>
        {
            if (_cts != run || run.IsCancellationRequested) return;
            switch (phase)
            {
                case SpeedTestPhase.Latency: _phase.Text = global::WinStatusBar.Services.Localization.T("Medindo latência…"); _ping.Text = value.ToString("0"); break;
                case SpeedTestPhase.Download: _phase.Text = global::WinStatusBar.Services.Localization.T("Download"); SetGauge(value); _down.Text = value.ToString(value >= 100 ? "0" : "0.#"); break;
                case SpeedTestPhase.Upload: _phase.Text = global::WinStatusBar.Services.Localization.T("Upload"); SetGauge(value); _up.Text = value.ToString(value >= 100 ? "0" : "0.#"); break;
            }
        });
        try
        {
            _phase.Text = "Conectando…";
            var r = await Task.Run(() => test.RunAsync(run.Token));
            run.Token.ThrowIfCancellationRequested();
            _down.Text = r.DownloadMbps is double d ? d.ToString(d >= 100 ? "0" : "0.#") : "—";
            _up.Text = r.UploadMbps is double u ? u.ToString(u >= 100 ? "0" : "0.#") : "—";
            _ping.Text = r.LatencyMs is double p ? p.ToString("0") : "—";
            SetGauge(r.DownloadMbps ?? 0);
            _phase.Text = r.DownloadMbps is null ? global::WinStatusBar.Services.Localization.T("Não foi possível concluir o teste.") : global::WinStatusBar.Services.Localization.T("Teste concluído");
        }
        catch (OperationCanceledException) { _phase.Text = global::WinStatusBar.Services.Localization.T("Teste cancelado."); StopAnimation(); DrawGauge(_displayed = _target = 0); }
        catch (Exception ex) { _phase.Text = "Falha no teste: " + ex.Message; StopAnimation(); DrawGauge(_displayed = _target = 0); }
        finally { _cts = null; AutoSpeedTest.ManualRunning = false; _run.Content = global::WinStatusBar.Services.Localization.T("Testar novamente"); }
    }
}
