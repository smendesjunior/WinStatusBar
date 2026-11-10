namespace WinStatusBar.Core.Ai.Codex.ApiResume;

public enum ApiResumeState
{
    Queued, WaitingForApp, QuotaBackWaitingUser, Starting, Running, Stopping,
    Completed, Stopped, Failed, Blocked, AlreadyContinued, Removed,
}

/// <summary>Registro de uma continuação pela API (jobs.json). Nunca guarda a chave nem o ambiente.</summary>
public sealed class ApiResumeJob
{
    public string Id { get; set; } = "";
    public string ThreadId { get; set; } = "";
    public string Title { get; set; } = "";
    public string RolloutPath { get; set; } = "";
    public string StoppedTurnId { get; set; } = "";
    public DateTimeOffset StoppedAt { get; set; }
    public string Model { get; set; } = "";
    public string Effort { get; set; } = "";
    public string SandboxMode { get; set; } = "";
    public string ApprovalPolicy { get; set; } = "";
    public string Cwd { get; set; } = "";
    public bool? NetworkAccess { get; set; }
    public List<string> ExtraDirs { get; set; } = [];

    public ApiResumeState State { get; set; }
    public string? Detail { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? EndedAt { get; set; }

    public int? Pid { get; set; }
    public DateTimeOffset? ProcessCreatedAt { get; set; }
    public string? Executable { get; set; }
    public long EventsOffset { get; set; }
    public long RolloutOffset { get; set; }
    public bool TurnStarted { get; set; }
    public string? ApiTurnId { get; set; }

    public TokenUsage? Tokens { get; set; }
    public DashboardSnapshot? Baseline { get; set; }
    public decimal? Spend { get; set; }
    public DateTimeOffset? SpendAt { get; set; }
    public string? Summary { get; set; }
    public AiErrorExplanation? Error { get; set; }

    public bool AllowWhenQuotaBack { get; set; }
    public bool Parallel { get; set; }
    public DateTimeOffset? StopRequestedAt { get; set; }
    public int Interrupts { get; set; }
    public bool ForceOffered { get; set; }
    public bool EndedWhileBarClosed { get; set; }
    public bool Notified { get; set; }
    public int RefreshesDone { get; set; }
    public DateTimeOffset? WaitingSince { get; set; }
    public DateTimeOffset? LastReminderAt { get; set; }
    public int Reminders { get; set; }

    public bool IsFinished => State is ApiResumeState.Completed or ApiResumeState.Stopped or ApiResumeState.Failed
        or ApiResumeState.Blocked or ApiResumeState.AlreadyContinued or ApiResumeState.Removed;

    public bool IsActive => State is ApiResumeState.Starting or ApiResumeState.Running or ApiResumeState.Stopping;

    public static ApiResumeJob From(QuotaStop stop, ResumeSpec spec, DateTimeOffset now) => new()
    {
        Id = Guid.NewGuid().ToString("N"),
        ThreadId = stop.ThreadId, Title = stop.Title, RolloutPath = stop.RolloutPath, RolloutOffset = stop.RolloutOffset,
        StoppedTurnId = stop.StoppedTurnId, StoppedAt = stop.StoppedAt,
        Model = spec.Model, Effort = spec.Effort, SandboxMode = spec.SandboxMode, ApprovalPolicy = spec.ApprovalPolicy,
        Cwd = spec.Cwd, NetworkAccess = spec.NetworkAccess, ExtraDirs = [.. spec.ExtraDirs],
        State = ApiResumeState.Queued, CreatedAt = now, UpdatedAt = now,
    };

    public ResumeSpec ToSpec() => new(ThreadId, Cwd, ExtraDirs, Model, Effort, SandboxMode, ApprovalPolicy, NetworkAccess);

    public ApiResumeJob Clone()
    {
        var copy = (ApiResumeJob)MemberwiseClone();
        copy.ExtraDirs = [.. ExtraDirs];
        return copy;
    }
}
