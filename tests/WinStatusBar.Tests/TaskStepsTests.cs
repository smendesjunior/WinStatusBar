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

    [Fact] public void GrowingTranscriptIsReadOnlyFromWhereItStopped()
    {
        // Memória e disco (09/11/2026): a cada mudança da conversa a barra relia o arquivo inteiro (8,8 MB numa sessão
        // longa do Claude Code), e as linhas grandes se acumulavam no heap de objetos grandes (~280 MB).
        string path = Path.Combine(Path.GetTempPath(), $"wsb-digest-{Guid.NewGuid():N}.jsonl");
        try
        {
            var sb = new StringBuilder().AppendLine("{\"type\":\"user\",\"timestamp\":\"2026-11-07T13:00:00Z\",\"message\":{\"role\":\"user\",\"content\":\"Ajuste o painel\"}}").AppendLine(PlanSet);
            string filler = "{\"type\":\"attachment\",\"text\":\"" + new string('x', 64 * 1024) + "\"}";
            for (int i = 0; i < 40; i++) sb.AppendLine(filler);
            File.WriteAllText(path, sb.ToString());
            var first = ActivityDigest.Read(path);
            Assert.True(ActivityDigest.LastBytesRead > 2 * 1024 * 1024); // primeira vez: o arquivo todo
            Assert.Equal(["in_progress", "pending", "pending"], first.Steps.Select(s => s.Status));

            // A IA avança o plano e escreve; a última linha ainda está sendo gravada (sem quebra de linha).
            string reply = "{\"type\":\"assistant\",\"message\":{\"role\":\"assistant\",\"content\":[{\"type\":\"text\",\"text\":\"Editando o painel\"}]}}";
            File.AppendAllText(path, Advance + "\n" + reply[..20]);
            var second = ActivityDigest.Read(path);
            Assert.True(ActivityDigest.LastBytesRead < 4096); // só o que foi acrescentado
            Assert.Equal(["Ler", "Editar", "Testar"], second.Steps.Select(s => s.Text)); // a lista inicial, de fora do trecho final
            Assert.Equal(["completed", "in_progress", "pending"], second.Steps.Select(s => s.Status));
            Assert.Equal(DateTimeOffset.Parse("2026-11-07T13:00:00Z"), second.StartedAt);

            File.AppendAllText(path, reply[20..] + "\n"); // a linha termina de ser gravada
            var third = ActivityDigest.Read(path);
            Assert.Equal("Editando o painel", third.Updates.Last().Text);
            Assert.Equal(["completed", "in_progress", "pending"], third.Steps.Select(s => s.Status));

            // Arquivo trocado por um menor (as duas primeiras linhas): lê de novo do começo.
            File.WriteAllText(path, string.Join("\n", sb.ToString().Split('\n')[..2]) + "\n");
            var fresh = ActivityDigest.Read(path);
            Assert.Equal(["in_progress", "pending", "pending"], fresh.Steps.Select(s => s.Status));
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

    [Fact] public void SimplestModelIgnoresFastInTheDefaultModelDescription()
    {
        // Catálogo real do Codex em 21/11/2026: o Sol vem primeiro e se descreve como "Fast"; antes ele era escolhido.
        string[] efforts = ["low", "medium", "high"];
        var codex = new AgentPrompt.Catalog([new("gpt-6.2-sol", "GPT-6.2-Sol", efforts, "medium"), new("gpt-6.2-astra", "GPT-6.2-Astra", efforts, "medium"),
            new("gpt-6.1-terra", "GPT-6.1 Terra", efforts, "medium"), new("gpt-6-luna", "GPT-6-Luna", efforts, "medium"),
            new("gpt-5.6-luna", "GPT-5.6-Luna", efforts, "medium")], "gpt-6.2-sol", "medium", null);
        var descriptions = new Dictionary<string, string>
        {
            ["gpt-6.2-sol"] = "Fast, go-to model for coding and everyday work.",
            ["gpt-6.1-terra"] = "Efficient model that excels at visual understanding and computer use.",
            ["gpt-6-luna"] = "Fast and affordable model for easier tasks.",
            ["gpt-5.6-luna"] = "Legacy fast model for simpler tasks.",
        };
        Assert.Equal(("gpt-6-luna", "low"), InferredPlan.SimplestModel(codex, descriptions));
        var onlyFast = new AgentPrompt.Catalog([new("x-default", "Default", efforts, null)], "x-default", null, null);
        Assert.Equal("x-default", InferredPlan.SimplestModel(onlyFast, new Dictionary<string, string> { ["x-default"] = "Fast model" }).Model);
    }

    [Fact] public void InferenceScheduleIsEconomical()
    {
        var t0 = new DateTimeOffset(2026, 11, 21, 12, 0, 0, TimeSpan.Zero);
        // Primeira dedução: logo, mesmo com a conversa em andamento.
        Assert.True(InferredPlan.ShouldInfer(t0, true, false, 0, default, t0, true));
        // Nada mudou: nunca.
        Assert.False(InferredPlan.ShouldInfer(t0.AddHours(1), false, true, 1, t0, t0, false));
        // Com etapas: menos de 5 min desde a última, não; mesmo parada.
        Assert.False(InferredPlan.ShouldInfer(t0.AddMinutes(4), true, true, 1, t0, t0, false));
        // 5 min e conversa mudando agora (ativa): espera ela parar.
        Assert.False(InferredPlan.ShouldInfer(t0.AddMinutes(6), true, true, 1, t0, t0.AddMinutes(6).AddSeconds(-10), true));
        // 5 min e parada há 60 s, ou turno encerrado: deduz.
        Assert.True(InferredPlan.ShouldInfer(t0.AddMinutes(6), true, true, 1, t0, t0.AddMinutes(5), true));
        Assert.True(InferredPlan.ShouldInfer(t0.AddMinutes(6), true, true, 1, t0, t0.AddMinutes(6), false));
        // Nunca para: uma a cada 15 min.
        Assert.True(InferredPlan.ShouldInfer(t0.AddMinutes(15), true, true, 1, t0, t0.AddMinutes(15), true));
        // Limite por conversa.
        Assert.False(InferredPlan.ShouldInfer(t0.AddHours(2), true, true, InferredPlan.MaxCallsPerConversation, t0, t0, false));
        // Intervalo configurado maior que 5 min vale.
        Assert.False(InferredPlan.ShouldInfer(t0.AddMinutes(8), true, true, 1, t0, t0, false, 600));
        // Pior caso em 1 hora de conversa sempre ativa, verificando a cada segundo: no máximo 5 chamadas (1 + 4 de 15 min).
        int calls = 0; var last = default(DateTimeOffset); bool steps = false;
        for (var now = t0; now < t0.AddHours(1); now = now.AddSeconds(1))
            if (InferredPlan.ShouldInfer(now, true, steps, calls, last, now, true)) { calls++; last = now; steps = true; }
        Assert.InRange(calls, 1, 5);
    }

    [Fact] public void CodexBriefingCallUsesShortBaseInstructions()
    {
        string service = RepoSource.Read("src", "WinStatusBar", "Services", "TaskBriefingService.cs");
        Assert.Contains("BaseInstructions = target == AgentPrompt.Target.Codex ? InferredPlan.BaseInstructions : null", service);
        string composer = RepoSource.Read("src", "WinStatusBar.Core", "Ai", "Codex", "CodexComposer.cs");
        Assert.Contains("settings[\"baseInstructions\"] = baseText", composer);
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
