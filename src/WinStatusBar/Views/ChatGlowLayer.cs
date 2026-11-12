using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using Windows.UI;
using WinStatusBar.Services;

namespace WinStatusBar.Views;

/// <summary>
/// Sombras e brilho do chat, desenhados numa camada de composição ATRÁS dos controles (sem mexer nos modelos do
/// WinUI nem no layout). Pedido de 12/11/2026: em repouso os botões ficam como os nativos; o brilho na cor de
/// destaque do Windows aparece só com o mouse em cima (pílula ligada: mais um traço embaixo). Cartões (campo de
/// texto, cápsula dos seletores) têm uma sombra de profundidade desenhada só do lado de fora, para não escurecer
/// o interior translúcido. Transições só com animações do Windows ligadas; no Alto Contraste não há brilho.
/// </summary>
internal sealed class ChatGlowLayer
{
    public enum Kind { Quiet, Accent, Toggle, Card, Capsule }

    private sealed class Item
    {
        public required FrameworkElement Target;
        public required Kind Kind;
        public required List<(SpriteVisual Sprite, DropShadow Shadow)> Parts;
        public required ShapeVisual Mask;
        public required CompositionRoundedRectangleGeometry Geometry;
        public required CompositionVisualSurface Surface;
        public ShapeVisual? Bar;
        public CompositionColorBrush? BarBrush;
        public bool Hover;
        public Vector2 Size;
        public Vector3 Offset;
    }

    private readonly FrameworkElement _layer;
    private readonly Compositor _compositor;
    private readonly ContainerVisual _root;
    private readonly List<Item> _items = [];
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _hoverWatch;

    public ChatGlowLayer(Panel host)
    {
        _layer = new Border { IsHitTestVisible = false };
        if (host is Grid grid)
        {
            Grid.SetColumnSpan(_layer, Math.Max(1, grid.ColumnDefinitions.Count));
            Grid.SetRowSpan(_layer, Math.Max(1, grid.RowDefinitions.Count));
        }
        host.Children.Insert(0, _layer);
        _compositor = ElementCompositionPreview.GetElementVisual(host).Compositor;
        _root = _compositor.CreateContainerVisual();
        ElementCompositionPreview.SetElementChildVisual(_layer, _root);
        // Posições e tamanhos acompanham o layout (pílulas que mudam de largura, botão que aparece ou some).
        host.LayoutUpdated += (_, _) => Sync();
    }

    private static bool IsCard(Kind kind) => kind is Kind.Card or Kind.Capsule;

    public void Add(FrameworkElement target, Kind kind)
    {
        var geometry = _compositor.CreateRoundedRectangleGeometry();
        var shape = _compositor.CreateSpriteShape(geometry);
        shape.FillBrush = _compositor.CreateColorBrush(Microsoft.UI.Colors.White);
        var mask = _compositor.CreateShapeVisual();
        mask.Shapes.Add(shape);
        var surface = _compositor.CreateVisualSurface();
        surface.SourceVisual = mask;
        var maskBrush = _compositor.CreateSurfaceBrush(surface);
        maskBrush.Stretch = CompositionStretch.Fill;
        // Uma sombra por item. Nos cartões ela é recortada pelo contorno arredondado (ver Sync), assim só aparece por
        // fora, inclusive nos cantos. As quatro faixas retas de antes deixavam os cantos sem sombra.
        var parts = new List<(SpriteVisual, DropShadow)>();
        for (int i = 0; i < 1; i++)
        {
            var shadow = _compositor.CreateDropShadow();
            shadow.Mask = maskBrush; // só a sombra aparece; o próprio retângulo não é desenhado
            var sprite = _compositor.CreateSpriteVisual();
            sprite.Brush = _compositor.CreateColorBrush(Color.FromArgb(0, 0, 0, 0));
            sprite.Shadow = shadow;
            _root.Children.InsertAtBottom(sprite);
            parts.Add((sprite, shadow));
        }
        var item = new Item { Target = target, Kind = kind, Parts = parts, Mask = mask, Geometry = geometry, Surface = surface };
        if (kind == Kind.Toggle)
        {
            var barGeometry = _compositor.CreateRoundedRectangleGeometry();
            barGeometry.Size = new Vector2(18, 3);
            barGeometry.CornerRadius = new Vector2(1.5f, 1.5f);
            item.BarBrush = _compositor.CreateColorBrush(Accent);
            var barShape = _compositor.CreateSpriteShape(barGeometry);
            barShape.FillBrush = item.BarBrush;
            item.Bar = _compositor.CreateShapeVisual();
            item.Bar.Size = new Vector2(18, 3);
            item.Bar.Shapes.Add(barShape);
            item.Bar.Opacity = 0;
            _root.Children.InsertAtTop(item.Bar);
        }
        _items.Add(item);

        if (!IsCard(kind))
        {
            target.PointerEntered += (_, _) => SetHover(item, true);
            target.PointerExited += (_, _) => SetHover(item, false);
            target.PointerCaptureLost += (_, _) => SetHover(item, false);
            target.PointerCanceled += (_, _) => SetHover(item, false);
            if (target is Control control) control.IsEnabledChanged += (_, _) => { if (!control.IsEnabled) item.Hover = false; Apply(item, animate: true); };
            if (target is ToggleButton toggle)
                toggle.RegisterPropertyChangedCallback(ToggleButton.IsCheckedProperty, (_, _) => Apply(item, animate: true));
        }
        Apply(item, animate: false);
    }

