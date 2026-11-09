using System.Numerics;
using Microsoft.Graphics.Canvas.Effects;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using Windows.UI;
using Windows.UI.ViewManagement;

namespace WinStatusBar.Views;

/// <summary>
/// Brilho do Rainbow Button da Magic UI, traduzido do CSS do componente para o compositor e estendido para 360°.
/// O original usa um gradiente LINEAR contínuo (--color-1, 5, 3, 4, 2) com background-size 200% e a posição animada
/// ("linear infinite"): as cores deslizam na horizontal. Aqui, cada camada é um único gradiente contínuo (sem emendas):
/// • linha: borda leve com o gradiente, no contorno inteiro;
/// • brilho: o mesmo gradiente desfocado, nascendo na borda e esmaecendo para dentro do elemento;
/// • halo: um contorno curto por fora, que cabe nos ~6 px livres em volta dos módulos sem ser cortado.
/// Duas direções: linha e halo deslizam para um lado e o brilho para o outro, com ciclos de tamanhos diferentes, e o
/// brilho respira devagar; assim o padrão não se repete. Roda no compositor e respeita "Efeitos de animação" do Windows.
/// </summary>
internal sealed class ContourGlow
{
    /// <summary>Ordem do gradiente do original: color-1, color-5, color-3, color-4, color-2 (hsl(… 100% 63%)).</summary>
    private static readonly Color[] Rainbow =
    [
        Color.FromArgb(255, 255, 66, 66), Color.FromArgb(255, 160, 255, 66), Color.FromArgb(255, 66, 160, 255),
        Color.FromArgb(255, 66, 207, 255), Color.FromArgb(255, 160, 66, 255),
    ];
    /// <summary>Arco-íris "lavado", como o brilho desfocado da referência: no escuro, 25% mais perto do branco; no claro, só 10%.</summary>
    private static Color[] Palette(bool dark) => Rainbow.Select(c => Wash(c, dark ? 0.25 : 0.1)).ToArray();
    private static Color Wash(Color c, double amount) => Color.FromArgb(255,
        (byte)(c.R + (255 - c.R) * amount), (byte)(c.G + (255 - c.G) * amount), (byte)(c.B + (255 - c.B) * amount));
    // Intensidades por tema: no claro, borda mais marcada e cores mais presentes.
    private static float GlowOpacity(bool dark) => dark ? 0.2f : 0.32f;
    private static float LineOpacity(bool dark) => dark ? 0.22f : 0.6f;
    private static float OuterOpacity(bool dark) => dark ? 0.3f : 0.45f;

    private const float BlurAmount = 5f;        // brilho macio para dentro
    private const float GlowStroke = 13f;       // faixa desfocada sobre a borda: só a metade de dentro aparece
    private const float LineWidth = 1f;
    // Halo: mesmo alcance (~7 px), mas com mais desfoque e traço mais fino, para ir sumindo em vez de marcar o limite.
    private const float OuterPad = 9f, OuterBlur = 2.1f, OuterStroke = 1.2f, OuterGrow = 0.6f;
    private const float PixelsPerSecond = 156f; // 1,2× a velocidade anterior (pedido de 08/11/2026)

    private readonly FrameworkElement _element;
    private readonly float _corner;
    private readonly Compositor _c;
    private readonly ContainerVisual _root, _lineRoot;
    private readonly LayerVisual _glowRoot, _outerRoot;
    private readonly CompositionRoundedRectangleGeometry _geometry, _outerGeometry;
    private readonly CompositionLinearGradientBrush _lineBrush, _glowBrush, _outerBrush;
    private readonly UISettings _ui = new();
    private Canvas? _outerHost;
    private Vector3 _placed = new(float.NaN);
    private bool _active, _running, _attached;
    private Vector2 _size;

    public bool Enabled { get; set; } = true;

