using System.Globalization;
namespace WinStatusBar.Core;
public static class LocalizedText
{
    public static Func<string,string> Translate { get; set; } = text => text;
    public static string T(string source) => Translate(source);
    public static string F(string source, params object?[] args)
    {
        try { return string.Format(CultureInfo.CurrentCulture, T(source), args); }
        catch (FormatException) { return string.Format(CultureInfo.CurrentCulture, source, args); }
    }
}
