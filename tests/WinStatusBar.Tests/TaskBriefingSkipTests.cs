using Xunit;

namespace WinStatusBar.Tests;

/// <summary>O Acompanhamento não tenta deduzir etapas pelo Codex enquanto a cota dele está bloqueada (WinUI: lê o código).</summary>
public class TaskBriefingSkipTests
{
    [Fact]
    public void NoInferenceForCodexWhileItIsBlocked()
    {
        string source = RepoSource.Read("src", "WinStatusBar", "Services", "TaskBriefingService.cs");
        Assert.Contains("A IA não registrou etapas; a dedução fica parada enquanto a cota do Codex estiver esgotada.", source);
        int paused = source.IndexOf("bool paused = allowed && SkipInference?.Invoke(session) == true;", StringComparison.Ordinal);
        int guard = source.IndexOf("if (!allowed || paused || !changed ||", StringComparison.Ordinal);
        int infer = source.IndexOf("await InferAsync(", StringComparison.Ordinal);
        Assert.True(paused >= 0 && paused < guard && guard < infer, $"ordem: paused={paused} guard={guard} infer={infer}");
    }
}
