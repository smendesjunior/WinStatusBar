namespace WinStatusBar.Core.Ai;

/// <summary>Agrega as sessões de todos os provedores e escolhe a sessão prioritária exibida na barra.</summary>
public sealed class TaskActivityService
{
    private readonly List<IAgentActivityProvider> _providers = new();
    public event EventHandler? Changed;

    public void Add(IAgentActivityProvider provider)
    {
        _providers.Add(provider);
        provider.SessionsChanged += (_, _) => Changed?.Invoke(this, EventArgs.Empty);
    }

    public bool IsEnabled(AgentKind kind) => EnabledKinds.Contains(kind);
    public HashSet<AgentKind> EnabledKinds { get; } = [AgentKind.Codex, AgentKind.Claude, AgentKind.Local, AgentKind.Api, AgentKind.Qira];

    public IReadOnlyList<AgentSession> AllSessions() =>
        _providers.Where(p => EnabledKinds.Contains(p.Kind)).SelectMany(p => p.Sessions)
            .OrderBy(s => Rank(s.State)).ThenByDescending(s => s.LastActivity).ToList();

    /// <summary>
    /// Prioridade: sessão fixada → espera por autorização/resposta → executando → concluída/erro recente → ociosa recente.
    /// Empates favorecem o agente cujo aplicativo está em primeiro plano e, depois, a atividade mais recente.
    /// </summary>
    public static AgentSession? ChoosePriority(IEnumerable<AgentSession> sessions, string? pinned, AgentKind? foreground)
    {
        var list = sessions.ToList();
        if (list.Count == 0) return null;
        if (!string.IsNullOrWhiteSpace(pinned) && pinned != "auto")
        {
            var byId = list.FirstOrDefault(s => s.Id == pinned);
            if (byId is not null) return byId;
            AgentKind? kind = pinned.Equals("codex", StringComparison.OrdinalIgnoreCase) ? AgentKind.Codex
                : pinned.Equals("claude", StringComparison.OrdinalIgnoreCase) ? AgentKind.Claude
                : pinned.Equals("local", StringComparison.OrdinalIgnoreCase) ? AgentKind.Local
                : pinned.Equals("qira", StringComparison.OrdinalIgnoreCase) ? AgentKind.Qira : null;
            if (kind is not null)
            {
                var k = list.Where(s => s.Kind == kind).OrderBy(s => Rank(s.State)).ThenByDescending(s => s.LastActivity).FirstOrDefault();
                if (k is not null) return k;
            }
        }
        return list
            .OrderBy(s => Rank(s.State))
            // Inferência local em andamento é o que está de fato processando na máquina: vence empates entre "Executando".
            .ThenByDescending(s => s.Kind == AgentKind.Local && s.State == AgentState.Running)
            .ThenByDescending(s => foreground is not null && s.Kind == foreground)
            .ThenByDescending(s => s.LastActivity)
            .First();
    }

    internal static int Rank(AgentState s) => s switch
    {
        AgentState.WaitingApproval => 0,
        AgentState.WaitingInput => 1,
        AgentState.Running or AgentState.WaitingResponse => 2,
        AgentState.Unknown => 3,
        AgentState.Interrupted => 4,
        AgentState.Error => 3,
        AgentState.Completed => 4,
        _ => 5,
    };
}

/// <summary>Cotas exibidas na barra (com tratamento de janelas expiradas).</summary>
public static class QuotaService
{
    public static string Text(QuotaWindow? w, bool showRemaining, DateTimeOffset now)
    {
        if (w is null) return global::WinStatusBar.Core.LocalizedText.T("Indisponível");
        if (w.IsExpired(now)) return "—"; // janela renovada após o último dado observado: aguarda novo dado real
        double v = showRemaining ? w.RemainingPercent : w.UsedPercent;
        return Math.Round(v).ToString("0") + " %";
    }

    public static double? Value(QuotaWindow? w, bool showRemaining, DateTimeOffset now)
    {
        if (w is null) return null;
        if (w.IsExpired(now)) return null;
        return showRemaining ? w.RemainingPercent : w.UsedPercent;
    }
}
