using System.ComponentModel;
using System.Runtime.InteropServices;

namespace WinStatusBar.Core.Metrics;

public sealed record PowerPreferences(Guid? AcMode, Guid? DcMode, uint? Threshold, uint? Brightness);

public static class WindowsPowerPreferences
{
    public static readonly Guid[] Modes = [new("961cc777-2547-4f9d-8174-7d86181b8a7a"), Guid.Empty, new("ded574b5-45a0-4f42-8737-46345c09c238")];
    private static readonly Guid Group = new("de830923-a562-41af-a086-e3a2c6bad2da");
    private static readonly Guid Threshold = new("e69653ca-cf7f-4f05-aa73-cb833fa90ad4");
    private static readonly Guid Brightness = new("13d09884-f74e-474a-a852-b6bde8ad03a8");
    [DllImport("powrprof.dll")] private static extern uint PowerGetUserConfiguredACPowerMode(out Guid mode);
    [DllImport("powrprof.dll")] private static extern uint PowerGetUserConfiguredDCPowerMode(out Guid mode);
    [DllImport("powrprof.dll")] private static extern uint PowerSetUserConfiguredACPowerMode(in Guid mode);
    [DllImport("powrprof.dll")] private static extern uint PowerSetUserConfiguredDCPowerMode(in Guid mode);
    [DllImport("powrprof.dll")] private static extern uint PowerGetActiveScheme(nint root, out nint scheme);
    [DllImport("kernel32.dll")] private static extern nint LocalFree(nint memory);
    [DllImport("powrprof.dll")] private static extern uint PowerReadDCValueIndex(nint root, in Guid scheme, in Guid group, in Guid setting, out uint value);
    [DllImport("powrprof.dll")] private static extern uint PowerWriteDCValueIndex(nint root, in Guid scheme, in Guid group, in Guid setting, uint value);
    [DllImport("powrprof.dll")] private static extern uint PowerSetActiveScheme(nint root, in Guid scheme);

    public static PowerPreferences Read()
    {
        Guid? ac = null, dc = null;
        try { if (PowerGetUserConfiguredACPowerMode(out var m) == 0) ac = m; } catch (EntryPointNotFoundException) { }
        try { if (PowerGetUserConfiguredDCPowerMode(out var m) == 0) dc = m; } catch (EntryPointNotFoundException) { }
        if (PowerGetActiveScheme(0, out var pointer) != 0) return new(ac, dc, null, null);
        try
        {
            var scheme = Marshal.PtrToStructure<Guid>(pointer);
            uint? threshold = PowerReadDCValueIndex(0, scheme, Group, Threshold, out uint t) == 0 && t <= 100 ? t : null;
            uint? brightness = PowerReadDCValueIndex(0, scheme, Group, Brightness, out uint b) == 0 && b <= 100 ? b : null;
            return new(ac, dc, threshold, brightness);
        }
        finally { LocalFree(pointer); }
    }

    public static void SetMode(bool ac, int index)
    {
        if (index < 0 || index >= Modes.Length) throw new ArgumentOutOfRangeException(nameof(index));
        Check(ac ? PowerSetUserConfiguredACPowerMode(Modes[index]) : PowerSetUserConfiguredDCPowerMode(Modes[index]));
    }

    public static void SetThreshold(uint value) { if (value > 100) throw new ArgumentOutOfRangeException(nameof(value)); Write(Threshold, value); }
    public static void SetDim(bool enabled, uint dimmedBrightness = 70) => Write(Brightness, enabled ? Math.Clamp(dimmedBrightness, 1u, 99u) : 100);
    private static void Write(Guid setting, uint value)
    {
        Check(PowerGetActiveScheme(0, out var pointer));
        try
        {
            var scheme = Marshal.PtrToStructure<Guid>(pointer);
            Check(PowerWriteDCValueIndex(0, scheme, Group, setting, value));
            Check(PowerSetActiveScheme(0, scheme));
        }
        finally { LocalFree(pointer); }
    }
    private static void Check(uint code) { if (code != 0) throw new Win32Exception((int)code); }
}
