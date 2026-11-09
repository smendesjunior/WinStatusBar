using System.Diagnostics;
using System.Runtime.InteropServices;
using Path = System.IO.Path;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;
using WinStatusBar.Services;

namespace WinStatusBar.Views;

/// <summary>Visible caret over the native editor, including when DestInvert fails on an acrylic surface.</summary>
internal sealed class ComposerCaret : Grid
{
    private readonly TextBox _input;
    private readonly Func<bool> _active;
    private readonly Rectangle _caret = new() { IsHitTestVisible = false, Visibility = Visibility.Collapsed };
    private readonly DispatcherQueueTimer _timer;
    private readonly Stopwatch _clock = new();
    private readonly int _interval;
    private readonly RectangleGeometry _clip = new();
    private Shape? _native;
    private Brush? _nativeFill;
    private ElementCompositeMode _nativeCompositeMode;
    private int _reportedEdges;
    private bool? _lastVisible;

    [DllImport("user32.dll")] private static extern uint GetCaretBlinkTime();

    public ComposerCaret(TextBox input, Func<bool> active)
    {
        _input = input; _active = active; Clip = _clip;
        Children.Add(input);
        var overlay = new Canvas { IsHitTestVisible = false };
        overlay.Children.Add(_caret); Children.Add(overlay);
        uint interval = GetCaretBlinkTime();
        _interval = interval is > 0 and < 10000 ? (int)interval : 530;
        _timer = DispatcherQueue.GetForCurrentThread().CreateTimer();
        _timer.Interval = TimeSpan.FromMilliseconds(_interval);
        _timer.IsRepeating = true;
        _timer.Tick += (_, _) => Render();
        input.GotFocus += (_, _) => Start();
        input.LostFocus += (_, _) => Pause();
        input.TextChanged += (_, _) => Start();
        input.SelectionChanged += (_, _) => Start();
        input.PointerPressed += (_, _) => Start();
        input.LayoutUpdated += (_, _) => { if (_clock.IsRunning) UpdatePosition(); };
        ActualThemeChanged += (_, _) => UpdatePosition();
        Unloaded += (_, _) => Pause();
    }

    public void Start()
    {
        if (!_active() || _input.FocusState == FocusState.Unfocused) return;
        _clock.Restart(); _reportedEdges = 0; _lastVisible = null;
        _timer.Start();
        Render();
    }

    public void Pause()
    {
        _timer.Stop(); _clock.Reset(); _caret.Visibility = Visibility.Collapsed;
        if (_native is not null) { _native.Fill = _nativeFill; _native.CompositeMode = _nativeCompositeMode; }
        _native = null;
    }

    private void Render()
    {
        if (!_active() || _input.FocusState == FocusState.Unfocused) { Pause(); return; }
        bool positioned = UpdatePosition();
        bool visible = positioned && _input.SelectionLength == 0 && (long)(_clock.Elapsed.TotalMilliseconds / _interval) % 2 == 0;
        _caret.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        if (_lastVisible != visible && _reportedEdges < 3)
        {
            App.Log($"Chat: cursor desenhado visível={visible}, posição={_input.SelectionStart}, intervalo={_interval} ms");
            _reportedEdges++;
        }
        _lastVisible = visible;
    }

