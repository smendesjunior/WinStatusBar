using System.Runtime.InteropServices;
using System.Text;

namespace WinStatusBar.Services;

/// <summary>
/// Texto real da janela anterior ao chat, sem instalar nada: seleção (UI Automation do Windows), documento do Word e
/// planilha do Excel abertos (COM do próprio Office, inclui alterações não salvas) e, nos navegadores, endereço e texto
/// da página inteira (árvore de acessibilidade do Chromium/Firefox). Só leitura; nunca ativa, rola ou altera o app.
/// </summary>
internal static class ScreenText
{
    /// <param name="Selection">Texto selecionado, se houver.</param>
    /// <param name="Url">Endereço da aba (navegadores).</param>
    /// <param name="Body">Texto completo do documento, planilha ou página.</param>
    /// <param name="Source">Descrição da origem do texto (para a IA).</param>
    /// <param name="Kind">"word", "excel", "web" ou "app".</param>
    public sealed record Result(string? Selection, string? Url, string? Body, string Source, string Kind, string? Name = null);

    public const int MaxSelection = 4000, MaxBody = 200_000;

    private static readonly HashSet<string> Browsers = new(StringComparer.OrdinalIgnoreCase)
        { "msedge", "chrome", "firefox", "brave", "opera", "vivaldi", "arc" };

    public static Result? Read(IntPtr top, string process, string title)
    {
        try
        {
            if (process.Equals("WINWORD", StringComparison.OrdinalIgnoreCase)) return FromWord(title) ?? FromUia(top, process);
            if (process.Equals("EXCEL", StringComparison.OrdinalIgnoreCase)) return FromExcel(title) ?? FromUia(top, process);
            // Seleção explícita tem prioridade sobre a página/janela inteira, inclusive em apps Electron.
            if (ReadSelection(top, process) is { } selected) return selected;
            if (Browsers.Contains(process)) return FromBrowser(top, process);
            return null;
        }
        catch (Exception ex) { App.Log("Contexto (texto): " + ex.GetType().Name + " · " + ex.Message); return null; }
    }

    // ------------------------------------------------------------------ Office (COM do app aberto)
    private static Result? FromWord(string title)
    {
        if (Active("Word.Application") is not { } app) return null;
        try
        {
            dynamic word = app;
            dynamic? doc = null;
            int count = (int)word.Documents.Count;
            for (int i = 1; i <= count && doc is null; i++)
            {
                dynamic d = word.Documents.Item(i);
                string name = (string)d.Name;
                if (title.Contains(Path.GetFileNameWithoutExtension(name), StringComparison.OrdinalIgnoreCase)) doc = d;
            }
            if (doc is null) { if (count == 0) return null; doc = word.ActiveDocument; }
            string? selection = null;
            try
            {
                dynamic sel = doc.ActiveWindow.Selection;
                if ((int)sel.Type != 1 /* wdSelectionIP: só o cursor */) selection = Clean((string)sel.Text, MaxSelection);
            }
            catch { }
            string body = Clean((string)doc.Content.Text, MaxBody);
            string full = (string)doc.FullName;
            bool saved = (bool)doc.Saved;
            return new(selection, null, body, $"Documento do Word aberto: {full}{(saved ? string.Empty : " (com alterações ainda não salvas, incluídas no texto)")}",
                "word", (string)doc.Name);
        }
        finally { Marshal.FinalReleaseComObject(app); }
    }

    private static Result? FromExcel(string title)
    {
        if (Active("Excel.Application") is not { } app) return null;
        try
        {
            dynamic excel = app;
            dynamic? book = excel.ActiveWorkbook;
            if (book is null) return null;
            dynamic sheet = excel.ActiveSheet;
            string? selection = null;
            try
            {
                dynamic sel = excel.Selection;
                string address = ((string)sel.Address).Replace("$", string.Empty);
                string cells = RangeText(sel, 200, 40);
                if (cells.Length > 0) selection = Clean($"Células {address}:\n{cells}", MaxSelection);
            }
            catch { }
            string used = RangeText(sheet.UsedRange, 2000, 60);
            return new(selection, null, Clean(used, MaxBody), $"Planilha do Excel aberta: {(string)book.FullName} · aba \"{(string)sheet.Name}\" (valores da área usada, separados por tabulação)",
                "excel", (string)book.Name);
        }
        finally { Marshal.FinalReleaseComObject(app); }
    }

