using Microsoft.UI.Xaml.Controls;
using Windows.Devices.Enumeration;
using Windows.Foundation;
namespace WinStatusBar.Services;

internal static class ConnectionPicker
{
    public static async Task ShowAsync(Button anchor, string kind, Action<string> status)
    {
        try
        {
            if (kind is "Wi-Fi" or "Bluetooth")
            {
                WinStatusBar.Views.ConnectionsFlyout.Show(anchor, kind == "Wi-Fi");
                return;
            }
            if (kind == "Hotspot móvel")
            {
                await Windows.System.Launcher.LaunchUriAsync(new Uri("ms-settings:network-mobilehotspot"));
                return;
            }

        }
        catch (Exception ex) { status("Não foi possível abrir a seleção: " + ex.Message); App.Log("Seletor de conexão: " + ex.Message); }
    }
}
