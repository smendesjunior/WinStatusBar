using System.Management;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using WinStatusBar.Core.Interop;
using WinStatusBar.Services;

namespace WinStatusBar.Views.Panels;

/// <summary>
/// Painel de memória no formato do Gerenciador de Tarefas: gráfico de uso (60 s contínuos), composição
/// (em uso, modificada, em espera, livre), leituras do gerenciador de memória e dados dos módulos (WMI).
/// Os contadores detalhados só são lidos com o painel aberto.
/// </summary>
internal sealed class MemoryDetailsPanel : IDetailsPanel
{
    private const string Committed = @"\Memory\Committed Bytes", Limit = @"\Memory\Commit Limit";
    private const string Paged = @"\Memory\Pool Paged Bytes", NonPaged = @"\Memory\Pool Nonpaged Bytes";
    private const string Modified = @"\Memory\Modified Page List Bytes", Free = @"\Memory\Free & Zero Page List Bytes";
    private static readonly string[] Standby =
        [@"\Memory\Standby Cache Core Bytes", @"\Memory\Standby Cache Normal Priority Bytes", @"\Memory\Standby Cache Reserve Bytes"];

    private readonly MonitoringHub _hub;
    private PdhQuery? _pdh;
    private readonly TextBlock _subtitle = PanelKit.Caption(global::WinStatusBar.Services.Localization.T("Memória física"), 12);
    private readonly TextBlock _kind = PanelKit.Caption(string.Empty, 13);
    private readonly AreaChart _chart = new(92);
    private readonly TextBlock _usedValue = AreaChart.Text("—"), _usedPercent = AreaChart.Text(), _totalValue = AreaChart.Text("—", secondary: true);
    private readonly Grid _composition = new() { Height = 22, CornerRadius = new CornerRadius(4) };
    private readonly TextBlock _legend = PanelKit.Caption(string.Empty, 11);
    private readonly Dictionary<string, TextBlock> _fields = new();
    private readonly Grid _specs = new();
    private bool _open, _disposed, _specsLoaded;
    public FrameworkElement Content { get; }

    public MemoryDetailsPanel(MonitoringHub hub)
    {
        _hub = hub;
        var body = new StackPanel { Spacing = 4 };
        body.Children.Add(PanelKit.Header(global::WinStatusBar.Services.Localization.T("Memória"), _subtitle, _kind));

        _chart.Root.Margin = new Thickness(0, 10, 0, 0);
        _chart.Left.Children.Add(AreaChart.Text(global::WinStatusBar.Services.Localization.T("Uso da memória")));
        _chart.Left.Children.Add(PanelKit.Dot()); _chart.Left.Children.Add(_usedValue);
        _chart.Left.Children.Add(PanelKit.Dot()); _chart.Left.Children.Add(_usedPercent);
        _chart.Right.Children.Add(_totalValue);
        body.Children.Add(_chart.Root);

        var compositionTitle = PanelKit.Caption(global::WinStatusBar.Services.Localization.T("Composição da memória"), 12);
        compositionTitle.Margin = new Thickness(0, 10, 0, 2);
        body.Children.Add(compositionTitle);
        body.Children.Add(_composition);
        body.Children.Add(_legend);

        var live = new StackPanel { Spacing = 8 };
        live.Children.Add(PanelKit.FieldRow(_fields, global::WinStatusBar.Services.Localization.T("Em uso"), global::WinStatusBar.Services.Localization.T("Disponível")));
        live.Children.Add(PanelKit.FieldRow(_fields, global::WinStatusBar.Services.Localization.T("Confirmado"), global::WinStatusBar.Services.Localization.T("Em cache")));
        live.Children.Add(PanelKit.FieldRow(_fields, global::WinStatusBar.Services.Localization.T("Paginável"), global::WinStatusBar.Services.Localization.T("Não paginável")));
        body.Children.Add(PanelKit.Summary(live, _specs));

        Content = PanelKit.Body(body);
    }

    public void Start()
    {
        if (_disposed || _open) return;
        _open = true;
        if (_pdh is null)
        {
            _pdh = new PdhQuery();
            foreach (var c in new[] { Committed, Limit, Paged, NonPaged, Modified, Free }.Concat(Standby)) _pdh.AddCounter(c);
            _pdh.Collect();
        }
        if (!_specsLoaded) { _specsLoaded = true; _ = LoadSpecsAsync(); }
        _usedPercent.Foreground = PanelKit.Res("AccentTextFillColorPrimaryBrush");
        _hub.CpuSampled += Render;
        Render();
    }

