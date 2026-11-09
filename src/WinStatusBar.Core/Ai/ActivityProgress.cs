namespace WinStatusBar.Core.Ai;

/// <summary>Progresso do plano registrado, sem estimar tempo ou inventar etapas.</summary>
public sealed record ActivityProgress(int Completed, int Total, double? Percent)
{
    public static ActivityProgress From(IReadOnlyList<ActivityStep> steps)
    {
        int completed = steps.Count(s => s.Status == "completed");
        return new(completed, steps.Count, steps.Count == 0 ? null : completed * 100.0 / steps.Count);
    }
}
