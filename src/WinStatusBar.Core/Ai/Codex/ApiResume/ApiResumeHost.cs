namespace WinStatusBar.Core.Ai.Codex.ApiResume;

/// <summary>Balão da continuação; o clique abre a conversa (codex://threads/&lt;ThreadId&gt;) quando há uma.</summary>
public sealed record ApiResumeNotice(string Title, string Message, string? ThreadId);

/// <summary>Quem inicia e confere o processo do Codex (trocado por uma versão falsa nos testes).</summary>
public interface IApiResumeLauncher
{
    DetachedStart Start(string exe, IReadOnlyList<string> args, string cwd, string stdoutFile, string stderrFile, IReadOnlyDictionary<string, string?> env);
    bool IsSame(int pid, DateTimeOffset createdAt);
    Task WaitForExitAsync(int pid, DateTimeOffset createdAt, CancellationToken ct);
    bool KillTree(int pid, DateTimeOffset createdAt);
}

/// <summary>Lançador real: <see cref="DetachedProcess"/>, sempre com a imagem codex.exe.</summary>
public sealed class DetachedProcessLauncher : IApiResumeLauncher
{
    private const string Image = "codex.exe";
    public DetachedStart Start(string exe, IReadOnlyList<string> args, string cwd, string stdoutFile, string stderrFile, IReadOnlyDictionary<string, string?> env) =>
        DetachedProcess.Start(exe, args, cwd, stdoutFile, stderrFile, env);
    public bool IsSame(int pid, DateTimeOffset createdAt) => DetachedProcess.IsSame(pid, createdAt, Image);
    public Task WaitForExitAsync(int pid, DateTimeOffset createdAt, CancellationToken ct) => DetachedProcess.WaitForExitAsync(pid, createdAt, Image, ct);
    public bool KillTree(int pid, DateTimeOffset createdAt) => DetachedProcess.KillTree(pid, createdAt, Image);
}

/// <summary>O que o supervisor precisa da barra (a barra implementa; os testes usam uma versão falsa).</summary>
public interface IApiResumeHost
{
    DateTimeOffset Now { get; }
    bool ScreenOn { get; }
    QuotaSnapshot? CodexQuota { get; }
    DashboardSnapshot? Dashboard { get; }
    string? FindCodexExecutable();
    /// <summary>Carrega a chave só para o início; <paramref name="sourceName"/> é o nome da fonte (nunca a chave).</summary>
    string? LoadKey(out string sourceName);
    string? ReadCodexConfig();
    Task<ModelCheck> CheckModelAsync(string model, string apiKey, bool fresh, CancellationToken ct);
    CodexModelLevels.Levels? Levels(string model);
    bool DirectoryExists(string path);
    RolloutTail? ReadTail(string rolloutPath);
    ThreadLockState ProbeLock(string threadId);
    bool ChatGptAppRunning();
    /// <summary>Espera o app do ChatGPT/Codex terminar, sem sondar (aguarda os processos).</summary>
    Task WaitChatGptExitAsync(CancellationToken ct);
    /// <summary>Tokens do turno com gatilho "exec" iniciado depois de <paramref name="startedAt"/>.</summary>
    TokenUsage? ExecTurnTokens(string threadId, DateTimeOffset startedAt);
    void RequestDashboardRefresh();
    Task<InterruptOutcome> SendInterruptAsync(int pid, DateTimeOffset createdAt);
    void SetNote(string threadId, string? note, DateTimeOffset? keepUntil);
    void Notify(ApiResumeNotice notice);
    void Log(string line);
}
