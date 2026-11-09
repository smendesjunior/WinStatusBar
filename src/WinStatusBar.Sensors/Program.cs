using WinStatusBar.Core.Integration;
using WinStatusBar.Core.Ipc;

try
{
    if (args.Length == 2 && args[0] == "--install-updates")
        return WinStatusBar.Core.Metrics.WindowsUpdateInstallation.InstallSelection(args[1]);
    if (args.Length == 3 && args[0] == "--install-update" && int.TryParse(args[2], out int revision))
        return WinStatusBar.Core.Metrics.WindowsUpdateInstallation.Install(args[1], revision);
    if (args.Length != 1) return 2;
    switch (args[0])
    {
        case "--enable": SensorPermissionTask.EnableFromCurrentExecutable(); return 0;
        case "--disable": SensorPermissionTask.Disable(); return 0;
        case "--worker": await CpuSensorPipe.RunWorkerAsync(CancellationToken.None); return 0;
        default: return 2;
    }
}
catch (Exception ex)
{
    try
    {
        if (SensorPermissionTask.IsAdministrator() && Directory.Exists(SensorPermissionTask.ProtectedRoot)
            && (File.GetAttributes(SensorPermissionTask.ProtectedRoot) & FileAttributes.ReparsePoint) == 0)
            File.WriteAllText(Path.Combine(SensorPermissionTask.ProtectedRoot, "last-error.log"), DateTime.Now.ToString("s") + "\n" + ex);
    }
    catch { }
    return 1;
}
