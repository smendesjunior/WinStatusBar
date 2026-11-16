using System.Management;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using WinStatusBar.Services;
using WinStatusBar.Views.Panels;

namespace WinStatusBar.Views;

/// <summary>
/// Painel da CPU (clique no módulo): gráfico de uso total, leituras e dados do processador lado a lado e
/// aplicativos abertos no formato do Gerenciador de Tarefas. Os números vêm da mesma amostra da barra
/// (evento <see cref="MonitoringHub.CpuSampled"/>), e o histórico de 60 s é contínuo, já preenchido ao abrir.
/// </summary>
internal sealed class CpuDetailsPanel : IDetailsPanel
{
    private readonly MonitoringHub _hub;
    private readonly TextBlock _name = Caption(string.Empty, 12);
    private readonly AreaChart _chart = new(92);
    private readonly TextBlock _usageValue = AreaChart.Text("—"), _idleValue = AreaChart.Text("—"), _usageState = AreaChart.Text();
    private readonly Dictionary<string, TextBlock> _fields = new();
    private readonly Grid _specs = new();
    private bool _open, _disposed;
    private int _ticks;
    public FrameworkElement Content { get; }

    public CpuDetailsPanel(MonitoringHub hub, WinStatusBar.Core.Settings.SettingsService settings)
    {
        _hub = hub;
        var body = new StackPanel { Spacing = 4 };
        body.Children.Add(PanelKit.Header(global::WinStatusBar.Services.Localization.T("CPU"), _name));

        _chart.Root.Margin = new Thickness(0, 10, 0, 0);
        _chart.Left.Children.Add(AreaChart.Text(global::WinStatusBar.Services.Localization.T("Uso da CPU")));
        _chart.Left.Children.Add(PanelKit.Dot()); _chart.Left.Children.Add(_usageValue);
        _chart.Left.Children.Add(PanelKit.Dot()); _chart.Left.Children.Add(_usageState);
        _chart.Right.Children.Add(AreaChart.Text(global::WinStatusBar.Services.Localization.T("Ociosa"))); _chart.Right.Children.Add(PanelKit.Dot()); _chart.Right.Children.Add(_idleValue);
        body.Children.Add(_chart.Root);

        var live = new StackPanel { Spacing = 8 };
        live.Children.Add(PanelKit.FieldRow(_fields, global::WinStatusBar.Services.Localization.T("Utilização"), global::WinStatusBar.Services.Localization.T("Temperatura")));
        live.Children.Add(PanelKit.FieldRow(_fields, global::WinStatusBar.Services.Localization.T("Velocidade"))); // linha inteira: traz o núcleo mais rápido
        live.Children.Add(PanelKit.FieldRow(_fields, global::WinStatusBar.Services.Localization.T("Processos"), global::WinStatusBar.Services.Localization.T("Threads"), global::WinStatusBar.Services.Localization.T("Identificadores")));
        live.Children.Add(PanelKit.FieldRow(_fields, global::WinStatusBar.Services.Localization.T("Tempo de atividade")));
        body.Children.Add(PanelKit.Summary(live, _specs));


        Content = PanelKit.Body(body);
        _ = LoadSpecsAsync();
    }

    public void Start()
    {
        if (_disposed || _open) return;
        _open = true; _ticks = 0;
        _usageState.Foreground = PanelKit.Res("AccentTextFillColorPrimaryBrush");
        _hub.CpuSampled += OnSampled;
        Render();
    }

    public void Stop()
    {
        if (!_open) return;
        _open = false;
        _hub.CpuSampled -= OnSampled;
    }

    private void OnSampled()
    {
        if (!_open || _disposed) return;
        Render();
    }

    // ------------------------------------------------------------------ desempenho
    private void Render()
    {
        try
        {
            var cpu = _hub.LastCpu;
            var history = _hub.CoreHistory;
            _name.Text = cpu?.ProcessorName ?? global::WinStatusBar.Services.Localization.T("Processador");
            _fields[global::WinStatusBar.Services.Localization.T("Utilização")].Text = cpu?.UsagePercent is double u ? $"{u:0}%" : "—";
            _fields[global::WinStatusBar.Services.Localization.T("Velocidade")].Text = cpu?.CurrentGHz is double g
                ? (cpu.PeakCoreGHz is double pk ? global::WinStatusBar.Services.Localization.F("{0} (pico: {1})",new object?[] {$"{g:0.00} GHz", $"{pk:0.00} GHz"}) : $"{g:0.00} GHz") // número e unidade nunca se separam na quebra de linha
                : "—";
            _fields[global::WinStatusBar.Services.Localization.T("Temperatura")].Text = _hub.LastTemperature?.Cpu is double t ? $"{t:0} °C" : "—";
            var up = TimeSpan.FromMilliseconds(Environment.TickCount64);
            _fields[global::WinStatusBar.Services.Localization.T("Tempo de atividade")].Text = $"{up.Days}:{up.Hours:00}:{up.Minutes:00}:{up.Seconds:00}";
            _fields[global::WinStatusBar.Services.Localization.T("Processos")].Text = Count(history.ProcessCount);
            _fields[global::WinStatusBar.Services.Localization.T("Threads")].Text = Count(history.ThreadCount);
            _fields[global::WinStatusBar.Services.Localization.T("Identificadores")].Text = Count(history.HandleCount);
            var total = history.TotalSnapshot();
            if (total.Length > 0)
            {
                double now = total[^1];
                _usageValue.Text = $"{now:0}%"; _idleValue.Text = $"{100 - now:0}%";
                _usageState.Text = now >= 85 ? global::WinStatusBar.Services.Localization.T("Alto") : now >= 50 ? global::WinStatusBar.Services.Localization.T("Moderado") : global::WinStatusBar.Services.Localization.T("Normal");
            }
            _chart.Update(new ChartSeries(total, 100));
        }
        catch (Exception ex) { App.Log(global::WinStatusBar.Services.Localization.T("Painel CPU: ") + ex.Message); }
    }