    private static string RangeText(dynamic range, int maxRows, int maxCols)
    {
        object? value = range.Value2;
        if (value is not object[,] grid) return value?.ToString() ?? string.Empty;
        var sb = new StringBuilder();
        int r0 = grid.GetLowerBound(0), c0 = grid.GetLowerBound(1);
        int rows = Math.Min(grid.GetLength(0), maxRows), cols = Math.Min(grid.GetLength(1), maxCols);
        for (int r = 0; r < rows; r++)
        {
            for (int c = 0; c < cols; c++) { if (c > 0) sb.Append('\t'); sb.Append(grid[r0 + r, c0 + c]); }
            sb.Append('\n');
        }
        if (grid.GetLength(0) > maxRows) sb.Append($"… ({grid.GetLength(0) - maxRows} linhas a mais não incluídas)\n");
        return sb.ToString();
    }

    private static object? Active(string progId)
    {
        if (CLSIDFromProgID(progId, out var clsid) != 0) return null;
        return GetActiveObject(ref clsid, IntPtr.Zero, out var obj) == 0 ? obj : null;
    }

    // ------------------------------------------------------------------ navegadores
    private static Result? FromBrowser(IntPtr top, string process)
    {
        var uia = (IUIAutomation)new CUIAutomation();
        var root = uia.ElementFromHandle(top);
        if (root is null) return null;
        // O Chromium só monta a árvore de acessibilidade quando um cliente pede: uma segunda tentativa curta.
        IUIAutomationElement? doc = null;
        for (int attempt = 0; attempt < 2 && doc is null; attempt++)
        {
            doc = root.FindFirst(TreeScopeSubtree, uia.CreatePropertyCondition(ControlTypeProperty, DocumentControl));
            if (doc is null && attempt == 0) Thread.Sleep(350);
        }
        string? url = null;
        try
        {
            var edit = root.FindFirst(TreeScopeSubtree, uia.CreatePropertyCondition(ControlTypeProperty, EditControl));
            if (edit?.GetCurrentPattern(ValuePatternId) is IUIAutomationValuePattern value) url = value.get_CurrentValue();
        }
        catch { }
        string? selection = null, body = null;
        if (doc?.GetCurrentPattern(TextPatternId) is IUIAutomationTextPattern text)
        {
            selection = SelectionOf(text);
            try { body = Clean(text.get_DocumentRange().GetText(MaxBody), MaxBody); } catch { }
        }
        if (selection is null && body is null && url is null) return null;
        return new(selection, url, body, $"Página aberta no navegador ({process}){(url is null ? string.Empty : ": " + url)}", "web");
    }

    // ------------------------------------------------------------------ qualquer app: seleção pelo UI Automation
    public static Result? ReadSelection(IntPtr top, string process)
    {
        try { return FromUia(top, process); }
        catch (Exception ex) { App.Log("Contexto (seleção): " + ex.GetType().Name); return null; }
    }

