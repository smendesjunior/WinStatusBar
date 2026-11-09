using System.Text;
using WinStatusBar.Core.Ai;
using Xunit;

namespace WinStatusBar.Tests;

/// <summary>Etapas do acompanhamento: plano registrado (transcript do app Claude) e roteiro deduzido por IA.</summary>
public class TaskStepsTests
{
    private const string PlanSet = """{"type":"assistant","message":{"role":"assistant","content":[{"type":"tool_use","name":"mcp__ccd_turn__update_plan","input":{"items":[{"content":"Ler","status":"in_progress"},{"content":"Editar","status":"pending"},{"content":"Testar","status":"pending"}]}}]}}""";
    private const string Advance = """{"type":"assistant","message":{"role":"assistant","content":[{"type":"tool_use","name":"mcp__ccd_turn__update_plan","input":{"advance":true}}]}}""";

    [Fact] public void RequestBlobsDoNotReapplyAnOlderPlan()
    {
        // O app desktop grava cópias "api-request-blob" de mensagens já vistas; não podem desfazer o "advance".
        string blob = PlanSet.Replace("\"type\":\"assistant\",\"message\"", "\"type\":\"api-request-blob\",\"message\"");
        var digest = ActivityDigest.Parse([PlanSet, Advance, blob]);
        Assert.Equal(["completed", "in_progress", "pending"], digest.Steps.Select(s => s.Status));
    }

    [Fact] public void LargeTranscriptKeepsThePlanSetBeforeTheTail()
    {
        string path = Path.Combine(Path.GetTempPath(), $"wsb-digest-{Guid.NewGuid():N}.jsonl");
        try
        {
            var sb = new StringBuilder().AppendLine("{\"type\":\"user\",\"timestamp\":\"2026-11-07T13:00:00Z\",\"message\":{\"role\":\"user\",\"content\":\"Ajuste o painel\"}}").AppendLine(PlanSet);
            string filler = "{\"type\":\"attachment\",\"text\":\"" + new string('x', 64 * 1024) + "\"}";
            for (int i = 0; i < 40; i++) sb.AppendLine(filler); // ~2,5 MB: a lista inicial fica fora do trecho final
            sb.AppendLine(Advance);
            File.WriteAllText(path, sb.ToString());
            var digest = ActivityDigest.Read(path);
            Assert.True(digest.Partial);
            Assert.Equal(DateTimeOffset.Parse("2026-11-07T13:00:00Z"), digest.StartedAt);
            Assert.Equal(["Ler", "Editar", "Testar"], digest.Steps.Select(s => s.Text));
            Assert.Equal(["completed", "in_progress", "pending"], digest.Steps.Select(s => s.Status));
        }
        finally { File.Delete(path); }
    }

    [Fact] public void InferredPlanParsesStatusLinesAndIgnoresNoise()
    {
        var steps = InferredPlan.Parse("Aqui está:\nc|Ler o código\n- i | Ajustar o painel\n2. p|Compilar\ni|Outra em andamento\nqualquer coisa");
        Assert.Equal(["Ler o código", "Ajustar o painel", "Compilar", "Outra em andamento"], steps.Select(s => s.Text));
        // No máximo uma etapa em andamento.
        Assert.Equal(["completed", "in_progress", "pending", "pending"], steps.Select(s => s.Status));
    }

    [Fact] public void SimplestModelPicksTheLightModelAndLowestEffort()
    {
        string[] efforts = ["low", "medium", "high"];
        var codex = new AgentPrompt.Catalog([new("gpt-6.1-sol", "Sol", efforts, "medium"), new("gpt-6-astra", "Astra", efforts, "high"),
            new("gpt-6-luna", "Luna", efforts, "medium"), new("gpt-5.6-luna", "Luna old", efforts, "medium")], "gpt-6.1-sol", "medium", null);
        Assert.Equal(("gpt-6-luna", "low"), InferredPlan.SimplestModel(codex));
        var claude = new AgentPrompt.Catalog([new("claude-opus-5-5", "Opus", efforts, "medium"), new("claude-haiku-5-5", "Haiku", efforts, "medium")], "claude-opus-5-5", null, null);
        Assert.Equal(("claude-haiku-5-5", "low"), InferredPlan.SimplestModel(claude));
        var described = new AgentPrompt.Catalog([new("x-big", "Big", efforts, null), new("x-small", "Small", efforts, null)], "x-big", null, null);
        Assert.Equal("x-small", InferredPlan.SimplestModel(described, new Dictionary<string, string> { ["x-small"] = "Fast and affordable model" }).Model);
    }

    [Fact] public void UpdatePromptCarriesThePreviousStepsForIncrementalUpdates()
    {
        string prompt = InferredPlan.Prompt("Pedido: x", false, [new("Ler o código", "completed"), new("Compilar", "pending")]);
        Assert.Contains("c|Ler o código", prompt);
        Assert.Contains("p|Compilar", prompt);
        Assert.DoesNotContain("ROTEIRO ANTERIOR", InferredPlan.Prompt("Pedido: x", false));
    }

    [Fact] public void InferredPlanCapsTheNumberOfSteps()
    {
        var steps = InferredPlan.Parse(string.Join("\n", Enumerable.Range(1, 25).Select(i => $"p|Etapa {i}")));
        Assert.Equal(InferredPlan.MaxSteps, steps.Count);
        Assert.Empty(InferredPlan.Parse(null));
    }
}
