using Microsoft.Win32;
using WinStatusBar.Core.Interop;
using static WinStatusBar.Core.Interop.NativeMethods;

namespace WinStatusBar.Core.Metrics;

/// <summary>
/// Utilização: contador PDH "% Processor Utility" (mesma métrica do Gerenciador de Tarefas);
/// se indisponível, cálculo próprio com GetSystemTimes.
/// Frequência atual: "Processor Frequency" (nominal, MHz) × "% Processor Performance" / 100.
/// </summary>
public sealed class CpuMetricsProvider : ISystemMetricsProvider<CpuSample>
{
    private const string Utility = @"\Processor Information(_Total)\% Processor Utility";
    private const string Frequency = @"\Processor Information(_Total)\Processor Frequency";
    private const string Performance = @"\Processor Information(_Total)\% Processor Performance";

    private readonly PdhQuery _pdh = new();
    private readonly string _name;
    private ulong _lastIdle, _lastKernel, _lastUser;
    private bool _hasTimes;

    public CpuMetricsProvider()
    {
        _name = ReadProcessorName();
        _pdh.AddCounter(Utility);
        _pdh.AddCounter(Frequency);
        _pdh.AddCounter(Performance);
        _pdh.Collect();
        SampleSystemTimes();
    }

    public CpuSample Sample()
    {
        double? fromTimes = SampleSystemTimes();
        double? usage = null, ghz = null;
        string source = "GetSystemTimes";
        if (_pdh.Collect())
        {
            var util = _pdh.GetValue(Utility, noCap: true);
            if (util is not null) { usage = Math.Clamp(util.Value, 0, 100); source = "PDH % Processor Utility"; }
            var mhz = _pdh.GetValue(Frequency);
            var perf = _pdh.GetValue(Performance, noCap: true);
            if (mhz is > 0 && perf is > 0) ghz = mhz.Value * perf.Value / 100.0 / 1000.0;
        }
        usage ??= fromTimes;
        return new CpuSample(usage, ghz, _name, Environment.ProcessorCount, source);
    }

    /// <summary>Uso = (kernel + user − idle) / (kernel + user) entre duas amostras. kernel já inclui idle.</summary>
    internal static double? ComputeUsage(ulong idleDelta, ulong kernelDelta, ulong userDelta)
    {
        ulong total = kernelDelta + userDelta;
        if (total == 0) return null;
        double busy = total - Math.Min(idleDelta, total);
        return Math.Clamp(busy * 100.0 / total, 0, 100);
    }

    private double? SampleSystemTimes()
    {
        if (!GetSystemTimes(out var idle, out var kernel, out var user)) return null;
        double? result = null;
        if (_hasTimes)
            result = ComputeUsage(idle.Value - _lastIdle, kernel.Value - _lastKernel, user.Value - _lastUser);
        _lastIdle = idle.Value; _lastKernel = kernel.Value; _lastUser = user.Value; _hasTimes = true;
        return result;
    }

    private static string ReadProcessorName()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
            return (key?.GetValue("ProcessorNameString") as string)?.Trim() ?? global::WinStatusBar.Core.LocalizedText.T("Processador");
        }
        catch { return global::WinStatusBar.Core.LocalizedText.T("Processador"); }
    }

    public void Dispose() => _pdh.Dispose();
}