    public ContourGlow(FrameworkElement element, float cornerRadius)
    {
        _element = element; _corner = cornerRadius;
        _c = ElementCompositionPreview.GetElementVisual(element).Compositor;
        _geometry = _c.CreateRoundedRectangleGeometry();
        _outerGeometry = _c.CreateRoundedRectangleGeometry();
        _lineBrush = Gradient(); _glowBrush = Gradient(); _outerBrush = Gradient();

        // Dentro do elemento (ele recorta o que passa da própria borda): brilho desfocado e, por cima, a linha.
        _glowRoot = Blurred(BlurAmount);
        _glowRoot.Children.InsertAtTop(Stroke(_geometry, _glowBrush, GlowStroke));
        _lineRoot = _c.CreateContainerVisual();
        _lineRoot.Children.InsertAtTop(Stroke(_geometry, _lineBrush, LineWidth));
        _root = _c.CreateContainerVisual();
        _root.IsVisible = false;
        _root.Children.InsertAtTop(_glowRoot);
        _root.Children.InsertAtTop(_lineRoot);
        // Por fora: halo curto numa tela vazia acima do elemento.
        _outerRoot = Blurred(OuterBlur);
        _outerRoot.IsVisible = false;
        _outerRoot.Children.InsertAtTop(Stroke(_outerGeometry, _outerBrush, OuterStroke));

        if (element.IsLoaded) Attach(); else element.Loaded += (_, _) => Attach();
        element.SizeChanged += (_, _) => Update(restart: true);
        element.LayoutUpdated += (_, _) => { if (_outerRoot.IsVisible) Place(); };
        UpdateColors();
    }

    /// <summary>Gradiente do original, repetido sem emenda (a última cor volta à primeira).</summary>
    private CompositionLinearGradientBrush Gradient()
    {
        var brush = _c.CreateLinearGradientBrush();
        brush.MappingMode = CompositionMappingMode.Absolute;
        brush.ExtendMode = CompositionGradientExtendMode.Wrap;
        for (int i = 0; i <= Rainbow.Length; i++)
            brush.ColorStops.Add(_c.CreateColorGradientStop(i / (float)Rainbow.Length, Rainbow[i % Rainbow.Length]));
        return brush;
    }

    private LayerVisual Blurred(float amount)
    {
        var blur = new GaussianBlurEffect { Name = "Blur", BlurAmount = amount, BorderMode = EffectBorderMode.Soft, Optimization = EffectOptimization.Balanced, Source = new CompositionEffectSourceParameter("source") };
        var layer = _c.CreateLayerVisual();
        layer.Effect = _c.CreateEffectFactory(blur).CreateBrush();
        return layer;
    }

    private ShapeVisual Stroke(CompositionGeometry geometry, CompositionBrush brush, float thickness)
    {
        var shape = _c.CreateSpriteShape(geometry);
        shape.StrokeBrush = brush;
        shape.StrokeThickness = thickness;
        var visual = _c.CreateShapeVisual();
        visual.Shapes.Add(shape);
        visual.RelativeSizeAdjustment = Vector2.One;
        return visual;
    }

    private void Attach()
    {
        if (_attached) return;
        _attached = true;
        ElementCompositionPreview.SetElementChildVisual(_element, _root);
        // Primeiro Grid acima do elemento e de qualquer ScrollViewer (dentro do conteúdo da janela): não recorta o halo.
        Grid? host = null;
        var top = _element.XamlRoot?.Content;
        for (DependencyObject? p = VisualTreeHelper.GetParent(_element); p is not null; p = VisualTreeHelper.GetParent(p))
        {
            if (p is ScrollViewer) host = null;
            else if (host is null && p is Grid { CornerRadius.TopLeft: 0 } grid) host = grid;
            if (ReferenceEquals(p, top)) break;
        }
        if (host is not null)
        {
            _outerHost = new Canvas { IsHitTestVisible = false };
            Grid.SetRowSpan(_outerHost, Math.Max(1, host.RowDefinitions.Count));
            Grid.SetColumnSpan(_outerHost, Math.Max(1, host.ColumnDefinitions.Count));
            host.Children.Add(_outerHost);
            ElementCompositionPreview.SetElementChildVisual(_outerHost, _outerRoot);
        }
        Update(restart: true);
    }

    public void SetActive(bool active) { _active = active; Update(restart: false); }

    /// <summary>Cores e intensidades do tema atual (no claro, mais vivas e com a borda mais marcada).</summary>
    public void UpdateColors()
    {
        bool dark = App.IsDarkTheme;
        var palette = Palette(dark);
        foreach (var brush in new[] { _lineBrush, _glowBrush, _outerBrush })
            for (int i = 0; i < brush.ColorStops.Count; i++) brush.ColorStops[i].Color = palette[i % palette.Length];
        _lineRoot.Opacity = LineOpacity(dark);
        _glowRoot.Opacity = GlowOpacity(dark);
        _outerRoot.Opacity = OuterOpacity(dark);
        if (_running) { Breathe(_glowRoot, GlowOpacity(dark), 7.3); Breathe(_outerRoot, OuterOpacity(dark), 9.1); }
    }

