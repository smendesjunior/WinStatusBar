#nullable disable
using System.ComponentModel;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace WinStatusBar.Clip;

/// <summary>Um emoji (versao base) com nome em portugues e variantes de tom de pele.</summary>
public class EmojiVM : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler PropertyChanged;
    public string Base { get; init; }
    public string Name { get; init; }
    public string English { get; init; }
    public string[] Keywords { get; init; } = Array.Empty<string>();
    public string Group { get; init; }
    public Dictionary<int, string> Tones;          // 1..5 -> sequencia com o tom
    internal string SearchText;                    // nome + palavras-chave, sem acentos

    public string Display => EmojiCatalog.Tone > 0 && Tones != null && Tones.TryGetValue(EmojiCatalog.Tone, out var v) ? v : Base;
    /// <summary>Recorte da folha pré-desenhada (instantâneo ao rolar); null até a folha ficar pronta.</summary>
    public Microsoft.UI.Xaml.Media.Brush Sprite => EmojiAtlas.BrushFor(Display);
    /// <summary>O texto (fonte) só aparece enquanto não há folha para este emoji.</summary>
    public Microsoft.UI.Xaml.Visibility TextVisibility => Sprite is null ? Microsoft.UI.Xaml.Visibility.Visible : Microsoft.UI.Xaml.Visibility.Collapsed;
    public void RaiseDisplay()
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Display)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Sprite)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(TextVisibility)));
    }
}

/// <summary>Grupo do painel (Usados recentemente, Smileys e emocoes, Pessoas...).</summary>
public class EmojiGroup : List<EmojiVM>
{
    public string Id { get; init; }
    public string Title { get; init; }
    public string Icon { get; init; }
    public EmojiGroup(IEnumerable<EmojiVM> items) : base(items) { }
}

public static class EmojiCatalog
{
    public static readonly List<EmojiVM> All = new();
    public static readonly Dictionary<string, EmojiVM> ByBase = new();
    public static readonly List<(string Id, string Title, string Icon)> GroupInfo = new()
    {
        // Icones Fluent do proprio painel de emojis do Windows (Segoe Fluent Icons)
        ("recent", global::WinStatusBar.Services.Localization.T("Usados recentemente"), ""),
        ("Smileys & Emotion", global::WinStatusBar.Services.Localization.T("Smileys e emoções"), ""),
        ("People & Body", global::WinStatusBar.Services.Localization.T("Pessoas"), ""),
        ("Animals & Nature", global::WinStatusBar.Services.Localization.T("Animais e natureza"), ""),
        ("Food & Drink", global::WinStatusBar.Services.Localization.T("Comidas e bebidas"), ""),
        ("Travel & Places", global::WinStatusBar.Services.Localization.T("Viagens e lugares"), ""),
        ("Activities", global::WinStatusBar.Services.Localization.T("Atividades"), ""),
        ("Objects", global::WinStatusBar.Services.Localization.T("Objetos"), ""),
        ("Symbols", global::WinStatusBar.Services.Localization.T("Símbolos"), ""),
        ("Flags", global::WinStatusBar.Services.Localization.T("Bandeiras"), ""),
    };
    public static int Tone;                                // 0 = padrao (amarelo), 1..5 = tons de pele
    public static readonly List<string> Recent = new();    // bases, mais recente primeiro
    static string settingsPath;
    const int MaxRecent = 36;

    // ------------------------------------------------------------------ carga
    public static void Load(string dataDir, string userDir)
    {
        settingsPath = Path.Combine(userDir, "emojis.json");
        LoadSettings();
        try { LoadCore(dataDir); }
        catch (Exception ex) { Log.Write(global::WinStatusBar.Services.Localization.T("Emojis: ") + ex); }
    }

    static void LoadCore(string dataDir)
    {
        var names = new Dictionary<string, (string tts, string[] kw)>();
        string locale = WinStatusBar.Services.Localization.Language.Split('-')[0];
        LoadAnnotations(Path.Combine(dataDir, "annotations-derived-" + locale + ".json"), "annotationsDerived", names);
        LoadAnnotations(Path.Combine(dataDir, "annotations-" + locale + ".json"), "annotations", names); // os principais prevalecem

        var cover = FontCoverage.Load("Segoe UI Emoji");
        var rows = new List<(string group, int[] cps, double ver, string en)>();
        string group = null;
        var reVer = new Regex(@"E(\d+\.\d+)\s+(.*)$");
        foreach (var line in File.ReadLines(Path.Combine(dataDir, "emoji-test.txt")))
        {
            if (line.StartsWith("# group: ")) { group = line[9..].Trim(); continue; }
            if (line.Length == 0 || line[0] == '#') continue;
            int semi = line.IndexOf(';'), hash = line.IndexOf('#');
            if (semi < 0 || hash < semi) continue;
            if (line[(semi + 1)..hash].Trim() != "fully-qualified" || group == "Component") continue;
            var cps = line[..semi].Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(h => Convert.ToInt32(h, 16)).ToArray();
            var m = reVer.Match(line[(hash + 1)..].Trim());
            if (!m.Success) continue;
            rows.Add((group, cps, double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture), m.Groups[2].Value));
        }

