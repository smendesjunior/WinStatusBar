#nullable enable
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace WinStatusBar.Clip;

/// <summary>
/// Folha de emojis pré-desenhados. A fonte Segoe UI Emoji atual (emojis 3D, COLRv1) é cara de desenhar e o WinUI
/// desenha texto na thread da interface: cada tela nova de emojis travava e ficava branca. Aqui todos os emojis
/// (com os tons de pele) são desenhados UMA vez, em segundo plano, com o Win2D, numa folha PNG guardada em cache no
/// disco (refeita só quando a fonte do Windows ou a escala da tela mudam). A grade mostra recortes dessa folha:
/// rolar passa a ser só mover imagens prontas, como no painel nativo do Windows.
/// </summary>
internal static class EmojiAtlas
{
    /// <summary>Célula da grade em DIP (o emoji tem 22 DIP de fonte, centralizado).</summary>
    public const double CellDip = 30, FontDip = 22;
    private const string Version = "1";
    private static readonly string Folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WinStatusBar", "cache", "emoji");

    private sealed record Index(int Cell, int Columns, double Scale, Dictionary<string, int> Slots);
    private static Index? _index;
    private static BitmapImage? _sheet;
    private static readonly Dictionary<string, ImageBrush> Brushes = new(StringComparer.Ordinal);
    private static bool _building;
    private static double _scale;

    /// <summary>A folha está pronta e carregada (antes disso a grade usa o texto, como antes).</summary>
    public static bool Ready => _sheet is not null && _index is not null;
    public static event Action? Changed;

    /// <summary>Recorte da folha para um emoji (pincel compartilhado entre os itens); null se ainda não houver folha.</summary>
    public static Brush? BrushFor(string glyph)
    {
        if (_sheet is null || _index is null || !_index.Slots.TryGetValue(glyph, out int slot)) return null;
        if (Brushes.TryGetValue(glyph, out var cached)) return cached;
        int col = slot % _index.Columns, row = slot / _index.Columns;
        // A folha está em pixels físicos: escala 1/escala para ficar nítida (1 pixel da folha = 1 pixel da tela). A célula
        // em DIP é o tamanho exato em pixels ÷ escala (ex.: 38 px a 125 % = 30,4 DIP), senão o recorte escorrega por coluna.
        double cellDip = _index.Cell / _index.Scale;
        var brush = new ImageBrush
        {
            ImageSource = _sheet, Stretch = Stretch.None, AlignmentX = AlignmentX.Left, AlignmentY = AlignmentY.Top,
            Transform = new CompositeTransform { ScaleX = 1 / _index.Scale, ScaleY = 1 / _index.Scale, TranslateX = -col * cellDip, TranslateY = -row * cellDip },
        };
        Brushes[glyph] = brush;
        return brush;
    }

    /// <summary>
    /// Garante a folha para a escala da tela: carrega do cache ou desenha em segundo plano (uma vez). Chamar na thread
    /// da interface; o desenho roda fora dela e o carregamento final volta para ela.
    /// </summary>
    public static void Ensure(IReadOnlyCollection<string> glyphs, double scale, DispatcherQueue ui)
    {
        scale = Math.Round(Math.Clamp(scale, 1, 4), 2);
        if (glyphs.Count == 0 || (_scale == scale && (_building || Ready))) return;
        _scale = scale; _building = true;
        string key = Key(glyphs, scale);
        string png = Path.Combine(Folder, $"emoji-{key}.png"), json = Path.Combine(Folder, $"emoji-{key}.json");
        _ = Task.Run(() =>
        {
            try
            {
                if (!File.Exists(png) || !File.Exists(json)) Render(glyphs.ToList(), scale, png, json);
                var index = JsonSerializer.Deserialize<Index>(File.ReadAllText(json))!;
                ui.TryEnqueue(DispatcherQueuePriority.Low, async () =>
                {
                    if (_scale != scale) return; // a escala mudou enquanto desenhava
                    try
                    {
                        var sheet = new BitmapImage();
                        using (var stream = File.OpenRead(png)) await sheet.SetSourceAsync(stream.AsRandomAccessStream());
                        _sheet = sheet; _index = index; Brushes.Clear();
                        Log.Write($"Emojis: folha pronta ({index.Slots.Count} emojis, {index.Cell} px por emoji).");
                        Changed?.Invoke();
                    }
                    catch (Exception ex) { Log.Write("Emojis: folha não abriu (segue com texto): " + ex.Message); }
                });
                Cleanup(key);
            }
            catch (Exception ex) { Log.Write("Emojis: folha não gerada (segue com texto): " + ex.Message); }
            finally { _building = false; }
        });
    }