    private void Place()
    {
        if (_outerHost is null) return;
        try
        {
            var p = _element.TransformToVisual(_outerHost).TransformPoint(new Windows.Foundation.Point(0, 0));
            var offset = new Vector3((float)p.X - OuterPad, (float)p.Y - OuterPad, 0);
            if (offset != _placed) { _outerRoot.Offset = offset; _placed = offset; }
        }
        catch (ArgumentException) { } // fora da árvore por um instante (reorganização dos módulos)
    }

    /// <summary>Desliza o gradiente um período inteiro por ciclo (emenda perfeita), em velocidade constante.</summary>
    private void Slide(CompositionLinearGradientBrush brush, float originX, float period, float direction, float pace)
    {
        brush.StartPoint = new Vector2(originX, 0);
        brush.EndPoint = new Vector2(originX + period, 0);
        var slide = _c.CreateVector2KeyFrameAnimation();
        slide.InsertKeyFrame(0f, Vector2.Zero);
        slide.InsertKeyFrame(1f, new Vector2(direction * period, 0), _c.CreateLinearEasingFunction());
        slide.Duration = TimeSpan.FromSeconds(period / (PixelsPerSecond * pace));
        slide.IterationBehavior = AnimationIterationBehavior.Forever;
        brush.StartAnimation("Offset", slide);
    }

    /// <summary>Respiração lenta da intensidade, num ritmo que não coincide com o das cores.</summary>
    private void Breathe(Visual layer, float opacity, double seconds)
    {
        var breathe = _c.CreateScalarKeyFrameAnimation();
        var ease = _c.CreateCubicBezierEasingFunction(new Vector2(0.4f, 0f), new Vector2(0.6f, 1f));
        breathe.InsertKeyFrame(0f, opacity);
        breathe.InsertKeyFrame(0.38f, opacity * 0.7f, ease);
        breathe.InsertKeyFrame(0.71f, opacity * 1.12f, ease);
        breathe.InsertKeyFrame(1f, opacity, ease);
        breathe.Duration = TimeSpan.FromSeconds(seconds);
        breathe.IterationBehavior = AnimationIterationBehavior.Forever;
        layer.StartAnimation("Opacity", breathe);
    }

    private void Update(bool restart)
    {
        var size = new Vector2((float)_element.ActualWidth, (float)_element.ActualHeight);
        bool animations = true;
        try { animations = _ui.AnimationsEnabled; } catch { }
        bool on = _active && Enabled && animations && _attached && _element.Visibility == Visibility.Visible && size.X > 20 && size.Y > 10;
        if (!on)
        {
            if (_running)
            {
                foreach (var brush in new[] { _lineBrush, _glowBrush, _outerBrush }) brush.StopAnimation("Offset");
                _glowRoot.StopAnimation("Opacity"); _outerRoot.StopAnimation("Opacity");
                _glowRoot.Opacity = GlowOpacity(App.IsDarkTheme); _outerRoot.Opacity = OuterOpacity(App.IsDarkTheme);
                _running = false;
            }
            _root.IsVisible = false;
            _outerRoot.IsVisible = false;
            return;
        }
        if (_running && !restart && size == _size) return;
        _size = size;
        float corner = Math.Min(_corner, (size.Y - 1) / 2);
        // O visual filho de um elemento não herda o tamanho dele: tamanhos explícitos.
        _root.Size = size; _glowRoot.Size = size; _lineRoot.Size = size;
        _geometry.Offset = new Vector2(0.5f);
        _geometry.Size = size - new Vector2(1f);
        _geometry.CornerRadius = new Vector2(corner);
        _outerRoot.Size = size + new Vector2(2 * OuterPad);
        _outerGeometry.Offset = new Vector2(OuterPad + 0.5f - OuterGrow);
        _outerGeometry.Size = size - new Vector2(1f) + new Vector2(2 * OuterGrow);
        _outerGeometry.CornerRadius = new Vector2(corner + OuterGrow);
        // Um ciclo de cores ocupa o dobro da largura (como o background-size 200% do original): poucas cores por vez.
        float period = Math.Max(240f, size.X * 2);
        Slide(_lineBrush, 0, period, 1, 1.00f);
        Slide(_glowBrush, 0, period * 1.37f, -1, 0.91f);     // sentido oposto ao da linha
        Slide(_outerBrush, OuterPad, period * 1.18f, 1, 0.87f);
        Breathe(_glowRoot, GlowOpacity(App.IsDarkTheme), 7.3);
        Breathe(_outerRoot, OuterOpacity(App.IsDarkTheme), 9.1);
        Place();
        _outerRoot.IsVisible = _outerHost is not null;
        _root.IsVisible = true;
        _running = true;
    }
}
