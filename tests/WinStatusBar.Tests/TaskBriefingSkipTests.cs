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

    [Theory]
    [InlineData("Hub.ChatGptChat.CodexThreadTitles = () => Hub.Codex.KnownTitles;")]
    [InlineData("Hub.ChatGptChat.CodexBlockedReason = () => Hub.Codex.Quota is { } q && q.IsBlocked(DateTimeOffset.Now) ? Core.Ai.Codex.CodexQuotaText.ShortReason(q, DateTimeOffset.Now) : null;")]
    [InlineData("Hub.Briefing.SkipInference = s => s.Kind == Core.Ai.AgentKind.Codex && Hub.Codex.Quota?.IsBlocked(DateTimeOffset.Now) == true;")]
    public void AppWiresTheCodexBlockBeforeTheHubStarts(string line)
    {
        string app = RepoSource.Read("src", "WinStatusBar", "App.xaml.cs");
        int created = app.IndexOf("Hub = new MonitoringHub(_dispatcher, Settings);", StringComparison.Ordinal);
        // As leituras ligam depois da primeira pintura: DeferredStep("monitoramento", Hub.Start) em StartDeferredServices.
        int started = app.IndexOf("Hub.Start)", created, StringComparison.Ordinal);
        int wired = app.IndexOf(line, StringComparison.Ordinal);
        Assert.True(created >= 0 && created < wired && wired < started, $"criado={created} ligado={wired} iniciado={started}");
    }
}
