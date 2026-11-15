using Microsoft.Win32;
using WinStatusBar.Core.Interop;
using static WinStatusBar.Core.Interop.NativeMethods;

namespace WinStatusBar.Core.Metrics;

/// <summary>
/// Utilização: contador PDH "% Processor Utility" (mesma métrica do Gerenciador de Tarefas);
/// se indisponível, cálculo próprio com GetSystemTimes.
/// Frequência atual: "Processor Frequency" (nominal, MHz) × "% Processor Performance" / 100.
/// Núcleo mais rápido: o mesmo cálculo por processador lógico, ficando com o maior (o Gerenciador de Tarefas só mostra a média).
/// </summary>
public sealed class CpuMetricsProvider : ISystemMetricsProvider<CpuSample>
{
    private const string Utility = @"\Processor Information(_Total)\% Processor Utility";
    private const string Frequency = @"\Processor Information(_Total)\Processor Frequency";
    private const string Performance = @"\Processor Information(_Total)\% Processor Performance";
    private const string CoreFrequency = @"\Processor Information(*)\Processor Frequency";
    private const string CorePerformance = @"\Processor Information(*)\% Processor Performance";

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
        _pdh.AddCounter(CoreFrequency);
        _pdh.AddCounter(CorePerformance);
        _pdh.Collect();
        SampleSystemTimes();
    }

    public CpuSample Sample()
    {
        double? fromTimes = SampleSystemTimes();
        double? usage = null, ghz = null, peak = null;
        string source = "GetSystemTimes";
        if (_pdh.Collect())
        {
            var util = _pdh.GetValue(Utility, noCap: true);
            if (util is not null) { usage = Math.Clamp(util.Value, 0, 100); source = "PDH % Processor Utility"; }
            var mhz = _pdh.GetValue(Frequency);
            var perf = _pdh.GetValue(Performance, noCap: true);
            if (mhz is > 0 && perf is > 0) ghz = mhz.Value * perf.Value / 100.0 / 1000.0;
            peak = ComputePeakGHz(_pdh.GetArray(CoreFrequency), _pdh.GetArray(CorePerformance), mhz);
        }
        usage ??= fromTimes;
        return new CpuSample(usage, ghz, _name, Environment.ProcessorCount, source, peak);
    }

    /// <summary>Maior frequência entre os processadores lógicos (ignora as instâncias "_Total").
    /// Usa a frequência nominal de cada instância; sem ela, a nominal geral.</summary>
    internal static double? ComputePeakGHz(IEnumerable<KeyValuePair<string, double>> frequencies, IEnumerable<KeyValuePair<string, double>> performances, double? fallbackMhz)
    {
        var nominal = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in frequencies) nominal[f.Key] = f.Value;
        double best = 0;
        foreach (var p in performances)
        {
            if (p.Key.Contains("_Total", StringComparison.OrdinalIgnoreCase) || p.Value <= 0) continue;
            double mhz = nominal.TryGetValue(p.Key, out var n) && n > 0 ? n : fallbackMhz ?? 0;
            best = Math.Max(best, mhz * p.Value / 100.0);
        }
        return best > 0 ? best / 1000.0 : null;
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
