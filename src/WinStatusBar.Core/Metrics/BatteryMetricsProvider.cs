using System.Runtime.InteropServices;
using System.Management;

namespace WinStatusBar.Core.Metrics;

public sealed record BatterySample(bool? Present, int? Percent, bool? Plugged, bool Charging,
    bool Saver, TimeSpan? Remaining, int? RateMilliwatts);

public sealed class BatteryMetricsProvider
{
    [StructLayout(LayoutKind.Sequential)]
    private struct PowerStatus
    {
        public byte Ac, Flags, Percent, Saver;
        public uint Remaining, FullTime;
    }

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemPowerStatus(out PowerStatus status);

    public BatterySample Sample()
    {
        if (!GetSystemPowerStatus(out var p)) return new(null, null, null, false, false, null, null);
        // The aggregate report handles machines with more than one battery and supplies nullable values.
        try
        {
            var r = Windows.Devices.Power.Battery.AggregateBattery.GetReport();
            return Decode(p.Ac, p.Flags, p.Percent, p.Remaining, p.Saver == 1,
                r.FullChargeCapacityInMilliwattHours, r.RemainingCapacityInMilliwattHours,
                r.ChargeRateInMilliwatts, null);
        }
        catch { return Decode(p.Ac, p.Flags, p.Percent, p.Remaining, p.Saver == 1, null, null, null, null); }
    }

    internal static BatterySample Decode(byte ac, byte flags, byte percent, uint seconds, bool saver,
        int? full, int? remaining, int? rate, uint? fallbackSeconds)
    {
        bool? present = flags == 255 ? null : (flags & 128) == 0;
        bool? plugged = ac == 255 ? null : ac == 1;
        bool charging = present == true && flags != 255 && (flags & 8) != 0;
        TimeSpan? time = null;
        if (present == true)
        {
            if (charging && rate is > 0 && full is > 0 && remaining is >= 0 && remaining < full)
                time = ChargeTime(full.Value, remaining.Value, rate.Value);
            else if (!charging && plugged == false)
            {
                uint duration = seconds != uint.MaxValue ? seconds : fallbackSeconds ?? uint.MaxValue;
                if (duration != uint.MaxValue) time = TimeSpan.FromSeconds(duration);
            }
        }
        return new(present, present == true && percent <= 100 ? percent : null, plugged, charging, saver, time,
            present == true && rate is not null && rate != int.MinValue ? rate : null);
    }

    /// <summary>
    /// Tempo até 100%. O lítio carrega em potência quase constante até ~80% e depois a potência cai (fase de tensão
    /// constante): a conta linear prometia "14 min" quando faltavam ~35. Até 80% usa a taxa medida; de 80% a 100%,
    /// ~40% dela; acima de 80% a taxa medida já caiu e segue caindo (média ~60% dela no que falta).
    /// </summary>
    internal static TimeSpan ChargeTime(int full, int remaining, int rate)
    {
        double knee = full * 0.8;
        if (remaining >= knee) return TimeSpan.FromHours((full - remaining) / (rate * 0.6));
        return TimeSpan.FromHours((knee - remaining) / rate + (full - knee) / (rate * 0.4));
    }

    /// <summary>Optional firmware information; each battery remains separate so capacities are never mixed.</summary>
    public static List<(string Label, string Value)> ReadDetails()
    {
        var rows = new List<(string, string)>();
        var full = ReadWmi("root/WMI", "BatteryFullChargedCapacity");
        var cycles = ReadWmi("root/WMI", "BatteryCycleCount");
        var batteries = ReadWmi("root/WMI", "BatteryStaticData");
        int batteryIndex = 0;
        foreach (var b in batteries)
        {
            string id = Text(b, "InstanceName");
            string suffix = batteries.Count > 1 ? $" ({++batteryIndex})" : "";
            void Add(string label, string value) { if (!string.IsNullOrWhiteSpace(value)) rows.Add((label + suffix, value)); }
            Add("Fabricante", Text(b, "ManufactureName"));
            Add("Modelo", Text(b, "DeviceName"));
            Add("Número de série", Text(b, "SerialNumber"));
            double design = Number(b, "DesignedCapacity");
            double capacity = full.Where(f => Text(f, "InstanceName") == id).Select(f => Number(f, "FullChargedCapacity")).FirstOrDefault();
            if (design > 0) Add("Capacidade de projeto", $"{design / 1000:0.00} Wh");
            if (capacity > 0) Add("Capacidade de carga completa", $"{capacity / 1000:0.00} Wh");
            if (capacity > 0 && design > 0) Add("Saúde estimada", $"{Math.Min(100, capacity / design * 100):0}%");
            double count = cycles.Where(f => Text(f, "InstanceName") == id).Select(f => Number(f, "CycleCount")).FirstOrDefault();
            if (count > 0) Add("Ciclos de carga", $"{count:0}");
        }
        return rows;
    }

    private static string Text(Dictionary<string, object?> row, string key) => row.GetValueOrDefault(key)?.ToString()?.Trim() ?? "";
    private static double Number(Dictionary<string, object?> row, string key)
        => double.TryParse(Text(row, key), out var n) && n > 0 && n < uint.MaxValue ? n : 0;
    private static List<Dictionary<string, object?>> ReadWmi(string scope, string name)
    {
        var rows = new List<Dictionary<string, object?>>();
        try
        {
            using var query = new ManagementObjectSearcher(scope, "SELECT * FROM " + name);
            query.Options.Timeout = TimeSpan.FromSeconds(3);
            using var found = query.Get();
            foreach (ManagementObject item in found)
                using (item) rows.Add(item.Properties.Cast<PropertyData>().ToDictionary(p => p.Name, p => (object?)p.Value));
        }
        catch { }
        return rows;
    }
}
