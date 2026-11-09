namespace WinStatusBar.Core.Ai;

public enum AgentKind { Codex, Claude, Local, Api, Qira }

public static class AgentKindText
{
    /// <summary>Nome do agente: "Claude Code" (sessões observáveis do app desktop e do CLI); "IA local" (qualquer servidor local).</summary>
    public static string Display(this AgentKind k) => k switch { AgentKind.Codex => global::WinStatusBar.Core.LocalizedText.T("Codex"), AgentKind.Claude => global::WinStatusBar.Core.LocalizedText.T("Claude Code"), AgentKind.Qira => "Lenovo Qira", AgentKind.Api => "API", _ => global::WinStatusBar.Core.LocalizedText.T("IA local") };

    public static string Key(this AgentKind k) => k switch { AgentKind.Codex => "codex", AgentKind.Claude => "claude", AgentKind.Qira => "qira", AgentKind.Api => "api", _ => "local" };
}

public enum AgentState { Idle, Running, WaitingApproval, WaitingInput, Completed, Error, WaitingResponse, Interrupted, Unknown }

public static class AgentStateText
{
    public static string ToPt(this AgentState s) => s switch
    {
        AgentState.Running => global::WinStatusBar.Core.LocalizedText.T("Executando"),
        AgentState.WaitingApproval => global::WinStatusBar.Core.LocalizedText.T("Aguardando autorização"),
        AgentState.WaitingInput => global::WinStatusBar.Core.LocalizedText.T("Aguardando"),
        AgentState.Completed => global::WinStatusBar.Core.LocalizedText.T("Resposta encerrada"),
        AgentState.WaitingResponse => "Aguardando resposta",
        AgentState.Interrupted => "Interrompido",
        AgentState.Unknown => "Estado desconhecido",
        AgentState.Error => global::WinStatusBar.Core.LocalizedText.T("Erro"),
        _ => global::WinStatusBar.Core.LocalizedText.T("Ocioso"),
    };

    public static bool IsActive(this AgentState s) => s is AgentState.Running or AgentState.WaitingResponse or AgentState.WaitingApproval or AgentState.WaitingInput;
}

/// <summary>Uma sessão/thread de agente. Campos de texto vêm apenas de metadados reais.</summary>
public sealed class AgentSession
{
    public required AgentKind Kind { get; init; }
    public required string Id { get; init; }
    /// <summary>Nome exibido quando difere do agente (ex.: "ChatGPT" e "Claude" para conversas de chat).</summary>
    public string? AgentName { get; init; }
    public string DisplayAgent => AgentName ?? Kind.Display();
    public string? Title { get; set; }
    public string? Caption { get; set; }
    public string? ProjectDir { get; set; }
    public string? Model { get; set; }
    public AgentState State { get; set; } = AgentState.Idle;
    public DateTimeOffset LastActivity { get; set; } = DateTimeOffset.Now;
    public DateTimeOffset? StartedAt { get; set; }
    /// <summary>Progresso numérico somente quando o provedor o fornece (ex.: plano com etapas concluídas).</summary>
    public double? ProgressPercent { get; set; }
    public bool ProgressIsPhaseEstimate { get; set; }
    /// <summary>Etapas do plano da sessão (PlanTracker: c/i/p por etapa), para acompanhar atualizações parciais.</summary>
    public string? PlanSteps { get; set; }
    public string? TurnId { get; set; }
    public string? ProviderId { get; init; }
    public string ObservationSource { get; set; } = "Eventos locais";
    public bool ObservationStale { get; set; }
    public bool ScopeChanged { get; set; }
    public bool ExecutionEnded { get; set; }
    public DateTimeOffset LastStateEvent { get; set; }
    public string ProgressBasis { get; set; } = "Etapas de mesmo peso";
    internal Dictionary<string, string> PendingPlans { get; set; } = new();
    public int ActiveSubagents { get; set; }
    public double? ContextUsedPercent { get; set; }
    public string? TranscriptPath { get; set; }
    /// <summary>Quem abriu a conversa (session_meta.originator do Codex; "winstatusbar" = a própria barra).</summary>
    public string? Originator { get; set; }

