#nullable disable
using System.ComponentModel;
using System.Text.RegularExpressions;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Storage;
using Windows.Storage.FileProperties;
using Windows.UI;

namespace WinStatusBar.Clip;

// ====================================================================== categorias
/// <summary>Categoria automatica do item: nome, cor (faixa lateral) e icone (Segoe Fluent Icons).</summary>
public class Category
{
    public string Name, Glyph, Key, Font; public Color Color;   // Font = null -> Segoe Fluent Icons
    SolidColorBrush brush;
    public Category(string name, uint rgb, string glyph, string key, string font = null) { Name = name; Glyph = glyph; Key = key; Font = font; Color = Color.FromArgb(255, (byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb); }
    public SolidColorBrush Brush => brush ??= new SolidColorBrush(Color);
    public const string NumberFont = "Segoe UI Variable Display";

    public static readonly Category Text     = new(global::WinStatusBar.Services.Localization.T("Texto"),             0x9E9E9E, "", "text");
    public static readonly Category Rich     = new(global::WinStatusBar.Services.Localization.T("Texto formatado"),   0x8764B8, "", "text");
    public static readonly Category Link     = new(global::WinStatusBar.Services.Localization.T("Link"),              0x2B88D8, "", "link");
    public static readonly Category Email    = new("E-mail",            0x00B7C3, "", "email");
    public static readonly Category Number   = new(global::WinStatusBar.Services.Localization.T("Número"),            0x6B69D6, "#", "number", NumberFont);
    public static readonly Category Code     = new(global::WinStatusBar.Services.Localization.T("Código"),            0xE3008C, "", "code");
    public static readonly Category Image    = new(global::WinStatusBar.Services.Localization.T("Imagem"),            0x13A10E, "", "image");
    public static readonly Category ImgFile  = new(global::WinStatusBar.Services.Localization.T("Arquivo de imagem"), 0x6BB700, "", "image");
    public static readonly Category File     = new(global::WinStatusBar.Services.Localization.T("Arquivo"),           0xCA5010, "", "file");
    public static readonly Category Files    = new(global::WinStatusBar.Services.Localization.T("Vários arquivos"),   0xCA5010, "", "file");
    public static readonly Category Folder   = new(global::WinStatusBar.Services.Localization.T("Pasta"),             0xE8A100, "", "folder");
    public static readonly Category Program  = new("Programa/atalho",   0xD13438, "", "program");

    /// <summary>Filtros da barra de categorias da area de transferencia (ordem de exibicao).</summary>
    public static readonly (string Key, string Name, string Glyph, uint Rgb, string Font)[] Filters =
    {
        ("all",     global::WinStatusBar.Services.Localization.T("Tudo"),               "", 0, null),
        ("pinned",  global::WinStatusBar.Services.Localization.T("Fixados"),            "", 0, null),
        ("text",    global::WinStatusBar.Services.Localization.T("Texto"),              "", 0x9E9E9E, null),
        ("link",    global::WinStatusBar.Services.Localization.T("Links"),              "", 0x2B88D8, null),
        ("email",   "E-mails",            "", 0x00B7C3, null),
        ("number",  global::WinStatusBar.Services.Localization.T("Números"),            "#",      0x6B69D6, NumberFont),
        ("code",    global::WinStatusBar.Services.Localization.T("Código"),             "", 0xE3008C, null),
        ("color",   global::WinStatusBar.Services.Localization.T("Cores"),              "", 0xF7630C, null),
        ("image",   global::WinStatusBar.Services.Localization.T("Imagens"),            "", 0x13A10E, null),
        ("file",    global::WinStatusBar.Services.Localization.T("Arquivos"),           "", 0xCA5010, null),
        ("folder",  global::WinStatusBar.Services.Localization.T("Pastas"),             "", 0xE8A100, null),
        ("program", global::WinStatusBar.Services.Localization.T("Programas e atalhos"),"", 0xD13438, null),
    };

    public static bool InFilter(ClipItem it, string key) => key switch
    {
        "all" => true,
        "pinned" => it.Pinned,
        _ => Of(it).Key == key,
    };

    static readonly Dictionary<string, Category> cache = new();
    static readonly Regex ReEmail = new(@"^[^@\s]+@[^@\s]+\.[A-Za-z]{2,}$");
    static readonly Regex ReHex = new(@"^#([0-9A-Fa-f]{6}|[0-9A-Fa-f]{3})$");
    static readonly Regex ReNumber = new(@"^[+\-]?[\d\s.,/()\-:%R$€]+$");
    static readonly string[] ProgramExts = { ".exe", ".lnk", ".url", ".msi", ".bat", ".cmd", ".ps1", ".appref-ms" };
    public static readonly string[] ImageExts = { ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".webp", ".tif", ".tiff", ".ico", ".heic", ".heif" };

    public static bool IsImagePath(string p) => ImageExts.Contains(Path.GetExtension(p).ToLowerInvariant());

    public static Category Of(ClipItem it)
    {
        if (cache.TryGetValue(it.Id, out var c)) return c;
        return cache[it.Id] = Classify(it);
    }

    static Category Classify(ClipItem it)
    {
        if (it.Kind == "image") return Image;
        if (it.Kind == "files")
        {
            if (it.Files == null || it.Files.Count == 0) return File;
            if (it.Files.Count > 1) return Files;
            string p = it.Files[0];
            string ext = Path.GetExtension(p).ToLowerInvariant();
            if (ProgramExts.Contains(ext)) return Program;
            if (IsImagePath(p)) return ImgFile;
            if (Directory.Exists(p) || (ext == "" && !System.IO.File.Exists(p))) return Folder;
            return File;
        }
        if (it.Kind == "link") return Link;
        string s = (it.Text ?? "").Trim();
        if (ReHex.IsMatch(s))
        {
            string h = s[1..];
            if (h.Length == 3) h = $"{h[0]}{h[0]}{h[1]}{h[1]}{h[2]}{h[2]}";
            return new Category(global::WinStatusBar.Services.Localization.T("Cor ") + s.ToUpperInvariant(), Convert.ToUInt32(h, 16), "", "color");
        }
        if (ReEmail.IsMatch(s)) return Email;
        if (s.Length <= 40 && s.Any(char.IsDigit) && ReNumber.IsMatch(s)) return Number;
        if (LooksLikeCode(s)) return Code;
        if (it.Html != null || it.Rtf != null) return Rich;
        return Text;
    }

    static bool LooksLikeCode(string s)
    {
        if (!s.Contains('\n') && s.Length < 30) return false;
        int sig = s.Count(ch => ch is '{' or '}' or ';' or '=' or '<' or '>');
        string l = s.TrimStart().ToLowerInvariant();
        bool kw = new[] { "function", "def ", "select ", "import ", "using ", "<?xml", "<!doctype", "$", "const ", "var ", "public ", "#include" }.Any(l.StartsWith);
        return kw || sig >= Math.Max(4, s.Length / 25);
    }
}

// ====================================================================== lista do historico
public interface IClipActions
{
    void ShowItemMenu(ClipVM vm, FrameworkElement anchor);
    void TogglePin(ClipVM vm);
}

/// <summary>Cabecalho de secao ("Fixados" / "Recentes").</summary>
public class SectionHeader
{
    public string Title { get; }
    public string Glyph { get; }
    public SectionHeader(string title, string glyph) { Title = title; Glyph = glyph; }
}

public class ClipVM : INotifyPropertyChanged
{
    public static IClipActions Actions;
    public static Store Store;
    public event PropertyChangedEventHandler PropertyChanged;
    public ClipItem Item { get; }
    public Category Cat { get; }

