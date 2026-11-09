using System.Runtime.InteropServices;

namespace WinStatusBar.Core.Integration;

/// <summary>
/// Distingue a instalação tradicional (Setup.exe, %LOCALAPPDATA%\Programs) da versão MSIX/Microsoft Store.
/// No MSIX a pasta do app é protegida e muda a cada versão; por isso o coletor é exposto por um
/// alias de execução fixo (%LOCALAPPDATA%\Microsoft\WindowsApps\WinStatusBar.Collector.exe).
/// </summary>
public static class PackageContext
{
    public const string CollectorFileName = "WinStatusBar.Collector.exe";
    public const string StartupTaskId = "WinStatusBarStartup";
    private const int AppModelErrorNoPackage = 15700;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetCurrentPackageFullName(ref int packageFullNameLength, char[]? packageFullName);

    public static bool IsPackaged { get; } = Detect();

    private static bool Detect()
    {
        try { int length = 0; return GetCurrentPackageFullName(ref length, null) != AppModelErrorNoPackage; }
        catch { return false; }
    }

    /// <summary>Caminho estável do coletor usado nos hooks e na statusLine do Claude Code.</summary>
    public static string CollectorPath(string appDirectory) => IsPackaged
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WindowsApps", CollectorFileName)
        : Path.Combine(appDirectory, CollectorFileName);
}
