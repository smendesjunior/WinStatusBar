using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using WinStatusBar.Core.Settings;
using WinStatusBar.Services;

namespace WinStatusBar.Views;

public sealed partial class SettingsWindow
{
    private StackPanel? _shortcutRows;

    private FrameworkElement BuildShortcutSettings()
    {
        _shortcutRows = new StackPanel();
        var definitions = new Dictionary<string, (string Title, string? Help, Func<AppSettings, bool> Get, Action<AppSettings, bool> Set)>
        {
            ["clipboard"] = ("Área de transferência", "Abre o histórico. Win+V continua funcionando mesmo sem o botão.", s => s.ShortcutClipboard, (s,v) => s.ShortcutClipboard = v),
            ["taskmanager"] = ("Gerenciador de Tarefas", null, s => s.ShortcutTaskManager, (s,v) => s.ShortcutTaskManager = v),
            ["settings"] = ("Configurações do Windows", null, s => s.ShortcutSettings, (s,v) => s.ShortcutSettings = v),
            ["store"] = ("Microsoft Store", null, s => s.ShortcutStore, (s,v) => s.ShortcutStore = v),
            ["explorer"] = ("Explorador de Arquivos", "Mostra sua pasta pessoal num painel; o botão \"Abrir pasta\" leva ao Explorador.", s => s.ShortcutExplorer, (s,v) => s.ShortcutExplorer = v),
            ["documents"] = ("Documentos", "Abre Documentos do seu usuário, inclusive no OneDrive.", s => s.ShortcutDocuments, (s,v) => s.ShortcutDocuments = v),
            ["downloads"] = ("Downloads", "Abre a pasta dos seus downloads.", s => s.ShortcutDownloads, (s,v) => s.ShortcutDownloads = v),
            ["personal"] = ("Pasta pessoal", "Abre a pasta principal do seu usuário.", s => s.ShortcutPersonal, (s,v) => s.ShortcutPersonal = v),
        };
        foreach (string key in S.ShortcutOrder)
        {
            if (!definitions.TryGetValue(key, out var item)) continue;
            var row = new Grid { Tag = key, AllowDrop = true, Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(40) });
            row.ColumnDefinitions.Add(new ColumnDefinition());
            var handle = new ShortcutReorderHandle { Content = new FontIcon { Glyph = "\uE700", FontSize = 16 }, CanDrag = true,
                Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent), BorderThickness = new Thickness(0),
                Padding = new Thickness(6), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            ToolTipService.SetToolTip(handle, "Arraste para reorganizar · Alt+↑ ou Alt+↓ para mover pelo teclado");
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(handle, "Mover " + item.Title);
            var content = Toggle(item.Title, item.Help, item.Get, item.Set);
            Grid.SetColumn(content, 1); row.Children.Add(handle); row.Children.Add(content);
            var marker = new Border { Height = 2, Background = ThemeBrushes.Get("AccentFillColorDefaultBrush"), Visibility = Visibility.Collapsed,
                VerticalAlignment = VerticalAlignment.Top, IsHitTestVisible = false };
            Grid.SetColumnSpan(marker, 2); row.Children.Add(marker);
            // Button consumes pointer events for Click; observe handled events and capture the gesture.
            Windows.Foundation.Point? pressed = null;
            bool moving = false;
            handle.CanDrag = false;
            handle.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler((_, e) =>
            {
                var point = e.GetCurrentPoint(_shortcutRows);
                if (!point.Properties.IsLeftButtonPressed) return;
                pressed = point.Position; moving = false;
                handle.CapturePointer(e.Pointer);
            }), true);
            handle.AddHandler(UIElement.PointerMovedEvent, new PointerEventHandler((_, e) =>
            {
                if (pressed is not { } start) return;
                var point = e.GetCurrentPoint(_shortcutRows);
                if (!point.Properties.IsLeftButtonPressed) { pressed = null; ClearShortcutDrag(); return; }
                if (!moving && Math.Abs(point.Position.Y - start.Y) < 5 && Math.Abs(point.Position.X - start.X) < 5) return;
                moving = true;
                MarkShortcutTarget(point.Position.Y); e.Handled = true;
            }), true);
            handle.Releasing += (_, e) =>
            {
                if (moving && pressed is not null)
                {
                    MoveShortcut(row, ShortcutDestination(e.GetCurrentPoint(_shortcutRows).Position.Y, row));
                    e.Handled = true;
                }
                pressed = null; moving = false;
                handle.ReleasePointerCapture(e.Pointer); ClearShortcutDrag();
            };
            handle.PointerCanceled += (_, _) => { pressed = null; moving = false; ClearShortcutDrag(); };
            handle.PointerCaptureLost += (_, _) => { pressed = null; moving = false; ClearShortcutDrag(); };
            handle.KeyDown += (_, e) =>
            {
                if (e.Key == Windows.System.VirtualKey.Escape)
                {
                    pressed = null; moving = false; handle.ReleasePointerCaptures(); ClearShortcutDrag(); e.Handled = true; return;
                }
                bool alt = (Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Menu)
                    & Windows.UI.Core.CoreVirtualKeyStates.Down) != 0;
                if (!alt || e.Key is not (Windows.System.VirtualKey.Up or Windows.System.VirtualKey.Down)) return;
                int from = _shortcutRows.Children.IndexOf(row);
                MoveShortcut(row, Math.Clamp(from + (e.Key == Windows.System.VirtualKey.Up ? -1 : 1), 0, _shortcutRows.Children.Count - 1));
                e.Handled = true;
            };
            _shortcutRows.Children.Add(row);
        }
        return new Border { Style = (Style)Root.Resources["SettingsGroup"], Child = _shortcutRows };
    }

    private int ShortcutDestination(double y, Grid source)
    {
        if (_shortcutRows is null) return 0;
        int destination = _shortcutRows.Children.Count;
        for (int i = 0; i < _shortcutRows.Children.Count; i++)
        {
            var target = (Grid)_shortcutRows.Children[i];
            double top = target.TransformToVisual(_shortcutRows).TransformPoint(new Windows.Foundation.Point()).Y;
            if (y < top + target.ActualHeight / 2) { destination = i; break; }
        }
        int from = _shortcutRows.Children.IndexOf(source);
        if (from < destination) destination--;
        return Math.Clamp(destination, 0, _shortcutRows.Children.Count - 1);
    }

    private void MarkShortcutTarget(double y)
    {
        if (_shortcutRows is null) return;
        ClearShortcutMarkers();
        Grid target = (Grid)_shortcutRows.Children[_shortcutRows.Children.Count - 1];
        bool below = true;
        foreach (Grid row in _shortcutRows.Children)
        {
            double top = row.TransformToVisual(_shortcutRows).TransformPoint(new Windows.Foundation.Point()).Y;
            if (y < top + row.ActualHeight / 2) { target = row; below = false; break; }
        }
        var marker = target.Children.OfType<Border>().First();
        marker.VerticalAlignment = below ? VerticalAlignment.Bottom : VerticalAlignment.Top;
        marker.Visibility = Visibility.Visible;
    }

    private void MoveShortcut(Grid row, int index)
    {
        if (_shortcutRows is null || _shortcutRows.Children.IndexOf(row) == index) return;
        _shortcutRows.Children.Remove(row); _shortcutRows.Children.Insert(index, row);
        Commit(s => s.ShortcutOrder = _shortcutRows.Children.OfType<Grid>().Select(r => (string)r.Tag).ToList());
    }
    private void ClearShortcutMarkers()
    {
        if (_shortcutRows is null) return;
        foreach (var row in _shortcutRows.Children.OfType<Grid>())
            foreach (var marker in row.Children.OfType<Border>()) marker.Visibility = Visibility.Collapsed;
    }
    private void ClearShortcutDrag() => ClearShortcutMarkers();
    private void RefreshShortcutOrder()
    {
        if (_shortcutRows is null) return;
        var rows = _shortcutRows.Children.OfType<Grid>().ToDictionary(r => (string)r.Tag);
        _shortcutRows.Children.Clear();
        foreach (string key in S.ShortcutOrder) if (rows.TryGetValue(key, out var row)) _shortcutRows.Children.Add(row);
    }
}

// Commit the move before Button releases capture: PointerCaptureLost clears the gesture state.
internal sealed class ShortcutReorderHandle : Button
{
    public event PointerEventHandler? Releasing;
    protected override void OnPointerReleased(PointerRoutedEventArgs e)
    {
        Releasing?.Invoke(this, e);
        base.OnPointerReleased(e);
    }
}
