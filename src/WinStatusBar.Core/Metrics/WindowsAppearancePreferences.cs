using Microsoft.Win32;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace WinStatusBar.Core.Metrics;

public sealed record WindowsAppearance(bool AppsLight, bool WindowsLight, bool Transparency, bool AccentOnBorders, bool AccentOnTaskbar);

public static class WindowsAppearancePreferences
{
    private const string Personalize = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";
    private const string Dwm = @"Software\Microsoft\Windows\DWM";
    private static bool ReadFlag(RegistryKey? key, string name, bool fallback)
        => key?.GetValue(name) is int value ? value != 0 : fallback;

    public static WindowsAppearance Read()
    {
        using var personalize = Registry.CurrentUser.OpenSubKey(Personalize);
        using var dwm = Registry.CurrentUser.OpenSubKey(Dwm);
        return new(ReadFlag(personalize, "AppsUseLightTheme", true), ReadFlag(personalize, "SystemUsesLightTheme", false),
            ReadFlag(personalize, "EnableTransparency", true), ReadFlag(dwm, "ColorPrevalence", false), ReadFlag(personalize, "ColorPrevalence", false));
    }

    public static void SetMode(bool? appsLight, bool? windowsLight)
    {
        using var key = Registry.CurrentUser.CreateSubKey(Personalize, true);
        if (appsLight is bool apps) key.SetValue("AppsUseLightTheme", apps ? 1 : 0, RegistryValueKind.DWord);
        if (windowsLight is bool windows) key.SetValue("SystemUsesLightTheme", windows ? 1 : 0, RegistryValueKind.DWord);
        Notify();
    }

    public static void SetTransparency(bool enabled) => WriteFlag(Personalize, "EnableTransparency", enabled);
    public static void SetAccentOnBorders(bool enabled) => WriteFlag(Dwm, "ColorPrevalence", enabled);
    public static void SetAccentOnTaskbar(bool enabled) => WriteFlag(Personalize, "ColorPrevalence", enabled);
    private static void WriteFlag(string path, string name, bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(path, true);
        key.SetValue(name, enabled ? 1 : 0, RegistryValueKind.DWord);
        Notify();
    }

    private static void Notify()
    {
        SendMessageTimeout(new IntPtr(0xffff), 0x001A, IntPtr.Zero, "ImmersiveColorSet", 0x0002, 1000, out _);
    }

    public static async Task RestartAsync()
    {
        // No /f and zero timeout: never force-close applications with unsaved documents.
        using var process = Process.Start(new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "shutdown.exe"), "/r /t 0")
        { UseShellExecute = false, CreateNoWindow = true }) ?? throw new InvalidOperationException("Não foi possível iniciar o reinício.");
        await process.WaitForExitAsync();
        if (process.ExitCode != 0) throw new InvalidOperationException("O Windows não permitiu reiniciar este computador.");
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr SendMessageTimeout(IntPtr hwnd, uint message, IntPtr wParam, string lParam, uint flags, uint timeout, out IntPtr result);
}
