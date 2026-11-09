using Windows.Devices.Bluetooth;
using Windows.Devices.Enumeration;

namespace WinStatusBar.Services;

internal static class BluetoothConnections
{
    internal const string ConnectedProperty = "System.Devices.Aep.IsConnected";

    internal static bool IsConnected(DeviceInformation device) =>
        device.Properties.TryGetValue(ConnectedProperty, out var value) && value is true;

    internal static DeviceWatcher CreateWatcher(bool includeUnpaired)
    {
        var selectors = new List<string>
        {
            BluetoothDevice.GetDeviceSelectorFromPairingState(true),
            BluetoothLEDevice.GetDeviceSelectorFromPairingState(true),
        };
        if (includeUnpaired)
        {
            selectors.Add(BluetoothDevice.GetDeviceSelectorFromPairingState(false));
            selectors.Add(BluetoothLEDevice.GetDeviceSelectorFromPairingState(false));
        }
        return DeviceInformation.CreateWatcher(string.Join(" OR ", selectors.Select(s => "(" + s + ")")),
            [ConnectedProperty], DeviceInformationKind.AssociationEndpoint);
    }
}