        // Versao maxima de emoji que a fonte deste Windows suporta (pelos emojis de um caractere so)
        double maxVer = 0;
        foreach (var r in rows)
        {
            var core = r.cps.Where(c => c != 0xFE0F).ToArray();
            if (core.Length == 1 && cover.Has(core[0])) maxVer = Math.Max(maxVer, r.ver);
        }
        if (maxVer == 0) maxVer = 15.1;

        bool Supported((string group, int[] cps, double ver, string en) r)
        {
            if (r.ver > maxVer) return false;
            foreach (int c in r.cps)
            {
                if (c is >= 0x1F1E6 and <= 0x1F1FF) return false;   // bandeiras de paises: o Windows nao desenha
                if (c is >= 0xE0020 and <= 0xE007F) return false;   // bandeiras regionais (tags)
                if (c is 0x200D or 0xFE0F or 0x20E3) continue;
                if (!cover.Has(c)) return false;
            }
            return true;
        }
        static bool IsTone(int c) => c is >= 0x1F3FB and <= 0x1F3FF;
        static string Str(IEnumerable<int> cps) => string.Concat(cps.Select(char.ConvertFromUtf32));
        static string Key(IEnumerable<int> cps) => Str(cps.Where(c => c != 0xFE0F && !IsTone(c)));

        var byKey = new Dictionary<string, EmojiVM>();
        foreach (var r in rows.Where(r => !r.cps.Any(IsTone) && Supported(r)))
        {
            string seq = Str(r.cps);
            var (tts, kw) = Lookup(names, seq);
            string name = Capitalize(tts ?? r.en);
            var vm = new EmojiVM { Base = seq, Name = name, English = r.en, Keywords = kw ?? Array.Empty<string>(), Group = r.group };
            vm.SearchText = Normalize(name + " " + string.Join(" ", vm.Keywords) + " " + r.en);
            All.Add(vm);
            ByBase[seq] = vm;
            byKey.TryAdd(Key(r.cps), vm);
        }
        foreach (var r in rows.Where(r => r.cps.Any(IsTone) && Supported(r)))
        {
            var tones = r.cps.Where(IsTone).Distinct().ToArray();
            if (tones.Length != 1) continue; // combinacoes com tons diferentes ficam de fora (como no painel nativo)
            if (!byKey.TryGetValue(Key(r.cps), out var vm)) continue;
            (vm.Tones ??= new())[tones[0] - 0x1F3FB + 1] = Str(r.cps);
        }
        Log.Write(global::WinStatusBar.Services.Localization.F("Emojis carregados: {0} (fonte ate a versao {1})", new object[] {All.Count,maxVer}));
    }

    static void LoadAnnotations(string path, string rootName, Dictionary<string, (string, string[])> into)
    {
        if (!File.Exists(path)) return;
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        if (!doc.RootElement.TryGetProperty(rootName, out var root) || !root.TryGetProperty("annotations", out var ann)) return;
        foreach (var p in ann.EnumerateObject())
        {
            string tts = null; string[] kw = null;
            if (p.Value.TryGetProperty("tts", out var t) && t.GetArrayLength() > 0) tts = t[0].GetString();
            if (p.Value.TryGetProperty("default", out var d)) kw = d.EnumerateArray().Select(x => x.GetString()).ToArray();
            into[p.Name] = (tts, kw);
        }
    }

    static (string, string[]) Lookup(Dictionary<string, (string, string[])> names, string seq)
    {
        if (names.TryGetValue(seq, out var v)) return v;
        if (names.TryGetValue(seq.Replace("️", ""), out v)) return v;
        return (null, null);
    }

    static string Capitalize(string s) => string.IsNullOrEmpty(s) ? s : char.ToUpper(s[0], CultureInfo.CurrentCulture) + s[1..];

    public static string Normalize(string s)
    {
        var sb = new StringBuilder();
        foreach (char c in s.Normalize(NormalizationForm.FormD))
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark) sb.Append(char.ToLowerInvariant(c));
        return sb.ToString();
    }

    // ------------------------------------------------------------------ grupos, pesquisa e recentes
    /// <summary>Todos os desenhos possíveis (base e tons de pele), para a folha pré-desenhada.</summary>
    public static IReadOnlyCollection<string> AllGlyphs()
        => All.Select(e => e.Base).Concat(All.Where(e => e.Tones != null).SelectMany(e => e.Tones.Values)).Distinct().ToList();

    public static List<EmojiGroup> BuildGroups()
    {
        var list = new List<EmojiGroup>();
        var recent = Recent.Select(b => ByBase.GetValueOrDefault(b)).Where(x => x != null).ToList();
        foreach (var (id, title, icon) in GroupInfo)
        {
            var items = id == "recent" ? recent : All.Where(e => e.Group == id).ToList();
            if (items.Count > 0) list.Add(new EmojiGroup(items) { Id = id, Title = title, Icon = icon });
        }
        return list;
    }

    public static List<EmojiGroup> Search(string q)
    {
        var terms = Normalize(q).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var hits = All.Where(e => terms.All(t => e.SearchText.Contains(t)))
                      .OrderBy(e => Normalize(e.Name).StartsWith(terms[0]) ? 0 : 1)
                      .Take(400).ToList();
        return new List<EmojiGroup> { new(hits) { Id = "search", Title = hits.Count == 0 ? global::WinStatusBar.Services.Localization.T("Nenhum emoji encontrado") : global::WinStatusBar.Services.Localization.T("Resultados"), Icon = "🔍" } };
    }

    public static void AddRecent(string baseSeq)
    {
        Recent.Remove(baseSeq);
        Recent.Insert(0, baseSeq);
        if (Recent.Count > MaxRecent) Recent.RemoveRange(MaxRecent, Recent.Count - MaxRecent);
        SaveSettings();
    }

    public static void SetTone(int tone)
    {
        Tone = tone;
        foreach (var e in All.Where(e => e.Tones != null)) e.RaiseDisplay();
        SaveSettings();
    }

    class Settings { public int Tone { get; set; } public List<string> Recent { get; set; } }

    static void LoadSettings()
    {
        try
        {
            if (!File.Exists(settingsPath)) return;
            var s = JsonSerializer.Deserialize<Settings>(File.ReadAllText(settingsPath));
            Tone = s?.Tone ?? 0;
            if (s?.Recent != null) Recent.AddRange(s.Recent);
        }
        catch { }
    }

    static void SaveSettings()
    {
        try { File.WriteAllText(settingsPath, JsonSerializer.Serialize(new Settings { Tone = Tone, Recent = Recent })); } catch { }
    }
}

