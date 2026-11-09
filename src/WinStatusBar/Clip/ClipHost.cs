#nullable disable
using Microsoft.UI.Dispatching;

namespace WinStatusBar.Clip;

/// <summary>
/// Área de transferência e emojis integrados ao WinStatusBar (antigo Windows Clipmanager, agora no mesmo processo):
/// histórico persistente e ilimitado em %APPDATA%\Windows Clipmanager (o mesmo do app antigo, sem migração),
/// Win+V / Win+. / Win+; por hook de teclado, captura pelo WM_CLIPBOARDUPDATE e painel em WinUI 3.
/// </summary>
public static class ClipHost
{
    public static DispatcherQueue UI { get; private set; }
    public static string IconPath => Path.Combine(AppContext.BaseDirectory, "Assets", "Clipboard.ico");
    public static bool Started { get; private set; }
    static Store store;
    static ClipboardService clip;
    static ClipWindow window;
    static KeyboardHook hook;

    public static string DataDir => store?.Dir;
    public static int Count => store?.Items.Count ?? 0;
    public static bool Paused { get => clip?.Paused == true; set { if (clip is not null) clip.Paused = value; Options.ClipboardPaused = value; } }

    /// <summary>Inicia na thread de UI da barra. Falhas não derrubam a barra: ficam no log da área de transferência.</summary>
    public static void Start()
    {
        if (Started) return;
        UI = DispatcherQueue.GetForCurrentThread();
        store = new Store();
        Log.Init(store.Dir);
        EmojiCatalog.Load(Path.Combine(AppContext.BaseDirectory, "Clip", "dados"), store.Dir);
        clip = new ClipboardService(store);
        ClipVM.Store = store;
        window = new ClipWindow(store, clip);
        hook = new KeyboardHook();
        hook.Pressed += tab => UI.TryEnqueue(() => window.TogglePanel(tab));
        hook.Start();
        Started = true;
        clip.Paused = Options.ClipboardPaused;
        Prune();
        Log.Write($"Iniciado dentro do WinStatusBar. {store.Items.Count} itens.");
    }

    /// <summary>Abre (ou fecha, se já estiver aberta) a aba pedida; com <paramref name="anchor"/> o painel abre sob o botão da barra.</summary>
    public static void Toggle(string tab = "clip", Windows.Graphics.PointInt32? anchor = null)
    {
        if (!Started) return;
        window.TogglePanel(tab, anchor);
    }

    public static void Show(string tab = "clip")
    {
        if (!Started) return;
        window.ShowPanel(tab);
    }

    // ------------------------------------------------------------------ configurações e limites
    /// <summary>Configurações atuais da barra (atualizadas por <see cref="ApplySettings"/>).</summary>
    public static Core.Settings.AppSettings Options { get; private set; } = new();
    static string[] ignored = [];

    public static bool Active => Started && Options.ClipboardEnabled;
    public static bool HandlesWinV => Active && Options.ClipboardWinV;
    public static bool HandlesEmojiKeys => Active && Options.ClipboardEmojiKeys;

    public static bool IsIgnored(string process)
        => ignored.Any(n => process.Equals(n, StringComparison.OrdinalIgnoreCase) || process.Equals(Path.GetFileNameWithoutExtension(n), StringComparison.OrdinalIgnoreCase));

    /// <summary>Aplica as configurações: liga/desliga, atalhos, pausa e limites do histórico.</summary>
    public static void ApplySettings(Core.Settings.AppSettings s)
    {
        Options = s;
        ignored = (s.ClipboardIgnoredApps ?? "").Split([',', ';', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (s.ClipboardEnabled && !Started) Start();
        if (clip is not null) clip.Paused = s.ClipboardPaused || !s.ClipboardEnabled;
        if (!s.ClipboardEnabled) window?.HidePanel();
        Prune();
    }

    /// <summary>
    /// Limites do histórico (fixados nunca são apagados): itens não usados há mais de N dias, quantidade máxima
    /// (sai o usado há mais tempo) e espaço em disco de imagens e cópias de arquivos.
    /// </summary>
    public static int Prune()
    {
        if (store is null) return 0;
        var s = Options;
        var victims = new HashSet<ClipItem>();
        var unpinned = store.Items.Where(i => !i.Pinned).OrderBy(i => i.LastUsed).ToList(); // mais antigo primeiro
        if (s.ClipboardMaxAgeDays > 0)
        {
            var cutoff = DateTime.Now.AddDays(-s.ClipboardMaxAgeDays);
            foreach (var i in unpinned.Where(i => i.LastUsed < cutoff)) victims.Add(i);
        }
        if (s.ClipboardMaxItems > 0)
        {
            int excess = store.Items.Count(i => !i.Pinned) - victims.Count - s.ClipboardMaxItems;
            foreach (var i in unpinned.Where(i => !victims.Contains(i)).Take(Math.Max(0, excess))) victims.Add(i);
        }
        if (s.ClipboardMaxStorageMb > 0)
        {
            long limit = s.ClipboardMaxStorageMb * 1048576L, used = StorageBytes(victims);
            foreach (var i in unpinned.Where(i => !victims.Contains(i) && (i.ImageFile != null || i.Backups != null)))
            {
                if (used <= limit) break;
                used -= ItemBytes(i);
                victims.Add(i);
            }
        }
        foreach (var i in victims) store.Delete(i);
        if (victims.Count > 0) { Log.Write($"Limites aplicados: {victims.Count} itens removidos."); window?.Invalidate(); }
        return victims.Count;
    }

    /// <summary>Espaço em disco do histórico (imagens + cópias de arquivos + itens), sem contar <paramref name="except"/>.</summary>
    public static long StorageBytes(ICollection<ClipItem> except = null)
        => store is null ? 0 : store.Items.Where(i => except is null || !except.Contains(i)).Sum(ItemBytes);

    static long ItemBytes(ClipItem i)
    {
        long n = 0;
        try
        {
            if (store.ImagePath(i) is { } img && File.Exists(img)) n += new FileInfo(img).Length;
            if (i.Backups != null) n += i.Backups.Where(b => b != null && File.Exists(b)).Sum(b => new FileInfo(b).Length);
            n += (i.Text?.Length ?? 0) * 2L + (i.Html?.Length ?? 0) + (i.Rtf?.Length ?? 0);
        }
        catch { }
        return n;
    }

    public static (int Items, int Pinned, long Bytes) Stats()
        => store is null ? (0, 0, 0) : (store.Items.Count, store.Items.Count(i => i.Pinned), StorageBytes());

    /// <summary>Apaga todos os itens não fixados (pedido explícito nas configurações).</summary>
    public static int ClearUnpinned()
    {
        if (store is null) return 0;
        var list = store.Items.Where(i => !i.Pinned).ToList();
        foreach (var i in list) store.Delete(i);
        window?.Invalidate();
        return list.Count;
    }

    public static void OpenDataFolder()
    {
        if (store is null) return;
        System.Diagnostics.Process.Start("explorer.exe", "\"" + store.Dir + "\"");
    }
}
