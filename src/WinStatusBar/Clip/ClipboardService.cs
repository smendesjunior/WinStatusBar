#nullable disable
using System.Runtime.InteropServices.WindowsRuntime;
using System.Security.Cryptography;
using System.Text;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Streams;

namespace WinStatusBar.Clip;

/// <summary>Captura e devolve conteudo pela API oficial do Windows (Windows.ApplicationModel.DataTransfer).</summary>
public class ClipboardService
{
    readonly Store store;
    DateTime ignoreUntil;
    public bool Paused;
    public event Action Changed;

    public ClipboardService(Store s) { store = s; }

    public async Task CaptureAsync()
    {
        if (Paused || DateTime.UtcNow < ignoreUntil) return;
        DataPackageView v = null;
        for (int i = 0; i < 8 && v == null; i++)
        {
            try { v = Clipboard.GetContent(); } catch { await Task.Delay(40); }
        }
        if (v == null) return;

        var fmts = v.AvailableFormats;
        // Conteudo que o app de origem pediu para nao guardar (ex.: gerenciadores de senha), igual ao Windows
        if (fmts.Contains("ExcludeClipboardContentFromMonitorProcessing") || fmts.Contains("Clipboard Viewer Ignore")) return;
        if (fmts.Contains("CanIncludeInClipboardHistory"))
        {
            try
            {
                if (await v.GetDataAsync("CanIncludeInClipboardHistory") is IRandomAccessStream s)
                {
                    var b = await ReadAll(s);
                    if (b.Length >= 4 && BitConverter.ToInt32(b, 0) == 0) return;
                }
            }
            catch { }
        }

        // Cópias do próprio WinStatusBar (ex.: Diagnóstico) entram no histórico; as feitas pelo painel são ignoradas por ignoreUntil.
        string src = Native.ClipboardSourceApp(out bool isSelf, out string process);
        if (isSelf) src = "WinStatusBar";
        // Apps ignorados nas configurações (ex.: gerenciadores de senha): nada deles entra no histórico.
        if (process != null && ClipHost.IsIgnored(process)) return;
        var opt = ClipHost.Options;
        var it = new ClipItem { Source = src, Created = DateTime.Now, LastUsed = DateTime.Now };

        // 1. Arquivos, pastas, atalhos, programas (copiados ou recortados)
        if (opt.ClipboardCaptureFiles && v.Contains(StandardDataFormats.StorageItems))
        {
            List<string> paths = null;
            try { paths = (await v.GetStorageItemsAsync()).Select(x => x.Path).Where(p => !string.IsNullOrEmpty(p)).ToList(); } catch { }
            if (paths is { Count: > 0 })
            {
                it.Kind = "files"; it.Files = paths; it.Text = string.Join("\n", paths); it.Hash = Hash("files:" + it.Text);
            }
        }
        // 2. Texto (com HTML/RTF quando houver)
        if (it.Kind == null && v.Contains(StandardDataFormats.Text))
        {
            string text = null;
            try { text = await v.GetTextAsync(); } catch { }
            if (!string.IsNullOrWhiteSpace(text))
            {
                it.Text = text;
                it.Kind = IsLink(text) ? "link" : "text";
                if (v.Contains(StandardDataFormats.Html)) try { var h = await v.GetHtmlFormatAsync(); if (h != null && h.Length < 3_000_000) it.Html = h; } catch { }
                if (v.Contains(StandardDataFormats.Rtf)) try { var r = await v.GetRtfAsync(); if (r != null && r.Length < 3_000_000) it.Rtf = r; } catch { }
                it.Hash = Hash("text:" + text);
            }
        }
        // 3. Imagem (PNG com transparencia, ou bitmap)
        if (it.Kind == null)
        {
            if (!opt.ClipboardCaptureImages) return;
            byte[] png = null;
            if (fmts.Contains("PNG")) try { if (await v.GetDataAsync("PNG") is IRandomAccessStream s) png = await ReadAll(s); } catch { }
            if (png == null && v.Contains(StandardDataFormats.Bitmap))
                try { var r = await v.GetBitmapAsync(); using var s = await r.OpenReadAsync(); png = await ToPng(s); } catch { }
            if (png == null) return;
            if (opt.ClipboardMaxImageMb > 0 && png.LongLength > opt.ClipboardMaxImageMb * 1048576L) { Log.Write(global::WinStatusBar.Services.Localization.F("Imagem ignorada: {0:0.0} MB (limite {1} MB)", new object[] {png.LongLength / 1048576.0,opt.ClipboardMaxImageMb})); return; }
            try
            {
                using var ms = new InMemoryRandomAccessStream();
                await ms.WriteAsync(png.AsBuffer()); ms.Seek(0);
                var dec = await BitmapDecoder.CreateAsync(ms);
                it.ImgW = (int)dec.PixelWidth; it.ImgH = (int)dec.PixelHeight;
            }
            catch { return; }
            it.Kind = "image";
            it.Hash = Hash(png);
            var dupI = store.FindHash(it.Hash);
            if (dupI != null) { Bump(dupI); return; }
            it.Id = NewId();
            it.ImageFile = it.Id + ".png";
            await File.WriteAllBytesAsync(Path.Combine(store.ImagesDir, it.ImageFile), png);
        }
        if (it.Kind == null) return;

        var dup = store.FindHash(it.Hash);
        if (dup != null) { if (it.Source != null) dup.Source = it.Source; Bump(dup); return; }
        it.Id ??= NewId();
        store.Items.Add(it);
        store.Save(it);
        store.Sort();
        if (it.Kind == "files") BackupFiles(it);
        ClipHost.Prune(); // aplica os limites (quantidade, idade, espaço) a cada novo item
        Changed?.Invoke();
    }

