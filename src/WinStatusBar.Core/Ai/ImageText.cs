using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage;

namespace WinStatusBar.Core.Ai;

/// <summary>
/// Texto de uma imagem pelo OCR do próprio Windows (sem rede, sem programa extra). Usado quando o modelo escolhido
/// só lê texto: a captura da janela vai como o texto reconhecido nela em vez de ser recusada pelo servidor.
/// </summary>
public static class ImageText
{
    private const int MaxChars = 12000;

    public static async Task<string?> ReadAsync(string path, CancellationToken ct)
    {
        try
        {
            var engine = OcrEngine.TryCreateFromUserProfileLanguages()
                ?? OcrEngine.AvailableRecognizerLanguages.Select(OcrEngine.TryCreateFromLanguage).FirstOrDefault(e => e is not null);
            if (engine is null) return null;
            var file = await StorageFile.GetFileFromPathAsync(Path.GetFullPath(path)).AsTask(ct);
            using var stream = await file.OpenReadAsync().AsTask(ct);
            var decoder = await BitmapDecoder.CreateAsync(stream).AsTask(ct);
            // O OCR recusa imagens maiores que MaxImageDimension: reduz mantendo a proporção.
            double scale = Math.Min(1.0, (double)OcrEngine.MaxImageDimension / Math.Max(decoder.PixelWidth, decoder.PixelHeight));
            var transform = new BitmapTransform
            {
                ScaledWidth = Math.Max(1u, (uint)(decoder.PixelWidth * scale)),
                ScaledHeight = Math.Max(1u, (uint)(decoder.PixelHeight * scale)),
                InterpolationMode = BitmapInterpolationMode.Fant,
            };
            using var bitmap = await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, transform,
                ExifOrientationMode.RespectExifOrientation, ColorManagementMode.DoNotColorManage).AsTask(ct);
            var result = await engine.RecognizeAsync(bitmap).AsTask(ct);
            string text = string.Join("\n", result.Lines.Select(l => l.Text)).Trim();
            if (text.Length == 0) return null;
            return text.Length > MaxChars ? text[..MaxChars] + "…" : text;
        }
        catch (OperationCanceledException) { throw; }
        catch { return null; }
    }
}
