using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Graphics;
using static WinStatusBar.Interop.Win32;

namespace WinStatusBar.Views.Panels;

// A real client window is required for desktop drag/drop. Unlike a light-dismiss
// flyout it stays available when the user selects a source file in Explorer.
internal sealed class FolderPopupWindow
{
    private readonly Window _window;
    private readonly FrameworkElement _content;
    private readonly Action _opening, _hidden;
    private bool _disposed;
    public bool IsOpen { get; private set; }
    public bool IsDragging { get; set; }

    public FolderPopupWindow(string title, FrameworkElement content, Action opening, Action hidden)
    {
        _content = content; _opening = opening; _hidden = hidden;
        _window = new Window { Title = title + " — WinStatusBar", Content = new Border { Padding = new Thickness(16), Child = content, Background = PanelKit.Res("SolidBackgroundFillColorBaseBrush") } };
        var presenter = OverlappedPresenter.Create();
        presenter.SetBorderAndTitleBar(true, false);
        presenter.IsResizable = false; presenter.IsMaximizable = false; presenter.IsMinimizable = false;
        presenter.IsAlwaysOnTop = true;
        _window.AppWindow.SetPresenter(presenter);
        _window.AppWindow.Closing += (_, e) => { if (!_disposed) { e.Cancel = true; Hide(); } };
        _window.Activated += (_, e) =>
        {
            if (e.WindowActivationState == WindowActivationState.Deactivated && !IsDragging) Hide();
        };
    }

    public void ShowAt(FrameworkElement? anchor, IntPtr owner = default)
    {
        if (_disposed) return;
        var display = DisplayArea.GetFromWindowId(_window.AppWindow.Id, DisplayAreaFallback.Primary);
        if (owner != IntPtr.Zero) display = DisplayArea.GetFromWindowId(Microsoft.UI.Win32Interop.GetWindowIdFromWindow(owner), DisplayAreaFallback.Nearest);
        double scale = anchor?.XamlRoot?.RasterizationScale ?? 1;
        int width = (int)Math.Round(512 * scale), height = (int)Math.Round(640 * scale);
        var work = display.WorkArea;
        height = Math.Min(height, work.Height);
        int x = work.X + work.Width - width, y = work.Y;
        if (anchor is not null && owner != IntPtr.Zero && GetWindowRect(owner, out var rect))
        {
            var point = anchor.TransformToVisual(null).TransformPoint(new Windows.Foundation.Point());
            x = rect.Left + (int)((point.X + anchor.ActualWidth) * scale) - width;
            y = rect.Bottom;
        }
        x = Math.Clamp(x, work.X, Math.Max(work.X, work.X + work.Width - width));
        y = Math.Clamp(y, work.Y, Math.Max(work.Y, work.Y + work.Height - height));
        _content.RequestedTheme = App.IsDarkTheme ? ElementTheme.Dark : ElementTheme.Light;
        // Resize the client, not the outer window: DWM borders also consume pixels at each DPI.
        _window.AppWindow.ResizeClient(new SizeInt32(width, height));
        var outer = _window.AppWindow.Size;
        x = Math.Clamp(x, work.X, Math.Max(work.X, work.X + work.Width - outer.Width));
        y = Math.Clamp(y, work.Y, Math.Max(work.Y, work.Y + work.Height - outer.Height));
        _window.AppWindow.Move(new PointInt32(x, y));
        IsOpen = true; _window.Activate(); _opening();
    }

    public void Hide() { if (!IsOpen) return; IsOpen = false; _window.AppWindow.Hide(); _hidden(); }
    public void Dispose() { if (_disposed) return; Hide(); _disposed = true; _window.Close(); }
}
