using System.Net.NetworkInformation;
using Microsoft.UI.Dispatching;
using Windows.Networking.Connectivity;
using WinStatusBar.Core.Metrics;
using WinStatusBar.Core.Settings;

namespace WinStatusBar.Services;

/// <summary>
/// Teste de velocidade leve (<see cref="SpeedTest.Light"/>, ~16 MB) a cada 10 minutos, com o resultado na linha
/// "Teste automático" do painel de rede. Pula com o teste desligado nas Configurações, a tela apagada, sem internet,
/// em rede limitada (plano de dados, roaming, celular roteado por USB) e durante o teste manual.
/// </summary>
internal static class AutoSpeedTest
{
    public static readonly TimeSpan Interval = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan FirstDelay = TimeSpan.FromMinutes(2);
    private static DispatcherQueueTimer? _timer;
    private static SettingsService? _settings;
    private static CancellationTokenSource? _running;
    private static DateTimeOffset _next;

    public static SpeedTestResult? Last { get; private set; }
    public static DateTimeOffset LastAt { get; private set; }
    public static bool Running => _running is not null;
    /// <summary>Motivo do último teste pulado (null quando o último ciclo testou).</summary>
    public static string? Paused { get; private set; }
    /// <summary>Teste manual em andamento: o automático espera o próximo ciclo.</summary>
    public static bool ManualRunning { get; set; }

    /// <summary>Resultado novo ou mudança de estado, já na thread da interface.</summary>
    public static event Action? Changed;

    public static void Start(SettingsService settings, DispatcherQueue dispatcher)
    {
        if (_timer is not null) return;
        _settings = settings;
        _next = DateTimeOffset.Now + FirstDelay;
        _timer = dispatcher.CreateTimer();
        _timer.Interval = TimeSpan.FromSeconds(30);
        _timer.Tick += async (_, _) => { if (DateTimeOffset.Now >= _next) await RunAsync(); };
        _timer.Start();
    }

    /// <summary>O teste manual tem prioridade: cancela o automático em andamento.</summary>
    public static void CancelCurrent() => _running?.Cancel();

    private static async Task RunAsync()
    {
        if (_running is not null) return;
        _next = DateTimeOffset.Now + Interval;
        if (_settings?.Current.SpeedTestEnabled != true || ManualRunning || WinStatusBar.Core.Presence.Away) return;
        string? limited = await Task.Run(LimitedReason);
        if (limited is not null)
        {
            if (Paused != limited) { Paused = limited; Changed?.Invoke(); }
            return;
        }
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        _running = cts;
        Changed?.Invoke();
        try
        {
            var r = await Task.Run(() => SpeedTest.Light().RunAsync(cts.Token));
            if (r.DownloadMbps is not null || r.UploadMbps is not null)
            {
                Last = r; LastAt = DateTimeOffset.Now; Paused = null;
                App.Log($"Teste automático de velocidade: D {r.DownloadMbps:0.#} · U {r.UploadMbps:0.#} Mbps · ping {r.LatencyMs:0} ms");
            }
        }
        catch (OperationCanceledException) { if (!ManualRunning) _next = DateTimeOffset.Now + TimeSpan.FromMinutes(1); }
        catch (Exception ex) { App.Log("Teste automático de velocidade: " + ex.Message); }
        finally { _running = null; Changed?.Invoke(); }
    }

    /// <summary>Rede em que um teste a cada 10 minutos gastaria o plano de dados (ou sem internet).</summary>
    private static string? LimitedReason()
    {
        try
        {
            var profile = NetworkInformation.GetInternetConnectionProfile();
            if (profile is null || profile.GetNetworkConnectivityLevel() != NetworkConnectivityLevel.InternetAccess)
                return Localization.T("pausado: sem internet");
            var cost = profile.GetConnectionCost();
            if (cost.NetworkCostType is NetworkCostType.Fixed or NetworkCostType.Variable || cost.Roaming || cost.OverDataLimit || cost.ApproachingDataLimit)
                return Localization.T("pausado: rede limitada");
            // Celular roteado por USB (RNDIS) costuma não ser marcado como limitado pelo Windows; conta só se for o adaptador da internet.
            var adapter = profile.NetworkAdapter?.NetworkAdapterId;
            bool tethered = adapter is Guid id && NetworkInterface.GetAllNetworkInterfaces().Any(n =>
                Guid.TryParse(n.Id, out var nid) && nid == id
                && (n.Description.Contains("Remote NDIS", StringComparison.OrdinalIgnoreCase) || n.Description.Contains("Tethering", StringComparison.OrdinalIgnoreCase)));
            if (tethered) return Localization.T("pausado: rede limitada");
            return null;
        }
        catch (Exception ex) { App.Log("Teste automático (custo da rede): " + ex.Message); return null; }
    }
}
