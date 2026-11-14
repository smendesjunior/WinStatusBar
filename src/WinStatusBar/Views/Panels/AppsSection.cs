using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using WinStatusBar.Services;

namespace WinStatusBar.Views.Panels;

/// <summary>
/// Aplicativos abertos no formato do Gerenciador de Tarefas (agrupados, com memória, uso da CPU e "Finalizar
/// tarefa"), com a pesquisa de processos. Fica no painel da memória; a lista se atualiza a cada 5 s com o painel
/// aberto. O uso da CPU de cada aplicativo é a soma dos processos dele, medida entre duas atualizações (como no
/// Gerenciador de Tarefas: 100% = todos os núcleos ocupados).
/// </summary>
internal sealed class AppsSection
{
    private readonly TextBlock _appsTitle = Title(global::WinStatusBar.Services.Localization.T("Aplicativos"));
    private readonly StackPanel _apps = new() { Spacing = 0 };
    private readonly HashSet<string> _expanded = new();
    private bool _open, _loadingApps, _disposed;
    private readonly WinStatusBar.Core.Settings.SettingsService _settings;
    private readonly FrameworkElement _appsSection;
    private readonly TextBox _search = new()
    {
        PlaceholderText = global::WinStatusBar.Services.Localization.T("Pesquisar processos, inclusive em segundo plano"),
        FontSize = 12, Margin = new Thickness(0, 6, 0, 2), MinHeight = 0,
    };
    private int _searchVersion;
    private string Query => _search.Text.Trim();
    private readonly CpuTracker _cpu = new();
    private Dictionary<uint, double?> _cpuNow = new();
    private double? CpuOf(IEnumerable<AppProcess> processes)
    {
        var values = processes.Select(p => p.Pid).Distinct().Select(pid => _cpuNow.GetValueOrDefault(pid)).ToList();
        return values.All(v => v is null) ? null : values.Sum(v => v ?? 0);
    }

    /// <summary>Título, pesquisa e lista, para pôr no corpo do painel.</summary>
    public StackPanel Root { get; } = new();
    public bool Enabled => _settings.Current.CpuPanelApps;

    public AppsSection(WinStatusBar.Core.Settings.SettingsService settings)
    {
        _settings = settings;
        _appsSection = Section(_appsTitle);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(_search, _search.PlaceholderText);
        ((StackPanel)_appsSection).Children.Add(_search);
        _search.TextChanged += (_, _) => { _searchVersion++; int v = _searchVersion; _ = SearchSoonAsync(v); };
        _search.KeyDown += (_, e) => { if (e.Key == Windows.System.VirtualKey.Escape && _search.Text.Length > 0) { _search.Text = ""; e.Handled = true; } };
        Root.Children.Add(_appsSection);
        Root.Children.Add(_apps);
    }

    public void Start()
    {
        _open = true;
        Root.Visibility = Enabled ? Visibility.Visible : Visibility.Collapsed;
        if (Enabled) { _ = RefreshAppsAsync(); _ = SecondReadAsync(); }
    }

    /// <summary>O uso da CPU precisa de duas leituras: a segunda vem logo, sem esperar os 5 s.</summary>
    private async Task SecondReadAsync()
    {
        await Task.Delay(1500);
        if (_open && !_disposed && Enabled) await RefreshAppsAsync();
    }

    public void Stop() => _open = false;

    /// <summary>Chamado a cada amostra (1 s): a lista se refaz a cada 5.</summary>
    public void Tick(int ticks) { if (_open && Enabled && ticks % 5 == 0) _ = RefreshAppsAsync(); }

    public void Dispose() { _disposed = true; _open = false; }
    private async Task SearchSoonAsync(int version)
    {
        await Task.Delay(250);
        if (version != _searchVersion || _disposed || !_open) return;
        await RefreshAppsAsync();
    }

