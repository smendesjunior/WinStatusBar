using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;

namespace WinStatusBar.Services;

/// <summary>
/// Contexto do aplicativo anterior à abertura do chat. Só consulta metadados de janelas
/// e monikers de documentos salvos; não lê conteúdo, não procura em disco e não ativa apps.
/// </summary>
internal static class ActiveDocumentContext
{
    /// <param name="Path">Arquivo salvo ou imagem da janela (anexo).</param>
    /// <param name="TextPath">Texto real lido do app (documento, planilha ou página inteira), num .txt anexado.</param>
    public sealed record Context(string? Path, string Label, string Description, string? TextPath = null, IntPtr Window = default);
    private static IntPtr _hook;
    private static readonly WinEventProc Callback = (_, _, _, _, _, _, _) => ObserveForeground();
    private static readonly HashSet<string> Supported = new(StringComparer.OrdinalIgnoreCase)
        { "WINWORD", "EXCEL", "POWERPNT", "acad", "Code", "devenv", "notepad", "notepad++" };
    /// <summary>Janelas do sistema que nunca são "o conteúdo": barra de tarefas, área de trabalho, Iniciar, pesquisa, alternador.</summary>
    private static readonly HashSet<string> ShellClasses = new(StringComparer.Ordinal)
        { "Shell_TrayWnd", "Shell_SecondaryTrayWnd", "Progman", "WorkerW", "Windows.UI.Core.CoreWindow",
          "NotifyIconOverflowWindow", "TopLevelWindowForOverflowXamlIsland", "XamlExplorerHostIslandWindow", "MultitaskingViewFrame", "ForegroundStaging" };
    /// <summary>Últimas janelas de app usadas (mais recente primeiro), para escolher a do monitor onde o chat abriu.</summary>
    private static readonly List<IntPtr> Recent = new();
    private static readonly object Gate = new();

    public static void Start()
    {
        if (_hook != IntPtr.Zero) return;
        ObserveForeground();
        _hook = SetWinEventHook(3, 3, IntPtr.Zero, Callback, 0, 0, 2);
        // Restaurar um app minimizado avisa "primeiro plano" ainda minimizado (descartado); MINIMIZEEND (0x17)
        // chega quando ele já voltou, e então entra como a janela em uso.
        _restoreHook = SetWinEventHook(0x17, 0x17, IntPtr.Zero, Callback, 0, 0, 2);
    }
    private static IntPtr _restoreHook;

    /// <summary>A janela de conteúdo em uso mudou (outro app foi para a frente): o chat atualiza o contexto na hora.</summary>
    public static event Action? ForegroundChanged;

    public static void ObserveForeground()
    {
        var hwnd = GetForegroundWindow();
        if (!IsContentWindow(hwnd)) return;
        bool changed;
        lock (Gate)
        {
            changed = Recent.Count == 0 || Recent[0] != hwnd;
            Recent.Remove(hwnd);
            Recent.Insert(0, hwnd);
            if (Recent.Count > 12) Recent.RemoveAt(Recent.Count - 1);
        }
        if (changed) ForegroundChanged?.Invoke();
    }