    private static void Render(List<string> glyphs, double scale, string png, string json)
    {
        var watch = Stopwatch.StartNew();
        var device = CanvasDevice.GetSharedDevice();
        int cell = (int)Math.Ceiling(CellDip * scale);
        int max = Math.Min(device.MaximumBitmapSizeInPixels, 8192);
        int columns = Math.Max(1, max / cell);
        columns = Math.Min(columns, (int)Math.Ceiling(Math.Sqrt(glyphs.Count)) * 2); // folha mais larga que alta
        int rows = (glyphs.Count + columns - 1) / columns;
        using var target = new CanvasRenderTarget(device, columns * cell, rows * cell, 96);
        using var format = new CanvasTextFormat
        {
            FontFamily = "Segoe UI Emoji", FontSize = (float)(FontDip * scale), Options = CanvasDrawTextOptions.EnableColorFont,
            HorizontalAlignment = CanvasHorizontalAlignment.Center, VerticalAlignment = CanvasVerticalAlignment.Center, WordWrapping = CanvasWordWrapping.NoWrap,
        };
        var slots = new Dictionary<string, int>(StringComparer.Ordinal);
        using (var ds = target.CreateDrawingSession())
        {
            ds.Clear(Microsoft.UI.Colors.Transparent);
            for (int i = 0; i < glyphs.Count; i++)
            {
                int col = i % columns, row = i / columns;
                ds.DrawText(glyphs[i], new Windows.Foundation.Rect(col * cell, row * cell, cell, cell), Microsoft.UI.Colors.Black, format);
                slots[glyphs[i]] = i;
            }
        }
        Directory.CreateDirectory(Folder);
        string tmp = png + ".tmp";
        target.SaveAsync(tmp, CanvasBitmapFileFormat.Png).AsTask().GetAwaiter().GetResult();
        File.Move(tmp, png, overwrite: true);
        File.WriteAllText(json, JsonSerializer.Serialize(new Index(cell, columns, scale, slots)));
        Log.Write($"Emojis: {glyphs.Count} desenhados em {watch.Elapsed.TotalSeconds:N1} s ({columns * cell}×{rows * cell} px, escala {scale}).");
    }

    /// <summary>Muda quando a fonte do Windows, a lista de emojis ou a escala mudam.</summary>
    private static string Key(IReadOnlyCollection<string> glyphs, double scale)
    {
        string font = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Fonts), "seguiemj.ttf");
        var info = new FileInfo(font);
        string seed = $"{Version}|{(info.Exists ? info.Length + "|" + info.LastWriteTimeUtc.Ticks : "nofont")}|{scale}|{string.Join("\u0001", glyphs)}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(seed)))[..16].ToLowerInvariant();
    }

    /// <summary>Mantém só as folhas recentes (uma por escala usada).</summary>
    private static void Cleanup(string keep)
    {
        try
        {
            foreach (var old in new DirectoryInfo(Folder).GetFiles("emoji-*").OrderByDescending(f => f.LastWriteTimeUtc).Skip(8))
                if (!old.Name.Contains(keep, StringComparison.Ordinal)) old.Delete();
        }
        catch { }
    }
}
