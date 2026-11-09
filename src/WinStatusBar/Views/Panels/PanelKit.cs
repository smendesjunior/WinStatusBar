using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;
using Windows.UI;

namespace WinStatusBar.Views.Panels;

/// <summary>
/// Peças visuais comuns dos painéis da barra (CPU, memória, GPU, rede), no estilo do Gerenciador de Tarefas
/// do Windows 11: título 18, rótulos 12 secundários, valores 17 regulares, especificações em "Rótulo: valor".
/// </summary>
internal static class PanelKit
{
    /// <summary>Largura útil de todos os painéis (o flyout soma 16 px de margem igual dos dois lados).</summary>
    public const double Width = 480;
    public const double OuterCornerRadius = 8;

    public static Brush Res(string key) => WinStatusBar.Services.ThemeBrushes.Get(key);

    /// <summary>
    /// Botão pequeno padrão de todos os painéis e menus da barra: mesma altura (32), fonte (12) e preenchimento,
    /// para os botões lado a lado nunca ficarem de tamanhos diferentes.
    /// </summary>
    public static T Small<T>(T button) where T : Control
    {
        button.FontSize = 12; button.MinHeight = 32; button.Padding = new Thickness(10, 6, 10, 6);
        button.VerticalAlignment = VerticalAlignment.Center;
        return button;
    }

    public static Color Accent()
        => (Res("AccentFillColorDefaultBrush") as SolidColorBrush)?.Color ?? Color.FromArgb(255, 0, 120, 212);

    public static TextBlock Title(string text) => new() { Text = text, FontSize = 15 };

    public static TextBlock Caption(string text, double size = 11) => new()
    {
        Text = text, FontSize = size, TextWrapping = TextWrapping.Wrap, Foreground = Res("TextFillColorSecondaryBrush"),
    };

    public static TextBlock Dot() => new() { Text = "•", FontSize = 13, Foreground = Res("TextFillColorTertiaryBrush") };

    /// <summary>Cabeçalho: título à esquerda, subtítulo (ex.: nome do processador) abaixo, e um texto/elemento à direita.</summary>
    public static Grid Header(string title, TextBlock subtitle, FrameworkElement? right = null)
    {
        var grid = new Grid { ColumnSpacing = 12 };
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var left = new StackPanel { Spacing = 0 };
        left.Children.Add(new TextBlock { Text = title, FontSize = 18, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        left.Children.Add(subtitle);
        grid.Children.Add(left);
        if (right is not null) { right.VerticalAlignment = VerticalAlignment.Top; Grid.SetColumn(right, 1); grid.Children.Add(right); }
        return grid;
    }

    /// <summary>Corpo rolável com a largura padrão e margens iguais (a barra de rolagem do WinUI sobrepõe o conteúdo).</summary>
    public static ScrollViewer Body(StackPanel body)
    {
        body.Width = Width;
        return new ScrollViewer
        {
            Content = body, MaxHeight = 650,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            HorizontalScrollMode = ScrollMode.Disabled,
        };
    }

    /// <summary>Linha de leituras lado a lado (rótulo 12 secundário, valor 17 regular), registrando os valores em <paramref name="fields"/>.</summary>
    public static StackPanel FieldRow(Dictionary<string, TextBlock> fields, params string[] labels)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 16 };
        foreach (var label in labels)
        {
            var panel = new StackPanel();
            panel.Children.Add(Caption(label, 12));
            var value = new TextBlock { Text = "—", FontSize = 17, LineHeight = 22 };
            panel.Children.Add(value);
            fields[label] = value;
            row.Children.Add(panel);
        }
        return row;
    }