    public ClipVM(ClipItem it) { Item = it; Cat = Category.Of(it); }

    /// <summary>Mesma linguagem visual da barra: ícone e faixa na cor de destaque; só a categoria "Cor" mostra a própria cor.</summary>
    public Brush CategoryBrush => Cat.Key == "color" ? Cat.Brush : WinStatusBar.Services.ThemeBrushes.Get("AccentTextFillColorPrimaryBrush");
    public string CategoryGlyph => Cat.Glyph;
    public string CategoryName => Cat.Name;
    static FontFamily iconFont, numberFont;
    public FontFamily CategoryFont => Cat.Font != null
        ? (numberFont ??= new FontFamily(Cat.Font))
        : (iconFont ??= new FontFamily("Segoe Fluent Icons"));

    // Borda de foco arredondada (desenhada no proprio card)
    bool focused;
    public bool Focused
    {
        get => focused;
        set { if (focused == value) return; focused = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(FocusVisibility))); }
    }
    public Visibility FocusVisibility => focused ? Visibility.Visible : Visibility.Collapsed;

    // Realce do mouse: 0 = normal, 1 = mouse em cima, 0.6 = pressionado
    double hover;
    public double HoverOpacity => hover;
    void SetHover(double v) { if (hover == v) return; hover = v; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HoverOpacity))); }
    public void PointerIn(object s, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e) => SetHover(1);
    public void PointerOut(object s, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e) => SetHover(0);
    public void PointerDown(object s, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e) => SetHover(0.6);
    public void PointerUp(object s, Microsoft.UI.Xaml.Input.PointerRoutedEventArgs e) => SetHover(1);

    public string PreviewText
    {
        get
        {
            if (Item.Kind is "image" or "files") return "";
            string t = Item.Text ?? "";
            if (t.Length > 1000) t = t[..1000];
            // Chaves e tokens aparecem mascarados; colar continua entregando o texto inteiro.
            return global::WinStatusBar.Core.Clip.SecretMask.Mask(t.Replace("\t", "    ").TrimStart());
        }
    }

    bool SingleImageFile => Item.Kind == "files" && Item.Files?.Count == 1 && Category.IsImagePath(Item.Files[0]);
    public Visibility TextVisibility => Item.Kind is "image" or "files" ? Visibility.Collapsed : Visibility.Visible;
    public Visibility ImageVisibility => Item.Kind == "image" || SingleImageFile ? Visibility.Visible : Visibility.Collapsed;
    public Visibility FilesVisibility => Item.Kind == "files" ? Visibility.Visible : Visibility.Collapsed;

    // Miniatura (carregada sob demanda, so quando o card aparece na tela)
    BitmapImage thumb; bool thumbLoading;
    public BitmapImage Thumbnail
    {
        get
        {
            if (thumb == null && !thumbLoading && ImageVisibility == Visibility.Visible) LoadThumb();
            return thumb;
        }
    }
    async void LoadThumb()
    {
        thumbLoading = true;
        try
        {
            string path = Item.Kind == "image" ? Store.ImagePath(Item) : Store.ResolveFile(Item, 0);
            if (path == null) return;
            var bmp = new BitmapImage { DecodePixelWidth = 280 };
            using var fs = System.IO.File.OpenRead(path);
            await bmp.SetSourceAsync(fs.AsRandomAccessStream());
            thumb = bmp;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Thumbnail)));
        }
        catch { }
    }

    List<FileRow> rows;
    public List<FileRow> FileRows
    {
        get
        {
            if (rows != null || Item.Kind != "files") return rows ??= new();
            rows = new();
            int n = Item.Files.Count;
            for (int i = 0; i < Math.Min(n, 3); i++) rows.Add(new FileRow(Item, i));
            if (n > 3) rows.Add(new FileRow($"+ {n - 3} itens"));
            return rows;
        }
    }

    public string PinGlyph => Item.Pinned ? "" : "";
    public string PinTip => Item.Pinned ? global::WinStatusBar.Services.Localization.T("Desafixar") : global::WinStatusBar.Services.Localization.T("Fixar");
    public void RaisePin()
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(PinGlyph)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(PinTip)));
    }

    public void MoreClick(object sender, RoutedEventArgs e) => Actions?.ShowItemMenu(this, (FrameworkElement)sender);
    public void PinClick(object sender, RoutedEventArgs e) => Actions?.TogglePin(this);
}

