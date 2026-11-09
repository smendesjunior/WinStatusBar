using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics.Imaging;
using Windows.Management.Deployment;
using Windows.Storage;
using static WinStatusBar.Interop.Win32;

namespace WinStatusBar.Services;

/// <summary>
/// Ícones reais do sistema e das ferramentas instaladas:
///  • Configurações, Microsoft Store e Gerenciador de Tarefas via IShellItemImageFactory (ícones do próprio Windows).
///  • Codex: símbolo oficial sem fundo (assets "unplated" do pacote instalado, variante clara/escura).
///  • Claude: glifo oficial (asset de bandeja do pacote instalado) na cor da marca.
/// Nada é recriado artificialmente; se a fonte não existir, a interface usa um glifo Fluent genérico.
/// </summary>
public static class IconService
{
    private static readonly Dictionary<string, ImageSource?> Cache = new();

    public static ImageSource? ShellIcon(string parsingName, int sizePx = 48)
    {
        string key = $"shell|{parsingName}|{sizePx}";
        if (Cache.TryGetValue(key, out var cached)) return cached;
        ImageSource? result = null;
        IntPtr hbm = IntPtr.Zero;
        try
        {
            SHCreateItemFromParsingName(parsingName, IntPtr.Zero, typeof(IShellItemImageFactory).GUID, out var factory);
            const int SIIGBF_BIGGERSIZEOK = 0x1, SIIGBF_ICONONLY = 0x4;
            if (factory.GetImage(new SIZE { cx = sizePx, cy = sizePx }, SIIGBF_ICONONLY | SIIGBF_BIGGERSIZEOK, out hbm) == 0 && hbm != IntPtr.Zero)
                result = FromHBitmap(hbm);
        }
        catch (Exception ex) { App.Log(global::WinStatusBar.Services.Localization.F("Ícone do Shell indisponível ({0}): {1}", new object?[] {parsingName,ex.Message})); }
        finally { if (hbm != IntPtr.Zero) DeleteObject(hbm); }
        Cache[key] = result;
        return result;
    }

    private static WriteableBitmap? FromHBitmap(IntPtr hbm)
    {
        if (GetObjectW(hbm, System.Runtime.InteropServices.Marshal.SizeOf<BITMAP>(), out var bmp) == 0) return null;
        int w = bmp.bmWidth, h = Math.Abs(bmp.bmHeight);
        var bmi = new BITMAPINFOHEADER
        {
            biSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<BITMAPINFOHEADER>(),
            biWidth = w, biHeight = -h, biPlanes = 1, biBitCount = 32, biCompression = 0,
        };
        var pixels = new byte[w * h * 4];
        IntPtr hdc = GetDC(IntPtr.Zero);
        try { if (GetDIBits(hdc, hbm, 0, (uint)h, pixels, ref bmi, 0) == 0) return null; }
        finally { ReleaseDC(IntPtr.Zero, hdc); }
        // Bitmaps de ícone do Shell já vêm com alfa pré-multiplicado (formato esperado pelo WriteableBitmap).
        var wb = new WriteableBitmap(w, h);
        using (var s = wb.PixelBuffer.AsStream()) s.Write(pixels, 0, pixels.Length);
        wb.Invalidate();
        return wb;
    }

    public static string? PackagePath(string familyName)
    {
        try
        {
            var pkg = new PackageManager().FindPackagesForUser(string.Empty, familyName).FirstOrDefault();
            return pkg?.InstalledLocation?.Path;
        }
        catch { return null; }
    }

    public static ImageSource? CodexIcon(bool dark)
    {
        string key = "codex|" + dark;
        if (Cache.TryGetValue(key, out var c)) return c;
        ImageSource? result = null;
        var root = PackagePath("OpenAI.Codex_2p2nqsd0c76g0");
        if (root is not null)
        {
            // "unplated" = símbolo branco (tema escuro); "lightunplated" = símbolo preto (tema claro). Fundo transparente.
            string file = Path.Combine(root, "assets", dark ? "Square44x44Logo.targetsize-256_altform-unplated.png" : "Square44x44Logo.targetsize-256_altform-lightunplated.png");
            if (File.Exists(file)) result = new BitmapImage(new Uri(file)) { DecodePixelWidth = 64, DecodePixelType = DecodePixelType.Physical };
        }
        Cache[key] = result;
        return result;
    }

    public static async Task<ImageSource?> ClaudeIconAsync()
    {
        const string key = "claude";
        if (Cache.TryGetValue(key, out var c)) return c;
        ImageSource? result = null;
        try
        {
            var root = PackagePath("AnthropicPBC.Claude_fnn82j28hfe8t");
            string? file = root is null ? null : new[] { "TrayIconTemplate@3x.png", "TrayIconTemplate@2x.png", "TrayIconTemplate.png" }
                .Select(n => Path.Combine(root, "app", "resources", n)).FirstOrDefault(File.Exists);
            if (file is not null) result = await TintAsync(file, 0xD9, 0x77, 0x57); // laranja da marca Claude
        }
        catch (Exception ex) { App.Log(global::WinStatusBar.Services.Localization.F("Ícone do Claude indisponível: {0}", new object?[] {ex.Message})); }
        Cache[key] = result;
        return result;
    }

