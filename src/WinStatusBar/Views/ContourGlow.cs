using System.Numerics;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Effects;
using Microsoft.Graphics.Canvas.UI.Composition;
using Microsoft.Graphics.DirectX;
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
/// Desempenho (09/11/2026): o desfoque é feito uma vez só, numa máscara parada (o contorno desfocado, refeito apenas
/// quando o tamanho, a escala ou a placa de vídeo mudam), e o gradiente desliza por trás dela. Antes, duas camadas com
/// desfoque eram refeitas a cada quadro (165 por segundo, nas duas barras): ~18 a 35% de um núcleo e ~8% da GPU
/// enquanto uma IA trabalhava. O gradiente varia ao longo de centenas de pixels e o desfoque é de poucos pixels, então
/// "gradiente × contorno desfocado" fica igual a "contorno colorido desfocado".
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
    private readonly LayerVisual _root; // camada própria: o compositor a guarda pronta e só a refaz quando o brilho muda
    private readonly ContainerVisual _lineRoot;
    private readonly SpriteVisual _glowRoot, _outerRoot;
    private readonly CompositionDrawingSurface _glowMask, _outerMask;
    private readonly CompositionRoundedRectangleGeometry _geometry;
    private readonly CompositionLinearGradientBrush _lineBrush, _glowBrush, _outerBrush;
    private readonly UISettings _ui = new();
    private Canvas? _outerHost;
    private Vector3 _placed = new(float.NaN);
    private bool _active, _running, _attached;
    private Vector2 _size;
    private (Vector2 Size, float Scale) _maskDrawn = (new(float.NaN), float.NaN);

    public bool Enabled { get; set; } = true;

    public ContourGlow(FrameworkElement element, float cornerRadius)
    {
        _element = element; _corner = cornerRadius;
        _c = ElementCompositionPreview.GetElementVisual(element).Compositor;
        _geometry = _c.CreateRoundedRectangleGeometry();
        _lineBrush = Gradient(); _glowBrush = Gradient(); _outerBrush = Gradient();

        // Dentro do elemento (ele recorta o que passa da própria borda): brilho desfocado e, por cima, a linha.
        _glowRoot = Masked(_glowBrush, out _glowMask);
        _lineRoot = _c.CreateContainerVisual();
        _lineRoot.Children.InsertAtTop(Stroke(_geometry, _lineBrush, LineWidth));
        _root = _c.CreateLayerVisual();
        _root.IsVisible = false;
        _root.Children.InsertAtTop(_glowRoot);
        _root.Children.InsertAtTop(_lineRoot);
        // Por fora: halo curto numa tela vazia acima do elemento.
        _outerRoot = Masked(_outerBrush, out _outerMask);
        _outerRoot.IsVisible = false;
        // Inscrito só enquanto o elemento está na tela: o evento é estático, e uma barra fechada (troca de monitor,
        // configurações aplicadas) ficaria presa na memória por ele.
        element.Loaded += (_, _) => { DeviceReplaced -= OnDeviceReplaced; DeviceReplaced += OnDeviceReplaced; };
        element.Unloaded += (_, _) => DeviceReplaced -= OnDeviceReplaced;
        if (element.IsLoaded) DeviceReplaced += OnDeviceReplaced;

        if (element.IsLoaded) Attach(); else element.Loaded += (_, _) => Attach();
        element.SizeChanged += (_, _) => Update(restart: true);
        element.LayoutUpdated += (_, _) => { if (_outerRoot.IsVisible) Place(); };
        UpdateColors();
    }

    // ------------------------------------------------------------------ máscaras desfocadas (Win2D, desenhadas uma vez)
    private static CompositionGraphicsDevice? _graphics;
    private static event Action? DeviceReplaced;

    private static CompositionGraphicsDevice Graphics(Compositor c)
    {
        if (_graphics is not null) return _graphics;
        var canvas = CanvasDevice.GetSharedDevice();
        canvas.DeviceLost += OnDeviceLost;
        _graphics = CanvasComposition.CreateCompositionGraphicsDevice(c, canvas);
        _graphics.RenderingDeviceReplaced += (_, _) => DeviceReplaced?.Invoke();
        return _graphics;
    }

    /// <summary>Placa de vídeo reiniciada (driver, troca de GPU): outro dispositivo, e as máscaras são redesenhadas.</summary>
    private static void OnDeviceLost(CanvasDevice sender, object args)
    {
        sender.DeviceLost -= OnDeviceLost;
        var next = CanvasDevice.GetSharedDevice();
        next.DeviceLost += OnDeviceLost;
        if (_graphics is not null) CanvasComposition.SetCanvasDevice(_graphics, next);
    }

    private void OnDeviceReplaced() => _element.DispatcherQueue?.TryEnqueue(() => { _maskDrawn = (new(float.NaN), float.NaN); if (_running) DrawMasks(_size); });

    /// <summary>Gradiente (que desliza) visto através de uma máscara parada: o contorno já desfocado.</summary>
    private SpriteVisual Masked(CompositionBrush source, out CompositionDrawingSurface mask)
    {
        mask = Graphics(_c).CreateDrawingSurface2(new Windows.Graphics.SizeInt32 { Width = 1, Height = 1 }, DirectXPixelFormat.B8G8R8A8UIntNormalized, DirectXAlphaMode.Premultiplied);
        var maskBrush = _c.CreateSurfaceBrush(mask);
        maskBrush.Stretch = CompositionStretch.Fill;
        var brush = _c.CreateMaskBrush();
        brush.Source = source;
        brush.Mask = maskBrush;
        var sprite = _c.CreateSpriteVisual();
        sprite.Brush = brush;
        return sprite;
    }

    /// <summary>Contornos desfocados do brilho (por dentro) e do halo (por fora), na escala da tela, só quando mudam.</summary>
    private void DrawMasks(Vector2 size)
    {
        float scale = (float)(_element.XamlRoot?.RasterizationScale ?? 1.0);
        if (_maskDrawn.Size == size && _maskDrawn.Scale == scale) return;
        float corner = Math.Min(_corner, (size.Y - 1) / 2);
        try
        {
            DrawStroke(_glowMask, size, scale, new Vector2(0.5f), size - new Vector2(1f), corner, GlowStroke, BlurAmount);
            var outer = size + new Vector2(2 * OuterPad);
            DrawStroke(_outerMask, outer, scale, new Vector2(OuterPad + 0.5f - OuterGrow), size - new Vector2(1f) + new Vector2(2 * OuterGrow), corner + OuterGrow, OuterStroke, OuterBlur);
            _maskDrawn = (size, scale);
        }
        catch (Exception ex) { App.Log("Brilho do contorno: máscara não desenhada: " + ex.Message); }
    }

    private static void DrawStroke(CompositionDrawingSurface surface, Vector2 sizeDip, float scale, Vector2 offset, Vector2 rect, float corner, float stroke, float blur)
    {
        var px = new Windows.Graphics.SizeInt32 { Width = Math.Max(1, (int)Math.Ceiling(sizeDip.X * scale)), Height = Math.Max(1, (int)Math.Ceiling(sizeDip.Y * scale)) };
        surface.Resize(px);
        using var ds = CanvasComposition.CreateDrawingSession(surface);
        ds.Clear(Microsoft.UI.Colors.Transparent);
        using var shape = new CanvasCommandList(ds);
        using (var s = shape.CreateDrawingSession())
        // Como a camada desfocada de antes, do tamanho do elemento: o que passa da borda é cortado antes do desfoque.
        using (s.CreateLayer(1f, new Windows.Foundation.Rect(0, 0, px.Width, px.Height)))
            s.DrawRoundedRectangle(offset.X * scale, offset.Y * scale, rect.X * scale, rect.Y * scale, corner * scale, corner * scale, Microsoft.UI.Colors.White, stroke * scale);
        using var blurred = new GaussianBlurEffect { Source = shape, BlurAmount = blur * scale, BorderMode = EffectBorderMode.Soft, Optimization = EffectOptimization.Quality };
        ds.DrawImage(blurred);
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

    /// <summary>
    /// Quadros por segundo do brilho. Ele é um efeito de fundo, lento e desfocado: 30 trocas por segundo bastam e cada uma
    /// custa o redesenho dos gradientes. Contínuo, ele mudava a cada quadro da tela (165 por segundo neste PC).
    /// </summary>
    private const double Fps = 30;

    /// <summary>Degraus de 1/30 s num trecho de animação: o valor só muda (e a tela só é redesenhada) a cada degrau.</summary>
    private CompositionEasingFunction Steps(double seconds) => _c.CreateStepEasingFunction(Math.Max(1, (int)Math.Round(seconds * Fps)));

    /// <summary>Desliza o gradiente um período inteiro por ciclo (emenda perfeita), em velocidade constante.</summary>
    private void Slide(CompositionLinearGradientBrush brush, float originX, float period, float direction, float pace)
    {
        brush.StartPoint = new Vector2(originX, 0);
        brush.EndPoint = new Vector2(originX + period, 0);
        double seconds = period / (PixelsPerSecond * pace);
        var slide = _c.CreateVector2KeyFrameAnimation();
        slide.InsertKeyFrame(0f, Vector2.Zero);
        slide.InsertKeyFrame(1f, new Vector2(direction * period, 0), Steps(seconds));
        slide.Duration = TimeSpan.FromSeconds(seconds);
        slide.IterationBehavior = AnimationIterationBehavior.Forever;
        brush.StartAnimation("Offset", slide);
    }

    /// <summary>Respiração lenta da intensidade, num ritmo que não coincide com o das cores.</summary>
    private void Breathe(Visual layer, float opacity, double seconds)
    {
        var breathe = _c.CreateScalarKeyFrameAnimation();
        breathe.InsertKeyFrame(0f, opacity);
        breathe.InsertKeyFrame(0.38f, opacity * 0.7f, Steps(seconds * 0.38));
        breathe.InsertKeyFrame(0.71f, opacity * 1.12f, Steps(seconds * 0.33));
        breathe.InsertKeyFrame(1f, opacity, Steps(seconds * 0.29));
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
        DrawMasks(size);
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
