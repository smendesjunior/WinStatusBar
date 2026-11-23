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
        case "--worker":
        {
            // Uso da rede por processo (eventos de rede do Windows), ao lado do sensor de temperatura. Uma cópia só:
            // um segundo auxiliar aberto pelo agendador não cria outra sessão de rastreamento.
            using var netSingleton = new Mutex(true, "WinStatusBar.NetUsageWorker." + SensorPermissionTask.CurrentSid, out bool netFirst);
            NetworkTrace? trace = null;
            Task net = Task.CompletedTask;
            // A barra só confia no canal se o servidor for o auxiliar protegido (conferido do lado dela).
            if (netFirst && SensorPermissionTask.IsAdministrator())
            {
                try { trace = new NetworkTrace(); trace.Start(); net = NetUsagePipe.RunServerAsync(trace.Read, CancellationToken.None); }
                catch { trace?.Dispose(); trace = null; } // sem o rastreamento, o sensor de temperatura continua
            }
            // Sensores de hardware (LibreHardwareMonitor + PawnIO): o canal fica aberto, mas o LibreHardwareMonitor só
            // é carregado quando a barra pede (opção "Sensores de hardware" ligada) e fecha após um minuto sem pedidos.
            using var hwSingleton = new Mutex(true, "WinStatusBar.HardwareWorker." + SensorPermissionTask.CurrentSid, out bool hwFirst);
            HardwareMonitor? hardware = null;
            if (hwFirst && SensorPermissionTask.IsAdministrator())
            {
                hardware = new HardwareMonitor();
                _ = HardwareSensorPipe.RunServerAsync(hardware.Read, CancellationToken.None);
            }
            try { await CpuSensorPipe.RunWorkerAsync(CancellationToken.None); }
            finally { trace?.Dispose(); hardware?.Dispose(); }
            return 0;
        }
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
