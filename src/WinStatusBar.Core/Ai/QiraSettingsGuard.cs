using System.Text.Json;
using System.Text.Json.Nodes;

namespace WinStatusBar.Core.Ai;

/// <summary>
/// O Qira 1.4 não grava em disco as preferências salvas em Configurações (idioma, voz, opções de inicialização…):
/// ficam só na memória do Hub e voltam ao padrão de fábrica quando ele reabre. A barra guarda uma cópia das escolhas
/// da pessoa e, se o Qira reaparecer com o padrão, regrava pela API oficial dele (Settings/SaveConfig).
/// Credenciais (grupo WakeWordCredentials) nunca são copiadas.
/// </summary>
public static class QiraSettingsGuard
{
    private static readonly string[] Excluded = ["WakeWordCredentials"];

    /// <summary>Padrões de fábrica observados no Qira 1.4 (generalData).</summary>
    public static readonly IReadOnlyDictionary<string, string> FactoryGeneral = new Dictionary<string, string>
    {
        ["language"] = "en-US", ["saveAllRecordingsToNotes"] = "true", ["promptBar"] = "true", ["wakeWord"] = "false",
        ["voiceTone"] = "AvaNeural", ["interactionMode"] = "true", ["alwaysOnTop"] = "false",
    };

    /// <summary>Grupos de GetConfig.Data ({grupo: [{Key, Value, LocalType}]}) sem os grupos excluídos.</summary>
    public static JsonObject? Groups(string? getConfigBody)
    {
        if (string.IsNullOrWhiteSpace(getConfigBody)) return null;
        try
        {
            var data = JsonNode.Parse(getConfigBody)?["Data"] as JsonObject;
            if (data is null || data.Count == 0) return null;
            var copy = new JsonObject();
            foreach (var (name, items) in data)
                if (!Excluded.Contains(name) && items is JsonArray array) copy[name] = array.DeepClone();
            return copy.Count == 0 ? null : copy;
        }
        catch (JsonException) { return null; }
    }

    private static Dictionary<string, string?> General(JsonObject groups)
        => (groups["generalData"] as JsonArray ?? [])
            .OfType<JsonObject>()
            .Where(i => i["Key"] is not null)
            .GroupBy(i => i["Key"]!.ToString())
            .ToDictionary(g => g.Key, g => g.First()["Value"]?.ToString());

    /// <summary>Configuração idêntica ao padrão de fábrica (o que o Qira mostra depois de perder as preferências).</summary>
    public static bool IsFactory(JsonObject groups)
    {
        var general = General(groups);
        return general.Count > 0 && FactoryGeneral.All(kv => general.TryGetValue(kv.Key, out var v) && string.Equals(v, kv.Value, StringComparison.OrdinalIgnoreCase));
    }

    public enum Action { None, Remember, Restore }

    /// <summary>Lembrar quando a pessoa mudou algo; restaurar quando o Qira voltou ao padrão e há escolhas guardadas.</summary>
    public static Action Decide(JsonObject current, JsonObject? remembered)
    {
        if (!IsFactory(current)) return remembered is not null && JsonNode.DeepEquals(current, remembered) ? Action.None : Action.Remember;
        return remembered is not null && !IsFactory(remembered) ? Action.Restore : Action.None;
    }

    /// <summary>Corpo Data do SaveConfig, no formato da interface oficial (JSON em texto).</summary>
    public static string SavePayload(JsonObject remembered) => remembered.ToJsonString();
}
