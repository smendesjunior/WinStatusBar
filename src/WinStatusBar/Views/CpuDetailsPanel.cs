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
    private readonly TextBlock _appsTitle = Title(global::WinStatusBar.Services.Localization.T("Aplicativos"));
    private readonly StackPanel _apps = new() { Spacing = 0 };
    private readonly HashSet<string> _expanded = new();
    private bool _open, _loadingApps, _disposed;
    private int _ticks;
    public FrameworkElement Content { get; }

    private readonly WinStatusBar.Core.Settings.SettingsService _settings;
    private FrameworkElement _appsSection = null!;
    private bool AppsOn => _settings.Current.CpuPanelApps;

    public CpuDetailsPanel(MonitoringHub hub, WinStatusBar.Core.Settings.SettingsService settings)
    {
        _hub = hub; _settings = settings;
        var body = new StackPanel { Spacing = 4 };
        body.Children.Add(PanelKit.Header(global::WinStatusBar.Services.Localization.T("CPU"), _name));

        _chart.Root.Margin = new Thickness(0, 10, 0, 0);
        _chart.Left.Children.Add(AreaChart.Text(global::WinStatusBar.Services.Localization.T("Uso da CPU")));
        _chart.Left.Children.Add(PanelKit.Dot()); _chart.Left.Children.Add(_usageValue);
        _chart.Left.Children.Add(PanelKit.Dot()); _chart.Left.Children.Add(_usageState);
        _chart.Right.Children.Add(AreaChart.Text(global::WinStatusBar.Services.Localization.T("Ociosa"))); _chart.Right.Children.Add(PanelKit.Dot()); _chart.Right.Children.Add(_idleValue);
        body.Children.Add(_chart.Root);

        var live = new StackPanel { Spacing = 8 };
        live.Children.Add(PanelKit.FieldRow(_fields, global::WinStatusBar.Services.Localization.T("Utilização"), global::WinStatusBar.Services.Localization.T("Velocidade"), global::WinStatusBar.Services.Localization.T("Temperatura")));
        live.Children.Add(PanelKit.FieldRow(_fields, global::WinStatusBar.Services.Localization.T("Processos"), global::WinStatusBar.Services.Localization.T("Threads"), global::WinStatusBar.Services.Localization.T("Identificadores")));
        live.Children.Add(PanelKit.FieldRow(_fields, global::WinStatusBar.Services.Localization.T("Tempo de atividade")));
        body.Children.Add(PanelKit.Summary(live, _specs));

        _appsSection = Section(_appsTitle);
        body.Children.Add(_appsSection);
        body.Children.Add(_apps);

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
        // Lista de aplicativos opcional (Configurações › Hardware e rede › Painéis de detalhes).
        _appsSection.Visibility = _apps.Visibility = AppsOn ? Visibility.Visible : Visibility.Collapsed;
        if (AppsOn) _ = RefreshAppsAsync();
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
        if (++_ticks % 5 == 0 && AppsOn) _ = RefreshAppsAsync(); // aplicativos a cada 5 s com o painel aberto
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
            _fields[global::WinStatusBar.Services.Localization.T("Velocidade")].Text = cpu?.CurrentGHz is double g ? $"{g:0.00} GHz" : "—";
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

    // ------------------------------------------------------------------ aplicativos
    private async Task RefreshAppsAsync()
    {
        if (_loadingApps || _disposed) return;
        _loadingApps = true;
        try
        {
            var apps = await Task.Run(RunningApps.Capture);
            if (_disposed || !_open) return;
            if (_apps.Children.OfType<FrameworkElement>().Any(e => e.Tag is "confirm")) return; // não reconstrói durante uma confirmação
            long totalMemory = apps.SelectMany(app => app.Processes).DistinctBy(process => process.Pid)
                .Sum(process => Math.Max(0, process.WorkingSet));
            _appsTitle.Text = global::WinStatusBar.Services.Localization.F("Aplicativos ({0}) · {1} de RAM", new object?[] { apps.Count, FormatAppMemory(totalMemory) });
            ToolTipService.SetToolTip(_appsTitle, global::WinStatusBar.Services.Localization.T(
                "Soma da memória residente dos processos dos aplicativos listados. Memória compartilhada pode ser contada em mais de um processo; não representa toda a RAM usada pelo Windows."));
            _apps.Children.Clear();
            if (apps.Count == 0) { _apps.Children.Add(Caption(global::WinStatusBar.Services.Localization.T("Nenhum aplicativo com janela aberta."), 12)); return; }
            foreach (var app in apps) _apps.Children.Add(AppRow(app));
        }
        catch (Exception ex) { App.Log(global::WinStatusBar.Services.Localization.T("Painel CPU (aplicativos): ") + ex.Message); }
        finally { _loadingApps = false; }
    }

    private static string FormatAppMemory(long bytes) => bytes <= 0 ? "—"
        : bytes >= 1073741824 ? $"{bytes / 1073741824.0:N2} GB" : $"{bytes / 1048576.0:N1} MB";

    private FrameworkElement AppRow(AppGroup app)
    {
        var container = new StackPanel();
        bool expanded = _expanded.Contains(app.Key);
        var chevron = new FontIcon { Glyph = expanded ? "" : "", FontSize = 10, Foreground = WinStatusBar.Services.ThemeBrushes.Get("TextFillColorSecondaryBrush") };
        var header = new Grid { Height = 30, Padding = new Thickness(2, 0, 6, 0), CornerRadius = new CornerRadius(4), Background = Transparent, ColumnSpacing = 8 };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(16) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(18) });
        header.ColumnDefinitions.Add(new ColumnDefinition());
        header.Children.Add(chevron);
        var icon = app.IconSource is { } src ? IconService.ShellIcon(src, 32) : null;
        FrameworkElement glyph = icon is not null
            ? new Image { Source = icon, Width = 18, Height = 18 }
            : new FontIcon { Glyph = "", FontSize = 15 };
        Grid.SetColumn(glyph, 1); header.Children.Add(glyph);
        var name = new TextBlock
        {
            Text = app.Processes.Count > 1 ? $"{app.Name} ({app.Processes.Count})" : app.Name,
            FontSize = 13, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, MaxLines = 1,
        };
        ToolTipService.SetToolTip(name, app.Name);
        Grid.SetColumn(name, 2); header.Children.Add(name);
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var memory = new TextBlock
        {
            Text = FormatAppMemory(app.Processes.DistinctBy(process => process.Pid).Sum(process => Math.Max(0, process.WorkingSet))),
            FontSize = 12, MinWidth = 72, TextAlignment = TextAlignment.Right,
        };
        ToolTipService.SetToolTip(memory, global::WinStatusBar.Services.Localization.T("Memória RAM residente somada de todos os processos deste aplicativo."));
        Grid.SetColumn(memory, 3); header.Children.Add(memory);
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var endButton = new Button
        {
            Content = global::WinStatusBar.Services.Localization.T("Finalizar tarefa"), FontSize = 11, Padding = new Thickness(9, 2, 9, 3), MinHeight = 0, Height = 24,
        };
        ToolTipService.SetToolTip(endButton, $"Finalizar {app.Name}");
        endButton.Click += (_, _) => ShowConfirm(container, app, force: false);
        endButton.Visibility = _settings.Current.AllowEndTask ? Visibility.Visible : Visibility.Collapsed;
        Grid.SetColumn(endButton, 4); header.Children.Add(endButton);
        foreach (var child in header.Children.OfType<FrameworkElement>()) child.VerticalAlignment = VerticalAlignment.Center;

        var details = new StackPanel { Visibility = expanded ? Visibility.Visible : Visibility.Collapsed, Margin = new Thickness(44, 0, 6, 4) };
        foreach (var p in app.Processes.Take(40))
        {
            var row = new Grid { Height = 22, ColumnSpacing = 8 };
            row.ColumnDefinitions.Add(new ColumnDefinition());
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var label = new TextBlock { Text = p.Label, FontSize = 12, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, MaxLines = 1 };
            var mem = Caption(p.WorkingSet > 0 ? $"{p.WorkingSet / 1048576.0:N1} MB" : "—", 11);
            mem.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(mem, 1); row.Children.Add(label); row.Children.Add(mem);
            details.Children.Add(row);
        }
        if (app.Processes.Count > 40) details.Children.Add(Caption($"+ {app.Processes.Count - 40} processos", 11));

        header.PointerEntered += (_, _) => header.Background = WinStatusBar.Services.ThemeBrushes.Get("SubtleFillColorSecondaryBrush");
        header.PointerExited += (_, _) => header.Background = Transparent;
        header.Tapped += (_, e) =>
        {
            // Clique no botão "Finalizar tarefa" não expande a linha.
            for (var el = e.OriginalSource as DependencyObject; el is not null; el = VisualTreeHelper.GetParent(el))
                if (ReferenceEquals(el, endButton)) return;
            bool open = details.Visibility != Visibility.Visible;
            details.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
            chevron.Glyph = open ? "" : "";
            if (open) _expanded.Add(app.Key); else _expanded.Remove(app.Key);
        };

        var menu = new MenuFlyout();
        PopupMaterial.Apply(menu);
        var switchTo = new MenuFlyoutItem { Text = global::WinStatusBar.Services.Localization.T("Alternar para"), Icon = new FontIcon { Glyph = "" } };
        switchTo.Click += (_, _) => RunningApps.SwitchTo(app);
        var end = new MenuFlyoutItem { Text = global::WinStatusBar.Services.Localization.T("Finalizar tarefa"), Icon = new FontIcon { Glyph = "" } };
        end.Click += (_, _) => ShowConfirm(container, app, force: false);
        menu.Items.Add(switchTo);
        if (_settings.Current.AllowEndTask) menu.Items.Add(end);
        header.ContextFlyout = menu;

        container.Children.Add(header);
        container.Children.Add(details);
        return container;
    }

    /// <summary>Confirmação dentro do próprio painel (um diálogo ficaria preso à janela de 48 px da barra).</summary>
    private void ShowConfirm(StackPanel container, AppGroup app, bool force)
    {
        foreach (var old in container.Children.OfType<FrameworkElement>().Where(e => e.Tag is "confirm").ToList()) container.Children.Remove(old);
        var box = new Grid
        {
            Tag = "confirm", Padding = new Thickness(12, 8, 8, 8), Margin = new Thickness(26, 2, 0, 6), ColumnSpacing = 6, RowSpacing = 6,
            CornerRadius = new CornerRadius(6), Background = WinStatusBar.Services.ThemeBrushes.Get("CardBackgroundFillColorSecondaryBrush"),
        };
        box.ColumnDefinitions.Add(new ColumnDefinition());
        box.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        box.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var text = new TextBlock
        {
            Text = force ? global::WinStatusBar.Services.Localization.F("{0} não respondeu. Forçar o encerramento?", new object?[] {app.Name}) : $"Finalizar {app.Name}?",
            FontSize = 12, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center,
        };
        var hint = Caption(force ? global::WinStatusBar.Services.Localization.T("Alterações não salvas serão perdidas.") : global::WinStatusBar.Services.Localization.T("O app recebe o pedido de fechar, como no botão X."), 11);
        var info = new StackPanel { Spacing = 1 }; info.Children.Add(text); info.Children.Add(hint);
        var ok = WinStatusBar.Views.Panels.PanelKit.Small(new Button { Content = force ? global::WinStatusBar.Services.Localization.T("Forçar") : global::WinStatusBar.Services.Localization.T("Finalizar"), FontSize = 12, Padding = new Thickness(10, 3, 10, 4), VerticalAlignment = VerticalAlignment.Center });
        if (Application.Current.Resources.TryGetValue("AccentButtonStyle", out var accentStyle)) ok.Style = (Style)accentStyle;
        var cancel = WinStatusBar.Views.Panels.PanelKit.Small(new Button { Content = global::WinStatusBar.Services.Localization.T("Cancelar"), FontSize = 12, Padding = new Thickness(10, 3, 10, 4), VerticalAlignment = VerticalAlignment.Center });
        Grid.SetColumn(ok, 1); Grid.SetColumn(cancel, 2);
        box.Children.Add(info); box.Children.Add(ok); box.Children.Add(cancel);
        container.Children.Insert(1, box);

        cancel.Click += (_, _) => container.Children.Remove(box);
        ok.Click += async (_, _) =>
        {
            ok.IsEnabled = cancel.IsEnabled = false;
            if (force) { await Task.Run(() => RunningApps.ForceEnd(app)); container.Children.Remove(box); await RefreshAppsAsync(); return; }
            RunningApps.RequestClose(app);
            hint.Text = global::WinStatusBar.Services.Localization.T("Aguardando o aplicativo fechar…");
            bool closed = false;
            for (int i = 0; i < 10 && !closed; i++) { await Task.Delay(500); closed = !RunningApps.IsRunning(app); }
            container.Children.Remove(box);
            if (!closed && !app.Protected) ShowConfirm(container, app, force: true);
            else await RefreshAppsAsync();
        };
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