    private static Result? FromUia(IntPtr top, string process)
    {
        uint thread = GetWindowThreadProcessId(top, out _);
        var info = new GuiThreadInfo { Size = Marshal.SizeOf<GuiThreadInfo>() };
        IntPtr focus = GetGUIThreadInfo(thread, ref info) && info.Focus != IntPtr.Zero ? info.Focus : top;
        var uia = (IUIAutomation)new CUIAutomation();
        var root = uia.ElementFromHandle(top);
        var element = uia.ElementFromHandle(focus) ?? root;
        if (element is null || root is null) return null;
        if (element.GetCurrentPattern(TextPatternId) is IUIAutomationTextPattern focused && SelectionOf(focused) is { } focusedText)
            return new(focusedText, null, null, $"Texto selecionado no aplicativo {process}", "app");
        // O primeiro TextPattern costuma ser uma caixa de busca ou o compositor, não o documento selecionado.
        var candidates = root.FindAll(TreeScopeSubtree, uia.CreatePropertyCondition(IsTextPatternAvailableProperty, true));
        if (candidates is not null)
        {
            for (int i = 0, count = Math.Min(candidates.get_Length(), 128); i < count; i++)
            {
                try
                {
                    if (candidates.GetElement(i).GetCurrentPattern(TextPatternId) is IUIAutomationTextPattern text
                        && SelectionOf(text) is { } selection)
                        return new(selection, null, null, $"Texto selecionado no aplicativo {process}", "app");
                }
                catch (COMException) { } // elemento deixou de existir durante a captura
            }
        }
        // Listas/arquivos e outros controles com seleção, sem simular Ctrl+C nem modificar o clipboard.
        var items = root.FindAll(TreeScopeSubtree, uia.CreatePropertyCondition(30079 /* SelectionItemIsSelected */, true));
        if (items is null) return null;
        var names = new List<string>();
        for (int i = 0, count = Math.Min(items.get_Length(), 40); i < count; i++)
        {
            var item = items.GetElement(i);
            // A aba ativa de um navegador/ribbon não é uma seleção de conteúdo do usuário.
            if (item.GetCurrentPropertyValue(ControlTypeProperty) is not int type || type is not (50007 or 50024 or 50029)) continue;
            if (item.GetCurrentPropertyValue(30005 /* Name */) is string name && !string.IsNullOrWhiteSpace(name))
                names.Add(name.Trim());
        }
        return names.Count == 0 ? null : new(Clean("Itens selecionados:\n• " + string.Join("\n• ", names.Distinct()), MaxSelection),
            null, null, $"Itens selecionados no aplicativo {process}", "app");
    }

    private static string? SelectionOf(IUIAutomationTextPattern text)
    {
        try
        {
            var ranges = text.GetSelection();
            if (ranges is null) return null;
            var sb = new StringBuilder();
            for (int i = 0, n = ranges.get_Length(); i < n && sb.Length < MaxSelection; i++)
                sb.Append(ranges.GetElement(i).GetText(MaxSelection)).Append('\n');
            string s = Clean(sb.ToString(), MaxSelection);
            return s.Length == 0 ? null : s;
        }
        catch { return null; }
    }

    private static string Clean(string? s, int max)
    {
        if (string.IsNullOrEmpty(s)) return string.Empty;
        s = s.Replace("\r\n", "\n").Replace('\r', '\n').Replace("\a", string.Empty).Replace("\v", "\n")
             .Replace("￼", string.Empty); // marcador de imagem/objeto embutido
        s = System.Text.RegularExpressions.Regex.Replace(s, @"[ \t]{2,}", " ");
        s = System.Text.RegularExpressions.Regex.Replace(s, @"\n\s*\n\s*\n+", "\n\n").Trim();
        return s.Length <= max ? s : s[..max] + "\n… (texto cortado)";
    }

    // ------------------------------------------------------------------ UI Automation (UIAutomationCore.dll, já no Windows)
    // Somente os métodos usados; os anteriores ocupam seus lugares na tabela da interface (ordem do UIAutomationClient.h).
    private const int TreeScopeSubtree = 7, TextPatternId = 10014, ValuePatternId = 10002;
    private const int ControlTypeProperty = 30003, IsTextPatternAvailableProperty = 30040, EditControl = 50004, DocumentControl = 50030;

    [ComImport, Guid("ff48dba4-60ef-4201-aa87-54103eef594e")] private class CUIAutomation { }