    public void Stop()
    {
        if (!_open) return;
        _open = false;
        _hub.CpuSampled -= Render;
    }

    private void Render()
    {
        if (!_open || _disposed || _hub.LastRam is not { } ram) return;
        try
        {
            double total = ram.TotalBytes, used = ram.TotalBytes - ram.AvailableBytes;
            _usedValue.Text = PanelKit.Gb(used);
            _usedPercent.Text = $"{used / Math.Max(1, total) * 100:0}%";
            _totalValue.Text = PanelKit.Gb(total);
            _chart.Update(new ChartSeries(_hub.RamUsedHistory.Snapshot(), total));

            double? committed = null, limit = null, paged = null, nonPaged = null, modified = null, free = null, standby = null;
            if (_pdh?.Collect() == true)
            {
                committed = _pdh.GetValue(Committed, true); limit = _pdh.GetValue(Limit, true);
                paged = _pdh.GetValue(Paged, true); nonPaged = _pdh.GetValue(NonPaged, true);
                modified = _pdh.GetValue(Modified, true); free = _pdh.GetValue(Free, true);
                var parts = Standby.Select(c => _pdh.GetValue(c, true)).Where(v => v is not null).ToList();
                standby = parts.Count > 0 ? parts.Sum(v => v!.Value) : null;
            }
            _fields[global::WinStatusBar.Services.Localization.T("Em uso")].Text = PanelKit.Gb(used);
            _fields[global::WinStatusBar.Services.Localization.T("Disponível")].Text = PanelKit.Gb(ram.AvailableBytes);
            _fields[global::WinStatusBar.Services.Localization.T("Confirmado")].Text = committed is double c && limit is double l ? $"{c / 1073741824.0:0.0}/{l / 1073741824.0:0.0} GB" : "—";
            _fields[global::WinStatusBar.Services.Localization.T("Em cache")].Text = standby is double s ? PanelKit.Gb(s + (modified ?? 0)) : "—";
            _fields[global::WinStatusBar.Services.Localization.T("Paginável")].Text = paged is double p ? PanelKit.Gb(p) : "—";
            _fields[global::WinStatusBar.Services.Localization.T("Não paginável")].Text = nonPaged is double np ? (np >= 1073741824 ? PanelKit.Gb(np) : $"{np / 1048576.0:0} MB") : "—";
            RenderComposition(used - (modified ?? 0), modified ?? 0, standby ?? 0, free ?? Math.Max(0, ram.AvailableBytes - (standby ?? 0)), total);
        }
        catch (Exception ex) { App.Log(global::WinStatusBar.Services.Localization.T("Painel memória: ") + ex.Message); }
    }

    /// <summary>Barra de composição: em uso (forte), modificada, em espera (suave) e livre (vazia), na cor de destaque.</summary>
    private void RenderComposition(double inUse, double modified, double standby, double free, double total)
    {
        double[] parts = [Math.Max(0, inUse), Math.Max(0, modified), Math.Max(0, standby), Math.Max(0, free)];
        string[] names = [global::WinStatusBar.Services.Localization.T("Em uso"), global::WinStatusBar.Services.Localization.T("Modificada"), global::WinStatusBar.Services.Localization.T("Em espera"), global::WinStatusBar.Services.Localization.T("Livre")];
        double[] alpha = [0.85, 0.55, 0.28, 0];
        if (_composition.ColumnDefinitions.Count != parts.Length)
        {
            _composition.Children.Clear(); _composition.ColumnDefinitions.Clear();
            _composition.BorderBrush = PanelKit.Res("AccentFillColorDefaultBrush");
            _composition.BorderThickness = new Thickness(1);
            for (int i = 0; i < parts.Length; i++)
            {
                _composition.ColumnDefinitions.Add(new ColumnDefinition());
                var segment = new Border { BorderBrush = PanelKit.Res("AccentFillColorDefaultBrush"), BorderThickness = new Thickness(i < parts.Length - 1 ? 0 : 0, 0, i < parts.Length - 1 ? 1 : 0, 0) };
                Grid.SetColumn(segment, i);
                _composition.Children.Add(segment);
            }
        }
        var accent = PanelKit.Accent();
        for (int i = 0; i < parts.Length; i++)
        {
            _composition.ColumnDefinitions[i].Width = new GridLength(Math.Max(parts[i], total * 0.004), GridUnitType.Star);
            var segment = (Border)_composition.Children[i];
            segment.Background = new SolidColorBrush(Windows.UI.Color.FromArgb((byte)(255 * alpha[i]), accent.R, accent.G, accent.B));
            ToolTipService.SetToolTip(segment, $"{names[i]}: {PanelKit.Gb(parts[i])}");
        }
        _legend.Text = global::WinStatusBar.Services.Localization.F("Em uso {0}  ·  Modificada {1:0} MB  ·  Em espera {2}  ·  Livre {3}", new object?[] {PanelKit.Gb(parts[0]),parts[1] / 1048576.0,PanelKit.Gb(parts[2]),PanelKit.Gb(parts[3])});
    }

