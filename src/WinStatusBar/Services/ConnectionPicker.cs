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
                // Menu na própria barra (ligar, conexão, nome, senha, banda, QR); o link do Windows fica dentro dele.
                WinStatusBar.Views.HotspotFlyout.Show(anchor, () => status(""));
                await Task.CompletedTask;
                return;
            }

        }
        catch (Exception ex) { status("Não foi possível abrir a seleção: " + ex.Message); App.Log("Seletor de conexão: " + ex.Message); }
    }
}
