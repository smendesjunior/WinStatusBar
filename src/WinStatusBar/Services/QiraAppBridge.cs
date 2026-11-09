using System.Diagnostics;
using System.Runtime.InteropServices;

namespace WinStatusBar.Services;

/// <summary>Qira is an application destination, never an Ollama/OpenAI endpoint.</summary>
internal static class QiraAppBridge
{
    internal static string? LauncherPath
    {
        get
        {
            var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                "Lenovo", "Lenovo Qira", "Lenovo Qira.Launcher.exe");
            return File.Exists(path) ? path : null;
        }
    }
    internal static bool IsInstalled => LauncherPath is not null;

    // O chat da barra usa QiraHubSession (ponte oficial do QuantumAI.Hub); aqui só instalação e janelas.

    internal static List<IntPtr> Windows()
    {
        var ids = new HashSet<uint>();
        foreach (var p in Process.GetProcessesByName("QuantumAI.Hub"))
        {
            using (p)
            {
                try
                {
                    string expected = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                        "Lenovo", "Lenovo Qira", "QuantumApp", "QuantumAI.Hub.exe");
                    if (string.Equals(p.MainModule?.FileName, expected, StringComparison.OrdinalIgnoreCase)) ids.Add((uint)p.Id);
                }
                catch { }
            }
        }
        var result = new List<IntPtr>();
        EnumWindows((h, _) => { GetWindowThreadProcessId(h, out uint pid); if (ids.Contains(pid) && IsWindowVisible(h)) result.Add(h); return true; }, IntPtr.Zero);
        return result;
    }

    private delegate bool WindowCallback(IntPtr h, IntPtr param);
    [DllImport("user32.dll")] private static extern bool EnumWindows(WindowCallback callback, IntPtr param);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
}