    public string ProjectName => string.IsNullOrWhiteSpace(ProjectDir) ? string.Empty : Path.GetFileName(ProjectDir.TrimEnd('\\', '/'));

    /// <summary>Título conforme a prioridade: título real → descrição estruturada → projeto → genérico.</summary>
    public string DisplayTitle => !string.IsNullOrWhiteSpace(Title) ? Title!
        : !string.IsNullOrWhiteSpace(ProjectName) ? $"{ProjectName}"
        : global::WinStatusBar.Core.LocalizedText.T(State.IsActive() ? "Executando tarefa" : "Tarefa");

    public AgentSession Clone() { var clone = (AgentSession)MemberwiseClone(); clone.PendingPlans = new(PendingPlans); return clone; }
}

public sealed record QuotaWindow(AgentKind Kind, string Label, double UsedPercent, DateTimeOffset? ResetsAt, int? WindowMinutes, DateTimeOffset ObservedAt, string Source)
{
    public double RemainingPercent => Math.Clamp(100 - UsedPercent, 0, 100);

    /// <summary>Uma janela cujo horário de renovação já passou não pode mais ser exibida como valor atual.</summary>
    public bool IsExpired(DateTimeOffset now) => ResetsAt is not null && now >= ResetsAt.Value;
}

public sealed record ResetCredit(string Title, string? Description, DateTimeOffset? ExpiresAt, string Status)
{
    public string? Id { get; init; }
    public bool CanConsume(DateTimeOffset now) => !string.IsNullOrWhiteSpace(Id) && Status == "available" && (ExpiresAt is null || ExpiresAt > now);
}

public sealed class QuotaSnapshot
{
    public QuotaWindow? Primary { get; init; }
    public QuotaWindow? Secondary { get; init; }
    public string? PlanType { get; init; }
    public string? Error { get; init; }
    public DateTimeOffset ObservedAt { get; init; } = DateTimeOffset.Now;
    /// <summary>Outras janelas informadas pelo provedor (ex.: limites por modelo, "Opus 7d").</summary>
    public IReadOnlyList<QuotaWindow> AdditionalWindows { get; init; } = [];
    /// <summary>Resets avulsos disponíveis (Codex: rateLimitResetCredits). null = o provedor não informa.</summary>
    public int? ResetCreditsAvailable { get; init; }
    public IReadOnlyList<ResetCredit> ResetCredits { get; init; } = [];
    /// <summary>Saldo de créditos / uso extra, quando informado.</summary>
    public string? CreditsText { get; init; }

    /// <summary>Copia os detalhes (que só a consulta oficial traz) para um snapshot mais recente vindo do rollout.</summary>
    public QuotaSnapshot WithDetailsFrom(QuotaSnapshot? other) => other is null ? this : new QuotaSnapshot
    {
        Primary = Primary, Secondary = Secondary, PlanType = PlanType ?? other.PlanType, Error = Error, ObservedAt = ObservedAt,
        AdditionalWindows = AdditionalWindows.Count > 0 ? AdditionalWindows : other.AdditionalWindows,
        ResetCreditsAvailable = ResetCreditsAvailable ?? other.ResetCreditsAvailable,
        ResetCredits = ResetCredits.Count > 0 ? ResetCredits : other.ResetCredits,
        CreditsText = CreditsText ?? other.CreditsText,
    };
}

public interface IAiQuotaProvider
{
    AgentKind Kind { get; }
    QuotaSnapshot? Quota { get; }
    event EventHandler? QuotaChanged;
}

public interface IAgentActivityProvider
{
    AgentKind Kind { get; }
    IReadOnlyList<AgentSession> Sessions { get; }
    event EventHandler? SessionsChanged;
}