    private async Task RefreshAppsAsync()
    {
        if (Query.Length > 0) { await RefreshSearchAsync(); return; }
        if (_loadingApps || _disposed) return;
        _loadingApps = true;
        try
        {
            var apps = await Task.Run(() => { var a = RunningApps.Capture(); _cpuNow = _cpu.Sample(a.SelectMany(x => x.Processes).Select(x => x.Pid)); return a; });
            if (_disposed || !_open || Query.Length > 0) return;
            if (_apps.Children.OfType<FrameworkElement>().Any(e => e.Tag is "confirm")) return; // não reconstrói durante uma confirmação
            long totalMemory = apps.SelectMany(app => app.Processes).DistinctBy(process => process.Pid)
                .Sum(process => Math.Max(0, process.WorkingSet));
            double? totalCpu = CpuOf(apps.SelectMany(app => app.Processes));
            _appsTitle.Text = global::WinStatusBar.Services.Localization.F("Aplicativos ({0}) · {1} de RAM", new object?[] { apps.Count, FormatAppMemory(totalMemory) })
                + (totalCpu is null ? "" : " · CPU " + CpuTracker.Text(totalCpu));
            ToolTipService.SetToolTip(_appsTitle, global::WinStatusBar.Services.Localization.T(
                "Soma da memória residente dos processos dos aplicativos listados. Memória compartilhada pode ser contada em mais de um processo; não representa toda a RAM usada pelo Windows."));
            _apps.Children.Clear();
            if (apps.Count == 0) { _apps.Children.Add(Caption(global::WinStatusBar.Services.Localization.T("Nenhum aplicativo com janela aberta."), 12)); return; }
            foreach (var app in apps) _apps.Children.Add(AppRow(app));
        }
        catch (Exception ex) { App.Log(global::WinStatusBar.Services.Localization.T("Painel CPU (aplicativos): ") + ex.Message); }
        finally { _loadingApps = false; }
    }

    private bool _searching;

    /// <summary>Com texto na pesquisa, a lista mostra todos os processos que casam (com ou sem janela) no lugar dos aplicativos.</summary>
    private async Task RefreshSearchAsync()
    {
        if (_searching || _disposed) return;
        _searching = true;
        string query = Query;
        try
        {
            var found = await Task.Run(() => { var f = RunningApps.Search(query); _cpuNow = _cpu.Sample(f.SelectMany(x => x.Processes).Select(x => x.Pid)); return f; });
            if (_disposed || !_open || query != Query) return;
            if (_apps.Children.OfType<FrameworkElement>().Any(e => e.Tag is "confirm")) return;
            _apps.Children.Clear();
            if (found.Count == 0) { _apps.Children.Add(Caption(global::WinStatusBar.Services.Localization.F("Nenhum processo com \"{0}\".", query), 12)); return; }
            foreach (var group in found) _apps.Children.Add(ProcessRow(group));
        }
        catch (Exception ex) { App.Log("Painel CPU (pesquisa): " + ex.Message); }
        finally
        {
            _searching = false;
            if (query != Query && _open && !_disposed) _ = RefreshAppsAsync(); // o texto mudou durante a busca
        }
    }