    private async Task LoadSpecsAsync()
    {
        var installed = _hub.LastRam?.InstalledBytes ?? 0;
        var totalPhys = _hub.LastRam?.TotalBytes ?? 0;
        var (rows, kind) = await Task.Run(() =>
        {
            var list = new List<(string, string)>();
            string kindText = string.Empty;
            try
            {
                int used = 0; uint speed = 0; int form = 0, type = 0; ulong capacity = 0;
                using (var s = new ManagementObjectSearcher("SELECT Capacity,Speed,ConfiguredClockSpeed,FormFactor,SMBIOSMemoryType FROM Win32_PhysicalMemory"))
                using (var found = s.Get())
                    foreach (ManagementObject m in found)
                        using (m)
                        {
                            used++;
                            capacity += Convert.ToUInt64(m["Capacity"] ?? 0);
                            speed = Math.Max(speed, Convert.ToUInt32(m["ConfiguredClockSpeed"] ?? m["Speed"] ?? 0));
                            form = Convert.ToInt32(m["FormFactor"] ?? 0); type = Convert.ToInt32(m["SMBIOSMemoryType"] ?? 0);
                        }
                int slots = 0;
                using (var s = new ManagementObjectSearcher("SELECT MemoryDevices FROM Win32_PhysicalMemoryArray"))
                using (var found = s.Get())
                    foreach (ManagementObject a in found) using (a) slots += Convert.ToInt32(a["MemoryDevices"] ?? 0);
                string typeName = type switch { 20 => "DDR", 21 => "DDR2", 24 => "DDR3", 26 => "DDR4", 27 => "LPDDR", 28 => "LPDDR2", 29 => "LPDDR3", 30 => "LPDDR4", 34 => "DDR5", 35 => "LPDDR5", _ => "" };
                string formName = form switch { 8 => "DIMM", 12 => "SODIMM", 13 => "SRIMM", 0 or 1 => "", _ => global::WinStatusBar.Services.Localization.T("Outro") };
                ulong size = capacity > 0 ? capacity : installed;
                kindText = size > 0 ? $"{size / 1073741824.0:0.0} GB {typeName}".Trim() : typeName;
                if (speed > 0) list.Add((global::WinStatusBar.Services.Localization.T("Velocidade"), $"{speed} MT/s"));
                if (used > 0) list.Add((global::WinStatusBar.Services.Localization.T("Slots usados"), slots > 0 ? $"{used} de {Math.Max(slots, used)}" : used.ToString()));
                if (formName.Length > 0) list.Add((global::WinStatusBar.Services.Localization.T("Fator forma"), formName));
            }
            catch { }
            ulong reserved = installed > totalPhys ? installed - totalPhys : 0;
            if (reserved > 0) list.Add((global::WinStatusBar.Services.Localization.T("Reservada para hardware"), reserved >= 1073741824 ? PanelKit.Gb(reserved) : $"{reserved / 1048576.0:0} MB"));
            return (list, kindText);
        });
        if (_disposed) return;
        _kind.Text = kind;
        PanelKit.FillSpecs(_specs, rows);
    }

    public void Dispose() { _disposed = true; Stop(); _pdh?.Dispose(); _pdh = null; }
}
