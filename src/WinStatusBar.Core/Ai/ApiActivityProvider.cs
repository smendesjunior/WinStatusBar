namespace WinStatusBar.Core.Ai;

/// <summary>Tracks only requests made by this app, never infers external account activity.</summary>
public sealed class ApiActivityProvider : IAgentActivityProvider
{
    private readonly object _gate = new();
    private readonly Dictionary<string, AgentSession> _sessions = new();
    public AgentKind Kind => AgentKind.Api;
    public event EventHandler? SessionsChanged;
    public IReadOnlyList<AgentSession> Sessions { get { lock (_gate) return _sessions.Values.Select(s => s.Clone()).ToList(); } }
    public string Begin(string conversation, string provider, string name, string? model)
    {
        string run = Guid.NewGuid().ToString("N");
        lock (_gate)
        {
            _sessions[conversation] = new AgentSession { Kind = Kind, Id = "api:" + conversation, ProviderId = provider,
                AgentName = name, Title = "Conversa na barra", Model = model, TurnId = run, State = AgentState.WaitingResponse,
                Caption = "Solicitação enviada · aguardando resposta", StartedAt = DateTimeOffset.Now, LastActivity = DateTimeOffset.Now,
                ObservationSource = "Requisição do chat da barra" };
            foreach (var old in _sessions.Where(x => x.Key != conversation && !x.Value.State.IsActive() && DateTimeOffset.Now - x.Value.LastActivity > TimeSpan.FromHours(12)).Select(x => x.Key).ToList()) _sessions.Remove(old);
        }
        SessionsChanged?.Invoke(this, EventArgs.Empty); return run;
    }
    public void Receiving(string conversation, string run) => Update(conversation, run, AgentState.Running, "Recebendo resposta", false);
    public void End(string conversation, string run, bool cancelled, string? error) => Update(conversation, run,
        cancelled ? AgentState.Interrupted : error is not null ? AgentState.Error : AgentState.Completed,
        cancelled ? "Solicitação interrompida" : error is not null ? "Falha na solicitação" : "Resposta concluída · ações propostas podem estar pendentes", true);
    private void Update(string conversation, string run, AgentState state, string caption, bool ended)
    {
        lock (_gate)
        {
            if (!_sessions.TryGetValue(conversation, out var session) || session.TurnId != run || session.ExecutionEnded) return;
            session.State = state; session.Caption = caption; session.ExecutionEnded = ended; session.LastActivity = DateTimeOffset.Now;
        }
        SessionsChanged?.Invoke(this, EventArgs.Empty);
    }
}
