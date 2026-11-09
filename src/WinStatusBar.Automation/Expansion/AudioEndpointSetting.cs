using System.Globalization;
using System.Runtime.InteropServices;
using WinStatusBar.Core.Ai;

namespace WinStatusBar.ExecutorExpansion;

/// <summary>Absolute state, not simulated media keys or toggles. Applies to captured default multimedia endpoint.</summary>
public sealed class AudioEndpointSetting(bool mute) : IWindowsSetting
{
    public string Id => mute ? "audio.mute" : "audio.volume";
    public Task<SettingReading> ReadAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        try
        {
            return Task.FromResult(WithEndpoint((endpoint, id) => new SettingReading(Id, Read(endpoint), true,
                "implemented", "Endpoint multimídia padrão consultado por CoreAudio: " + id)));
        }
        catch (COMException)
        { return Task.FromResult(new SettingReading(Id, null, false, "unavailable", "Endpoint de áudio indisponível nesta sessão.")); }
    }
    public Task<DesktopActionResult> ApplyAsync(string value, CancellationToken ct)
    {
        bool desiredMute = false; float desiredVolume = 0;
        if (mute)
        {
            if (!bool.TryParse(value, out desiredMute)) throw new ArgumentException("Mute aceita true/false.");
        }
        else
        {
            if (!float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out float volume) ||
                !float.IsFinite(volume) || volume is < 0 or > 100) throw new ArgumentException("Volume aceita 0 a 100.");
            desiredVolume = volume / 100;
        }
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(WithEndpoint((endpoint, id) =>
        {
            string before = Read(endpoint); Guid context = Guid.Empty;
            ct.ThrowIfCancellationRequested();
            if (mute) Marshal.ThrowExceptionForHR(endpoint.SetMute(desiredMute, ref context));
            else Marshal.ThrowExceptionForHR(endpoint.SetMasterVolumeLevelScalar(desiredVolume, ref context));
            string after = Read(endpoint);
            bool matches = mute ? bool.Parse(after) == desiredMute :
                Math.Abs(float.Parse(after, CultureInfo.InvariantCulture) - desiredVolume * 100) < 0.1;
            return matches ? new DesktopActionResult($"{Id}={after} relido no mesmo endpoint. Anterior={before}.", Method: "CoreAudio/endpoint/verified")
                : new("Áudio alterado, mas valor final não confere. Consulte o estado antes de repetir.", Success: false, Status: DesktopStatus.Partial, Method: "CoreAudio/endpoint");
        }));
    }
    private string Read(IVolume endpoint)
    {
        if (mute) { Marshal.ThrowExceptionForHR(endpoint.GetMute(out bool value)); return value ? "true" : "false"; }
        Marshal.ThrowExceptionForHR(endpoint.GetMasterVolumeLevelScalar(out float scalar));
        return (scalar * 100).ToString("0.###", CultureInfo.InvariantCulture);
    }
    private static T WithEndpoint<T>(Func<IVolume, string, T> work)
    {
        int initialized = CoInitializeEx(0, 0);
        if (initialized < 0 && initialized != unchecked((int)0x80010106)) Marshal.ThrowExceptionForHR(initialized);
        object? enumeratorObject = null, volumeObject = null; IDevice? device = null;
        try
        {
            Guid clsid = new("BCDE0395-E52F-467C-8E3D-C4579291692E"), iid = typeof(IEnumerator).GUID;
            Marshal.ThrowExceptionForHR(CoCreateInstance(ref clsid, 0, 1, ref iid, out enumeratorObject));
            Marshal.ThrowExceptionForHR(((IEnumerator)enumeratorObject).GetDefaultAudioEndpoint(0, 1, out device));
            Marshal.ThrowExceptionForHR(device.GetId(out string id));
            Guid volumeId = typeof(IVolume).GUID;
            Marshal.ThrowExceptionForHR(device.Activate(ref volumeId, 23, 0, out volumeObject));
            return work((IVolume)volumeObject, id);
        }
        finally
        {
            foreach (object? value in new[] { volumeObject, device, enumeratorObject })
                if (value is not null && Marshal.IsComObject(value)) Marshal.ReleaseComObject(value);
            if (initialized >= 0) CoUninitialize();
        }
    }
    [DllImport("ole32.dll")] private static extern int CoInitializeEx(nint reserved, uint mode);
    [DllImport("ole32.dll")] private static extern void CoUninitialize();
    [DllImport("ole32.dll")] private static extern int CoCreateInstance(ref Guid clsid, nint outer, uint context, ref Guid iid, [MarshalAs(UnmanagedType.IUnknown)] out object value);
    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(int flow, uint state, out nint collection);
        [PreserveSig] int GetDefaultAudioEndpoint(int flow, int role, out IDevice device);
        [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IDevice device);
        [PreserveSig] int RegisterEndpointNotificationCallback(nint callback);
        [PreserveSig] int UnregisterEndpointNotificationCallback(nint callback);
    }
    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDevice
    {
        [PreserveSig] int Activate(ref Guid iid, uint context, nint parameters, [MarshalAs(UnmanagedType.IUnknown)] out object value);
        [PreserveSig] int OpenPropertyStore(uint access, out nint store);
        [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
        [PreserveSig] int GetState(out uint state);
    }
    [ComImport, Guid("5CDF2C82-841E-4546-9722-0CF74078229A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IVolume
    {
        [PreserveSig] int RegisterControlChangeNotify(nint callback);
        [PreserveSig] int UnregisterControlChangeNotify(nint callback);
        [PreserveSig] int GetChannelCount(out uint count);
        [PreserveSig] int SetMasterVolumeLevel(float level, ref Guid context);
        [PreserveSig] int SetMasterVolumeLevelScalar(float level, ref Guid context);
        [PreserveSig] int GetMasterVolumeLevel(out float level);
        [PreserveSig] int GetMasterVolumeLevelScalar(out float level);
        [PreserveSig] int SetChannelVolumeLevel(uint channel, float level, ref Guid context);
        [PreserveSig] int SetChannelVolumeLevelScalar(uint channel, float level, ref Guid context);
        [PreserveSig] int GetChannelVolumeLevel(uint channel, out float level);
        [PreserveSig] int GetChannelVolumeLevelScalar(uint channel, out float level);
        [PreserveSig] int SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, ref Guid context);
        [PreserveSig] int GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);
        [PreserveSig] int GetVolumeStepInfo(out uint step, out uint count);
        [PreserveSig] int VolumeStepUp(ref Guid context);
        [PreserveSig] int VolumeStepDown(ref Guid context);
        [PreserveSig] int QueryHardwareSupport(out uint mask);
        [PreserveSig] int GetVolumeRange(out float min, out float max, out float step);
    }
}
