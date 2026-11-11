#nullable disable
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;

namespace WinStatusBar.Clip;

/// <summary>Um item do historico. Mesmo formato JSON da versao 1 (compativel com o historico existente).</summary>
[DataContract]
public class ClipItem
{
    [DataMember] public string Id;
    [DataMember] public string Kind;          // text | link | image | files
    [DataMember] public string Text;
    [DataMember] public string Html;
    [DataMember] public string Rtf;
    [DataMember] public List<string> Files;
    [DataMember] public List<string> Backups; // copia de seguranca de cada arquivo (ou null)
    [DataMember] public string ImageFile;
    [DataMember] public int ImgW;
    [DataMember] public int ImgH;
    [DataMember] public string Hash;
    [DataMember] public bool Pinned;
    [DataMember] public DateTime Created;
    [DataMember] public DateTime LastUsed;
    [DataMember] public string Source;
}

/// <summary>Historico salvo em disco: um JSON por item em %APPDATA%\Windows Clipmanager\items. Sem limite.</summary>
public class Store
{
    public string Dir, ItemsDir, ImagesDir, FilesDir;
    public List<ClipItem> Items = new();
    readonly DataContractJsonSerializer ser = new(typeof(ClipItem));

    public Store()
    {
        string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        Dir = Path.Combine(appData, global::WinStatusBar.Services.Localization.T("Windows Clipmanager"));
        string oldDir = Path.Combine(appData, "ClipEterno");
        bool migrated = false;
        if (!Directory.Exists(Dir) && Directory.Exists(oldDir))
        {
            try { Directory.Move(oldDir, Dir); migrated = true; } catch (Exception ex) { Log.Early = "Migracao falhou: " + ex.Message; }
        }
        ItemsDir = Path.Combine(Dir, "items");
        ImagesDir = Path.Combine(Dir, "images");
        FilesDir = Path.Combine(Dir, "files");
        Directory.CreateDirectory(ItemsDir);
        Directory.CreateDirectory(ImagesDir);
        Directory.CreateDirectory(FilesDir);
        foreach (string f in Directory.GetFiles(ItemsDir, "*.json"))
        {
            try { using var fs = File.OpenRead(f); Items.Add((ClipItem)ser.ReadObject(fs)); }
            catch (Exception ex) { Log.Write("Falha ao ler " + f + ": " + ex.Message); }
        }
        if (migrated)
        {
            string oldFiles = Path.Combine(oldDir, "files");
            foreach (var it in Items.Where(i => i.Backups != null))
            {
                bool changed = false;
                for (int i = 0; i < it.Backups.Count; i++)
                    if (it.Backups[i] != null && it.Backups[i].StartsWith(oldFiles, StringComparison.OrdinalIgnoreCase))
                    { it.Backups[i] = FilesDir + it.Backups[i][oldFiles.Length..]; changed = true; }
                if (changed) try { Save(it); } catch { }
            }
        }
        Sort();
    }

    public void Sort() => Items.Sort((a, b) => a.Pinned != b.Pinned ? (a.Pinned ? -1 : 1) : b.LastUsed.CompareTo(a.LastUsed));

    public void Save(ClipItem it)
    {
        string path = Path.Combine(ItemsDir, it.Id + ".json");
        string tmp = path + ".tmp";
        using (var fs = File.Create(tmp)) ser.WriteObject(fs, it);
        File.Move(tmp, path, true);
        ForgetMedia(it); // o item mudou (ex.: as copias de seguranca acabaram de ser gravadas): mede de novo
    }

    // Tamanho em disco da midia de cada item (imagem + copias de arquivos), por Id. Com limite de espaco ligado,
    // cada nova copia media todos os arquivos de todos os itens na thread de UI; agora mede so o que mudou.
    readonly Dictionary<string, long> mediaBytes = new();

    /// <summary>Bytes em disco da imagem e das copias de arquivos do item (sem o texto). Guardado ate o item ser salvo ou apagado.</summary>
    public long MediaBytes(ClipItem it)
    {
        if (it == null) return 0;
        if (it.Id != null) lock (mediaBytes) if (mediaBytes.TryGetValue(it.Id, out long cached)) return cached;
        long n = 0;
        try
        {
            if (ImagePath(it) is { } img && File.Exists(img)) n += new FileInfo(img).Length;
            if (it.Backups != null) n += it.Backups.Where(b => b != null && File.Exists(b)).Sum(b => new FileInfo(b).Length);
        }
        catch { return n; } // falha de leitura: nao guarda um valor parcial
        if (it.Id != null) lock (mediaBytes) mediaBytes[it.Id] = n;
        return n;
    }

    void ForgetMedia(ClipItem it)
    {
        if (it?.Id != null) lock (mediaBytes) mediaBytes.Remove(it.Id);
    }

    public void Delete(ClipItem it)
    {
        ForgetMedia(it);
        Items.Remove(it);
        TryDel(Path.Combine(ItemsDir, it.Id + ".json"));
        if (it.ImageFile != null) TryDel(Path.Combine(ImagesDir, it.ImageFile));
        string bdir = Path.Combine(FilesDir, it.Id);
        try { if (Directory.Exists(bdir)) Directory.Delete(bdir, true); } catch { }
    }

    static void TryDel(string p) { try { if (File.Exists(p)) File.Delete(p); } catch { } }

    public ClipItem FindHash(string h) => Items.FirstOrDefault(i => i.Hash == h);

    public string ImagePath(ClipItem it) => it.ImageFile == null ? null : Path.Combine(ImagesDir, it.ImageFile);

    /// <summary>Caminho utilizavel do arquivo i: o original ou, se ele sumiu, a copia de seguranca.</summary>
    public static string ResolveFile(ClipItem it, int i)
    {
        string p = it.Files[i];
        if (File.Exists(p) || Directory.Exists(p)) return p;
        if (it.Backups != null && i < it.Backups.Count && it.Backups[i] != null && File.Exists(it.Backups[i])) return it.Backups[i];
        return null;
    }
}

public static class Log
{
    public static string Early;
    static string path;
    static readonly object gate = new();
    public static void Init(string dir) { path = Path.Combine(dir, "log.txt"); if (Early != null) Write(Early); }
    public static void Write(string s)
    {
        if (path == null) return;
        try
        {
            lock (gate)
            {
                if (File.Exists(path) && new FileInfo(path).Length > 1024 * 1024) File.Move(path, path + ".old", true);
                File.AppendAllText(path, DateTime.Now.ToString("s") + "  " + s + Environment.NewLine);
            }
        }
        catch { }
    }
}
