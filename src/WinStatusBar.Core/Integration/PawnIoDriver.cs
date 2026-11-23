using Microsoft.Win32;

namespace WinStatusBar.Core.Integration;

/// <summary>
/// Driver PawnIO (namazso), usado pelo LibreHardwareMonitor e pelo Lenovo Legion Toolkit para ler registros da CPU.
/// Um só por computador: se outro programa já o instalou, ele é reaproveitado; o WinStatusBar nunca o remove.
/// </summary>
public static class PawnIoDriver
{
    public const string WingetId = "namazso.PawnIO";

    public static bool IsInstalled()
    {
        try
        {
            using var service = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\PawnIO");
            return service is not null;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException) { return false; }
    }

    /// <summary>Programa que instalou o driver, pelo registro de desinstalação (ex.: "PawnIO 2.2.0.0").</summary>
    public static string? InstalledVersion()
    {
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            try
            {
                using var root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
                using var uninstall = root.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall");
                if (uninstall is null) continue;
                foreach (string name in uninstall.GetSubKeyNames())
                {
                    using var key = uninstall.OpenSubKey(name);
                    if (key?.GetValue("DisplayName") is string display && display.Equals("PawnIO", StringComparison.OrdinalIgnoreCase))
                        return key.GetValue("DisplayVersion") as string ?? string.Empty;
                }
            }
            catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException) { }
        }
        return null;
    }
}