    [ComImport, Guid("30cbe57d-d9d0-452a-ab13-7ac5ac4825ee"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IUIAutomation
    {
        void CompareElements(); void CompareRuntimeIds(); void GetRootElement();
        IUIAutomationElement ElementFromHandle(IntPtr hwnd);
        void ElementFromPoint(); void GetFocusedElement(); void GetRootElementBuildCache(); void ElementFromHandleBuildCache();
        void ElementFromPointBuildCache(); void GetFocusedElementBuildCache(); void CreateTreeWalker();
        void get_ControlViewWalker(); void get_ContentViewWalker(); void get_RawViewWalker();
        void get_RawViewCondition(); void get_ControlViewCondition(); void get_ContentViewCondition();
        void CreateCacheRequest(); void CreateTrueCondition(); void CreateFalseCondition();
        IUIAutomationCondition CreatePropertyCondition(int propertyId, [MarshalAs(UnmanagedType.Struct)] object value);
    }

    [ComImport, Guid("352ffba8-0973-437c-a61f-f64cafd81df9"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IUIAutomationCondition { }

    [ComImport, Guid("d22108aa-8ac5-49a5-837b-37bbb3d7591e"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IUIAutomationElement
    {
        void SetFocus(); void GetRuntimeId();
        IUIAutomationElement? FindFirst(int scope, IUIAutomationCondition condition);
        IUIAutomationElementArray? FindAll(int scope, IUIAutomationCondition condition);
        void FindFirstBuildCache(); void FindAllBuildCache(); void BuildUpdatedCache();
        [return: MarshalAs(UnmanagedType.Struct)] object? GetCurrentPropertyValue(int propertyId);
        void GetCurrentPropertyValueEx(); void GetCachedPropertyValue(); void GetCachedPropertyValueEx();
        void GetCurrentPatternAs(); void GetCachedPatternAs();
        [return: MarshalAs(UnmanagedType.IUnknown)] object? GetCurrentPattern(int patternId);
    }

    [ComImport, Guid("14314595-b4bc-4055-95f2-58f2e42c9855"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IUIAutomationElementArray
    {
        int get_Length();
        IUIAutomationElement GetElement(int index);
    }

    [ComImport, Guid("32eba289-3583-42c9-9c59-3b6d9a1e9b6a"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IUIAutomationTextPattern
    {
        void RangeFromPoint(); void RangeFromChild();
        IUIAutomationTextRangeArray? GetSelection();
        void GetVisibleRanges();
        IUIAutomationTextRange get_DocumentRange();
    }

    [ComImport, Guid("ce4ae76a-e717-4c98-81ea-47371d028eb6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IUIAutomationTextRangeArray
    {
        int get_Length();
        IUIAutomationTextRange GetElement(int index);
    }

    [ComImport, Guid("a543cc6a-f4ae-494b-8239-c814481187a8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IUIAutomationTextRange
    {
        void Clone(); void Compare(); void CompareEndpoints(); void ExpandToEnclosingUnit(); void FindAttribute();
        void FindText(); void GetAttributeValue(); void GetBoundingRectangles(); void GetEnclosingElement();
        [return: MarshalAs(UnmanagedType.BStr)] string GetText(int maxLength);
    }

    [ComImport, Guid("a94cd8b1-0844-4cd6-9d2d-640537ab39e9"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IUIAutomationValuePattern
    {
        void SetValue();
        [return: MarshalAs(UnmanagedType.BStr)] string get_CurrentValue();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct GuiThreadInfo
    {
        public int Size, Flags;
        public IntPtr Active, Focus, Capture, MenuOwner, MoveSize, Caret;
        public int CaretLeft, CaretTop, CaretRight, CaretBottom;
    }

    [DllImport("user32.dll")] private static extern bool GetGUIThreadInfo(uint thread, ref GuiThreadInfo info);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    [DllImport("ole32.dll", CharSet = CharSet.Unicode)] private static extern int CLSIDFromProgID(string progId, out Guid clsid);
    [DllImport("oleaut32.dll")] private static extern int GetActiveObject(ref Guid clsid, IntPtr reserved, [MarshalAs(UnmanagedType.IUnknown)] out object obj);
}
