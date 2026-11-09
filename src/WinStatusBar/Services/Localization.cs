using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Windows.ApplicationModel.Resources;

namespace WinStatusBar.Services;
internal static class Localization
{
    private static ResourceManager? _manager;
    private static ResourceContext? _context;
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string,string> Cache = new();
    public static string Language { get; private set; } = "pt-BR";
    public static void Initialize(string? preferred)
    {
        string[] supportedPrefixes = ["pt", "en", "es", "ja", "zh", "fr"];
        string candidate = string.IsNullOrWhiteSpace(preferred)
            ? Windows.System.UserProfile.GlobalizationPreferences.Languages.FirstOrDefault(x => supportedPrefixes.Contains(x.Split('-')[0].ToLowerInvariant())) ?? "en-US"
            : preferred;
        string prefix = candidate.Split('-')[0].ToLowerInvariant();
        Language = prefix switch { "pt" => "pt-BR", "en" => "en-US", "es" => "es", "ja" => "ja-JP", "zh" => "zh-Hans", "fr" => "fr-FR", _ => "en-US" };
        _manager = new ResourceManager(); _context = _manager.CreateResourceContext(); _context.QualifierValues["Language"] = Language;
        var culture = CultureInfo.GetCultureInfo(Language);
        CultureInfo.CurrentUICulture = culture; CultureInfo.CurrentCulture = culture;
        CultureInfo.DefaultThreadCurrentUICulture = culture; CultureInfo.DefaultThreadCurrentCulture = culture;
        Cache.Clear();
        WinStatusBar.Core.LocalizedText.Translate = T;
        Microsoft.Windows.Globalization.ApplicationLanguages.PrimaryLanguageOverride = Language;
    }
    public static string T(string source)
    {
        if (string.IsNullOrEmpty(source)) return source;
        if (Cache.TryGetValue(source, out var value)) return value;
        string key = "S" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(source))).Substring(0,16);
        try { value = _manager?.MainResourceMap.GetSubtree("Resources").GetValue(key, _context!).ValueAsString ?? source; }
        catch { value = source; }
        Cache[source] = value; return value;
    }
    public static string F(string source, params object?[] args)
    {
        try { return string.Format(CultureInfo.CurrentCulture, T(source), args); }
        catch (FormatException) { return string.Format(CultureInfo.CurrentCulture, source, args); }
    }
}