/// <summary>Quais caracteres a fonte realmente desenha (tabela cmap, formatos 4 e 12).</summary>
public class FontCoverage
{
    readonly List<(int start, int end)> ranges = new();
    public bool Loaded => ranges.Count > 0;

    public bool Has(int cp)
    {
        if (!Loaded) return true;
        int lo = 0, hi = ranges.Count - 1;
        while (lo <= hi)
        {
            int mid = (lo + hi) / 2;
            if (cp < ranges[mid].start) hi = mid - 1;
            else if (cp > ranges[mid].end) lo = mid + 1;
            else return true;
        }
        return false;
    }

    public static FontCoverage Load(string face)
    {
        var fc = new FontCoverage();
        try
        {
            var b = Native.ReadCmap(face);
            if (b == null) return fc;
            int U16(int o) => (b[o] << 8) | b[o + 1];
            long U32(int o) => ((long)b[o] << 24) | ((long)b[o + 1] << 16) | ((long)b[o + 2] << 8) | b[o + 3];
            int n = U16(2);
            int best12 = -1, best4 = -1;
            for (int i = 0; i < n; i++)
            {
                int rec = 4 + i * 8;
                int plat = U16(rec), enc = U16(rec + 2);
                int off = (int)U32(rec + 4);
                int fmt = U16(off);
                if (plat == 3 && enc == 10 && fmt == 12) best12 = off;
                if (plat == 3 && enc == 1 && fmt == 4) best4 = off;
            }
            if (best12 >= 0)
            {
                long groups = U32(best12 + 12);
                for (int g = 0; g < groups; g++)
                {
                    int o = best12 + 16 + g * 12;
                    fc.ranges.Add(((int)U32(o), (int)U32(o + 4)));
                }
            }
            else if (best4 >= 0)
            {
                int segX2 = U16(best4 + 6);
                for (int s = 0; s < segX2 / 2; s++)
                {
                    int end = U16(best4 + 14 + s * 2);
                    int start = U16(best4 + 16 + segX2 + s * 2);
                    if (start != 0xFFFF) fc.ranges.Add((start, end));
                }
            }
            fc.ranges.Sort((x, y) => x.start.CompareTo(y.start));
        }
        catch (Exception ex) { Log.Write("cmap: " + ex.Message); }
        return fc;
    }
}