    /// <summary>Leituras ao vivo à esquerda e especificações à direita, como no Gerenciador de Tarefas.</summary>
    public static Grid Summary(FrameworkElement live, FrameworkElement specs)
    {
        var grid = new Grid { ColumnSpacing = 20, Margin = new Thickness(0, 12, 0, 0) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        Grid.SetColumn(specs, 1);
        grid.Children.Add(live); grid.Children.Add(specs);
        return grid;
    }

    /// <summary>Especificações "Rótulo:  valor" com a coluna de rótulos alinhada pelo maior texto.</summary>
    public static void FillSpecs(Grid host, IEnumerable<(string Label, string Value)> rows)
    {
        host.Children.Clear(); host.RowDefinitions.Clear(); host.ColumnDefinitions.Clear();
        host.ColumnSpacing = 10; host.RowSpacing = 3;
        host.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        host.ColumnDefinitions.Add(new ColumnDefinition());
        int r = 0;
        foreach (var (label, value) in rows)
        {
            host.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var l = Caption(label + ":", 12); l.TextWrapping = TextWrapping.Wrap; l.MaxWidth = 150;
            var v = new TextBlock { Text = value, FontSize = 12, TextWrapping = TextWrapping.Wrap };
            Grid.SetRow(l, r); Grid.SetRow(v, r); Grid.SetColumn(v, 1);
            host.Children.Add(l); host.Children.Add(v);
            r++;
        }
    }

    /// <summary>Flyout padrão dos painéis: Desktop Acrylic, margens iguais de 16 px e largura sem limite do WinUI (456 px).</summary>
    public static Flyout CreateFlyout(FrameworkElement content, Action onOpening, Action onClosed)
    {
        var flyout = new Flyout
        {
            Content = content,
            SystemBackdrop = new DesktopAcrylicBackdrop(),
            ShouldConstrainToRootBounds = false,
            Placement = Microsoft.UI.Xaml.Controls.Primitives.FlyoutPlacementMode.BottomEdgeAlignedRight,
        };
        var style = new Style(typeof(FlyoutPresenter));
        style.Setters.Add(new Setter(FlyoutPresenter.BackgroundProperty, new SolidColorBrush(Colors.Transparent)));
        style.Setters.Add(new Setter(FlyoutPresenter.PaddingProperty, new Thickness(16)));
        style.Setters.Add(new Setter(FlyoutPresenter.CornerRadiusProperty, new CornerRadius(OuterCornerRadius)));
        // Contorno fino e sombra padrão dos menus do Windows 11 (profundidade sobre o Acrílico).
        style.Setters.Add(new Setter(FlyoutPresenter.BorderBrushProperty, Res("SurfaceStrokeColorDefaultBrush")));
        style.Setters.Add(new Setter(FlyoutPresenter.BorderThicknessProperty, new Thickness(1)));
        style.Setters.Add(new Setter(FlyoutPresenter.IsDefaultShadowEnabledProperty, true));
        style.Setters.Add(new Setter(FlyoutPresenter.MaxWidthProperty, Width + 48));
        style.Setters.Add(new Setter(ScrollViewer.HorizontalScrollModeProperty, ScrollMode.Disabled));
        style.Setters.Add(new Setter(ScrollViewer.HorizontalScrollBarVisibilityProperty, ScrollBarVisibility.Disabled));
        flyout.FlyoutPresenterStyle = style;
        flyout.Opening += (_, _) =>
        {
            content.RequestedTheme = App.IsDarkTheme ? ElementTheme.Dark : ElementTheme.Light;
            onOpening();
        };
        flyout.Closed += (_, _) => onClosed();
        WinStatusBar.Services.PopupMaterial.FollowAppTheme(flyout, content);
        return flyout;
    }

    public static string Gb(double bytes, string format = "0.0") => (bytes / 1073741824.0).ToString(format) + " GB";

    /// <summary>Taxa em bits por segundo, como o Gerenciador de Tarefas (Kbps/Mbps/Gbps).</summary>
    public static string Bits(double bytesPerSecond)
    {
        double bits = bytesPerSecond * 8;
        return bits >= 1e9 ? $"{bits / 1e9:0.0} Gbps" : bits >= 1e6 ? $"{bits / 1e6:0.0} Mbps" : $"{bits / 1e3:0} Kbps";
    }
}

/// <summary>Painel de detalhes aberto pelo clique num módulo da barra; só trabalha enquanto está aberto.</summary>
internal interface IDetailsPanel : IDisposable
{
    FrameworkElement Content { get; }
    void Start();
    void Stop();
}

/// <summary>Série de um <see cref="AreaChart"/>: valores (mais antigo primeiro), escala máxima e estilo.</summary>
internal sealed record ChartSeries(double[] Values, double Max, bool Dashed = false, bool Fill = true, double Opacity = 1);

/// <summary>
/// Cartão com gráfico de área suave (Catmull-Rom → Bézier), degradê na cor de destaque e guias pontilhadas.
/// Cabeçalho livre à esquerda/direita. Usado nos painéis de CPU, memória, GPU e rede.
/// </summary>
internal sealed class AreaChart
{
    private readonly Grid _plot = new();
    private readonly List<(Microsoft.UI.Xaml.Shapes.Path Area, Microsoft.UI.Xaml.Shapes.Path Line)> _series = new();
    private ChartSeries[] _last = [];
    public Grid Root { get; }
    public StackPanel Left { get; } = new() { Orientation = Orientation.Horizontal, Spacing = 6 };
    public StackPanel Right { get; } = new() { Orientation = Orientation.Horizontal, Spacing = 6, HorizontalAlignment = HorizontalAlignment.Right };