    public void Bump(ClipItem it)
    {
        it.LastUsed = DateTime.Now;
        store.Save(it);
        store.Sort();
        Changed?.Invoke();
    }

    /// <summary>Coloca o item na area de transferencia. plain = so texto, sem formatacao.</summary>
    public async Task<bool> PutAsync(ClipItem it, bool plain)
    {
        var dp = new DataPackage { RequestedOperation = DataPackageOperation.Copy };
        if (it.Kind == "image")
        {
            string path = store.ImagePath(it);
            var f = await StorageFile.GetFileFromPathAsync(path);
            dp.SetBitmap(RandomAccessStreamReference.CreateFromFile(f));
            var ms = new InMemoryRandomAccessStream();
            await ms.WriteAsync((await File.ReadAllBytesAsync(path)).AsBuffer()); ms.Seek(0);
            dp.SetData("PNG", ms);
        }
        else if (it.Kind == "files" && !plain)
        {
            var list = new List<IStorageItem>();
            for (int i = 0; i < it.Files.Count; i++)
            {
                string p = Store.ResolveFile(it, i);
                if (p == null) continue;
                try { list.Add(Directory.Exists(p) ? await StorageFolder.GetFolderFromPathAsync(p) : await StorageFile.GetFileFromPathAsync(p)); } catch { }
            }
            if (list.Count == 0) return false;
            dp.SetStorageItems(list);
        }
        else
        {
            dp.SetText(it.Text ?? "");
            if (!plain)
            {
                if (it.Html != null) dp.SetHtmlFormat(it.Html);
                if (it.Rtf != null) dp.SetRtf(it.Rtf);
            }
        }
        ignoreUntil = DateTime.UtcNow.AddMilliseconds(900);
        Clipboard.SetContent(dp);
        try { Clipboard.Flush(); } catch { }
        return true;
    }

    public async Task PutTextAsync(string s)
    {
        var dp = new DataPackage(); dp.SetText(s);
        Clipboard.SetContent(dp);
        try { Clipboard.Flush(); } catch { }
        await Task.CompletedTask;
    }

    void BackupFiles(ClipItem it)
    {
        var files = it.Files.ToList();
        string dir = Path.Combine(store.FilesDir, it.Id);
        _ = Task.Run(() =>
        {
            try
            {
                long total = files.Where(File.Exists).Sum(f => new FileInfo(f).Length);
                long limit = ClipHost.Options.ClipboardBackupLimitMb * 1048576L;
                if (total == 0 || limit == 0 || total > limit) return;
                var backups = new List<string>();
                Directory.CreateDirectory(dir);
                for (int i = 0; i < files.Count; i++)
                {
                    if (!File.Exists(files[i])) { backups.Add(null); continue; }
                    string sub = Path.Combine(dir, i.ToString());
                    Directory.CreateDirectory(sub);
                    string dst = Path.Combine(sub, Path.GetFileName(files[i]));
                    File.Copy(files[i], dst, true);
                    backups.Add(dst);
                }
                ClipHost.UI.TryEnqueue(() =>
                {
                    if (!store.Items.Contains(it)) { try { Directory.Delete(dir, true); } catch { } return; }
                    it.Backups = backups;
                    store.Save(it);
                });
            }
            catch (Exception ex) { Log.Write("Backup: " + ex.Message); }
        });
    }

    static async Task<byte[]> ReadAll(IRandomAccessStream s)
    {
        s.Seek(0);
        var reader = new DataReader(s.GetInputStreamAt(0));
        uint n = await reader.LoadAsync((uint)s.Size);
        var b = new byte[n];
        reader.ReadBytes(b);
        return b;
    }

    static async Task<byte[]> ToPng(IRandomAccessStream s)
    {
        var dec = await BitmapDecoder.CreateAsync(s);
        var sb = await dec.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);
        var outS = new InMemoryRandomAccessStream();
        var enc = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, outS);
        enc.SetSoftwareBitmap(sb);
        await enc.FlushAsync();
        return await ReadAll(outS);
    }

    static string NewId() => DateTime.Now.ToString("yyyyMMddHHmmssfff") + "_" + Guid.NewGuid().ToString("N")[..6];
    static string Hash(string s) => Hash(Encoding.UTF8.GetBytes(s));
    static string Hash(byte[] b) => Convert.ToHexString(SHA1.HashData(b));

    static bool IsLink(string s)
    {
        s = s.Trim();
        if (s.Contains(' ') || s.Contains('\n') || s.Length > 2048) return false;
        if (s.StartsWith("www.", StringComparison.OrdinalIgnoreCase)) return true;
        return Uri.TryCreate(s, UriKind.Absolute, out var u) && (u.Scheme is "http" or "https" or "ftp" or "mailto");
    }
}