    /// <summary>Janela de app com conteúdo: visível, não minimizada, não do WinStatusBar, não do sistema, não ferramenta flutuante.</summary>
    private static bool IsContentWindow(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero || !IsWindowVisible(hwnd) || IsIconic(hwnd)) return false;
        GetWindowThreadProcessId(hwnd, out uint pid);
        if (pid == Environment.ProcessId) return false;
        if (DwmGetWindowAttribute(hwnd, 14 /* DWMWA_CLOAKED */, out int cloaked, sizeof(int)) == 0 && cloaked != 0) return false;
        long ex = GetWindowLongPtrW(hwnd, -20 /* GWL_EXSTYLE */).ToInt64();
        if ((ex & 0x80 /* WS_EX_TOOLWINDOW */) != 0) return false;
        var cls = new StringBuilder(128); GetClassNameW(hwnd, cls, cls.Capacity);
        return !ShellClasses.Contains(cls.ToString());
    }

    /// <summary>
    /// A janela do conteúdo: a usada mais recentemente que está no monitor onde o chat abriu (a "tela ativa");
    /// senão, a que está na frente nesse monitor; sem monitor informado, a última usada em qualquer tela.
    /// Antes valia a última janela de qualquer tela: o Explorador do outro monitor entrava no lugar do PDF visível.
    /// </summary>
    private static IntPtr ChooseWindow(Rect? monitor)
    {
        List<IntPtr> recent;
        lock (Gate) recent = Recent.Where(IsContentWindow).ToList();
        if (monitor is not { } m) return recent.FirstOrDefault();
        bool OnMonitor(IntPtr h)
        {
            if (!GetWindowRect(h, out var r)) return false;
            int cx = (r.Left + r.Right) / 2, cy = (r.Top + r.Bottom) / 2;
            return cx >= m.Left && cx < m.Right && cy >= m.Top && cy < m.Bottom;
        }
        // A janela que está de fato na frente nesta tela (ordem Z) é o que a pessoa está vendo: vale mais que o
        // histórico, que pode ter perdido um app restaurado da barra de tarefas (Outlook/WhatsApp minimizados).
        IntPtr visible = IntPtr.Zero;
        EnumWindows((h, _) =>
        {
            if (!IsContentWindow(h) || !OnMonitor(h)) return true;
            if (!GetWindowRect(h, out var r) || r.Right - r.Left < 320 || r.Bottom - r.Top < 240) return true;
            if ((GetWindowLongPtrW(h, -20).ToInt64() & 0x8 /* WS_EX_TOPMOST */) != 0) return true; // widgets fixos por cima
            visible = h; return false;
        }, IntPtr.Zero);
        if (visible != IntPtr.Zero) return visible;
        var here = recent.FirstOrDefault(OnMonitor);
        if (here != IntPtr.Zero) return here;
        IntPtr front = IntPtr.Zero;
        EnumWindows((h, _) =>
        {
            if (!IsContentWindow(h) || !OnMonitor(h)) return true;
            if (!GetWindowRect(h, out var r) || r.Right - r.Left < 120 || r.Bottom - r.Top < 80) return true;
            front = h; return false; // primeira na ordem Z = a que está na frente
        }, IntPtr.Zero);
        return front != IntPtr.Zero ? front : recent.FirstOrDefault();
    }

    public static string? FindSavedFile(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return null;
        try
        {
            GetWindowThreadProcessId(hwnd, out uint pid);
            using var process = Process.GetProcessById((int)pid);
            var text = new StringBuilder(2048);
            GetWindowTextW(hwnd, text, text.Capacity);
            string title = text.ToString();
            if (Supported.Contains(process.ProcessName))
            {
                // Editores que exibem o caminho completo no título.
                foreach (var part in title.Split([" — ", " - ", " – ", " • "], StringSplitOptions.None))
                    if (ExistingFile(part.Trim().Trim('*', '"')) is { } full) return full;
                // Office/AutoCAD podem registrar documentos na Running Object Table.
                // Aceita somente um caminho real cujo nome esteja no título do app em foco.
                if (FromRunningDocuments(title) is { } running) return running;
            }
            // Qualquer app que mostra o nome do arquivo no título (Acrobat, visualizadores, CAD…): o Windows guarda
            // um atalho em "Itens recentes" para cada arquivo aberto; o nome do título leva ao caminho real.
            return FromRecentItems(title);
        }
        catch { return null; }
    }

    private static readonly System.Text.RegularExpressions.Regex FileNameInTitle = new(
        @"[^\\/:*?""<>|\r\n]+?\.(pdf|docx?|xlsx?|xlsm|pptx?|dwg|dxf|txt|md|csv|rtf|odt|ods|png|jpe?g|tiff?|svg|json|xml|html?)\b",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);

    private static string? FromRecentItems(string title)
    {
        var m = FileNameInTitle.Match(title);
        if (!m.Success) return null;
        string name = m.Value.Trim().Trim('*', '"', '•', ' ');
        string recentDir = Environment.GetFolderPath(Environment.SpecialFolder.Recent);
        string lnk = System.IO.Path.Combine(recentDir, name + ".lnk");
        if (!File.Exists(lnk)) return null;
        try
        {
            var type = Type.GetTypeFromProgID("WScript.Shell");
            if (type is null) return null;
            dynamic shell = Activator.CreateInstance(type)!;
            try
            {
                dynamic shortcut = shell.CreateShortcut(lnk);
                string target = (string)shortcut.TargetPath;
                return ExistingFile(target) is { } full && string.Equals(System.IO.Path.GetFileName(full), name, StringComparison.OrdinalIgnoreCase) ? full : null;
            }
            finally { Marshal.FinalReleaseComObject(shell); }
        }
        catch { return null; }
    }

    /// <param name="monitor">Área do monitor onde o chat abriu (a tela ativa); null = qualquer tela.</param>
    public static async Task<Context?> CaptureSelectionAsync(Rect? monitor = null)
    {
        var hwnd = ChooseWindow(monitor);
        if (hwnd == IntPtr.Zero) return null;
        GetWindowThreadProcessId(hwnd, out uint pid);
        string app;
        try { using var process = Process.GetProcessById((int)pid); app = process.ProcessName; }
        catch { return null; }
        var read = Task.Run(() => ScreenText.ReadSelection(hwnd, app));
        if (await Task.WhenAny(read, Task.Delay(2000)) != read) return null;
        var selected = await read;
        if (selected?.Selection is not { Length: > 0 } text) return null;
        var title = new StringBuilder(2048); GetWindowTextW(hwnd, title, title.Capacity);
        string? path = SaveText(selected, text, title.ToString());
        return new(null, "Seleção · " + Friendly(pid, app),
            "Seleção explícita do usuário no aplicativo " + app + ". Esta seleção é o foco principal do pedido:\n\"\"\"\n" + text + "\n\"\"\"", path, hwnd);
    }

    /// <summary>Nome que a pessoa reconhece na etiqueta ("Outlook", "WhatsApp"), não o do processo ("olk", "WhatsApp.Root").</summary>
    private static string Friendly(uint pid, string app)
    {
        try
        {
            using var p = Process.GetProcessById((int)pid);
            string? name = p.MainModule?.FileVersionInfo.FileDescription?.Trim();
            if (string.IsNullOrEmpty(name) || name.Length > 32) return app;
            return name.StartsWith("Microsoft ", StringComparison.OrdinalIgnoreCase) && name.Length > 10 ? name[10..] : name;
        }
        catch { return app; }
    }

    public static async Task<Context> CaptureAsync(Rect? monitor = null)
    {
        var hwnd = ChooseWindow(monitor);
        if (hwnd == IntPtr.Zero)
            return new(null, "Sem contexto", "Não foi possível identificar a janela do conteúdo nesta tela.");
        GetWindowThreadProcessId(hwnd, out uint pid);
        string app;
        try { using var p = Process.GetProcessById((int)pid); app = p.ProcessName; }
        catch { return new(null, "Sem contexto", "O aplicativo foi fechado."); }
        var title = new StringBuilder(2048); GetWindowTextW(hwnd, title, title.Capacity);
        string titleText = title.ToString();
        string description = $"Janela em uso na tela onde o chat foi aberto: {app}. Título da janela: {titleText}.";

        // 1) Texto real (seleção, Word/Excel abertos, página web inteira). Limite de 2,5 s: um app travado não segura o chat.
        var read = Task.Run(() => ScreenText.Read(hwnd, app, titleText));
        ScreenText.Result? text = await Task.WhenAny(read, Task.Delay(2500)) == read ? read.Result : null;
        if (text is null && !read.IsCompleted) App.Log("Contexto (texto): o aplicativo não respondeu a tempo");
        string? textPath = text?.Body is { Length: > 0 } body ? SaveText(text, body, titleText) : null;
        if (text is not null)
        {
            description += "\nOrigem: " + text.Source + ".";
            if (text.Url is { Length: > 0 } url) description += "\nEndereço: " + url;
            if (text.Selection is { Length: > 0 } sel)
                description += "\nTexto selecionado pelo usuário (foco principal do pedido):\n\"\"\"\n" + sel + "\n\"\"\"";
            if (textPath is not null) description += "\nO texto completo está no arquivo anexado " + System.IO.Path.GetFileName(textPath) + ".";
        }
        string? kindLabel = text?.Kind switch
        {
            "word" => "Word · " + (text.Name ?? app), "excel" => "Excel · " + (text.Name ?? app),
            "web" => "Página · " + Shorten(titleText, 40), _ => null,
        };
        string selMark = text?.Selection is { Length: > 0 } ? " · seleção" : string.Empty;

        // Word/Excel lidos do app: o texto (com alterações não salvas) dispensa o arquivo e a imagem.
        if (text?.Kind is "word" or "excel" && textPath is not null) return new(null, kindLabel + selMark, description, textPath, hwnd);

        // 2) Arquivo salvo dos editores conhecidos.
        if (FindSavedFile(hwnd) is { } file)
            return new(file, System.IO.Path.GetFileName(file) + selMark, description + " O arquivo salvo está anexado; alterações ainda não salvas podem diferir.", textPath, hwnd);

        // 3) Imagem só desta janela (a parte visível). Na web, vai junto com o texto da página inteira.
        string? capture = null;
        try { capture = await CaptureWindowAsync(hwnd); }
        catch (Exception ex) { App.Log("Captura do contexto: " + ex.GetType().Name); }
        string label = kindLabel ?? (capture is null ? "Contexto · " + Friendly(pid, app) : "Janela · " + Friendly(pid, app));
        return new(capture, label + selMark,
            description + (capture is null ? " A captura visual não está disponível."
                : " A imagem anexada mostra a parte visível desta janela; não inclui outras abas nem o que está fora da tela."), textPath, hwnd);
    }

    private static string Shorten(string s, int max) => s.Length <= max ? s : s[..max].TrimEnd() + "…";

    /// <summary>Grava o texto lido num .txt com nome legível, na área privada de cache do app.</summary>
    private static string? SaveText(ScreenText.Result text, string body, string title)
    {
        try
        {
            string folder = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "WinStatusBar", "cache", "context", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
            string name = text.Kind switch { "word" => "texto-do-documento.txt", "excel" => "dados-da-planilha.txt", "web" => "texto-da-pagina.txt", _ => "texto-da-janela.txt" };
            var sb = new StringBuilder().Append("Origem: ").Append(text.Source).Append('\n');
            if (text.Url is { Length: > 0 }) sb.Append("Endereço: ").Append(text.Url).Append('\n');
            sb.Append("Janela: ").Append(title).Append('\n').Append("Capturado em: ").Append(DateTime.Now.ToString("dd/MM/yyyy HH:mm")).Append("\n\n").Append(body);
            string path = System.IO.Path.Combine(folder, name);
            File.WriteAllText(path, sb.ToString(), new UTF8Encoding(true));
            return path;
        }
        catch (Exception ex) { App.Log("Contexto (texto): falha ao gravar · " + ex.Message); return null; }
    }

    private static async Task<string?> CaptureWindowAsync(IntPtr hwnd)
    {
        if (!GetWindowRect(hwnd, out var r)) return null;
        int width = r.Right - r.Left, height = r.Bottom - r.Top;
        if (width < 50 || height < 50 || width > 8192 || height > 8192) return null;
        IntPtr dc = GetDC(hwnd), mem = IntPtr.Zero, bitmap = IntPtr.Zero, old = IntPtr.Zero;
        byte[] pixels;
        try
        {
            mem = CreateCompatibleDC(dc); bitmap = CreateCompatibleBitmap(dc, width, height);
            if (bitmap == IntPtr.Zero || mem == IntPtr.Zero) return null;
            old = SelectObject(mem, bitmap);
            if (!PrintWindow(hwnd, mem, 2)) return null;
            SelectObject(mem, old); old = IntPtr.Zero;
            var info = new BitmapInfo { Size = 40, Width = width, Height = -height, Planes = 1, BitCount = 32 };
            pixels = new byte[checked(width * height * 4)];
            if (GetDIBits(mem, bitmap, 0, (uint)height, pixels, ref info, 0) == 0) return null;
        }
        finally
        {
            if (old != IntPtr.Zero) SelectObject(mem, old);
            if (bitmap != IntPtr.Zero) DeleteObject(bitmap);
            if (mem != IntPtr.Zero) DeleteDC(mem);
            if (dc != IntPtr.Zero) ReleaseDC(hwnd, dc);
        }
        // Área privada de cache. Somente a janela selecionada, nunca a área de trabalho inteira.
        string folder = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WinStatusBar", "cache", "context");
        Directory.CreateDirectory(folder);
        string path = System.IO.Path.Combine(folder, Guid.NewGuid().ToString("N") + ".png");
        var file = await Windows.Storage.StorageFolder.GetFolderFromPathAsync(folder);
        var output = await file.CreateFileAsync(System.IO.Path.GetFileName(path));
        using var stream = await output.OpenAsync(Windows.Storage.FileAccessMode.ReadWrite);
        var encoder = await Windows.Graphics.Imaging.BitmapEncoder.CreateAsync(Windows.Graphics.Imaging.BitmapEncoder.PngEncoderId, stream);
        encoder.SetPixelData(Windows.Graphics.Imaging.BitmapPixelFormat.Bgra8, Windows.Graphics.Imaging.BitmapAlphaMode.Ignore,
            (uint)width, (uint)height, 96, 96, pixels);
        await encoder.FlushAsync();
        return path;
    }

    private static string? ExistingFile(string path)
    {
        try { return Path.IsPathFullyQualified(path) && File.Exists(path) ? Path.GetFullPath(path) : null; }
        catch { return null; }
    }

    private static string? FromRunningDocuments(string title)
    {
        IRunningObjectTable? table = null; IBindCtx? context = null; IEnumMoniker? enumerator = null;
        var candidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (GetRunningObjectTable(0, out table) != 0 || CreateBindCtx(0, out context) != 0) return null;
            table.EnumRunning(out enumerator);
            var entries = new IMoniker[1];
            for (int i = 0; i < 256 && enumerator.Next(1, entries, IntPtr.Zero) == 0; i++)
            {
                try
                {
                    entries[0].GetDisplayName(context, null, out string display);
                    string? path = ExistingFile(display.TrimStart('!'));
                    if (path is not null && (title.Contains(Path.GetFileName(path), StringComparison.OrdinalIgnoreCase)
                        || title.StartsWith(Path.GetFileNameWithoutExtension(path) + " - ", StringComparison.OrdinalIgnoreCase)))
                        candidates.Add(path);
                }
                catch { }
                finally { if (entries[0] is not null) Marshal.ReleaseComObject(entries[0]); }
            }
        }
        catch { }
        finally
        {
            if (enumerator is not null) Marshal.ReleaseComObject(enumerator);
            if (context is not null) Marshal.ReleaseComObject(context);
            if (table is not null) Marshal.ReleaseComObject(table);
        }
        return candidates.Count == 1 ? candidates.Single() : null;
    }

    private delegate void WinEventProc(IntPtr hook, uint ev, IntPtr hwnd, int obj, int child, uint thread, uint time);
    [DllImport("user32.dll")] private static extern IntPtr SetWinEventHook(uint min, uint max, IntPtr module, WinEventProc proc, uint pid, uint tid, uint flags);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowTextW(IntPtr hwnd, StringBuilder title, int size);
    [DllImport("ole32.dll")] private static extern int GetRunningObjectTable(int reserved, out IRunningObjectTable table);
    [DllImport("ole32.dll")] private static extern int CreateBindCtx(int reserved, out IBindCtx context);
    [StructLayout(LayoutKind.Sequential)] public struct Rect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct BitmapInfo
    { public uint Size; public int Width, Height; public ushort Planes, BitCount; public uint Compression, ImageSize; public int XPels, YPels; public uint Used, Important; }
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out Rect rect);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hwnd);
    private delegate bool EnumProc(IntPtr hwnd, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumProc cb, IntPtr lParam);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassNameW(IntPtr hwnd, StringBuilder name, int max);
    [DllImport("user32.dll")] private static extern IntPtr GetWindowLongPtrW(IntPtr hwnd, int index);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attr, out int value, int size);
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);
    [DllImport("user32.dll")] private static extern bool PrintWindow(IntPtr hwnd, IntPtr dc, uint flags);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleBitmap(IntPtr dc, int width, int height);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr obj);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern int GetDIBits(IntPtr dc, IntPtr bitmap, uint start, uint lines, byte[] pixels, ref BitmapInfo info, uint usage);
}
