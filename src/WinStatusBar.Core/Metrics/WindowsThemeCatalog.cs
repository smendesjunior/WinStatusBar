using Microsoft.Win32;
using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;

namespace WinStatusBar.Core.Metrics;

public sealed record WindowsThemePreview(string Path, string Name, string? Wallpaper, uint Color);

public static class WindowsThemeCatalog
{
    /// <summary>
    /// Temas do Windows: os de fábrica (C:\Windows\Resources\Themes) e os instalados ou salvos pela pessoa
    /// (%LOCALAPPDATA%\Microsoft\Windows\Themes: pacotes da Microsoft Store, .deskthemepack e "Personalizado").
    /// Lido a cada abertura das configurações, então temas novos aparecem sem reiniciar a barra.
    /// </summary>
    public static IReadOnlyList<WindowsThemePreview> Read()
    {
        var result = new List<WindowsThemePreview>();
        string root = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Resources", "Themes");
        if (Directory.Exists(root))
            foreach (string file in Directory.EnumerateFiles(root, "*.theme").OrderBy(x => x))
                if (Load(file, System.IO.Path.GetFileNameWithoutExtension(file) switch
                    { "aero" => "Windows claro", "dark" => "Windows escuro", "spotlight" => "Destaques do Windows", "themeA" => "Brilho", "themeB" => "Movimento capturado", "themeC" => "Nascer do sol", "themeD" => "Fluxo", _ => null }) is { } t)
                    result.Add(t);

        string user = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "Windows", "Themes");
        if (Directory.Exists(user))
        {
            var files = new List<string>();
            try
            {
                foreach (string dir in Directory.EnumerateDirectories(user).OrderBy(x => x))
                    files.AddRange(Directory.EnumerateFiles(dir, "*.theme").OrderBy(x => x).Take(1));
                files.AddRange(Directory.EnumerateFiles(user, "*.theme").OrderBy(x => x));
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            foreach (string file in files)
                if (Load(file, null) is { } t && !result.Any(r => string.Equals(r.Name, t.Name, StringComparison.OrdinalIgnoreCase)))
                    result.Add(t);
        }
        return result;
    }

    private static readonly string[] ImageExt = [".jpg", ".jpeg", ".png", ".bmp"];

    private static WindowsThemePreview? Load(string file, string? fixedName)
    {
        try
        {
            var text = File.ReadAllText(file);
            string name = fixedName ?? DisplayName(text, file);
            string wallpaper = Environment.ExpandEnvironmentVariables(Value(text, "Wallpaper"));
            if (!File.Exists(wallpaper))
            {
                // Pacotes de tema guardam as imagens em DesktopBackground (apresentação de slides) ao lado do .theme.
                string folder = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(file) ?? "", "DesktopBackground");
                wallpaper = Directory.Exists(folder)
                    ? Directory.EnumerateFiles(folder).Where(f => ImageExt.Contains(System.IO.Path.GetExtension(f).ToLowerInvariant())).OrderBy(f => f).FirstOrDefault() ?? ""
                    : "";
            }
            uint.TryParse(Value(text, "ColorizationColor").Replace("0X", "", StringComparison.OrdinalIgnoreCase), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint color);
            return new(file, name, File.Exists(wallpaper) ? wallpaper : null, color == 0 ? 0xff0078d4 : color);
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    /// <summary>Nome do tema: DisplayName do arquivo (quando não é um recurso "@…"), senão o nome da pasta/arquivo.</summary>
    internal static string DisplayName(string text, string file)
    {
        var m = Regex.Match(text, @"^\s*DisplayName\s*=\s*(.+?)\s*$", RegexOptions.Multiline | RegexOptions.IgnoreCase);
        string stem = System.IO.Path.GetFileNameWithoutExtension(file);
        // Tema atual não salvo ("Tema Não Salvo", gravado pelo Windows em codificações variadas) e Custom.theme.
        if (stem.Equals("Custom", StringComparison.OrdinalIgnoreCase) || stem.Equals("config", StringComparison.OrdinalIgnoreCase)
            || m.Success && Regex.IsMatch(m.Groups[1].Value, @"N\S{1,6}o Salvo|Unsaved", RegexOptions.IgnoreCase)) return "Personalizado";
        if (m.Success && !m.Groups[1].Value.StartsWith('@') && !m.Groups[1].Value.Contains('�')) return m.Groups[1].Value;
        string folder = System.IO.Path.GetFileName(System.IO.Path.GetDirectoryName(file) ?? "") ?? "";
        return folder.Equals("Themes", StringComparison.OrdinalIgnoreCase) || folder.Length == 0 ? stem : folder;
    }

    public static bool AutomaticAccent()
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Control Panel\Desktop");
        return key?.GetValue("AutoColorization") is int v && v != 0;
    }

    public static IReadOnlyList<uint> RecentColors()
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\History\Colors");
        var colors = new List<uint>();
        for (int i = 0; i < 5; i++)
            if (key?.GetValue("ColorHistory" + i) is int value)
            {
                uint abgr = unchecked((uint)value);
                colors.Add(0xff000000 | ((abgr & 255) << 16) | (abgr & 0xff00) | ((abgr >> 16) & 255));
            }
        return colors.Distinct().ToArray();
    }

    // Let the native theme handler apply all accent surfaces consistently.
    public static void ApplyAccent(bool automatic, uint? argb = null)
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes");
        var current = key?.GetValue("CurrentTheme") as string;
        if (string.IsNullOrWhiteSpace(current) || !File.Exists(current)) throw new IOException("Tema atual indisponível.");
        string text = File.ReadAllText(current);
        text = SetVisualStyle(text, "AutoColorization", automatic ? "1" : "0");
        if (argb is uint color) text = SetVisualStyle(text, "ColorizationColor", "0X" + color.ToString("X8"));
        string folder = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WinStatusBar", "Themes");
        Directory.CreateDirectory(folder);
        string target = System.IO.Path.Combine(folder, "Accent.theme");
        File.WriteAllText(target, text, System.Text.Encoding.Unicode);
        Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
    }

    internal static string Value(string text, string key) => Regex.Match(text, @"(?im)^" + Regex.Escape(key) + @"\s*=\s*(.*)$").Groups[1].Value.Trim();
    private static string SetVisualStyle(string text, string key, string value)
    {
        var section = Regex.Match(text, @"(?ims)^\[VisualStyles\]\s*\r?\n(?<body>.*?)(?=^\[|\z)");
        if (!section.Success) throw new IOException("Tema sem estilos visuais.");
        string body = section.Groups["body"].Value;
        string pattern = @"(?im)^" + Regex.Escape(key) + @"\s*=.*$";
        body = Regex.IsMatch(body, pattern) ? Regex.Replace(body, pattern, key + "=" + value) : body.TrimEnd() + "\r\n" + key + "=" + value + "\r\n\r\n";
        return text[..section.Groups["body"].Index] + body + text[(section.Groups["body"].Index + section.Groups["body"].Length)..];
    }
}