    private FrameworkElement ProcessRow(ProcessGroup group)
    {
        var container = new StackPanel();
        var header = new Grid { Height = 36, Padding = new Thickness(2, 0, 6, 0), CornerRadius = new CornerRadius(4), ColumnSpacing = 8 };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(18) });
        header.ColumnDefinitions.Add(new ColumnDefinition());
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var icon = group.IconSource is { } src ? IconService.ShellIcon(src, 32) : null;
        FrameworkElement glyph = icon is not null ? new Image { Source = icon, Width = 18, Height = 18 } : new FontIcon { Glyph = "", FontSize = 15 };
        header.Children.Add(glyph);
        var names = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        names.Children.Add(new TextBlock { Text = group.Name, FontSize = 13, TextTrimming = TextTrimming.CharacterEllipsis, MaxLines = 1 });
        string kind = group.HasWindow ? global::WinStatusBar.Services.Localization.T("com janela") : global::WinStatusBar.Services.Localization.T("segundo plano");
        string count = group.Processes.Count > 1 ? " · " + global::WinStatusBar.Services.Localization.F("{0} processos", group.Processes.Count) : "";
        names.Children.Add(Caption(group.Image + count + " · " + kind, 10));
        ToolTipService.SetToolTip(names, string.Join("\n", group.Processes.Take(15).Select(p => $"{p.Label} · PID {p.Pid}")));
        Grid.SetColumn(names, 1); header.Children.Add(names);
        var memory = new TextBlock { Text = FormatAppMemory(group.Processes.Sum(p => Math.Max(0, p.WorkingSet))), FontSize = 12, MinWidth = 72, TextAlignment = TextAlignment.Right };
        Grid.SetColumn(memory, 3); header.Children.Add(memory);
        var cpu = CpuCell(CpuOf(group.Processes));
        Grid.SetColumn(cpu, 2); header.Children.Add(cpu);
        var endButton = new Button { Content = global::WinStatusBar.Services.Localization.T("Finalizar tarefa"), FontSize = 11, Padding = new Thickness(9, 2, 9, 3), MinHeight = 0, Height = 24 };
        endButton.Visibility = _settings.Current.AllowEndTask && !group.Critical ? Visibility.Visible : Visibility.Collapsed;
        endButton.Click += (_, _) => ShowProcessConfirm(container, group);
        Grid.SetColumn(endButton, 4); header.Children.Add(endButton);
        if (group.Critical) ToolTipService.SetToolTip(header, global::WinStatusBar.Services.Localization.T("Processo essencial do Windows: não pode ser finalizado por aqui."));
        foreach (var child in header.Children.OfType<FrameworkElement>()) child.VerticalAlignment = VerticalAlignment.Center;
        container.Children.Add(header);
        return container;
    }

    private void ShowProcessConfirm(StackPanel container, ProcessGroup group)
    {
        foreach (var old in container.Children.OfType<FrameworkElement>().Where(e => e.Tag is "confirm").ToList()) container.Children.Remove(old);
        var box = new Grid
        {
            Tag = "confirm", Padding = new Thickness(12, 8, 8, 8), Margin = new Thickness(26, 2, 0, 6), ColumnSpacing = 6,
            CornerRadius = new CornerRadius(6), Background = WinStatusBar.Services.ThemeBrushes.Get("CardBackgroundFillColorSecondaryBrush"),
        };
        box.ColumnDefinitions.Add(new ColumnDefinition());
        box.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        box.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var text = new TextBlock
        {
            Text = group.Processes.Count > 1
                ? global::WinStatusBar.Services.Localization.F("Finalizar {0} ({1} processos)?", group.Name, group.Processes.Count)
                : global::WinStatusBar.Services.Localization.F("Finalizar {0}?", group.Name),
            FontSize = 12, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center,
        };
        var hint = Caption(global::WinStatusBar.Services.Localization.T("Encerramento forçado: alterações não salvas serão perdidas."), 11);
        hint.TextWrapping = TextWrapping.Wrap;
        var info = new StackPanel { Spacing = 1 }; info.Children.Add(text); info.Children.Add(hint);
        var ok = PanelKit.Small(new Button { Content = global::WinStatusBar.Services.Localization.T("Finalizar"), FontSize = 12, Padding = new Thickness(10, 3, 10, 4), VerticalAlignment = VerticalAlignment.Center });
        if (Application.Current.Resources.TryGetValue("AccentButtonStyle", out var accentStyle)) ok.Style = (Style)accentStyle;
        var cancel = PanelKit.Small(new Button { Content = global::WinStatusBar.Services.Localization.T("Cancelar"), FontSize = 12, Padding = new Thickness(10, 3, 10, 4), VerticalAlignment = VerticalAlignment.Center });
        Grid.SetColumn(ok, 1); Grid.SetColumn(cancel, 2);
        box.Children.Add(info); box.Children.Add(ok); box.Children.Add(cancel);
        container.Children.Add(box);
        cancel.Click += (_, _) => container.Children.Remove(box);
        ok.Click += async (_, _) =>
        {
            ok.IsEnabled = cancel.IsEnabled = false;
            int failed = await Task.Run(() => RunningApps.ForceEnd(group));
            App.Log($"Pesquisa de processos: finalizado {group.Image} ({group.Processes.Count - failed}/{group.Processes.Count})");
            if (failed > 0)
            {
                text.Text = global::WinStatusBar.Services.Localization.F("{0} processo(s) não puderam ser finalizados.", failed);
                hint.Text = global::WinStatusBar.Services.Localization.T("Sem permissão: processos do sistema ou de administrador só pelo Gerenciador de Tarefas como administrador.");
                ok.Visibility = Visibility.Collapsed; cancel.Content = global::WinStatusBar.Services.Localization.T("Fechar"); cancel.IsEnabled = true;
                return;
            }
            container.Children.Remove(box);
            await RefreshSearchAsync();
        };
    }

    /// <summary>Coluna do uso da CPU (soma dos processos; "—" na primeira leitura).</summary>
    private static TextBlock CpuCell(double? percent)
    {
        var cell = new TextBlock { Text = CpuTracker.Text(percent), FontSize = 12, MinWidth = 46, TextAlignment = TextAlignment.Right };
        if (percent >= 25) cell.Foreground = PanelKit.Res("AccentTextFillColorPrimaryBrush");
        ToolTipService.SetToolTip(cell, global::WinStatusBar.Services.Localization.T("Uso da CPU somado dos processos (100% = todos os núcleos)."));
        return cell;
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
        var appCpu = CpuCell(CpuOf(app.Processes));
        Grid.SetColumn(appCpu, 3); header.Children.Add(appCpu);
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var memory = new TextBlock
        {
            Text = FormatAppMemory(app.Processes.DistinctBy(process => process.Pid).Sum(process => Math.Max(0, process.WorkingSet))),
            FontSize = 12, MinWidth = 72, TextAlignment = TextAlignment.Right,
        };
        ToolTipService.SetToolTip(memory, global::WinStatusBar.Services.Localization.T("Memória RAM residente somada de todos os processos deste aplicativo."));
        Grid.SetColumn(memory, 4); header.Children.Add(memory);
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var endButton = new Button
        {
            Content = global::WinStatusBar.Services.Localization.T("Finalizar tarefa"), FontSize = 11, Padding = new Thickness(9, 2, 9, 3), MinHeight = 0, Height = 24,
        };
        ToolTipService.SetToolTip(endButton, $"Finalizar {app.Name}");
        endButton.Click += (_, _) => ShowConfirm(container, app, force: false);
        endButton.Visibility = _settings.Current.AllowEndTask ? Visibility.Visible : Visibility.Collapsed;
        Grid.SetColumn(endButton, 5); header.Children.Add(endButton);
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

    private static readonly Brush Transparent = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
    private static TextBlock Title(string text) => PanelKit.Title(text);
    private static TextBlock Caption(string text, double size = 11) => PanelKit.Caption(text, size);

    private static FrameworkElement Section(TextBlock title)
    {
        var section = new StackPanel { Spacing = 1, Margin = new Thickness(0, 12, 0, 4) };
        section.Children.Add(title);
        return section;
    }
}

