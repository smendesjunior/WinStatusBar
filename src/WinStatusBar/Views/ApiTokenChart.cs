using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;
using WinStatusBar.Core.Ai;
using WinStatusBar.Views.Panels;
namespace WinStatusBar.Views;

internal sealed class ApiTokenChart : StackPanel
{
    private readonly string _kind;
    private readonly Canvas _plot = new() { Height = 75, Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent) };
    private readonly TextBlock _scale = PanelKit.Caption("", 11);
    private readonly TextBlock _dates = PanelKit.Caption("", 11);
    private readonly ToolTip _tip = new();
    private IReadOnlyList<ApiUsageDay> _days = [];
    private readonly SolidColorBrush _input = new(Windows.UI.Color.FromArgb(255, 235, 118, 42));
    private readonly SolidColorBrush _output = new(Windows.UI.Color.FromArgb(255, 216, 65, 128));
    public ApiTokenChart(string kind = "Tokens")
    {
        _kind = kind;
        if (kind != "Tokens") _plot.Height = 45;
        Spacing = 4;
        Children.Add(_scale); Children.Add(_plot); Children.Add(_dates);
        var legend = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 16 };
        legend.Children.Add(new TextBlock { Text = "● Entrada", Foreground = _input, FontSize = 12 });
        legend.Children.Add(new TextBlock { Text = "● Saída", Foreground = _output, FontSize = 12 });
        if (kind == "Tokens") Children.Add(legend);
        ToolTipService.SetToolTip(_plot, _tip);
        _plot.SizeChanged += (_, _) => Draw();
        _plot.PointerMoved += (_, e) =>
        {
            if (_days.Count == 0 || _plot.ActualWidth <= 0) return;
            int index = Math.Clamp((int)Math.Round(e.GetCurrentPoint(_plot).Position.X / _plot.ActualWidth * (_days.Count - 1)), 0, _days.Count - 1);
            var d = _days[index];
            _tip.Content = $"{d.Date:dd/MM} UTC\nEntrada: {d.Input:N0}\nSaída: {d.Output:N0}\nRequisições: {d.Requests:N0}\nCusto: US$ {d.Cost:N4}";
            _tip.IsOpen = true;
        };
        _plot.PointerExited += (_, _) => _tip.IsOpen = false;
        Unloaded += (_, _) => _tip.IsOpen = false;
    }
    public void Update(IReadOnlyList<ApiUsageDay> days) { _days = days; Draw(); }
    private void Draw()
    {
        _plot.Children.Clear();
        if (_days.Count == 0) return;
        double width = _plot.ActualWidth;
        double max = Math.Max(_kind == "Costs" ? 0.01 : 1, _days.Max(d => _kind == "Costs" ? (double)d.Cost : _kind == "Requests" ? d.Requests : Math.Max(d.Input, d.Output)));
        _scale.Text = _kind == "Costs" ? $"US$ {max:N2}/dia" : $"{max:N0} " + (_kind == "Requests" ? "requisições/dia" : "tokens/dia");
        _dates.Text = $"{_days[0].Date:dd/MM} — {_days[^1].Date:dd/MM}";
        if (width <= 0) return;
        double bottom = _plot.Height - 5;
        foreach (double y in new[] { 5d, bottom / 2, bottom })
            _plot.Children.Add(new Line { X1 = 0, X2 = width, Y1 = y, Y2 = y, Stroke = PanelKit.Res("DividerStrokeColorDefaultBrush"), StrokeThickness = 1 });
        if (_kind == "Requests")
        {
            double step = width / _days.Count;
            for (int i = 0; i < _days.Count; i++)
            {
                double height = Math.Max(1, (bottom - 5) * _days[i].Requests / max);
                var bar = new Rectangle { Width = Math.Max(1, step - 3), Height = height, RadiusX = 2, RadiusY = 2, Fill = PanelKit.Res("AccentFillColorDefaultBrush"), IsHitTestVisible = false };
                Canvas.SetLeft(bar, i * step); Canvas.SetTop(bar, bottom - height); _plot.Children.Add(bar);
            }
            return;
        }
        foreach (bool input in _kind == "Tokens" ? new[] { true, false } : new[] { true })
        {
            Brush brush = _kind == "Costs" ? PanelKit.Res("AccentFillColorDefaultBrush") : input ? _input : _output;
            var line = new Polyline { Stroke = brush, StrokeThickness = 1.8, StrokeLineJoin = PenLineJoin.Round, IsHitTestVisible = false };
            for (int i = 0; i < _days.Count; i++)
                line.Points.Add(new Point(4 + (width - 8) * i / Math.Max(1, _days.Count - 1), bottom - (bottom - 5) * (_kind == "Costs" ? (double)_days[i].Cost : input ? _days[i].Input : _days[i].Output) / max));
            _plot.Children.Add(line);
            var point = line.Points[^1];
            var dot = new Ellipse { Width = 7, Height = 7, Stroke = brush, StrokeThickness = 1.8, Fill = PanelKit.Res("CardBackgroundFillColorDefaultBrush"), IsHitTestVisible = false };
            Canvas.SetLeft(dot, point.X - 3.5); Canvas.SetTop(dot, point.Y - 3.5); _plot.Children.Add(dot);
        }
    }
}
