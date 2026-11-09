using System.Security.Principal;
using System.Text.Json.Serialization;

namespace WinStatusBar.Core.Ipc;

/// <summary>
/// Mensagem enviada pelo coletor (hooks/statusLine do Claude Code) ao WinStatusBar.
/// Contém apenas metadados mínimos — nunca a conversa, conteúdo de arquivos ou credenciais.
/// </summary>
public sealed class EventMessage
{
    [JsonPropertyName("v")] public int Version { get; set; } = 1;
    [JsonPropertyName("src")] public string Source { get; set; } = "claude";
    [JsonPropertyName("kind")] public string Kind { get; set; } = "hook"; // hook | statusline
    [JsonPropertyName("event")] public string? Event { get; set; }
    [JsonPropertyName("sid")] public string? SessionId { get; set; }
    [JsonPropertyName("cwd")] public string? Cwd { get; set; }
    [JsonPropertyName("transcript")] public string? TranscriptPath { get; set; }
    [JsonPropertyName("tool")] public string? ToolName { get; set; }
    [JsonPropertyName("detail")] public string? Detail { get; set; }
    [JsonPropertyName("ntype")] public string? NotificationType { get; set; }
    [JsonPropertyName("prompt")] public string? PromptSnippet { get; set; }
    [JsonPropertyName("agent")] public string? AgentType { get; set; }
    [JsonPropertyName("progress")] public double? Progress { get; set; }
    /// <summary>Operação do plano de etapas (PlanTracker): lista inteira ou atualização parcial.</summary>
    [JsonPropertyName("plan")] public string? Plan { get; set; }
    [JsonPropertyName("model")] public string? Model { get; set; }
    [JsonPropertyName("sname")] public string? SessionName { get; set; }
    [JsonPropertyName("pdir")] public string? ProjectDir { get; set; }
    [JsonPropertyName("ctx")] public double? ContextUsedPercent { get; set; }
    [JsonPropertyName("qsource")] public string? QuotaSource { get; set; }
    [JsonPropertyName("rl5")] public double? FiveHourUsed { get; set; }
    [JsonPropertyName("rl5r")] public long? FiveHourResetsAt { get; set; }
    [JsonPropertyName("rl7")] public double? SevenDayUsed { get; set; }
    [JsonPropertyName("rl7r")] public long? SevenDayResetsAt { get; set; }
    [JsonPropertyName("ts")] public long Timestamp { get; set; } = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
}

[JsonSerializable(typeof(EventMessage))]
[JsonSourceGenerationOptions(DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
public partial class EventMessageContext : JsonSerializerContext;

public static class PipeNames
{
    /// <summary>Pipe exclusivo por usuário (SID), com ACL restrita ao próprio usuário.</summary>
    public static string Events
    {
        get
        {
            string sid;
            try { sid = WindowsIdentity.GetCurrent().User?.Value ?? Environment.UserName; }
            catch { sid = Environment.UserName; }
            return "WinStatusBar.Events." + sid;
        }
    }
}