    private bool UpdatePosition()
    {
        if (!_input.IsLoaded || _input.ActualWidth <= 0) return false;
        SuppressNativeCaret(_input);
        // Use the editor's actual insertion caret, not a character-edge rectangle.
        // Its transform includes padding, wrapping, scrolling and the active insertion line.
        if (_native is null || _native.Height <= 0)
        {
            _caret.Visibility = Visibility.Collapsed;
            return false;
        }
        double scale = XamlRoot?.RasterizationScale ?? 1;
        Rect rect = _native.TransformToVisual(this).TransformBounds(
            new Rect(0, 0, _native.Width, _native.Height));
        double x = Math.Round(rect.X * scale) / scale;
        double y = Math.Round(rect.Y * scale) / scale;
        double height = Math.Round(rect.Height * scale) / scale;
        double width = 2 / scale;
        if (Math.Abs(Canvas.GetLeft(_caret) - x) > .01 || double.IsNaN(Canvas.GetLeft(_caret))) Canvas.SetLeft(_caret, x);
        if (Math.Abs(Canvas.GetTop(_caret) - y) > .01 || double.IsNaN(Canvas.GetTop(_caret))) Canvas.SetTop(_caret, y);
        if (Math.Abs(_caret.Width - width) > .01 || double.IsNaN(_caret.Width)) _caret.Width = width;
        if (Math.Abs(_caret.Height - height) > .01 || double.IsNaN(_caret.Height)) _caret.Height = height;
        _caret.Fill = ThemeBrushes.ForTheme("TextFillColorPrimaryBrush", ActualTheme);
        // Keep the caret inside the text viewport when the native editor scrolls.
        var clip = new Rect(0, 0, ActualWidth, ActualHeight);
        if (_clip.Rect != clip) _clip.Rect = clip;
        return true;
    }

#if DEBUG
    private bool _verified;
    public async Task VerifyBlinkAsync()
    {
        if (_verified) return;
        _verified = true;
        try
        {
            var alphas = new List<int>();
            for (int frame = 0; frame < 3; frame++)
            {
                bool expected = frame != 1;
                var limit = Stopwatch.StartNew();
                while ((_caret.Visibility == Visibility.Visible) != expected && limit.ElapsedMilliseconds < 2000) await Task.Delay(10);
                if ((_caret.Visibility == Visibility.Visible) != expected) throw new InvalidOperationException("O cursor não alternou no prazo esperado.");
                if (!_active()) throw new InvalidOperationException("O campo perdeu a ativação durante a conferência.");
                var bitmap = new Microsoft.UI.Xaml.Media.Imaging.RenderTargetBitmap();
                await bitmap.RenderAsync(this);
                var buffer = await bitmap.GetPixelsAsync();
                byte[] bytes = System.Runtime.InteropServices.WindowsRuntime.WindowsRuntimeBufferExtensions.ToArray(buffer);
                int x = (int)Math.Round((Canvas.GetLeft(_caret) + _caret.Width / 2) * bitmap.PixelWidth / ActualWidth);
                int y = (int)Math.Round((Canvas.GetTop(_caret) + _caret.Height / 2) * bitmap.PixelHeight / ActualHeight);
                int pixel = (y * bitmap.PixelWidth + x) * 4;
                alphas.Add(bytes[pixel] + bytes[pixel + 1] + bytes[pixel + 2]);
                string path = Path.Combine(Path.GetTempPath(), $"WinStatusBar-caret-frame-{frame}.png");
                using var file = File.Open(path, FileMode.Create, FileAccess.Write);
                using var stream = System.IO.WindowsRuntimeStreamExtensions.AsRandomAccessStream(file);
                var encoder = await Windows.Graphics.Imaging.BitmapEncoder.CreateAsync(Windows.Graphics.Imaging.BitmapEncoder.PngEncoderId, stream);
                encoder.SetPixelData(Windows.Graphics.Imaging.BitmapPixelFormat.Bgra8, Windows.Graphics.Imaging.BitmapAlphaMode.Premultiplied,
                    (uint)bitmap.PixelWidth, (uint)bitmap.PixelHeight, 96, 96, bytes);
                await encoder.FlushAsync();
                await Task.Delay(120);
            }
            bool success = alphas[0] > alphas[1] && alphas[2] > alphas[1];
            App.Log($"Chat: conferência visual do cursor={success}, intensidade dos quadros={string.Join(",", alphas)}, nativoSuprimido={_native is not null}");
        }
        catch (Exception ex) { App.Log("Chat: conferência visual do cursor: " + ex.Message); }
    }
#endif

    private void SuppressNativeCaret(DependencyObject element)
    {
        // WinUI creates a thin, non-interactive Shape for the native caret (TextBoxView.cpp).
        // Making only its fill transparent prevents two cursors; native editing/selection is unchanged.
        if (_native is not null)
        {
            _native.CompositeMode = ElementCompositeMode.SourceOver;
            if (_native.Fill is not SolidColorBrush { Color.A: 0 }) _native.Fill = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
            return;
        }
        int count = VisualTreeHelper.GetChildrenCount(element);
        for (int i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(element, i);
            if (child is Shape shape && !shape.IsHitTestVisible && shape.Width is > 0 and <= 3 && shape.Height >= 8)
            {
                _native = shape; _nativeFill = shape.Fill; _nativeCompositeMode = shape.CompositeMode;
                shape.CompositeMode = ElementCompositeMode.SourceOver;
                shape.Fill = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
                return;
            }
            SuppressNativeCaret(child);
            if (_native is not null) return;
        }
    }
}
