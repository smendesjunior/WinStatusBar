using System.Diagnostics;
using System.Globalization;
using System.Xml.Linq;

namespace WinStatusBar.Core.Metrics;

public sealed record WindowsBatterySpan(DateTimeOffset Start, DateTimeOffset End, double Percent, double EndPercent, bool Plugged, string State)
{
    public bool Charging => Plugged && EndPercent > Percent;
    public double LevelAt(DateTimeOffset time) => End <= Start ? Percent : Percent + (EndPercent - Percent) * Math.Clamp((time - Start).TotalSeconds / (End - Start).TotalSeconds, 0, 1);
}

public sealed record WindowsBatteryReport(DateTimeOffset Generated, WindowsBatterySpan[] Spans)
{
    public static WindowsBatteryReport Parse(string xml)
    {
        var doc = XDocument.Parse(xml);
        XNamespace ns = "http://schemas.microsoft.com/battery/2012";
        DateTimeOffset Time(string? s) => DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var d) ? d : default;
        double Number(XElement e, string name) => double.TryParse((string?)e.Attribute(name), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : 0;
        var generated = Time(doc.Root?.Element(ns + "ReportInformation")?.Element(ns + "ScanTime")?.Value);
        var entries = doc.Descendants(ns + "UsageEntry").OrderBy(e => Time((string?)e.Attribute("Timestamp"))).ToArray();
        var spans = new List<WindowsBatterySpan>();
        for (int i = 0; i < entries.Length; i++)
        {
            var e = entries[i];
            string state = (string?)e.Attribute("EntryType") ?? "";
            if (state is not ("Active" or "Suspend" or "ConnectedStandby" or "ScreenOff")) continue;
            var start = Time((string?)e.Attribute("Timestamp"));
            double duration = Number(e, "Duration"); // Windows reports 100-nanosecond ticks, including UINT64 sentinels.
            if (start == default || duration <= 0 || duration > TimeSpan.FromDays(8).Ticks) continue;
            double full = Number(e, "FullChargeCapacity"), capacity = Number(e, "ChargeCapacity");
            if (full <= 0 || capacity < 0 || e.Attribute("ChargeCapacity") is null) continue;
            var end = start.AddTicks((long)duration);
            if (i + 1 < entries.Length)
            {
                var next = Time((string?)entries[i + 1].Attribute("Timestamp"));
                if (next > start && end > next) end = next;
            }
            if (generated != default && end > generated) end = generated;
            if (end <= start) continue;
            double percent = Math.Clamp(capacity / full * 100, 0, 100);
            double endPercent = Math.Clamp((capacity - Number(e, "Discharge")) / full * 100, 0, 100);
            spans.Add(new(start, end, percent, endPercent, (string?)e.Attribute("Ac") == "1", state));
        }
        return new(generated, spans.ToArray());
    }

    public TimeSpan Duration(string state, DateTimeOffset start, DateTimeOffset end)
        => TimeSpan.FromTicks(Spans.Where(s => s.State == state).Sum(s => Math.Max(0, (Min(s.End, end) - Max(s.Start, start)).Ticks)));
    private static DateTimeOffset Min(DateTimeOffset a, DateTimeOffset b) => a < b ? a : b;
    private static DateTimeOffset Max(DateTimeOffset a, DateTimeOffset b) => a > b ? a : b;
}

public static class WindowsBatteryHistory
{
    public static async Task<WindowsBatteryReport> ReadAsync()
    {
        string path = Path.Combine(Path.GetTempPath(), "WinStatusBar-battery-" + Guid.NewGuid().ToString("N") + ".xml");
        try
        {
            var start = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "powercfg.exe"))
                { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (string arg in new[] { "/batteryreport", "/xml", "/duration", "7", "/output", path }) start.ArgumentList.Add(arg);
            using var process = Process.Start(start) ?? throw new IOException("Não foi possível consultar o histórico do Windows.");
            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            try { await process.WaitForExitAsync(timeout.Token); }
            catch (OperationCanceledException) { try { process.Kill(); } catch { } throw new IOException("A consulta do histórico do Windows excedeu o tempo limite."); }
            await Task.WhenAll(output, error);
            if (process.ExitCode != 0 || !File.Exists(path)) throw new IOException("O Windows não disponibilizou o relatório de bateria.");
            return WindowsBatteryReport.Parse(await File.ReadAllTextAsync(path));
        }
        finally { try { File.Delete(path); } catch { } }
    }
}
