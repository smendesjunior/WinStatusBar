using System.Collections.Specialized;
using System.Drawing;
using System.Text.Json;
using WinStatusBar.Core.Ai;

namespace WinStatusBar.ExecutorExpansion;

public sealed record ClipboardReading(string? Text, string[] Paths, string[] Formats, int? ImageWidth, int? ImageHeight);
public interface IClipboardAccess
{
    ClipboardReading Read();
    void SetText(string text);
    void SetPaths(string[] paths);
    void SetImage(string path);
}
public sealed class ClipboardExpansion(IClipboardAccess? access = null)
{
    private readonly IClipboardAccess backend = access ?? new WindowsClipboardAccess();
    public Task<DesktopActionResult> ExecuteAsync(DesktopActionRequest r, CancellationToken ct)
    {
        var completion = new TaskCompletionSource<DesktopActionResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var worker = new Thread(() =>
        {
            bool mutationStarted = false;
            try
            {
                ct.ThrowIfCancellationRequested();
                if (r.Action == "clipboard_get")
                {
                    var read = backend.Read();
                    completion.TrySetResult(new(JsonSerializer.Serialize(read), Method: "clipboard/STA/read"));
                    return;
                }
                if (r.Action != "clipboard_set") throw new ArgumentException("Ação não é clipboard.");
                bool verified;
                if (r.Paths is { Length: > 0 })
                {
                    if (r.Paths.Any(p => !Path.IsPathFullyQualified(p) || !File.Exists(p) && !Directory.Exists(p)))
                        throw new ArgumentException("Todos os paths devem ser absolutos e existir.");
                    if (r.Text is not null || r.Format is not null) throw new ArgumentException("Informe um único tipo de conteúdo.");
                    mutationStarted = true; backend.SetPaths(r.Paths);
                    verified = backend.Read().Paths.SequenceEqual(r.Paths, StringComparer.OrdinalIgnoreCase);
                }
                else if (r.Format == "image")
                {
                    if (r.Path is null || !Path.IsPathFullyQualified(r.Path) || !File.Exists(r.Path))
                        throw new ArgumentException("Informe path absoluto da imagem.");
                    if (r.Text is not null) throw new ArgumentException("Imagem e texto não podem ser misturados.");
                    using var image = Image.FromFile(r.Path);
                    if ((long)image.Width * image.Height > 50_000_000) throw new ArgumentException("Imagem excede 50 milhões de pixels.");
                    mutationStarted = true; backend.SetImage(r.Path);
                    var after = backend.Read();
                    verified = after.ImageWidth == image.Width && after.ImageHeight == image.Height;
                }
                else
                {
                    if (r.Format is not (null or "text")) throw new ArgumentException("format não suportado: use text/image ou paths.");
                    string text = r.Text ?? "";
                    mutationStarted = true; backend.SetText(text);
                    verified = (backend.Read().Text ?? "") == text;
                }
                completion.TrySetResult(verified
                    ? new("Área de transferência atualizada; texto/lista de arquivos ou dimensões da imagem relidos.", Method: "clipboard/STA/verified")
                    : new("Conteúdo da área de transferência mudou ou não confere. Não repita sem leitura.", Success: false, Status: DesktopStatus.Partial, Method: "clipboard/STA"));
            }
            catch (OperationCanceledException) { completion.TrySetCanceled(ct); }
            catch (ArgumentException e) when (!mutationStarted)
            { completion.TrySetResult(new(e.Message, Success: false, Status: DesktopStatus.Blocked, Method: "clipboard/validation")); }
            catch
            { completion.TrySetResult(new("Área de transferência indisponível ou resultado não confirmado.", Success: false, Status: mutationStarted ? DesktopStatus.Partial : DesktopStatus.Blocked, Method: "clipboard/STA")); }
        }) { IsBackground = true, Name = "WinStatusBar.Clipboard.STA" };
        worker.SetApartmentState(ApartmentState.STA); worker.Start();
        return completion.Task;
    }
}
public sealed class WindowsClipboardAccess : IClipboardAccess
{
    public ClipboardReading Read()
    {
        var data = Clipboard.GetDataObject();
        string? text = Clipboard.ContainsText() ? Clipboard.GetText() : null;
        if (text?.Length > 200000) text = text[..200000] + "\n[texto truncado no limite de 200000 caracteres]";
        string[] paths = Clipboard.ContainsFileDropList() ? Clipboard.GetFileDropList().Cast<string>().ToArray() : [];
        using var image = Clipboard.ContainsImage() ? Clipboard.GetImage() : null;
        return new(text, paths, data?.GetFormats(false) ?? [], image?.Width, image?.Height);
    }
    public void SetText(string text) { if (text.Length == 0) Clipboard.Clear(); else Clipboard.SetText(text); }
    public void SetPaths(string[] paths) { var items = new StringCollection(); items.AddRange(paths); Clipboard.SetFileDropList(items); }
    public void SetImage(string path) { using var image = Image.FromFile(path); using var bitmap = new Bitmap(image); Clipboard.SetImage(bitmap); }
}
