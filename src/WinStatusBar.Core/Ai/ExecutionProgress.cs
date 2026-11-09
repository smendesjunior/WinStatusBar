namespace WinStatusBar.Core.Ai;

/// <summary>Coarse stage estimate, never a clock or a count of tool calls. A declared plan takes precedence.</summary>
public static class ExecutionProgress
{
    /// <summary>A provider's end-of-turn event confirms a response ended, not that the user's goal succeeded.</summary>
    public static void FinishResponse(AgentSession session)
    {
        session.State = AgentState.Completed;
        session.ExecutionEnded = true;
        if (session.ProgressIsPhaseEstimate)
        {
            session.ProgressPercent = null;
            session.ProgressIsPhaseEstimate = false;
            session.ProgressBasis = "Resposta encerrada sem progresso confirmado por plano";
        }
        session.Caption = session.PlanSteps is not null && session.ProgressPercent is < 100
            ? "Resposta encerrada · plano pendente" : "Resposta encerrada";
    }

    public static void Observe(AgentSession session, string stage)
    {
        if (session.ExecutionEnded || session.PlanSteps is not null || !session.State.IsActive()) return;
        double? value = stage switch { "reading" => 10, "working" => 35, "editing" => 50, "checking" => 80, "finalizing" => 95, "retry" => 50, _ => null };
        if (value is null) return;
        bool rework = session.ProgressPercent >= 75 && stage is "working" or "editing" or "retry";
        if (!rework && session.ProgressPercent >= value) return;
        session.ScopeChanged |= rework;
        session.ProgressPercent = value;
        session.ProgressIsPhaseEstimate = true;
        session.ProgressBasis = "Estimativa por etapa observada · baixa confiança; não mede todo o pedido";
    }

    public static void Tool(AgentSession session, string? tool) => Observe(session, tool switch
    {
        "Read" or "Grep" or "Glob" or "WebSearch" or "WebFetch" => "reading",
        "Edit" or "Write" or "MultiEdit" or "apply_patch" => "editing",
        _ => "working"
    });
}