    /// <summary>Tema, cor de destaque ou Alto Contraste mudaram: reaplica as cores sem animar.</summary>
    public void Refresh()
    {
        foreach (var item in _items) Apply(item, animate: false);
    }

    /// <summary>Desliga qualquer brilho (janela ocultada, menu aberto por cima).</summary>
    public void ClearHover()
    {
        foreach (var item in _items)
            if (item.Hover) { item.Hover = false; Apply(item, animate: false); }
        _hoverWatch?.Stop();
    }

    private void SetHover(Item item, bool hover)
    {
        if (item.Hover == hover) return;
        item.Hover = hover;
        Apply(item, animate: true);
        if (hover) WatchHover();
    }

    /// <summary>
    /// Um menu aberto por cima do botão é outra janela: o WinUI não avisa que o mouse saiu e o brilho ficava preso.
    /// Enquanto houver brilho, confere a cada 150 ms se o cursor ainda está sobre o botão (e sobre esta janela).
    /// </summary>
    private void WatchHover()
    {
        if (_hoverWatch is null)
        {
            _hoverWatch = _layer.DispatcherQueue.CreateTimer();
            _hoverWatch.Interval = TimeSpan.FromMilliseconds(150);
            _hoverWatch.IsRepeating = true;
            _hoverWatch.Tick += (_, _) =>
            {
                bool any = false;
                foreach (var item in _items)
                {
                    if (!item.Hover) continue;
                    if (PointerIsOver(item.Target)) { any = true; continue; }
                    item.Hover = false; Apply(item, animate: true);
                }
                if (!any) _hoverWatch.Stop();
            };
        }
        _hoverWatch.Start();
    }

    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT point);
    [DllImport("user32.dll")] private static extern bool ScreenToClient(IntPtr hwnd, ref POINT point);
    [DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(POINT point);
    [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);

    private static bool PointerIsOver(FrameworkElement target)
    {
        try
        {
            if (target.XamlRoot is not { } root || target.Visibility != Visibility.Visible) return false;
            var hwnd = Microsoft.UI.Win32Interop.GetWindowFromWindowId(root.ContentIslandEnvironment.AppWindowId);
            if (!GetCursorPos(out var screen)) return true;
            var under = WindowFromPoint(screen);
            if (under != hwnd && GetAncestor(under, 2 /* GA_ROOT */) != hwnd) return false; // um menu (outra janela) está por cima
            var client = screen;
            ScreenToClient(hwnd, ref client);
            double scale = root.RasterizationScale;
            var origin = target.TransformToVisual(null).TransformPoint(new Windows.Foundation.Point(0, 0));
            double x = client.X / scale, y = client.Y / scale;
            return x >= origin.X && y >= origin.Y && x <= origin.X + target.ActualWidth && y <= origin.Y + target.ActualHeight;
        }
        catch { return false; }
    }

    private static Color Accent =>
        ThemeBrushes.Get("AccentFillColorDefaultBrush") is SolidColorBrush brush ? brush.Color : Color.FromArgb(255, 0, 120, 212);

    private void Sync()
    {
        foreach (var item in _items)
        {
            var target = item.Target;
            bool shown = target.Visibility == Visibility.Visible && target.ActualWidth >= 1 && target.ActualHeight >= 1;
            foreach (var (sprite, _) in item.Parts) sprite.IsVisible = shown;
            if (item.Bar is not null) item.Bar.IsVisible = shown;
            if (!shown) continue;
            var origin = target.TransformToVisual(_layer).TransformPoint(new Windows.Foundation.Point(0, 0));
            var size = new Vector2((float)target.ActualWidth, (float)target.ActualHeight);
            var offset = new Vector3((float)origin.X, (float)origin.Y, 0);
            if (size == item.Size && offset == item.Offset) continue;
            item.Size = size; item.Offset = offset;
            float radius = Math.Min(CornerOf(target), Math.Min(size.X, size.Y) / 2f);
            item.Geometry.Size = size;
            item.Geometry.CornerRadius = new Vector2(radius, radius);
            item.Mask.Size = size;
            item.Surface.SourceSize = size;
            foreach (var (sprite, _) in item.Parts)
            {
                sprite.Size = size;
                sprite.Offset = offset;
                if (IsCard(item.Kind)) sprite.Clip = OutsideClip(size, radius);
            }
            if (item.Bar is not null)
                item.Bar.Offset = new Vector3(offset.X + size.X / 2f - 9f, offset.Y + size.Y + 3f, 0);
        }
    }

    /// <summary>
    /// Recorte "só por fora": uma área 40 px maior que o cartão menos o próprio cartão com os cantos arredondados.
    /// A sombra nunca escurece o interior translúcido e acompanha a curva dos cantos.
    /// </summary>
    private CompositionGeometricClip OutsideClip(Vector2 size, float radius)
    {
        const float margin = 40f;
        var device = Microsoft.Graphics.Canvas.CanvasDevice.GetSharedDevice();
        using var outer = Microsoft.Graphics.Canvas.Geometry.CanvasGeometry.CreateRectangle(device,
            -margin, -margin, size.X + margin * 2, size.Y + margin * 2);
        using var inner = Microsoft.Graphics.Canvas.Geometry.CanvasGeometry.CreateRoundedRectangle(device,
            0, 0, size.X, size.Y, radius, radius);
        var ring = outer.CombineWith(inner, Matrix3x2.Identity, Microsoft.Graphics.Canvas.Geometry.CanvasGeometryCombine.Exclude);
        var path = _compositor.CreatePathGeometry(new CompositionPath(ring));
        return _compositor.CreateGeometricClip(path);
    }

    private static float CornerOf(FrameworkElement element) => element switch
    {
        Control c => (float)c.CornerRadius.TopLeft,
        Border b => (float)b.CornerRadius.TopLeft,
        _ => 8f,
    };

    private void Apply(Item item, bool animate)
    {
        bool dark = App.IsDarkTheme;
        bool highContrast = ChatFluentSkin.IsHighContrast;
        bool enabled = item.Target is not Control c || c.IsEnabled;
        bool on = item.Target is ToggleButton { IsChecked: true };
        var accent = Accent;

        // Repouso: botões sem sombra nem brilho; cartões com sombra de profundidade só por fora.
        Color color = Color.FromArgb(255, 0, 0, 0);
        float opacity = 0f, blur = 4f;
        var offset = Vector3.Zero;
        if (item.Kind == Kind.Card) { opacity = dark ? 0.45f : 0.16f; blur = 16f; offset = new Vector3(0, 4, 0); }
        else if (item.Kind == Kind.Capsule) { opacity = dark ? 0.30f : 0.10f; blur = 8f; offset = new Vector3(0, 2, 0); }
        else if (!highContrast && enabled && item.Hover)
        {
            color = accent; opacity = dark ? 0.42f : 0.3f; blur = 12f; offset = Vector3.Zero;
        }

        if (item.BarBrush is not null && item.Bar is not null)
        {
            item.BarBrush.Color = accent;
            float barOpacity = on && enabled && !highContrast && item.Hover ? 1f : 0f; // traço só no hover
            if (animate && ChatFluentSkin.UseAnimations)
            {
                var a = _compositor.CreateScalarKeyFrameAnimation();
                a.InsertKeyFrame(1f, barOpacity); a.Duration = TimeSpan.FromMilliseconds(180);
                item.Bar.StartAnimation("Opacity", a);
            }
            else item.Bar.Opacity = barOpacity;
        }

        foreach (var (_, shadow) in item.Parts)
        {
            if (!animate || !ChatFluentSkin.UseAnimations)
            {
                shadow.StopAnimation("Color"); shadow.StopAnimation("Opacity"); shadow.StopAnimation("BlurRadius"); shadow.StopAnimation("Offset");
                shadow.Color = color; shadow.Opacity = opacity; shadow.BlurRadius = blur; shadow.Offset = offset;
                continue;
            }
            var duration = TimeSpan.FromMilliseconds(item.Hover ? 160 : 220);
            var colorAnim = _compositor.CreateColorKeyFrameAnimation();
            colorAnim.InsertKeyFrame(1f, color); colorAnim.Duration = duration;
            var opacityAnim = _compositor.CreateScalarKeyFrameAnimation();
            opacityAnim.InsertKeyFrame(1f, opacity); opacityAnim.Duration = duration;
            var blurAnim = _compositor.CreateScalarKeyFrameAnimation();
            blurAnim.InsertKeyFrame(1f, blur); blurAnim.Duration = duration;
            var offsetAnim = _compositor.CreateVector3KeyFrameAnimation();
            offsetAnim.InsertKeyFrame(1f, offset); offsetAnim.Duration = duration;
            shadow.StartAnimation("Color", colorAnim);
            shadow.StartAnimation("Opacity", opacityAnim);
            shadow.StartAnimation("BlurRadius", blurAnim);
            shadow.StartAnimation("Offset", offsetAnim);
        }
    }
}
