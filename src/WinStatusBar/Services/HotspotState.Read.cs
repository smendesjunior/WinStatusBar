using Windows.Networking.Connectivity;
using Windows.Networking.NetworkOperators;

namespace WinStatusBar.Services;

internal static partial class HotspotState
{
    /// <summary>
    /// Estado lido do Windows (a mesma informação de Configurações › Rede › Hotspot móvel). null quando o PC não tem
    /// conexão para compartilhar ou o Windows não informa o estado.
    /// </summary>
    public static Snapshot? Read()
    {
        try
        {
            var profile = NetworkInformation.GetInternetConnectionProfile();
            if (profile is null) return null;
            var manager = NetworkOperatorTetheringManager.CreateFromConnectionProfile(profile);
            bool on = manager.TetheringOperationalState == TetheringOperationalState.On;
            return new(on, on ? (int)manager.ClientCount : 0, (int)manager.MaxClientCount);
        }
        catch { return null; }
    }
}