/// <summary>Uma linha da lista de arquivos de um card (icone real do Windows + nome + estado).</summary>
public class FileRow : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler PropertyChanged;
    readonly string path;
    public string Name { get; }
    public string Status { get; } = "";
    public double NameOpacity { get; } = 1;

    public FileRow(string moreText) { Name = moreText; NameOpacity = 0.7; }

    public FileRow(ClipItem it, int i)
    {
        string orig = it.Files[i];
        Name = Path.GetFileName(orig.TrimEnd('\\'));
        if (string.IsNullOrEmpty(Name)) Name = orig;
        path = Store.ResolveFile(it, i);
        if (path == null) { Status = global::WinStatusBar.Services.Localization.T("não encontrado"); NameOpacity = 0.6; }
        else if (!string.Equals(path, orig, StringComparison.OrdinalIgnoreCase)) Status = global::WinStatusBar.Services.Localization.T("cópia salva");
    }

    BitmapImage icon; bool loading;
    public BitmapImage Icon
    {
        get { if (icon == null && !loading && path != null) Load(); return icon; }
    }
    async void Load()
    {
        loading = true;
        try
        {
            StorageItemThumbnail t = Directory.Exists(path)
                ? await (await StorageFolder.GetFolderFromPathAsync(path)).GetThumbnailAsync(ThumbnailMode.SingleItem, 32)
                : await (await StorageFile.GetFileFromPathAsync(path)).GetThumbnailAsync(ThumbnailMode.SingleItem, 32, ThumbnailOptions.UseCurrentScale);
            var b = new BitmapImage();
            await b.SetSourceAsync(t);
            icon = b;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Icon)));
        }
        catch { }
    }
}

public partial class ClipTemplateSelector : DataTemplateSelector
{
    public DataTemplate Card { get; set; }
    public DataTemplate Header { get; set; }
    protected override DataTemplate SelectTemplateCore(object item) => item is SectionHeader ? Header : Card;
    protected override DataTemplate SelectTemplateCore(object item, DependencyObject container) => SelectTemplateCore(item);
}
