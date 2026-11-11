using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.Storage.Streams;

namespace WinStatusBar.Views;

/// <summary>
/// Imagens no chat da barra (todas as IAs): clique amplia numa janela de visualização com zoom; botão direito copia,
/// salva, abre no visualizador do Windows ou mostra o arquivo na pasta.
/// </summary>
internal static class ChatImageActions
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(60) };

    public static void Attach(FrameworkElement target, Uri uri, string alt, bool openOnTap = true)
    {
        if (openOnTap)
        {
            target.Tapped += (_, e) => { e.Handled = true; ShowViewer(uri, alt); };
            target.PointerEntered += (s, _) => { if (s is UIElement u) SetHand(u, true); };
            target.PointerExited += (s, _) => { if (s is UIElement u) SetHand(u, false); };
            ToolTipService.SetToolTip(target, (alt.Length > 0 ? alt + "\n" : "") + "Clique para ampliar · botão direito para copiar ou salvar");
        }

        var menu = new MenuFlyout();
        Add(menu, "", "Copiar imagem", () => CopyAsync(uri));
        Add(menu, "", "Salvar como…", () => SaveAsAsync(uri));
        menu.Items.Add(new MenuFlyoutSeparator());
        Add(menu, "", "Ampliar", () => { ShowViewer(uri, alt); return Task.CompletedTask; });
        Add(menu, "", "Abrir no visualizador do Windows", () => OpenExternalAsync(uri));
        if (uri.IsFile) Add(menu, "", "Mostrar na pasta", () => { Process.Start(new ProcessStartInfo("explorer.exe", "/select,\"" + uri.LocalPath + "\"") { UseShellExecute = true })?.Dispose(); return Task.CompletedTask; });
        target.ContextFlyout = menu;
    }

    /// <summary>Item do menu; erros das ações assíncronas (download, área de transferência, salvar) também são registrados.</summary>
    private static void Add(MenuFlyout menu, string glyph, string text, Func<Task> click)
    {
        var item = new MenuFlyoutItem { Text = text, Icon = new FontIcon { Glyph = glyph } };
        item.Click += async (_, _) => { try { await click(); } catch (Exception ex) { App.Log($"Imagem do chat ({text}): {ex.Message}"); } };
        menu.Items.Add(item);
    }

    private static void SetHand(UIElement element, bool hand)
    {
        try
        {
            var prop = typeof(UIElement).GetProperty("ProtectedCursor", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            prop?.SetValue(element, hand ? InputSystemCursor.Create(InputSystemCursorShape.Hand) : null);
        }
        catch { }
    }

    /// <summary>Arquivo local da imagem (baixa para a pasta temporária quando o endereço é remoto).</summary>
    private static async Task<string> LocalFileAsync(Uri uri)
    {
        if (uri.IsFile) return uri.LocalPath;
        string ext = Path.GetExtension(uri.AbsolutePath) is { Length: > 1 and < 6 } e ? e : ".png";
        DeleteOldDownloads();
        string path = Path.Combine(Path.GetTempPath(), "wsb-imagem-" + Guid.NewGuid().ToString("N")[..8] + ext);
        await File.WriteAllBytesAsync(path, await Http.GetByteArrayAsync(uri));
        return path;
    }

    /// <summary>
    /// Cada Copiar, Salvar ou Abrir de uma imagem remota baixa uma cópia para a pasta temporária e nada a apagava.
    /// Antes de baixar outra, somem as cópias com mais de um dia (as recentes podem estar abertas ou na área de transferência).
    /// </summary>
    private static void DeleteOldDownloads()
    {
        try
        {
            var limit = DateTime.UtcNow.AddDays(-1);
            foreach (var old in Directory.EnumerateFiles(Path.GetTempPath(), "wsb-imagem-*"))
                try { if (File.GetLastWriteTimeUtc(old) < limit) File.Delete(old); } catch { }
        }
        catch { }
    }

    private static async Task CopyAsync(Uri uri)
    {
        var file = await StorageFile.GetFileFromPathAsync(await LocalFileAsync(uri));
        var data = new DataPackage();
        data.SetBitmap(RandomAccessStreamReference.CreateFromFile(file));
        data.SetStorageItems([file]);
        Clipboard.SetContent(data);
        Clipboard.Flush();
    }

    private static async Task SaveAsAsync(Uri uri)
    {
        string source = await LocalFileAsync(uri);
        string ext = Path.GetExtension(source) is { Length: > 1 } x ? x : ".png";
        var picker = new FileSavePicker { SuggestedFileName = Path.GetFileNameWithoutExtension(source), SuggestedStartLocation = PickerLocationId.PicturesLibrary };
        picker.FileTypeChoices.Add("Imagem", [ext]);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, GetForegroundWindow());
        var target = await picker.PickSaveFileAsync();
        if (target is null) return;
        await FileIO.WriteBytesAsync(target, await File.ReadAllBytesAsync(source));
    }

    private static async Task OpenExternalAsync(Uri uri)
        => Process.Start(new ProcessStartInfo(await LocalFileAsync(uri)) { UseShellExecute = true })?.Dispose();

    /// <summary>Janela de visualização: imagem ajustada à tela, zoom com a roda do mouse/pinça, Esc ou clique fora fecha.</summary>
    private static void ShowViewer(Uri uri, string alt)
    {
        var image = new Image { Source = new BitmapImage(uri), Stretch = Stretch.Uniform };
        var scroller = new ScrollViewer
        {
            Content = image, ZoomMode = ZoomMode.Enabled, MinZoomFactor = 0.5f, MaxZoomFactor = 8,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollMode = ScrollMode.Auto, VerticalScrollMode = ScrollMode.Auto,
            Background = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 20, 20, 20)),
        };
        var window = new Window { Title = alt.Length > 0 ? alt : "Imagem", Content = scroller };
        image.Loaded += (_, _) => { image.MaxWidth = scroller.ActualWidth; image.MaxHeight = scroller.ActualHeight; };
        scroller.SizeChanged += (_, _) => { image.MaxWidth = scroller.ActualWidth; image.MaxHeight = scroller.ActualHeight; };
        scroller.KeyDown += (_, e) => { if (e.Key == Windows.System.VirtualKey.Escape) window.Close(); };
        scroller.DoubleTapped += (_, _) => scroller.ChangeView(null, null, scroller.ZoomFactor > 1.01f ? 1f : 2f);
        Attach(image, uri, alt, openOnTap: false); // o mesmo menu (copiar/salvar) dentro da visualização
        try
        {
            var area = Microsoft.UI.Windowing.DisplayArea.Primary.WorkArea;
            int w = (int)(area.Width * 0.8), h = (int)(area.Height * 0.85);
            window.AppWindow.MoveAndResize(new Windows.Graphics.RectInt32(area.X + (area.Width - w) / 2, area.Y + (area.Height - h) / 2, w, h));
            window.AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "WinStatusBar.ico"));
        }
        catch { }
        window.Activate();
        scroller.Focus(FocusState.Programmatic);
    }

    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
}
