using System.Globalization;

namespace WinStatusBar.Core.Ai.Codex.ApiResume;

public enum ApiResumeAction { Stop, ForceKill, AllowWhenQuotaBack, RemoveFromQueue, Retry, ContinueAgain, OpenInCodex, Dismiss }

/// <summary>Uma conversa parada na lista para marcar: por que não dá para continuar (se for o caso), se está aberta no app
/// (a trava está presa), se já está na fila e quais pastas extras faltam agora.</summary>
public sealed record ApiResumeCandidate(QuotaStop Stop, AiErrorExplanation? Unavailable, bool OpenInApp, bool AlreadyQueued,
    IReadOnlyList<string> MissingExtraDirs)
{
    public bool CanMark => Unavailable is null && !AlreadyQueued;
}

/// <summary>O que a janela "Continuar pela API" mostra: as candidatas, quantas ficaram de fora e de onde vêm chave e cobrança.</summary>
public sealed record ApiResumeChoices(IReadOnlyList<ApiResumeCandidate> Candidates, QuotaStopExclusions Excluded, bool UsedFallback,
    string? KeySource, string? Organization, string? DashboardAccount);

public static class ApiResumeCandidates
{
    /// <summary>Confere de graça o turno, o modelo na API e a pasta. Erro de rede na conferência do modelo não bloqueia: o início confere de novo.</summary>
    public static ApiResumeCandidate Evaluate(QuotaStop stop, CodexModelLevels.Levels? levels, ModelCheck? model,
        Func<string, bool> dirExists, ThreadLockState lockState, bool alreadyQueued, DateTimeOffset? quotaReturns)
    {
        var problem = CodexResumeCommand.Check(stop, levels)
            ?? model?.Result switch
            {
                ModelCheckResult.ModelNotFound => ApiResumeProblem.ModelNotInApi,
                ModelCheckResult.KeyRejected => ApiResumeProblem.KeyRejected,
                _ => (ApiResumeProblem?)null,
            }
            ?? (string.IsNullOrEmpty(stop.Cwd) || !dirExists(stop.Cwd) ? ApiResumeProblem.FolderUnavailable : null);
        CodexResumeCommand.ToSpec(stop, dirExists, out var missing);
        var unavailable = problem is { } p
            ? ApiResumeErrors.For(p, stop.Model, stop.Effort, stop.Cwd, quotaReturns, technical: p is ApiResumeProblem.ModelNotInApi or ApiResumeProblem.KeyRejected ? model?.Detail : null)
            : null;
        return new ApiResumeCandidate(stop, unavailable, lockState != ThreadLockState.Free, alreadyQueued, missing);
    }

    /// <summary>"Continuar pela API: candidatas=1 (01aa0000 «título» modelo/nível parou dd/MM HH:mm); excluídas: …".</summary>
    public static string Log(IReadOnlyList<ApiResumeCandidate> candidates, QuotaStopExclusions excluded)
    {
        string list = string.Join("; ", candidates.Select(c =>
            $"{(c.Stop.ThreadId.Length > 8 ? c.Stop.ThreadId[..8] : c.Stop.ThreadId)} «{c.Stop.Title}» {c.Stop.Model}/{c.Stop.Effort} parou " +
            c.Stop.StoppedAt.ToLocalTime().ToString("dd/MM HH:mm", CultureInfo.InvariantCulture)));
        return $"Continuar pela API: candidatas={candidates.Count}{(list.Length > 0 ? $" ({list})" : "")}; excluídas: " +
            $"interrompidas por você={excluded.Interrupted}, exec/scripts={excluded.ExecOrScripts}, subagentes={excluded.Subagents}, " +
            $"barra={excluded.Bar}, outras={excluded.Other}";
    }
}
