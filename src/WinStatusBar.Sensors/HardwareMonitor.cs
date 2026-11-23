using System.Runtime.InteropServices;
using LibreHardwareMonitor.Hardware;
using WinStatusBar.Core.Integration;
using WinStatusBar.Core.Metrics;

/// <summary>
/// LibreHardwareMonitor no leitor protegido (administrador). Um laço próprio abre e atualiza os sensores em segundo plano;
/// o canal só devolve a última leitura, sem esperar o hardware. Abre quando a barra pede e fecha depois de um minuto sem
/// pedidos (opção desligada ou tela apagada). Na bateria, GPUs dedicadas não são consultadas, para não acordá-las.
/// </summary>
internal sealed class HardwareMonitor : IDisposable
{
    private static readonly TimeSpan Idle = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan RetryOpen = TimeSpan.FromSeconds(30);
    private readonly CancellationTokenSource _stop = new();
    private readonly SemaphoreSlim _wake = new(0);
    private readonly Task _loop;
    private Computer? _computer;
    private volatile HardwareReading? _last;
    private long _lastRequestTicks;

    public HardwareMonitor() => _loop = Task.Run(LoopAsync);

    /// <summary>Última leitura (resposta imediata). Antes da primeira, devolve uma leitura vazia marcada como "iniciando".</summary>
    public HardwareReading Read()
    {
        long previous = Interlocked.Exchange(ref _lastRequestTicks, DateTimeOffset.UtcNow.UtcTicks);
        if (DateTimeOffset.UtcNow.UtcTicks - previous > Idle.Ticks) _wake.Release(); // estava ocioso: acorda o laço
        var now = DateTimeOffset.UtcNow;
        var last = _last;
        return last is not null && last.IsFresh(now) ? last
            : new HardwareReading(null, null, null, null, null, null, null, [], PawnIoDriver.IsInstalled(), now, Starting: true);
    }

    private async Task LoopAsync()
    {
        var ct = _stop.Token;
        DateTimeOffset openFailedAt = DateTimeOffset.MinValue;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                bool wanted = DateTimeOffset.UtcNow.UtcTicks - Interlocked.Read(ref _lastRequestTicks) < Idle.Ticks;
                if (!wanted)
                {
                    CloseComputer();
                    await _wake.WaitAsync(ct);
                    continue;
                }
                if (_computer is null && DateTimeOffset.UtcNow - openFailedAt > RetryOpen)
                {
                    var computer = new Computer { IsCpuEnabled = true, IsGpuEnabled = true, IsMemoryEnabled = true };
                    try { computer.Open(); _computer = computer; }
                    catch (Exception ex) { openFailedAt = DateTimeOffset.UtcNow; Log("Open: " + ex.Message); try { computer.Close(); } catch { } }
                }
                if (_computer is not null)
                {
                    bool onBattery = OnBattery();
                    var values = new List<HardwareSensorValue>();
                    foreach (var hardware in _computer.Hardware)
                    {
                        // Placa dedicada na bateria: consultar a cada segundo a manteria acordada (como o nvidia-smi da barra).
                        if (onBattery && hardware.HardwareType is HardwareType.GpuNvidia or HardwareType.GpuAmd) continue;
                        Collect(hardware, values);
                    }
                    _last = HardwareSensors.Summarize(values, PawnIoDriver.IsInstalled(), DateTimeOffset.UtcNow);
                }
                await Task.Delay(Interval, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch (Exception ex) { Log(ex.Message); try { await Task.Delay(Interval * 5, ct); } catch (OperationCanceledException) { return; } }
        }
    }

    private static void Collect(IHardware hardware, List<HardwareSensorValue> values)
    {
        try { hardware.Update(); } catch { return; } // um componente com falha não derruba os demais
        foreach (var sensor in hardware.Sensors)
            if (sensor.Value is float value)
                values.Add(new(hardware.HardwareType.ToString(), hardware.Name, sensor.SensorType.ToString(), sensor.Name, value, hardware.Identifier.ToString()));
        foreach (var sub in hardware.SubHardware) Collect(sub, values);
    }

    private void CloseComputer()
    {
        if (_computer is null) return;
        try { _computer.Close(); } catch { }
        _computer = null; _last = null;
    }

    private static bool OnBattery() => GetSystemPowerStatus(out var status) && status.ACLineStatus == 0;

    private static void Log(string message)
    {
        try
        {
            // Mesmo cuidado do registro de erros do Program.cs: só como administrador, na pasta protegida, sem seguir links.
            if (SensorPermissionTask.IsAdministrator() && Directory.Exists(SensorPermissionTask.ProtectedRoot)
                && (File.GetAttributes(SensorPermissionTask.ProtectedRoot) & FileAttributes.ReparsePoint) == 0)
                File.AppendAllText(Path.Combine(SensorPermissionTask.ProtectedRoot, "hardware.log"), DateTime.Now.ToString("s") + " " + message + Environment.NewLine);
        }
        catch { }
    }

    public void Dispose()
    {
        _stop.Cancel();
        try { _loop.Wait(TimeSpan.FromSeconds(3)); } catch { }
        CloseComputer();
        _stop.Dispose();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SYSTEM_POWER_STATUS { public byte ACLineStatus, BatteryFlag, BatteryLifePercent, SystemStatusFlag; public int BatteryLifeTime, BatteryFullLifeTime; }
    [DllImport("kernel32.dll")] private static extern bool GetSystemPowerStatus(out SYSTEM_POWER_STATUS status);
}
