namespace WinStatusBar.Services;

/// <summary>Hotspot móvel do Windows: ligado ou não e quantos dispositivos estão conectados (a leitura fica em HotspotState.Read.cs).</summary>
internal static partial class HotspotState
{
    public sealed record Snapshot(bool On, int Clients, int MaxClients);

    /// <summary>Texto do botão: "Hotspot móvel" desligado; ligado, o número de dispositivos ("Hotspot · 2").</summary>
    public static string Label(Snapshot? s) => s is { On: true } ? $"Hotspot · {s.Clients}" : "Hotspot móvel";

    public static string Tooltip(Snapshot? s) => s is not { On: true }
        ? "Hotspot móvel desligado · abrir as configurações do Windows"
        : s.Clients switch
        {
            0 => "Hotspot móvel ligado · nenhum dispositivo conectado",
            1 => $"Hotspot móvel ligado · 1 dispositivo conectado (máximo {s.MaxClients})",
            _ => $"Hotspot móvel ligado · {s.Clients} dispositivos conectados (máximo {s.MaxClients})",
        };
}