/// <summary>
/// Uso da CPU por processo entre duas leituras (tempo de processador gasto ÷ tempo passado ÷ núcleos), como o
/// Gerenciador de Tarefas. A primeira leitura de um processo não tem valor (precisa de duas).
/// </summary>
internal sealed class CpuTracker
{
    private readonly Dictionary<uint, (TimeSpan Cpu, long At)> _last = new();
    private readonly object _sync = new();

    /// <summary>Lê agora o tempo de processador dos processos e devolve o uso de cada um (null: ainda sem medida).</summary>
    public Dictionary<uint, double?> Sample(IEnumerable<uint> pids)
    {
        var result = new Dictionary<uint, double?>();
        long now = System.Diagnostics.Stopwatch.GetTimestamp();
        lock (_sync)
        {
            var seen = new HashSet<uint>();
            foreach (uint pid in pids)
            {
                if (!seen.Add(pid)) continue;
                TimeSpan? cpu = null;
                try { using var p = System.Diagnostics.Process.GetProcessById((int)pid); cpu = p.TotalProcessorTime; } catch { }
                if (cpu is not { } c) { result[pid] = null; continue; }
                if (_last.TryGetValue(pid, out var prev))
                {
                    double elapsed = (now - prev.At) / (double)System.Diagnostics.Stopwatch.Frequency;
                    result[pid] = elapsed <= 0 ? null : Math.Clamp((c - prev.Cpu).TotalSeconds / elapsed / Environment.ProcessorCount * 100, 0, 100);
                }
                else result[pid] = null;
                _last[pid] = (c, now);
            }
            foreach (var gone in _last.Keys.Where(k => !seen.Contains(k)).ToList()) _last.Remove(gone);
        }
        return result;
    }

    public static string Text(double? percent) => percent is not double v ? "—" : v < 0.1 ? "0%" : v < 10 ? $"{v:0.0}%" : $"{v:0}%";
}
