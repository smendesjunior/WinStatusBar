using Microsoft.UI.Dispatching;
using Windows.Networking.Connectivity;
using Windows.Networking.NetworkOperators;
using WinStatusBar.Core.Settings;

namespace WinStatusBar.Services;

/// <summary>
/// "Manter hotspot sempre ligado": a cada 30 s, se a opção estiver ligada e o hotspot do Windows estiver desligado
/// (logon, economia de energia sem dispositivos, troca de rede), liga de novo pela mesma API das Configurações.
/// </summary>
internal static class HotspotKeeper
{
    private static DispatcherQueueTimer? _timer;
    private static SettingsService? _settings;
    private static bool _busy;
    private static string? _lastStatus;

    public static void Start(SettingsService settings, DispatcherQueue dispatcher)
    {
        if (_timer is not null) return;
        _settings = settings;
        _timer = dispatcher.CreateTimer();
        _timer.Interval = TimeSpan.FromSeconds(30);
        _timer.Tick += async (_, _) => await CheckAsync();
        _timer.Start();
        _ = CheckAsync();
    }

    /// <summary>Desligado à mão pela barra: a pessoa quer o hotspot desligado, então a opção sai também.</summary>
    public static void TurnedOffByUser()
    {
        if (_settings?.Current.KeepHotspotOn != true) return;
        _settings.Current.KeepHotspotOn = false;
        _settings.Save();
        App.Log("Hotspot sempre ligado: desativado porque o hotspot foi desligado pela barra");
    }

    public static async Task CheckAsync()
    {
        if (_busy || _settings?.Current.KeepHotspotOn != true) return;
        _busy = true;
        try
        {
            var profile = NetworkInformation.GetInternetConnectionProfile();
            if (profile is null) return;
            var manager = NetworkOperatorTetheringManager.CreateFromConnectionProfile(profile);
            if (manager.TetheringOperationalState != TetheringOperationalState.Off) return;
            var result = await manager.StartTetheringAsync();
            string status = result.Status.ToString();
            if (status != _lastStatus || result.Status == TetheringOperationStatus.Success)
                App.Log($"Hotspot sempre ligado: religar → {status}");
            _lastStatus = status;
        }
        catch (Exception ex)
        {
            if (_lastStatus != ex.Message) App.Log("Hotspot sempre ligado: " + ex.Message);
            _lastStatus = ex.Message;
        }
        finally { _busy = false; }
    }
}
