using System.Drawing.Text;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using WinStatusBar.Core.Ai;

namespace WinStatusBar.ExecutorExpansion;

/// <summary>Only additional Office operations. Never creates, opens, saves, closes or quits user applications.</summary>
public static class OfficeExpansion
{
    public static bool Handles(DesktopActionRequest r) => r.Action == "office" &&
        ((r.Application == "word" && r.Op is "state" or "format" or "insert_image") ||
         (r.Application == "excel" && r.Op is "state" or "format"));
    public static Task<DesktopActionResult> ExecuteAsync(DesktopActionRequest r, CancellationToken ct)
    {
        var completion = new TaskCompletionSource<DesktopActionResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try { ct.ThrowIfCancellationRequested(); completion.TrySetResult(Execute(r, ct)); }
            catch (OperationCanceledException) { completion.TrySetCanceled(ct); }
            catch (Exception e) when (e is ArgumentException or InvalidOperationException)
            { completion.TrySetResult(new(e.Message, Success: false, Status: DesktopStatus.Blocked, Method: "Office/COM")); }
            catch { completion.TrySetResult(new("Office não confirmou a operação; consulte state antes de repetir.",
                Success: false, Status: DesktopStatus.Partial, Method: "Office/COM")); }
        }) { IsBackground = true, Name = "WinStatusBar.OfficeExpansion.STA" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        // Existing broker terminates itself on timeout. This task itself does not pretend to interrupt COM.
        return completion.Task;
    }
    private static DesktopActionResult Execute(DesktopActionRequest r, CancellationToken ct)
    {
        ValidateFormat(r);
        object? application = null, document = null, range = null;
        try
        {
            application = Active(r.Application == "word" ? "Word.Application" : "Excel.Application");
            dynamic app = application;
            if (r.Application == "word")
            {
                if ((int)app.Documents.Count == 0) throw new InvalidOperationException("Nenhum documento aberto.");
                document = app.ActiveDocument;
                dynamic doc = document;
                GuardDocument(r, (string)doc.Name, (string)doc.FullName);
                if (r.Op == "state")
                    return Ok(JsonSerializer.Serialize(new { application = "word", name = (string)doc.Name,
                        path = (string)doc.FullName, saved = (bool)doc.Saved, readOnly = (bool)doc.ReadOnly,
                        characters = (int)doc.Content.End, inlineImages = (int)doc.InlineShapes.Count,
                        selectionStart = (int)app.Selection.Start, selectionEnd = (int)app.Selection.End }));
                if ((bool)doc.ReadOnly) throw new InvalidOperationException("Documento está somente leitura.");
                if (r.Op == "insert_image")
                {
                    if (r.Path is null || !File.Exists(r.Path)) throw new ArgumentException("Imagem não encontrada.");
                    string extension = Path.GetExtension(r.Path).ToLowerInvariant();
                    if (extension is not (".png" or ".jpg" or ".jpeg" or ".bmp" or ".gif" or ".tif" or ".tiff"))
                        throw new ArgumentException("Use uma imagem raster PNG/JPEG/BMP/GIF/TIFF.");
                    if (r.Scope is not (null or "selection" or "end")) throw new ArgumentException("Imagem: scope selection/end.");
                    if ((r.Width is not null && r.Height is not null) || r.Width is < 1 or > 2000 || r.Height is < 1 or > 2000)
                        throw new ArgumentException("Informe só width ou height, 1–2000 pontos, para preservar proporção.");
                    if (r.Scope == "end") { range = doc.Content; ((dynamic)range).Collapse(0); }
                    else
                    {
                        if ((string)app.Selection.Document.FullName != (string)doc.FullName)
                            throw new InvalidOperationException("Seleção pertence a outro documento.");
                        range = app.Selection.Range.Duplicate;
                    }
                    int before = (int)doc.InlineShapes.Count;
                    ct.ThrowIfCancellationRequested();
                    object? shapeObject = null;
                    try
                    {
                        shapeObject = doc.InlineShapes.AddPicture(Path.GetFullPath(r.Path), false, true, range);
                        dynamic shape = shapeObject;
                        shape.LockAspectRatio = -1;
                        if (r.Width is int width) shape.Width = (float)width;
                        if (r.Height is int height) shape.Height = (float)height;
                        bool sizeOk = (r.Width is null || Math.Abs((double)shape.Width - r.Width.Value) < 0.1) &&
                            (r.Height is null || Math.Abs((double)shape.Height - r.Height.Value) < 0.1);
                        return (int)doc.InlineShapes.Count == before + 1 && sizeOk
                            ? Ok($"Imagem inserida e contagem/dimensão conferidas em {(string)doc.Name}. Documento permanece aberto, sem salvar.")
                            : Partial("Imagem pode ter sido inserida; conferência final incompleta. Não repita sem state.");
                    }
                    finally { Release(shapeObject); }
                }
                range = WordRange(app, doc, r);
                dynamic target = range;
                ct.ThrowIfCancellationRequested();
                // One captured Range: a focus change cannot redirect formatting to another document.
                bool customRecord = false;
                try
                {
                    app.UndoRecord.StartCustomRecord("WinStatusBar: formatar"); customRecord = true;
                    SetWord(target, r);
                    return VerifyWord(target, r) ? Ok($"Formatação relida em {(string)doc.Name}, scope={r.Scope ?? "selection"}. Sem salvar.")
                        : Partial("Formatação parcialmente aplicada ou não uniforme. Use Desfazer no Word ou consulte state.");
                }
                finally { if (customRecord) app.UndoRecord.EndCustomRecord(); }
            }
            if ((int)app.Workbooks.Count == 0) throw new InvalidOperationException("Nenhuma pasta de trabalho aberta.");
            document = app.ActiveWorkbook;
            dynamic book = document;
            GuardDocument(r, (string)book.Name, (string)book.FullName);
            if (r.Op == "state") return Ok(JsonSerializer.Serialize(new { application = "excel",
                name = (string)book.Name, path = (string)book.FullName, saved = (bool)book.Saved,
                readOnly = (bool)book.ReadOnly, sheets = (int)book.Worksheets.Count }));
            if ((bool)book.ReadOnly) throw new InvalidOperationException("Pasta de trabalho está somente leitura.");
            string spec = r.Range ?? throw new ArgumentException("Informe range explícito.");
            var match = Regex.Match(spec, @"^(?:(?<sheet>'(?:[^']|'')+'|[^!\[\]]+)!)?(?<cells>\$?[A-Za-z]{1,3}\$?[1-9]\d{0,6}(?::\$?[A-Za-z]{1,3}\$?[1-9]\d{0,6})?)$");
            if (!match.Success) throw new ArgumentException("Use intervalo A1 explícito, sem referências externas.");
            object? sheetObject = null;
            try
            {
                string sheetName = match.Groups["sheet"].Value;
                if (sheetName.StartsWith('\'') && sheetName.EndsWith('\'')) sheetName = sheetName[1..^1].Replace("''", "'");
                sheetObject = sheetName.Length == 0 ? book.ActiveSheet : book.Worksheets[sheetName];
                range = ((dynamic)sheetObject).Range[match.Groups["cells"].Value];
                dynamic cells = range;
                if ((double)cells.CountLarge > 10000) throw new ArgumentException("Formato limitado a 10.000 células por etapa.");
                ct.ThrowIfCancellationRequested();
                if (r.Scope == "autofit")
                {
                    cells.Columns.AutoFit();
                    return Ok("AutoFit executado; resultado visual e conteúdo truncado devem ser inspecionados. Sem salvar.");
                }
                if (r.Scope is not (null or "range")) throw new ArgumentException("Excel: scope range/autofit.");
                if (r.Font is not null) cells.Font.Name = r.Font;
                if (r.Size is double size) cells.Font.Size = size;
                if (r.Bold is bool bold) cells.Font.Bold = bold;
                if (r.Italic is bool italic) cells.Font.Italic = italic;
                if (r.Underline is bool underline) cells.Font.Underline = underline ? 2 : -4142;
                if (r.Color is not null) cells.Font.Color = ColorNumber(r.Color);
                if (r.Align is not null) cells.HorizontalAlignment = ExcelAlignment(r.Align);
                if (r.Value is not null) cells.NumberFormat = r.Value;
                bool verified = (r.Font is null || (string?)cells.Font.Name == r.Font) &&
                    (r.Size is null || Convert.ToDouble(cells.Font.Size) == r.Size) &&
                    (r.Bold is null || Equals(cells.Font.Bold, r.Bold.Value)) &&
                    (r.Italic is null || Equals(cells.Font.Italic, r.Italic.Value)) &&
                    (r.Underline is null || Convert.ToInt32(cells.Font.Underline) == (r.Underline.Value ? 2 : -4142)) &&
                    (r.Color is null || Convert.ToInt32(cells.Font.Color) == ColorNumber(r.Color)) &&
                    (r.Align is null || Convert.ToInt32(cells.HorizontalAlignment) == ExcelAlignment(r.Align)) &&
                    (r.Value is null || (string?)cells.NumberFormat == r.Value);
                return verified ? Ok("Formatação do intervalo relida e conferida. Sem salvar.") :
                    Partial("Formatação não uniforme ou conferência incompleta. Não repita sem leitura.");
            }
            finally { Release(sheetObject); }
        }
        finally { Release(range); Release(document); Release(application); }
    }
    private static void GuardDocument(DesktopActionRequest r, string name, string fullName)
    {
        string? expected = r.Target ?? (r.Op == "insert_image" ? null : r.Path);
        if (expected is not null && !expected.Equals(name, StringComparison.OrdinalIgnoreCase) &&
            !expected.Equals(fullName, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Documento ativo não corresponde ao alvo. Nada foi alterado.");
    }
    private static object WordRange(dynamic app, dynamic doc, DesktopActionRequest r)
    {
        switch (r.Scope ?? "selection")
        {
            case "document": return doc.Content;
            case "selection":
            case "paragraph":
                if ((string)app.Selection.Document.FullName != (string)doc.FullName)
                    throw new InvalidOperationException("Seleção mudou de documento.");
                return r.Scope == "paragraph" ? app.Selection.Paragraphs[1].Range : app.Selection.Range.Duplicate;
            case "find":
                string text = (string)doc.Content.Text;
                if (string.IsNullOrEmpty(r.Find)) throw new ArgumentException("Informe find literal.");
                int index = text.IndexOf(r.Find, StringComparison.Ordinal);
                if (index < 0 || text.IndexOf(r.Find, index + 1, StringComparison.Ordinal) >= 0)
                    throw new InvalidOperationException("find deve ocorrer exatamente uma vez.");
                return doc.Range(index, index + r.Find.Length);
            default: throw new ArgumentException("scope inválido.");
        }
    }
    public static void ValidateFormat(DesktopActionRequest r)
    {
        if (r.Size is double size && (!double.IsFinite(size) || size is < 1 or > 400))
            throw new ArgumentException("size deve ser 1–400 pontos.");
        if (r.Align is not (null or "left" or "center" or "right" or "justify")) throw new ArgumentException("align inválido.");
        if (r.Color is not null) _ = ColorNumber(r.Color);
        if (r.Font is not null)
        {
            using var fonts = new InstalledFontCollection();
            if (!fonts.Families.Any(f => f.Name.Equals(r.Font, StringComparison.OrdinalIgnoreCase)))
                throw new ArgumentException("Fonte não instalada: " + r.Font);
        }
    }
    private static void SetWord(dynamic range, DesktopActionRequest r)
    {
        if (r.Font is not null) range.Font.Name = r.Font;
        if (r.Size is double size) range.Font.Size = (float)size;
        if (r.Bold is bool bold) range.Font.Bold = bold ? -1 : 0;
        if (r.Italic is bool italic) range.Font.Italic = italic ? -1 : 0;
        if (r.Underline is bool underline) range.Font.Underline = underline ? 1 : 0;
        if (r.Color is not null) range.Font.Color = ColorNumber(r.Color);
        if (r.Align is not null) range.ParagraphFormat.Alignment = WordAlignment(r.Align);
    }
    private static bool VerifyWord(dynamic range, DesktopActionRequest r) =>
        (r.Font is null || (string?)range.Font.Name == r.Font) &&
        (r.Size is null || Math.Abs(Convert.ToDouble(range.Font.Size) - r.Size.Value) < 0.01) &&
        (r.Bold is null || (int)range.Font.Bold == (r.Bold.Value ? -1 : 0)) &&
        (r.Italic is null || (int)range.Font.Italic == (r.Italic.Value ? -1 : 0)) &&
        (r.Underline is null || (int)range.Font.Underline == (r.Underline.Value ? 1 : 0)) &&
        (r.Color is null || (int)range.Font.Color == ColorNumber(r.Color)) &&
        (r.Align is null || (int)range.ParagraphFormat.Alignment == WordAlignment(r.Align));
    private static int WordAlignment(string align) => align switch { "left" => 0, "center" => 1, "right" => 2, "justify" => 3, _ => throw new ArgumentException("align") };
    private static int ExcelAlignment(string align) => align switch { "left" => -4131, "center" => -4108, "right" => -4152, "justify" => -4130, _ => throw new ArgumentException("align") };
    private static int ColorNumber(string color)
    {
        if (!Regex.IsMatch(color, "^#[a-fA-F0-9]{6}$")) throw new ArgumentException("Color deve ser #RRGGBB.");
        int rgb = int.Parse(color[1..], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        return ((rgb >> 16) & 255) | (rgb & 0xff00) | ((rgb & 255) << 16); // Office OLE BGR
    }
    private static object Active(string progId)
    {
        Guid id = Guid.Empty;
        foreach (var view in new[] { Microsoft.Win32.RegistryView.Registry64, Microsoft.Win32.RegistryView.Registry32 })
        {
            using var root = Microsoft.Win32.RegistryKey.OpenBaseKey(Microsoft.Win32.RegistryHive.ClassesRoot, view);
            using var key = root.OpenSubKey(progId + @"\CLSID");
            if (key?.GetValue("") is string registered && Guid.TryParse(registered, out id)) break;
        }
        if (id == Guid.Empty) throw new InvalidOperationException("Aplicativo Office não registrado nesta máquina; nenhum aplicativo foi iniciado.");
        if (GetActiveObject(ref id, 0, out var app) < 0)
            throw new InvalidOperationException("Instância Office aberta indisponível nesta sessão; nenhum aplicativo foi iniciado.");
        return app;
    }
    private static void Release(object? value) { if (value is not null && Marshal.IsComObject(value)) Marshal.ReleaseComObject(value); }
    private static DesktopActionResult Ok(string text) => new(text, Method: "Office/COM/STA");
    private static DesktopActionResult Partial(string text) => new(text, Success: false, Status: DesktopStatus.Partial, Method: "Office/COM/STA");
    [DllImport("oleaut32.dll", PreserveSig = true)] private static extern int GetActiveObject(ref Guid clsid, nint reserved, [MarshalAs(UnmanagedType.IUnknown)] out object app);
}
