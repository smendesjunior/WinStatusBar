using WinStatusBar.Core.Ai;
using Xunit;

namespace WinStatusBar.Tests;

public class ActivityDigestTests
{
    [Fact]
    public void KeepsOriginalGoalAndLaterCorrectionsButNeverReasoningOrToolOutputs()
    {
        var digest = ActivityDigest.Parse([
            """{"type":"response_item","payload":{"type":"message","role":"user","content":[{"type":"input_text","text":"Criar um aplicativo"}]}}""",
            """{"type":"response_item","payload":{"type":"message","role":"assistant","channel":"analysis","content":[{"type":"output_text","text":"RACIOCINIO PRIVADO"}]}}""",
            """{"type":"response_item","payload":{"type":"function_call_output","output":"SEGREDO"}}""",
            """{"type":"response_item","payload":{"type":"message","role":"assistant","channel":"commentary","content":[{"type":"output_text","text":"A tela foi implementada; falta validar."}]}}""",
            """{"type":"response_item","payload":{"type":"message","role":"user","content":[{"type":"input_text","text":"Preserve os dados existentes"}]}}"""
        ]);
        Assert.Equal("Criar um aplicativo", digest.Request);
        Assert.Contains(digest.Updates, u => u.Text.Contains("Preserve os dados"));
        Assert.DoesNotContain(digest.Updates, u => u.Text.Contains("PRIVADO") || u.Text.Contains("SEGREDO"));
    }

    [Fact]
    public void ClaudePartialPlanAdvancementKeepsStepNamesAndPendingWork()
    {
        var digest = ActivityDigest.Parse([
            """{"message":{"role":"assistant","content":[{"type":"tool_use","name":"TodoWrite","input":{"todos":[{"content":"Implementar","status":"in_progress"},{"content":"Validar","status":"pending"}]}}]}}""",
            """{"message":{"role":"assistant","content":[{"type":"tool_use","name":"update_plan","input":{"advance":true}}]}}""",
            "{incompleto"
        ]);
        Assert.Equal("completed", digest.Steps[0].Status);
        Assert.Equal("Validar", digest.Steps[1].Text);
        Assert.Equal("in_progress", digest.Steps[1].Status);
    }

    [Fact]
    public void NewCodexPlanReplacesOldPlanInsteadOfInventingRemainingWork()
    {
        var digest = ActivityDigest.Parse([
            """{"type":"response_item","payload":{"type":"function_call","name":"functions.update_plan","arguments":"{\"plan\":[{\"step\":\"Publicar\",\"status\":\"pending\"}]}"}}""",
            """{"type":"response_item","payload":{"type":"function_call","name":"update_plan","arguments":"{\"plan\":[{\"step\":\"Revisar\",\"status\":\"in_progress\"}]}"}}"""
        ]);
        Assert.Single(digest.Steps);
        Assert.Equal("Revisar", digest.Steps[0].Text);
        Assert.False(ActivityDigest.Read(null).HasTranscript);
    }
}