    public AreaChart(double height, bool compact = false)
    {
        Root = new Grid
        {
            Padding = compact ? new Thickness(10, 6, 10, 8) : new Thickness(14, 10, 14, 10),
            RowSpacing = compact ? 4 : 8,
            CornerRadius = new CornerRadius(8),
            Background = PanelKit.Res("CardBackgroundFillColorDefaultBrush"),
            BorderBrush = PanelKit.Res("CardStrokeColorDefaultBrush"), BorderThickness = new Thickness(1),
        };
        Root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(height) });
        var head = new Grid();
        head.Children.Add(Left); head.Children.Add(Right);
        Root.Children.Add(head);
        foreach (double level in new[] { 0.25, 0.5, 0.75 })
            _plot.Children.Add(new Line
            {
                Stroke = PanelKit.Res("DividerStrokeColorDefaultBrush"), StrokeThickness = 1,
                StrokeDashArray = new DoubleCollection { 2, 4 }, VerticalAlignment = VerticalAlignment.Top, Tag = level,
            });
        _plot.SizeChanged += (_, e) => { LayoutGuides(e.NewSize); Draw(); };
        Grid.SetRow(_plot, 1);
        Root.Children.Add(_plot);
    }

    private void LayoutGuides(Size size)
    {
        foreach (var g in _plot.Children.OfType<Line>())
        {
            g.X2 = size.Width;
            g.Margin = new Thickness(0, 4 + (size.Height - 5) * (1 - (double)g.Tag), 0, 0);
        }
    }

    public void Update(params ChartSeries[] series) { _last = series; Draw(); }

    private void Draw()
    {
        double w = _plot.ActualWidth, h = _plot.ActualHeight;
        var accent = PanelKit.Accent();
        while (_series.Count < _last.Length)
        {
            var area = new Microsoft.UI.Xaml.Shapes.Path();
            var line = new Microsoft.UI.Xaml.Shapes.Path { StrokeThickness = 1.6, StrokeLineJoin = PenLineJoin.Round };
            _plot.Children.Add(area); _plot.Children.Add(line);
            _series.Add((area, line));
        }
        for (int i = 0; i < _series.Count; i++)
        {
            var (area, line) = _series[i];
            if (i >= _last.Length || w <= 0 || h <= 0 || _last[i].Values.Length < 2) { area.Data = null; line.Data = null; continue; }
            var s = _last[i];
            var stroke = new SolidColorBrush(accent) { Opacity = s.Opacity };
            line.Stroke = stroke;
            line.StrokeDashArray = s.Dashed ? new DoubleCollection { 3, 2 } : null;
            if (s.Fill)
            {
                var gradient = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(0, 1) };
                gradient.GradientStops.Add(new GradientStop { Color = Color.FromArgb((byte)(120 * s.Opacity), accent.R, accent.G, accent.B), Offset = 0 });
                gradient.GradientStops.Add(new GradientStop { Color = Color.FromArgb(0, accent.R, accent.G, accent.B), Offset = 1 });
                area.Fill = gradient;
            }
            else area.Fill = null;
            const double top = 4;
            double bottom = h - 1, capacity = 60, step = w / (capacity - 1), max = s.Max <= 0 ? 1 : s.Max;
            double x0 = (capacity - Math.Min(s.Values.Length, capacity)) * step;
            var pts = s.Values.TakeLast((int)capacity)
                .Select((v, k) => new Point(x0 + k * step, bottom - Math.Clamp(v / max, 0, 1) * (bottom - top))).ToArray();
            line.Data = Smooth(pts, close: false, bottom);
            area.Data = s.Fill ? Smooth(pts, close: true, bottom) : null;
        }
    }

    private static PathGeometry Smooth(Point[] p, bool close, double bottom)
    {
        var figure = new PathFigure { StartPoint = close ? new Point(p[0].X, bottom) : p[0], IsClosed = close, IsFilled = close };
        if (close) figure.Segments.Add(new LineSegment { Point = p[0] });
        for (int i = 0; i < p.Length - 1; i++)
        {
            Point a = p[Math.Max(0, i - 1)], b = p[i], c = p[i + 1], d = p[Math.Min(p.Length - 1, i + 2)];
            var c1 = new Point(b.X + (c.X - a.X) / 6, Math.Clamp(b.Y + (c.Y - a.Y) / 6, 0, bottom));
            var c2 = new Point(c.X - (d.X - b.X) / 6, Math.Clamp(c.Y - (d.Y - b.Y) / 6, 0, bottom));
            figure.Segments.Add(new BezierSegment { Point1 = c1, Point2 = c2, Point3 = c });
        }
        if (close) figure.Segments.Add(new LineSegment { Point = new Point(p[^1].X, bottom) });
        var geometry = new PathGeometry();
        geometry.Figures.Add(figure);
        return geometry;
    }

    /// <summary>Texto de cabeçalho no tamanho padrão do cartão.</summary>
    public static TextBlock Text(string text = "", bool secondary = false, double size = 13) => new()
    {
        Text = text, FontSize = size, Foreground = secondary ? PanelKit.Res("TextFillColorSecondaryBrush") : PanelKit.Res("TextFillColorPrimaryBrush"),
    };
}