    /// <summary>
    /// Logotipo de uma cor só, como os ícones Fluent da barra: branco no tema escuro, quase preto no claro.
    /// "claude" usa o glifo oficial do aplicativo instalado; os demais vêm de Assets\AgentLogos\{nome}.svg.
    /// </summary>
    public static async Task<ImageSource?> MonoLogoAsync(string name, bool dark)
    {
        string key = $"mono|{name}|{dark}";
        if (Cache.TryGetValue(key, out var c)) return c;
        ImageSource? result = null;
        byte v = dark ? (byte)0xFF : (byte)0x1B;
        try
        {
            if (name == "claude")
            {
                var root = PackagePath("AnthropicPBC.Claude_fnn82j28hfe8t");
                string? file = root is null ? null : new[] { "TrayIconTemplate@3x.png", "TrayIconTemplate@2x.png", "TrayIconTemplate.png" }
                    .Select(n => Path.Combine(root, "app", "resources", n)).FirstOrDefault(File.Exists);
                if (file is not null) result = await TintAsync(file, v, v, v);
            }
            else
            {
                string path = Path.Combine(AppContext.BaseDirectory, "Assets", "AgentLogos", name + ".svg");
                if (File.Exists(path))
                {
                    string svg = WinStatusBar.Core.Metrics.SvgRecolor.Monochrome(await File.ReadAllTextAsync(path), $"#{v:X2}{v:X2}{v:X2}");
                    var stream = new Windows.Storage.Streams.InMemoryRandomAccessStream();
                    using (var writer = new Windows.Storage.Streams.DataWriter(stream))
                    {
                        writer.WriteString(svg);
                        await writer.StoreAsync();
                        writer.DetachStream();
                    }
                    stream.Seek(0);
                    var source = new SvgImageSource();
                    await source.SetSourceAsync(stream);
                    result = source;
                }
            }
        }
        catch (Exception ex) { App.Log($"Ícone monocromático ({name}) indisponível: {ex.Message}"); }
        Cache[key] = result;
        return result;
    }

    /// <summary>Recolore um glifo-modelo (preto + alfa) mantendo o canal alfa (saída pré-multiplicada).</summary>
    private static async Task<WriteableBitmap> TintAsync(string file, byte r, byte g, byte b)
    {
        var sf = await StorageFile.GetFileFromPathAsync(file);
        using var stream = await sf.OpenReadAsync();
        var decoder = await BitmapDecoder.CreateAsync(stream);
        var data = await decoder.GetPixelDataAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Straight,
            new BitmapTransform(), ExifOrientationMode.IgnoreExifOrientation, ColorManagementMode.DoNotColorManage);
        var px = data.DetachPixelData();
        for (int i = 0; i < px.Length; i += 4)
        {
            byte a = px[i + 3];
            px[i] = (byte)(b * a / 255); px[i + 1] = (byte)(g * a / 255); px[i + 2] = (byte)(r * a / 255);
        }
        // Assets de bandeja podem conter margem transparente: normaliza o glifo dentro de um quadrado.
        int width = (int)decoder.PixelWidth, height = (int)decoder.PixelHeight;
        int left = width, top = height, right = -1, bottom = -1;
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
                if (px[(y * width + x) * 4 + 3] > 8)
                {
                    left = Math.Min(left, x); top = Math.Min(top, y);
                    right = Math.Max(right, x); bottom = Math.Max(bottom, y);
                }
        if (right < left || bottom < top) return new WriteableBitmap(1, 1);
        int glyphWidth = right - left + 1, glyphHeight = bottom - top + 1;
        int size = Math.Max(glyphWidth, glyphHeight);
        var normalized = new byte[size * size * 4];
        int offsetX = (size - glyphWidth) / 2, offsetY = (size - glyphHeight) / 2;
        for (int y = 0; y < glyphHeight; y++)
            Buffer.BlockCopy(px, ((top + y) * width + left) * 4,
                normalized, ((offsetY + y) * size + offsetX) * 4, glyphWidth * 4);
        var wb = new WriteableBitmap(size, size);
        using (var s = wb.PixelBuffer.AsStream()) s.Write(normalized, 0, normalized.Length);
        wb.Invalidate();
        return wb;
    }

    public static void ClearThemeCache()
    {
        foreach (var k in Cache.Keys.Where(k => k.StartsWith("codex|", StringComparison.Ordinal)).ToList()) Cache.Remove(k);
    }
}
