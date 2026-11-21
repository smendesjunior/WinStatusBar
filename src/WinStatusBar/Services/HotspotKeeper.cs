using Microsoft.UI.Dispatching;
using Windows.Networking.Connectivity;
using Windows.Networking.NetworkOperators;
using WinStatusBar.Core.Settings;

namespace WinStatusBar.Services;

/// <summary>
/// Mantém o hotspot ligado sem deixar a economia de energia do Windows derrubá-lo quando fica sem dispositivos.
/// A cada 30 s, também verifica se precisa ligá-lo após entrada no Windows ou troca de rede.
/// </summary>
internal static class HotspotKeeper
{
    private static DispatcherQueueTimer? _timer;
    private static SettingsService? _settings;
    private static bool _busy;
    private static string? _lastStatus;
    private static string? _lastTimeoutError;

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
        _ = CheckAsync();
    }

    public static async Task CheckAsync()
    {
        if (_busy || _settings is not { } settings) return;
        var options = settings.Current;
        if (!options.KeepHotspotOn && !options.RestoreHotspotIdleTimeout) return;
        _busy = true;
        try
        {
            await ApplyIdleTimeoutAsync(settings);
            if (!settings.Current.KeepHotspotOn) return;

            var profile = NetworkInformation.GetInternetConnectionProfile();
            if (profile is null || profile.GetNetworkConnectivityLevel() == NetworkConnectivityLevel.None) return;
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

    private static async Task ApplyIdleTimeoutAsync(SettingsService settings)
    {
        try
        {
            var options = settings.Current;
            bool enabled = NetworkOperatorTetheringManager.IsNoConnectionsTimeoutEnabled();
            if (options.KeepHotspotOn && enabled)
            {
                await NetworkOperatorTetheringManager.DisableNoConnectionsTimeoutAsync();
                settings.Current.RestoreHotspotIdleTimeout = true;
                settings.Save();
                App.Log("Hotspot sempre ligado: suspensão por inatividade desativada");
            }
            else if (!options.KeepHotspotOn && options.RestoreHotspotIdleTimeout)
            {
                if (!enabled) await NetworkOperatorTetheringManager.EnableNoConnectionsTimeoutAsync();
                settings.Current.RestoreHotspotIdleTimeout = false;
                settings.Save();
            }
            _lastTimeoutError = null;
        }
        catch (Exception ex)
        {
            if (_lastTimeoutError != ex.Message)
                App.Log("Hotspot sempre ligado: falha ao ajustar suspensão por inatividade: " + ex.Message);
            _lastTimeoutError = ex.Message;
        }
    }
}
