using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;
using WinStatusBar.Core.Ai;
namespace WinStatusBar.Automation;

/// <summary>
/// Ações prontas do executor (iguais para todas as IAs): texto e área de transferência, janelas e apps, controles por
/// nome, arquivos, Office por COM, registro, serviços, instalação (winget), sistema, impressão, download e PowerShell.
/// Cada uma confere o próprio resultado e devolve um texto curto. Retorna null quando a ação é do núcleo.
/// </summary>
internal sealed partial class DesktopRuntime
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(30) };

    private async Task<DesktopActionResult?> SkillAsync(DesktopActionRequest r, CancellationToken ct)
    {
        switch (r.Action)
        {
            case "context":
            {
                // A barra costuma estar em primeiro plano quando o pedido sai dela (e é omitida): vale a janela de cima.
                Refresh();
                long fg = GetForegroundWindow().ToInt64();
                var top = _windows.ContainsKey(fg) ? _windows[fg] : _windows.Values.FirstOrDefault();
                var sb = new StringBuilder();
                if (top is null) sb.Append("Nenhuma janela de aplicativo aberta.");
                else
                {
                    sb.Append(top.Hwnd.ToInt64() == fg ? "Janela ativa: " : "Janela de cima (a barra estava em foco): ").Append(Label(top.Hwnd)).Append($" [{top.Hwnd.ToInt64()}]");
                    if (DialogNote(top.Hwnd) is { } dialog) sb.Append('\n').Append(dialog);
                    if (OfficeContext(top.Hwnd) is { } office) sb.Append('\n').Append(office);
                }
                var others = _windows.Values.Where(w => w != top).Take(15).Select(w => $"[{w.Hwnd.ToInt64()}] {w.Process} — {(w.Title.Length == 0 ? "(sem título)" : Cap(w.Title, 60))}").ToList();
                if (others.Count > 0) sb.Append("\nOutras janelas: ").Append(string.Join("; ", others));
                return new(sb.ToString());
            }
            case "clipboard_get": { string t = ClipboardText() ?? ""; return new(t.Length == 0 ? "Área de transferência sem texto." : "Texto na área de transferência:\n" + Cap(t, 8000)); }
            case "clipboard_set": SetClipboardText(r.Text ?? ""); return new($"Área de transferência atualizada ({(r.Text ?? "").Length} caracteres).");
            case "paste": return await PasteAsync(r, ct);
            case "copy_selection":
            {
                IntPtr h = ResolveWindow(r); Activate(h); await WaitForInputFocusAsync(h, ct);
                SetClipboardText("");
                SendKeys("Ctrl+C"); string text = "";
                for (int i = 0; i < 15 && text.Length == 0; i++) { await Task.Delay(60, ct); text = ClipboardText() ?? ""; }
                return text.Length == 0 ? new("Nada foi copiado (não há seleção de texto em " + Label(h) + ").", Success: false)
                    : new("Copiado de " + Label(h) + $" ({text.Length} caracteres):\n" + Cap(text, 4000));
            }
            case "select_all": case "undo":
            {
                IntPtr h = ResolveWindow(r); Activate(h); await WaitForInputFocusAsync(h, ct);
                SendKeys(r.Action == "undo" ? "Ctrl+Z" : "Ctrl+A"); return new((r.Action == "undo" ? "Desfeito" : "Tudo selecionado") + " em " + Label(h) + ".");
            }
            case "focus":
            {
                IntPtr h = ResolveWindow(r); Activate(h); await WaitForInputFocusAsync(h, ct);
                return new("Em primeiro plano: " + Label(h) + ".");
            }
            case "window": return WindowOp(r);
            case "open":
            {
                string target = r.Path ?? r.Url ?? r.Application ?? throw new InvalidOperationException("Informe path, url ou application.");
                if (r.Path is { } p && !File.Exists(p) && !Directory.Exists(p)) throw new InvalidOperationException("Caminho não encontrado: " + p);
                var psi = new ProcessStartInfo(target) { UseShellExecute = true };
                foreach (var arg in r.Arguments ?? []) psi.ArgumentList.Add(arg);
                return await OpenAndWaitAsync(psi, ct);
            }
            case "start_search":
            {
                var before = GetForegroundWindow();
                SendKeys("Win+S");
                for (int i = 0; i < 30 && GetForegroundWindow() == before; i++) await Task.Delay(100, ct);
                await Task.Delay(250, ct);
                if (!string.IsNullOrEmpty(r.Text)) await PasteIntoForegroundAsync(r.Text, ct);
                Refresh();
                return new("Pesquisa do Iniciar aberta" + (string.IsNullOrEmpty(r.Text) ? "." : $" com \"{Cap(r.Text, 80)}\". Para abrir o primeiro resultado: keys Enter."));
            }
            case "settings":
            {
                string page = (r.Value ?? r.Text ?? "").Trim().Replace("ms-settings:", "");
                Process.Start(new ProcessStartInfo("ms-settings:" + page) { UseShellExecute = true })?.Dispose();
                await Task.Delay(1200, ct); Refresh();
                return new("Configurações abertas em ms-settings:" + page + ". Janela: " + Label(GetForegroundWindow()) + ".");
            }
            case "process": return ProcessOp(r);
            case "wait": await Task.Delay(Math.Clamp(r.Ms ?? 500, 0, 30000), ct); return new($"Esperou {Math.Clamp(r.Ms ?? 500, 0, 30000)} ms.");
            case "wait_window":
            {
                string t = r.Target ?? throw new InvalidOperationException("Informe target.");
                var until = DateTime.UtcNow.AddSeconds(Math.Clamp(r.TimeoutSeconds ?? 20, 1, 300));
                while (DateTime.UtcNow < until) { if (FindWindow(t) is { } h) return new("Janela encontrada: " + Label(h) + "."); await Task.Delay(300, ct); }
                return new($"Nenhuma janela com \"{t}\" apareceu no prazo.", Success: false);
            }
            case "find":
            {
                IntPtr h = ResolveWindow(r);
                var snap = WinStatusBar.Services.ContextActions.Capture(h);
                var hits = snap.Controls.Where(c => r.Name is null || c.Name.Contains(r.Name, StringComparison.OrdinalIgnoreCase) || c.Value.Contains(r.Name, StringComparison.OrdinalIgnoreCase)).Take(40).ToList();
                return new(hits.Count == 0 ? $"Nenhum controle com \"{r.Name}\" em {Label(h)}."
                    : $"Controles em {Label(h)}: " + string.Join("; ", hits.Select(c => $"#{c.Id} {Cap(c.Name, 40)}" + (c.Value.Length > 0 ? $" = \"{Cap(c.Value, 50)}\"" : "") + (c.Actions.Length > 0 ? " (" + string.Join("/", c.Actions) + ")" : ""))));
            }
            case "file": return FileOp(r);
            case "zip": return ZipOp(r);
            case "office": return await RunStaAsync(() => OfficeOp(r));
            case "registry": return RegistryOp(r);
            case "service":
            {
                string name = r.Name ?? throw new InvalidOperationException("Informe name.");
                string cmd = (r.Op ?? "status") switch
                {
                    "status" => $"Get-Service -Name '{Ps(name)}' | Format-List Name,DisplayName,Status,StartType",
                    "start" => $"Start-Service -Name '{Ps(name)}' -ErrorAction Stop; (Get-Service '{Ps(name)}').Status",
                    "stop" => $"Stop-Service -Name '{Ps(name)}' -ErrorAction Stop; (Get-Service '{Ps(name)}').Status",
                    "restart" => $"Restart-Service -Name '{Ps(name)}' -ErrorAction Stop; (Get-Service '{Ps(name)}').Status",
                    var o => throw new InvalidOperationException("op de service inválida: " + o),
                };
                return await PowerShellAsync(cmd, ct);
            }
            case "install":
            {
                string name = r.Name ?? throw new InvalidOperationException("Informe name (id ou nome do pacote).");
                string[] args = (r.Op ?? "search") switch
                {
                    "search" => ["search", name, "--accept-source-agreements"],
                    "install" => ["install", "--id", name, "-e", "--accept-source-agreements", "--accept-package-agreements", "--silent"],
                    "uninstall" => ["uninstall", "--id", name, "-e", "--silent"],
                    "upgrade" => ["upgrade", "--id", name, "-e", "--accept-source-agreements", "--accept-package-agreements", "--silent"],
                    var o => throw new InvalidOperationException("op de install inválida: " + o),
                };
                return await RunProcessAsync("winget", args, ct);
            }
            case "print":
            {
                string path = r.Path ?? throw new InvalidOperationException("Informe path.");
                if (!File.Exists(path)) throw new InvalidOperationException("Arquivo não encontrado: " + path);
                Process.Start(new ProcessStartInfo(path) { UseShellExecute = true, Verb = "print" })?.Dispose();
                return new("Enviado para a impressora padrão: " + Path.GetFileName(path) + ". Confira a fila de impressão se necessário.");
            }
            case "download":
            {
                string url = r.Url ?? throw new InvalidOperationException("Informe url."); string to = r.To ?? r.Path ?? throw new InvalidOperationException("Informe to.");
                if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("https" or "http")) throw new InvalidOperationException("URL inválida.");
                if (Directory.Exists(to)) to = Path.Combine(to, Path.GetFileName(uri.LocalPath) is { Length: > 0 } f ? f : "download");
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(to))!);
                using (var src = await Http.GetStreamAsync(uri, ct)) using (var dst = File.Create(to)) await src.CopyToAsync(dst, ct);
                return new($"Baixado: {to} ({new FileInfo(to).Length / 1024.0:N0} KB).");
            }
            case "powershell": return await PowerShellAsync(r.Text ?? throw new InvalidOperationException("Informe text com o comando."), ct);
            case "system": return await SystemOp(r, ct);
            default: return null;
        }
    }

    private static string Cap(string s, int n) => s.Length <= n ? s : s[..n] + "…";

    // ------------------------------------------------------------------ contexto da observação (segundo plano)
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassNameW(IntPtr h, StringBuilder text, int count);
    [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr h, uint cmd);
    [DllImport("user32.dll")] private static extern bool IsWindowEnabled(IntPtr h);
    /// <summary>Diálogo aberto (a janela é um diálogo, ou está bloqueada por um diálogo modal).</summary>
    private string? DialogNote(IntPtr hwnd)
    {
        var cls = new StringBuilder(64); GetClassNameW(hwnd, cls, cls.Capacity);
        if (cls.ToString() == "#32770") return "Diálogo aberto: " + Label(hwnd) + " — responda por uia (botões pelo nome) ou keys (Enter/Escape).";
        if (!IsWindowEnabled(hwnd) && GetWindow(hwnd, 6) is var popup && popup != IntPtr.Zero && popup != hwnd) // GW_ENABLEDPOPUP
            return "Janela bloqueada por um diálogo: " + Label(popup) + " — resolva o diálogo primeiro (target com o título dele).";
        return null;
    }
    /// <summary>Word/Excel/PowerPoint em primeiro plano: documento, salvo?, seleção (COM, até 1,5 s; nunca abre nada).</summary>
    private string? OfficeContext(IntPtr hwnd)
    {
        GetWindowThreadProcessId(hwnd, out uint pid);
        string process; try { using var p = Process.GetProcessById((int)pid); process = p.ProcessName.ToUpperInvariant(); } catch { return null; }
        string? progId = process switch { "WINWORD" => "Word.Application", "EXCEL" => "Excel.Application", "POWERPNT" => "PowerPoint.Application", _ => null };
        if (progId is null) return null;
        var work = RunStaAsync<string?>(() =>
        {
            dynamic app;
            try { app = OfficeApp(progId, create: false); } catch { return null; }
            if (progId.StartsWith("Word"))
            {
                if ((int)app.Documents.Count == 0) return "Word sem documento aberto.";
                dynamic doc = app.ActiveDocument; dynamic sel = app.Selection;
                string selected = (string)sel.Text ?? "";
                return $"Word: {(string)doc.Name} · {((bool)doc.Saved ? "salvo" : "ALTERAÇÕES NÃO SALVAS (edite na instância aberta)")} · {(int)doc.ComputeStatistics(0)} palavras · "
                    + (selected.Length > 1 ? $"seleção: \"{Cap(selected.Replace('\r', ' ').Trim(), 120)}\"" : "cursor na posição " + (int)sel.Start);
            }
            if (progId.StartsWith("Excel"))
            {
                if ((int)app.Workbooks.Count == 0) return "Excel sem pasta de trabalho aberta.";
                dynamic wb = app.ActiveWorkbook;
                return $"Excel: {(string)wb.Name} · {((bool)wb.Saved ? "salvo" : "ALTERAÇÕES NÃO SALVAS")} · planilha {(string)app.ActiveSheet.Name} · seleção {(string)app.Selection.Address}";
            }
            if ((int)app.Presentations.Count == 0) return "PowerPoint sem apresentação aberta.";
            dynamic pres = app.ActivePresentation;
            return $"PowerPoint: {(string)pres.Name} · {(int)pres.Slides.Count} slides · {((int)pres.Saved != 0 ? "salvo" : "ALTERAÇÕES NÃO SALVAS")}";
        });
        try { return work.Wait(1500) ? work.Result : "Office ocupado (não respondeu em 1,5 s); tente de novo em instantes."; } catch { return null; }
    }
    private static string Ps(string s) => s.Replace("'", "''");

    private static Task<DesktopActionResult> PowerShellAsync(string command, CancellationToken ct)
        => RunProcessAsync("powershell.exe", ["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-Command",
            "[Console]::OutputEncoding=[Text.Encoding]::UTF8; $ProgressPreference='SilentlyContinue'; " + command], ct);

    // ------------------------------------------------------------------ área de transferência (thread STA)
    private static T RunSta<T>(Func<T> work)
    {
        T result = default!; Exception? error = null;
        var thread = new Thread(() => { try { result = work(); } catch (Exception ex) { error = ex; } });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); thread.Join();
        if (error is not null) throw error;
        return result;
    }
    private static Task<T> RunStaAsync<T>(Func<T> work) => Task.Run(() => RunSta(work));
    private static string? ClipboardText() => RunSta(() => { for (int i = 0; i < 5; i++) { try { return Clipboard.ContainsText() ? Clipboard.GetText() : ""; } catch (ExternalException) { Thread.Sleep(50); } } return null; });
    private static void SetClipboardText(string text) => RunSta(() =>
    {
        for (int i = 0; i < 5; i++) { try { if (text.Length == 0) Clipboard.Clear(); else Clipboard.SetText(text); return true; } catch (ExternalException) { Thread.Sleep(50); } }
        throw new InvalidOperationException("A área de transferência está em uso por outro programa.");
    });

    /// <summary>Cola o texto exato (rápido, com acentos) e devolve a área de transferência anterior do usuário.</summary>
    private async Task<DesktopActionResult> PasteAsync(DesktopActionRequest r, CancellationToken ct)
    {
        IntPtr h = ResolveWindow(r);
        Activate(h); await WaitForInputFocusAsync(h, ct);
        await PasteIntoForegroundAsync(r.Text ?? throw new InvalidOperationException("Informe text."), ct);
        return new($"Colado em {Label(h)} ({r.Text.Length} caracteres).");
    }
    private static async Task PasteIntoForegroundAsync(string text, CancellationToken ct)
    {
        string? previous = ClipboardText();
        SetClipboardText(text);
        await Task.Delay(60, ct);
        SendKeys("Ctrl+V");
        await Task.Delay(250, ct);
        if (previous is { Length: > 0 }) { try { SetClipboardText(previous); } catch { } }
    }

    // ------------------------------------------------------------------ janelas e processos
    private DesktopActionResult WindowOp(DesktopActionRequest r)
    {
        IntPtr h = ResolveWindow(r); string label = Label(h);
        switch (r.Op ?? "restore")
        {
            case "minimize": ShowWindow(h, 6); break;
            case "maximize": ShowWindow(h, 3); break;
            case "restore": ShowWindow(h, 9); Activate(h); break;
            case "close": PostMessage(h, 0x0010, IntPtr.Zero, IntPtr.Zero); break; // WM_CLOSE: o app pode perguntar se salva
            case "snap_left": Activate(h); SendKeys("Win+Left"); break;
            case "snap_right": Activate(h); SendKeys("Win+Right"); break;
            default: throw new InvalidOperationException("op de window inválida: " + r.Op);
        }
        return new($"{r.Op}: {label}.");
    }

    private DesktopActionResult ProcessOp(DesktopActionRequest r)
    {
        if ((r.Op ?? "list") == "list")
        {
            var list = Process.GetProcesses().Where(p => { try { return p.MainWindowHandle != IntPtr.Zero && (r.Name is null || p.ProcessName.Contains(r.Name, StringComparison.OrdinalIgnoreCase)); } catch { return false; } })
                .Select(p => { using (p) return $"{p.ProcessName} (PID {p.Id}) — {Cap(p.MainWindowTitle, 60)}"; }).Take(60).ToList();
            return new(list.Count == 0 ? "Nenhum processo com janela encontrado." : "Processos com janela:\n" + string.Join("\n", list));
        }
        if (r.Op == "kill")
        {
            string name = r.Name ?? throw new InvalidOperationException("Informe name.");
            int n = 0; foreach (var p in Process.GetProcessesByName(name.Replace(".exe", ""))) using (p) { try { p.Kill(true); n++; } catch { } }
            return new(n == 0 ? $"Nenhum processo \"{name}\" encerrado (inexistente ou sem permissão)." : $"{n} processo(s) \"{name}\" encerrado(s).", Success: n > 0);
        }
        throw new InvalidOperationException("op de process inválida: " + r.Op);
    }

    // ------------------------------------------------------------------ arquivos e pastas
    private static DesktopActionResult FileOp(DesktopActionRequest r)
    {
        string path = Environment.ExpandEnvironmentVariables(r.Path ?? throw new InvalidOperationException("Informe path."));
        string? to = r.To is null ? null : Environment.ExpandEnvironmentVariables(r.To);
        bool overwrite = r.Overwrite == true;
        if (r.Op is "write" or "append" or "move" or "rename" or "delete" && InUse(path) is { } user)
            return new($"O arquivo está aberto em outro programa ({user}); nada foi alterado. Salve/feche-o ou edite na instância aberta (office).",
                Success: false, Status: DesktopStatus.Blocked);
        switch (r.Op ?? "info")
        {
            case "exists": return new(File.Exists(path) ? "Arquivo existe." : Directory.Exists(path) ? "Pasta existe." : "Não existe.");
            case "info":
                if (File.Exists(path)) { var f = new FileInfo(path); return new($"{f.FullName} · {f.Length / 1024.0:N1} KB · modificado {f.LastWriteTime:dd/MM/yyyy HH:mm}"); }
                if (Directory.Exists(path)) { var d = new DirectoryInfo(path); return new($"{d.FullName} · pasta · {d.EnumerateFileSystemInfos().Count()} itens · modificada {d.LastWriteTime:dd/MM/yyyy HH:mm}"); }
                return new("Não existe: " + path, Success: false);
            case "list":
            {
                var d = new DirectoryInfo(path); if (!d.Exists) return new("Pasta não encontrada: " + path, Success: false);
                var items = d.EnumerateFileSystemInfos(r.Pattern ?? "*", r.Recursive == true ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly).Take(300)
                    .Select(i => (i is DirectoryInfo ? "📁 " : "• ") + Path.GetRelativePath(d.FullName, i.FullName) + (i is FileInfo fi ? $" ({fi.Length / 1024.0:N0} KB)" : "")).ToList();
                return new(items.Count == 0 ? "Nenhum item." : $"{d.FullName} ({items.Count}{(items.Count == 300 ? "+" : "")} itens):\n" + string.Join("\n", items));
            }
            case "search":
            {
                var root = new DirectoryInfo(path); if (!root.Exists) return new("Pasta não encontrada: " + path, Success: false);
                var opts = new EnumerationOptions { RecurseSubdirectories = r.Recursive != false, IgnoreInaccessible = true, MatchCasing = MatchCasing.CaseInsensitive };
                var hits = root.EnumerateFiles(r.Pattern ?? "*", opts).Take(200).Select(f => f.FullName).ToList();
                return new(hits.Count == 0 ? "Nada encontrado." : $"{hits.Count}{(hits.Count == 200 ? "+" : "")} arquivo(s):\n" + string.Join("\n", hits));
            }
            case "read":
            {
                if (!File.Exists(path)) return new("Arquivo não encontrado: " + path, Success: false);
                string text = File.ReadAllText(path); return new($"{Path.GetFileName(path)} ({text.Length} caracteres):\n" + Cap(text, 20000));
            }
            case "write":
                if (File.Exists(path) && !overwrite) return new("O arquivo já existe; nada foi sobrescrito. Use append, ou \"overwrite\":true (a versão atual vai para a Lixeira).", Success: false, Status: DesktopStatus.Blocked);
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
                if (File.Exists(path)) RecycleBin(path);
                File.WriteAllText(path, r.Text ?? "", new UTF8Encoding(true));
                return new($"Gravado e conferido: {path} ({new FileInfo(path).Length / 1024.0:N1} KB).");
            case "append": File.AppendAllText(path, r.Text ?? "", new UTF8Encoding(true)); return new($"Acrescentado em {path}.");
            case "mkdir": Directory.CreateDirectory(path); return new("Pasta pronta: " + path);
            case "copy":
            {
                string dest = Destination(path, to, overwrite);
                if (Directory.Exists(path)) CopyDirectory(path, dest); else File.Copy(path, dest, overwrite: false);
                return new($"Copiado: {path} → {dest}");
            }
            case "move": case "rename":
            {
                string dest = Destination(path, r.Op == "rename" && to is not null && !to.Contains('\\') && !to.Contains('/') ? Path.Combine(Path.GetDirectoryName(path)!, to) : to, overwrite);
                if (Directory.Exists(path)) Directory.Move(path, dest); else File.Move(path, dest, overwrite: false);
                return new($"{(r.Op == "rename" ? "Renomeado" : "Movido")}: {path} → {dest}");
            }
            case "delete":
                if (!File.Exists(path) && !Directory.Exists(path)) return new("Não existe: " + path, Success: false);
                RecycleBin(path); return new("Enviado para a Lixeira: " + path);
            default: throw new InvalidOperationException("op de file inválida: " + r.Op);
        }
    }
    private static string Destination(string source, string? to, bool overwrite = false)
    {
        if (string.IsNullOrWhiteSpace(to)) throw new InvalidOperationException("Informe to.");
        string dest = Directory.Exists(to) ? Path.Combine(to, Path.GetFileName(source.TrimEnd('\\', '/'))) : to;
        if (File.Exists(dest) || Directory.Exists(dest))
        {
            if (!overwrite) throw new InvalidOperationException("Já existe no destino (nada foi sobrescrito): " + dest + ". Use \"overwrite\":true para mandar o existente para a Lixeira.");
            if (string.Equals(Path.GetFullPath(dest), Path.GetFullPath(source), StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Origem e destino são o mesmo item.");
            RecycleBin(dest);
        }
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(dest))!);
        return dest;
    }
    private static void CopyDirectory(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var f in Directory.EnumerateFiles(from)) File.Copy(f, Path.Combine(to, Path.GetFileName(f)));
        foreach (var d in Directory.EnumerateDirectories(from)) CopyDirectory(d, Path.Combine(to, Path.GetFileName(d)));
    }
    /// <summary>Nome do motivo se o arquivo estiver bloqueado por outro programa; null se livre ou inexistente.</summary>
    private static string? InUse(string path)
    {
        if (!File.Exists(path)) return null;
        try { using var f = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None); return null; }
        catch (IOException ex) when ((ex.HResult & 0xFFFF) is 32 or 33) { return "em uso"; }
        catch { return null; }
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ShFileOp { public IntPtr Hwnd; public uint Func; public string From; public string? To; public ushort Flags; public bool Aborted; public IntPtr Mappings; public string? Title; }
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern int SHFileOperation(ref ShFileOp op);
    private static void RecycleBin(string path)
    {
        var op = new ShFileOp { Func = 3, From = Path.GetFullPath(path) + "\0\0", Flags = 0x0040 | 0x0010 | 0x0400 | 0x0004 }; // DELETE, ALLOWUNDO|NOCONFIRMATION|NOERRORUI|SILENT
        int code = SHFileOperation(ref op);
        if (code != 0 || op.Aborted) throw new InvalidOperationException($"Não foi possível enviar para a Lixeira (código {code}).");
    }

    private static DesktopActionResult ZipOp(DesktopActionRequest r)
    {
        string path = r.Path ?? throw new InvalidOperationException("Informe path."); string to = r.To ?? throw new InvalidOperationException("Informe to.");
        if (r.Op == "extract") { ZipFile.ExtractToDirectory(path, to, overwriteFiles: false); return new($"Extraído: {path} → {to}"); }
        if (File.Exists(to)) throw new InvalidOperationException("O arquivo zip já existe: " + to);
        if (Directory.Exists(path)) ZipFile.CreateFromDirectory(path, to, CompressionLevel.Optimal, includeBaseDirectory: true);
        else { using var zip = ZipFile.Open(to, ZipArchiveMode.Create); zip.CreateEntryFromFile(path, Path.GetFileName(path), CompressionLevel.Optimal); }
        return new($"Compactado: {to} ({new FileInfo(to).Length / 1024.0:N0} KB)");
    }

    // ------------------------------------------------------------------ registro
    private static DesktopActionResult RegistryOp(DesktopActionRequest r)
    {
        string full = r.Path ?? throw new InvalidOperationException("Informe path (ex.: HKCU\\Software\\...).");
        int slash = full.IndexOf('\\');
        string hive = slash < 0 ? full : full[..slash], sub = slash < 0 ? "" : full[(slash + 1)..];
        RegistryKey root = hive.ToUpperInvariant() switch
        {
            "HKCU" or "HKEY_CURRENT_USER" => Registry.CurrentUser, "HKLM" or "HKEY_LOCAL_MACHINE" => Registry.LocalMachine,
            "HKCR" or "HKEY_CLASSES_ROOT" => Registry.ClassesRoot, "HKU" or "HKEY_USERS" => Registry.Users, _ => throw new InvalidOperationException("Raiz do registro inválida: " + hive),
        };
        switch (r.Op ?? "read")
        {
            case "read":
            {
                using var key = root.OpenSubKey(sub); if (key is null) return new("Chave não existe: " + full, Success: false);
                if (r.Name is not null) return new($"{r.Name} = {key.GetValue(r.Name) ?? "(não existe)"}");
                return new($"{full}:\n" + string.Join("\n", key.GetValueNames().Take(80).Select(n => $"{(n.Length == 0 ? "(Padrão)" : n)} = {key.GetValue(n)}"))
                    + (key.SubKeyCount > 0 ? "\nSubchaves: " + string.Join(", ", key.GetSubKeyNames().Take(80)) : ""));
            }
            case "write":
            {
                using var key = root.CreateSubKey(sub, writable: true);
                var kind = (r.Format ?? "String").ToLowerInvariant() switch { "dword" => RegistryValueKind.DWord, "qword" => RegistryValueKind.QWord, "expandstring" => RegistryValueKind.ExpandString, _ => RegistryValueKind.String };
                object value = kind is RegistryValueKind.DWord ? int.Parse(r.Value ?? "0") : kind is RegistryValueKind.QWord ? long.Parse(r.Value ?? "0") : (object)(r.Value ?? "");
                key.SetValue(r.Name ?? "", value, kind); return new($"Gravado: {full}\\{r.Name} = {r.Value} ({kind}).");
            }
            case "delete":
            {
                using var key = root.OpenSubKey(sub, writable: true); if (key is null) return new("Chave não existe: " + full, Success: false);
                if (r.Name is null) throw new InvalidOperationException("Informe name (o valor a apagar); apagar chaves inteiras exige powershell explícito.");
                key.DeleteValue(r.Name, throwOnMissingValue: false); return new($"Valor apagado: {full}\\{r.Name}");
            }
            default: throw new InvalidOperationException("op de registry inválida: " + r.Op);
        }
    }

    // ------------------------------------------------------------------ sistema
    private async Task<DesktopActionResult> SystemOp(DesktopActionRequest r, CancellationToken ct)
    {
        switch (r.Op ?? "info")
        {
            case "info":
            {
                var drives = DriveInfo.GetDrives().Where(d => d.IsReady).Select(d => $"{d.Name} {d.AvailableFreeSpace / 1e9:N0}/{d.TotalSize / 1e9:N0} GB livres");
                return new($"{Environment.OSVersion} · {Environment.MachineName} · usuário {Environment.UserName} · {Environment.ProcessorCount} núcleos\nDiscos: {string.Join("; ", drives)}");
            }
            case "volume_up": case "volume_down": case "mute":
            {
                ushort vk = r.Op == "mute" ? (ushort)0xad : r.Op == "volume_up" ? (ushort)0xaf : (ushort)0xae;
                int times = r.Op == "mute" ? 1 : Math.Clamp(int.TryParse(r.Value, out int n) ? n : 5, 1, 50);
                for (int i = 0; i < times; i++) { Send([Key(vk, '\0', 0), Key(vk, '\0', 2)]); await Task.Delay(20, ct); }
                return new($"{r.Op} ({times}×).");
            }
            case "lock": LockWorkStation(); return new("Tela bloqueada.");
            case "screenshot":
            {
                var bounds = SystemInformation.VirtualScreen;
                string folder = r.To ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "Screenshots");
                Directory.CreateDirectory(folder);
                string file = Path.Combine(folder, $"Captura {DateTime.Now:yyyy-MM-dd HHmmss}.png");
                using (var bmp = new Bitmap(bounds.Width, bounds.Height)) { using (var g = Graphics.FromImage(bmp)) g.CopyFromScreen(bounds.Left, bounds.Top, 0, 0, bounds.Size); bmp.Save(file, System.Drawing.Imaging.ImageFormat.Png); }
                return new("Captura da tela salva: " + file, file);
            }
            case "notify":
            {
                using var icon = new NotifyIcon { Icon = SystemIcons.Information, Visible = true };
                icon.ShowBalloonTip(5000, "WinStatusBar", r.Text ?? r.Value ?? "", ToolTipIcon.Info);
                await Task.Delay(1500, ct); return new("Notificação exibida.");
            }
            case "battery":
            {
                var p = SystemInformation.PowerStatus;
                return new($"Bateria {p.BatteryLifePercent:P0} · {(p.PowerLineStatus == PowerLineStatus.Online ? "na tomada" : "na bateria")}" + (p.BatteryLifeRemaining > 0 ? $" · {TimeSpan.FromSeconds(p.BatteryLifeRemaining):h\\:mm} restantes" : ""));
            }
            case "wifi": return await RunProcessAsync("netsh", ["wlan", "show", "interfaces"], ct);
            case "ip": return await PowerShellAsync("Get-NetIPAddress -AddressFamily IPv4 | Where-Object { $_.IPAddress -notlike '127.*' } | Format-Table InterfaceAlias,IPAddress -AutoSize | Out-String -Width 200", ct);
            case "printers": return await PowerShellAsync("Get-Printer | Format-Table Name,PrinterStatus,Default -AutoSize | Out-String -Width 200", ct);
            case "apps": return await PowerShellAsync("Get-ItemProperty HKLM:\\Software\\Microsoft\\Windows\\CurrentVersion\\Uninstall\\*,HKLM:\\Software\\WOW6432Node\\Microsoft\\Windows\\CurrentVersion\\Uninstall\\*,HKCU:\\Software\\Microsoft\\Windows\\CurrentVersion\\Uninstall\\* -ErrorAction SilentlyContinue | Where-Object DisplayName" + (r.Value is { } f ? $" | Where-Object DisplayName -like '*{Ps(f)}*'" : "") + " | Sort-Object DisplayName | Select-Object -First 150 DisplayName,DisplayVersion | Format-Table -AutoSize | Out-String -Width 200", ct);
            case "services": return await PowerShellAsync("Get-Service" + (r.Value is { } s ? $" -Name '*{Ps(s)}*'" : "") + " | Select-Object -First 100 Status,Name,DisplayName | Format-Table -AutoSize | Out-String -Width 200", ct);
            case "startup": return await PowerShellAsync("Get-CimInstance Win32_StartupCommand | Format-Table Name,Command,Location -AutoSize | Out-String -Width 250", ct);
            default: throw new InvalidOperationException("op de system inválida: " + r.Op);
        }
    }

    // ------------------------------------------------------------------ Office (COM, no documento aberto ou no arquivo indicado)
    [DllImport("ole32.dll", CharSet = CharSet.Unicode)] private static extern int CLSIDFromProgID(string progId, out Guid clsid);
    [DllImport("oleaut32.dll")] private static extern int GetActiveObject(ref Guid clsid, IntPtr reserved, [MarshalAs(UnmanagedType.IUnknown)] out object obj);
    private static dynamic OfficeApp(string progId, bool create)
    {
        if (CLSIDFromProgID(progId, out var clsid) == 0 && GetActiveObject(ref clsid, IntPtr.Zero, out var running) == 0) return running;
        if (!create) throw new InvalidOperationException(progId + " não está aberto.");
        var type = Type.GetTypeFromProgID(progId) ?? throw new InvalidOperationException(progId.Split('.')[0] + " não está instalado neste PC.");
        dynamic app = Activator.CreateInstance(type)!;
        try { app.Visible = true; } catch { }
        return app;
    }

    private static DesktopActionResult OfficeOp(DesktopActionRequest r)
    {
        string app = (r.Application ?? "word").ToLowerInvariant();
        string op = r.Op ?? "read";
        string? path = r.Path is null ? null : Path.GetFullPath(Environment.ExpandEnvironmentVariables(r.Path));
        string? to = r.To is null ? null : Path.GetFullPath(Environment.ExpandEnvironmentVariables(r.To));
        if (app.Contains("word"))
        {
            dynamic word = OfficeApp("Word.Application", create: true);
            dynamic doc = op == "new" ? word.Documents.Add() : path is not null ? OpenWordDoc(word, path) : word.Documents.Count > 0 ? word.ActiveDocument : word.Documents.Add();
            string name = doc.Name;
            switch (op)
            {
                case "new": return new("Documento novo criado no Word: " + name);
                case "read": { string t = doc.Content.Text; return new($"{name} ({t.Length} caracteres):\n" + Cap(t.Replace("\r", "\n"), 20000)); }
                case "write": doc.Content.Text = r.Text ?? ""; return new($"Texto substituído em {name} ({(r.Text ?? "").Length} caracteres). Use save para gravar.");
                case "append": doc.Content.InsertAfter((r.Text ?? "")); return new($"Texto acrescentado ao fim de {name}. Use save para gravar.");
                case "insert": word.Selection.TypeText(r.Text ?? ""); return new($"Texto inserido no cursor em {name}.");
                case "replace":
                {
                    if (string.IsNullOrEmpty(r.Find)) throw new InvalidOperationException("Informe find.");
                    dynamic range = doc.Content; bool found = range.Find.Execute(r.Find, false, false, false, false, false, true, 1, false, r.Replace ?? "", 2);
                    return new(found ? $"Substituído \"{r.Find}\" por \"{r.Replace}\" em {name}." : $"\"{r.Find}\" não encontrado em {name}.", Success: found);
                }
                case "save": doc.Save(); return new("Salvo: " + (string)doc.FullName);
                case "save_as": { string dest = Free(to ?? throw new InvalidOperationException("Informe to."), r.Overwrite == true); doc.SaveAs2(dest); return Produced("Salvo como", dest); }
                case "export_pdf": { string pdf = Free(to ?? Path.ChangeExtension((string)doc.FullName, ".pdf"), r.Overwrite == true); doc.ExportAsFixedFormat(pdf, 17); return Produced("PDF exportado", pdf); }
                case "close": doc.Close(-1); return new("Fechado (alterações salvas): " + name);
                default: throw new InvalidOperationException("op de Word inválida: " + op);
            }
        }
        if (app.Contains("excel"))
        {
            dynamic excel = OfficeApp("Excel.Application", create: true);
            dynamic wb = op == "new" ? excel.Workbooks.Add() : path is not null ? OpenWorkbook(excel, path) : excel.Workbooks.Count > 0 ? excel.ActiveWorkbook : excel.Workbooks.Add();
            string name = wb.Name;
            dynamic RangeOf()
            {
                string spec = r.Range ?? "A1";
                int bang = spec.IndexOf('!');
                dynamic sheet = bang > 0 ? wb.Worksheets[spec[..bang].Trim('\'')] : wb.ActiveSheet;
                return sheet.Range(bang > 0 ? spec[(bang + 1)..] : spec);
            }
            switch (op)
            {
                case "new": return new("Pasta de trabalho nova no Excel: " + name);
                case "read":
                {
                    dynamic range = r.Range is null ? wb.ActiveSheet.UsedRange : RangeOf();
                    object v = range.Value2;
                    if (v is object[,] grid)
                    {
                        var sb = new StringBuilder();
                        for (int i = grid.GetLowerBound(0); i <= Math.Min(grid.GetUpperBound(0), grid.GetLowerBound(0) + 199); i++)
                        {
                            var row = new List<string>();
                            for (int j = grid.GetLowerBound(1); j <= grid.GetUpperBound(1); j++) row.Add(grid[i, j]?.ToString() ?? "");
                            sb.AppendLine(string.Join(" | ", row));
                        }
                        return new($"{name} · {(string)range.Address}:\n" + Cap(sb.ToString(), 20000));
                    }
                    return new($"{name} · {(string)range.Address} = {v}");
                }
                case "write":
                {
                    var values = r.Values ?? (r.Text is not null ? [[r.Text]] : throw new InvalidOperationException("Informe values ou text."));
                    dynamic start = RangeOf().Cells(1, 1);
                    int rows = values.Length, cols = values.Max(v => v.Length);
                    var grid = new object[rows, cols];
                    for (int i = 0; i < rows; i++) for (int j = 0; j < cols; j++) grid[i, j] = j < values[i].Length ? ExcelValue(values[i][j]) : "";
                    dynamic target = start.Resize(rows, cols); target.Value2 = grid;
                    for (int i = 0; i < rows; i++) for (int j = 0; j < values[i].Length; j++)
                        if (values[i][j].StartsWith('=')) target.Cells(i + 1, j + 1).FormulaLocal = values[i][j]; // =SOMA(...) no idioma do Excel
                    return new($"Gravado em {name} · {(string)target.Address} ({rows}×{cols}). Use save para gravar o arquivo.");
                }
                case "save": wb.Save(); return new("Salvo: " + (string)wb.FullName);
                case "save_as": { string dest = Free(to ?? throw new InvalidOperationException("Informe to."), r.Overwrite == true); wb.SaveAs(dest); return Produced("Salvo como", dest); }
                case "export_pdf": { string pdf = Free(to ?? Path.ChangeExtension((string)wb.FullName, ".pdf"), r.Overwrite == true); wb.ExportAsFixedFormat(0, pdf); return Produced("PDF exportado", pdf); }
                case "close": wb.Close(true); return new("Fechado (alterações salvas): " + name);
                default: throw new InvalidOperationException("op de Excel inválida: " + op);
            }
        }
        if (app.Contains("powerpoint"))
        {
            dynamic ppt = OfficeApp("PowerPoint.Application", create: true);
            dynamic pres = path is not null ? ppt.Presentations.Open(path) : ppt.Presentations.Count > 0 ? ppt.ActivePresentation : throw new InvalidOperationException("Nenhuma apresentação aberta; informe path.");
            string name = pres.Name;
            switch (op)
            {
                case "read":
                {
                    var sb = new StringBuilder();
                    foreach (dynamic slide in pres.Slides)
                    {
                        sb.Append("Slide ").Append((int)slide.SlideIndex).Append(": ");
                        foreach (dynamic shape in slide.Shapes) { try { if ((int)shape.HasTextFrame != 0) sb.Append((string)shape.TextFrame.TextRange.Text).Append(" · "); } catch { } }
                        sb.AppendLine();
                    }
                    return new($"{name}:\n" + Cap(sb.ToString(), 20000));
                }
                case "export_pdf": { string pdf = Free(to ?? Path.ChangeExtension((string)pres.FullName, ".pdf"), r.Overwrite == true); pres.SaveAs(pdf, 32); return Produced("PDF exportado", pdf); }
                case "save": pres.Save(); return new("Salvo: " + name);
                case "close": pres.Save(); pres.Close(); return new("Fechado (salvo): " + name);
                default: throw new InvalidOperationException("op de PowerPoint inválida: " + op);
            }
        }
        if (app.Contains("outlook"))
        {
            if (op != "draft") throw new InvalidOperationException("Outlook: use op draft (cria um rascunho; nunca envia).");
            dynamic outlook = OfficeApp("Outlook.Application", create: true);
            dynamic mail = outlook.CreateItem(0);
            mail.To = r.To ?? ""; mail.Subject = r.Value ?? ""; mail.Body = r.Text ?? "";
            if (r.Path is { } attach && File.Exists(attach)) mail.Attachments.Add(attach);
            mail.Save(); mail.Display();
            return new("Rascunho criado e aberto no Outlook (não enviado). Revise e envie você mesmo.");
        }
        throw new InvalidOperationException("Aplicativo do Office não suportado: " + r.Application + " (use word, excel, powerpoint ou outlook).");
    }
    /// <summary>Destino livre: sem overwrite, falha se existir; com overwrite, o existente vai para a Lixeira.</summary>
    private static string Free(string path, bool overwrite)
    {
        path = Path.GetFullPath(Environment.ExpandEnvironmentVariables(path));
        if (File.Exists(path))
        {
            if (!overwrite) throw new InvalidOperationException("Já existe: " + path + ". Use outro nome ou \"overwrite\":true (o existente vai para a Lixeira).");
            RecycleBin(path);
        }
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        return path;
    }
    private static DesktopActionResult Produced(string what, string path)
    {
        for (int i = 0; i < 20 && !File.Exists(path); i++) Thread.Sleep(150);
        return File.Exists(path) && new FileInfo(path).Length > 0
            ? new($"{what} e conferido: {path} ({new FileInfo(path).Length / 1024.0:N0} KB).")
            : new($"{what}: o Office não confirmou o arquivo {path}.", Success: false, Status: DesktopStatus.Partial);
    }
    private static object ExcelValue(string s) => double.TryParse(s, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var d) && !s.StartsWith('0') || s == "0" ? d : s;
    private static dynamic OpenWordDoc(dynamic word, string path)
    {
        foreach (dynamic d in word.Documents) if (string.Equals((string)d.FullName, path, StringComparison.OrdinalIgnoreCase)) return d;
        if (!File.Exists(path)) throw new InvalidOperationException("Documento não encontrado: " + path);
        return word.Documents.Open(path);
    }
    private static dynamic OpenWorkbook(dynamic excel, string path)
    {
        foreach (dynamic w in excel.Workbooks) if (string.Equals((string)w.FullName, path, StringComparison.OrdinalIgnoreCase)) return w;
        if (!File.Exists(path)) throw new InvalidOperationException("Planilha não encontrada: " + path);
        return excel.Workbooks.Open(path);
    }
}
