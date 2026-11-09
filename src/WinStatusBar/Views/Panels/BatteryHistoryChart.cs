using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using WinStatusBar.Core.Metrics;
using WinStatusBar.Services;

namespace WinStatusBar.Views.Panels;

internal sealed class BatteryHistoryChart
{
    public StackPanel Content { get; } = new() { Spacing = 5, Padding = new Thickness(12, 8, 12, 8), CornerRadius = new CornerRadius(8),
        Background = PanelKit.Res("CardBackgroundFillColorDefaultBrush"), BorderBrush = PanelKit.Res("CardStrokeColorDefaultBrush"), BorderThickness = new Thickness(1) };
    private readonly ComboBox _period = new() { FontSize = 12, MinHeight = 26, HorizontalAlignment = HorizontalAlignment.Right };
    private readonly Grid _bars = new() { Height = 90, ColumnSpacing = 3 };
    private readonly Grid _ticks = new() { Margin = new Thickness(30, 0, 0, 0) };
    private readonly TextBlock _status = PanelKit.Caption("");
    private readonly TextBlock _totals = PanelKit.Caption("", 11);
    private WindowsBatteryReport? _report;
    private DateTime _attempt;
    private bool _loading;
    private string _drawn = "";

    public BatteryHistoryChart()
    {
        _period.Items.Add(Localization.T("Últimas 24 horas"));
        _period.Items.Add(Localization.T("Últimos 7 dias"));
        _period.SelectedIndex = ((App)Application.Current).Settings.Current.BatteryHistoryDays == 7 ? 1 : 0;
        var head = new Grid();
        head.Children.Add(PanelKit.Caption(Localization.T("Nível da bateria"), 12));
        head.Children.Add(_period); Content.Children.Add(head);
        var plot = new Grid { ColumnSpacing = 4 };
        plot.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(26) });
        plot.ColumnDefinitions.Add(new ColumnDefinition());
        var axis = new Grid { Height = 90 };
        foreach (var (label, align) in new[] { ("100%", VerticalAlignment.Top), ("50%", VerticalAlignment.Center), ("0%", VerticalAlignment.Bottom) })
        {
            var text = PanelKit.Caption(label, 10); text.VerticalAlignment = align; axis.Children.Add(text);
        }
        plot.Children.Add(axis); Grid.SetColumn(_bars, 1); plot.Children.Add(_bars);
        Content.Children.Add(plot); Content.Children.Add(_ticks); Content.Children.Add(_totals); Content.Children.Add(_status);
        _period.SelectionChanged += (_, _) => { _drawn = ""; Render(); };
    }

    public void Render()
    {
        if (!_loading && DateTime.UtcNow - _attempt > TimeSpan.FromMinutes(2)) _ = RefreshAsync();
        if (_report is not { } report) return;
        string signature = report.Generated.ToString("O") + _period.SelectedIndex;
        if (_drawn == signature) return;
        _drawn = signature;
        var end = report.Generated;
        TimeSpan span = TimeSpan.FromDays(_period.SelectedIndex == 1 ? 7 : 1);
        var start = end - span;
        int count = _period.SelectedIndex == 1 ? 56 : 48;
        _bars.Children.Clear(); _bars.ColumnDefinitions.Clear(); _ticks.Children.Clear(); _ticks.ColumnDefinitions.Clear();
        // Guide lines use the same vertical scale as the battery fill.
        foreach (double position in new[] { 0.0, 0.5, 1.0 })
        {
            var line = new Border { Height = 1, Background = PanelKit.Res("DividerStrokeColorDefaultBrush"),
                VerticalAlignment = position == 0 ? VerticalAlignment.Top : position == 1 ? VerticalAlignment.Bottom : VerticalAlignment.Center, IsHitTestVisible = false };
            Grid.SetColumnSpan(line, count); _bars.Children.Add(line);
        }
        bool previousCharging = false;
        for (int i = 0; i < count; i++)
        {
            _bars.ColumnDefinitions.Add(new ColumnDefinition());
            var from = start + TimeSpan.FromTicks(span.Ticks * i / count);
            var to = start + TimeSpan.FromTicks(span.Ticks * (i + 1) / count);
            var intervals = report.Spans.Where(s => s.Start < to && s.End > from).ToArray();
            var cell = new Grid { Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent) };
            string tip = $"{from.ToLocalTime():dd/MM HH:mm}–{to.ToLocalTime():HH:mm}";
            if (intervals.Length > 0)
            {
                double duration = 0, weighted = 0;
                foreach (var s in intervals)
                {
                    var a = s.Start > from ? s.Start : from; var b = s.End < to ? s.End : to;
                    double weight = (b - a).TotalSeconds;
                    weighted += (s.LevelAt(a) + s.LevelAt(b)) / 2 * weight; duration += weight;
                }
                double level = weighted / Math.Max(1, duration);
                bool charging = intervals.Any(s => s.Charging);
                if (charging) cell.Children.Add(new Border { Background = PanelKit.Res("AccentFillColorDefaultBrush"), Opacity = .12, IsHitTestVisible = false });
                cell.Children.Add(new Border { Height = Math.Max(1, level / 100 * 90), CornerRadius = new CornerRadius(3), Margin = new Thickness(1, 0, 1, 0),
                    VerticalAlignment = VerticalAlignment.Bottom, Background = PanelKit.Res("AccentFillColorDefaultBrush"), Opacity = charging ? 0.55 : 1 });
                tip += $"\n≈ {level:0}% · " + Localization.T(charging ? "Carregando" : intervals.Any(s => s.Plugged) ? "Conectada à tomada" : "Bateria");
                tip += "\n" + Localization.T("Média do intervalo · relatório do Windows");
                if (charging && !previousCharging) cell.Children.Add(new FontIcon { Glyph = "\uE945", FontSize = 10, Foreground = PanelKit.Res("AccentTextFillColorPrimaryBrush"), VerticalAlignment = VerticalAlignment.Top, IsHitTestVisible = false });
                previousCharging = charging;
            }
            else { previousCharging = false; tip += "\n" + Localization.T("Sem registros do Windows neste intervalo"); }
            var tooltip = new ToolTip { Content = tip, Placement = Microsoft.UI.Xaml.Controls.Primitives.PlacementMode.Top };
            ToolTipService.SetToolTip(cell, tooltip);
            cell.PointerEntered += (_, _) => { tooltip.IsOpen = true; cell.BorderBrush = PanelKit.Res("AccentFillColorDefaultBrush"); cell.BorderThickness = new Thickness(1, 0, 0, 0); };
            cell.PointerExited += (_, _) => { tooltip.IsOpen = false; cell.BorderThickness = new Thickness(0); };
            Grid.SetColumn(cell, i); _bars.Children.Add(cell);
        }
        for (int i = 0; i <= 4; i++)
        {
            _ticks.ColumnDefinitions.Add(new ColumnDefinition());
            var at = start + TimeSpan.FromTicks(span.Ticks * i / 4);
            var tick = PanelKit.Caption(at.ToLocalTime().ToString(_period.SelectedIndex == 1 ? "dd/MM" : "HH:mm"), 10);
            tick.HorizontalAlignment = i == 0 ? HorizontalAlignment.Left : i == 4 ? HorizontalAlignment.Right : HorizontalAlignment.Center;
            Grid.SetColumn(tick, i); _ticks.Children.Add(tick);
        }
        static string Duration(TimeSpan value) => $"{(int)value.TotalHours} h {value.Minutes:00} min";
        _totals.Text = Localization.T("Em uso") + "  " + Duration(report.Duration("Active", start, end)) + "   ·   "
            + Localization.T("Repouso") + "  " + Duration(report.Duration("Suspend", start, end) + report.Duration("ConnectedStandby", start, end));
        if (report.Spans.Any(s => s.State == "ScreenOff")) _totals.Text += "   ·   " + Localization.T("Tela desligada") + "  " + Duration(report.Duration("ScreenOff", start, end));
        ToolTipService.SetToolTip(_totals, Localization.T("Uso ativo não equivale necessariamente a tela ligada. O relatório de bateria não separa esses tempos em todos os PCs."));
        _status.Text = Localization.T("Fonte: Windows") + $" · {end.ToLocalTime():dd/MM HH:mm}";
    }

    private async Task RefreshAsync()
    {
        _loading = true; _attempt = DateTime.UtcNow;
        if (_report is null) _status.Text = Localization.T("Consultando histórico do Windows…");
        try { _report = await WindowsBatteryHistory.ReadAsync(); _drawn = ""; }
        catch (Exception ex) { _status.Text = Localization.T("Histórico do Windows indisponível."); App.Log("Histórico de bateria: " + ex.Message); }
        finally { _loading = false; }
        Render();
    }
}