    private static string Count(double? n) => n is double v ? v.ToString("N0") : "—";

    private async Task LoadSpecsAsync()
    {
        var rows = await Task.Run(() =>
        {
            var list = new List<(string, string)>();
            try
            {
                int sockets = 0, cores = 0, logical = 0; double mhz = 0; ulong l2 = 0, l3 = 0, l1 = 0;
                using (var search = new ManagementObjectSearcher("SELECT NumberOfCores,NumberOfLogicalProcessors,MaxClockSpeed,L2CacheSize,L3CacheSize FROM Win32_Processor"))
                using (var found = search.Get())
                    foreach (ManagementObject p in found)
                        using (p)
                        {
                            sockets++; cores += Convert.ToInt32(p["NumberOfCores"] ?? 0); logical += Convert.ToInt32(p["NumberOfLogicalProcessors"] ?? 0);
                            mhz = Math.Max(mhz, Convert.ToDouble(p["MaxClockSpeed"] ?? 0));
                            l2 += Convert.ToUInt64(p["L2CacheSize"] ?? 0); l3 += Convert.ToUInt64(p["L3CacheSize"] ?? 0);
                        }
                // L1 não existe em Win32_Processor; Win32_CacheMemory lista um item por tipo de núcleo (Level 3 = L1).
                using (var search = new ManagementObjectSearcher("SELECT Level,InstalledSize FROM Win32_CacheMemory"))
                using (var found = search.Get())
                    foreach (ManagementObject c in found)
                        using (c) if (Convert.ToInt32(c["Level"] ?? 0) == 3) l1 += Convert.ToUInt64(c["InstalledSize"] ?? 0);
                // Virtualização: habilitada no firmware, ou já em uso por um hipervisor (Hyper-V/VBS),
                // caso em que o Windows deixa VirtualizationFirmwareEnabled = false.
                bool? virt = null;
                try
                {
                    using (var s = new ManagementObjectSearcher("SELECT HypervisorPresent FROM Win32_ComputerSystem"))
                    using (var f = s.Get())
                        foreach (ManagementObject o in f) using (o) if (o["HypervisorPresent"] is bool h && h) virt = true;
                    if (virt is null)
                        using (var s = new ManagementObjectSearcher("SELECT VirtualizationFirmwareEnabled FROM Win32_Processor"))
                        using (var f = s.Get())
                            foreach (ManagementObject o in f) using (o) if (o["VirtualizationFirmwareEnabled"] is bool e) virt = e;
                }
                catch { }
                static string Cache(ulong kb) => kb == 0 ? "—" : kb < 1024 ? $"{kb} KB" : $"{kb / 1024.0:0.0} MB";
                list.Add((global::WinStatusBar.Services.Localization.T("Velocidade base"), mhz > 0 ? $"{mhz / 1000:0.00} GHz" : "—"));
                list.Add((global::WinStatusBar.Services.Localization.T("Soquetes"), sockets.ToString()));
                list.Add((global::WinStatusBar.Services.Localization.T("Núcleos"), cores.ToString()));
                list.Add((global::WinStatusBar.Services.Localization.T("Processadores lógicos"), logical.ToString()));
                if (virt is bool v) list.Add((global::WinStatusBar.Services.Localization.T("Virtualização"), v ? global::WinStatusBar.Services.Localization.T("Habilitado") : global::WinStatusBar.Services.Localization.T("Desabilitado")));
                list.Add((global::WinStatusBar.Services.Localization.T("Cache L1"), Cache(l1)));
                list.Add((global::WinStatusBar.Services.Localization.T("Cache L2"), Cache(l2)));
                list.Add((global::WinStatusBar.Services.Localization.T("Cache L3"), Cache(l3)));
            }
            catch { list.Add((global::WinStatusBar.Services.Localization.T("Detalhes do processador"), global::WinStatusBar.Services.Localization.T("indisponíveis"))); }
            return list;
        });
        if (_disposed) return;
        PanelKit.FillSpecs(_specs, rows);
    }

    // ------------------------------------------------------------------ estilos
    private static readonly Brush Transparent = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
    private static TextBlock Title(string text) => PanelKit.Title(text);
    private static TextBlock Caption(string text, double size = 11) => PanelKit.Caption(text, size);

    private static FrameworkElement Section(TextBlock title)
    {
        var section = new StackPanel { Spacing = 1, Margin = new Thickness(0, 12, 0, 4) };
        section.Children.Add(title);
        return section;
    }

    public void Dispose() { _disposed = true; Stop(); }
}
