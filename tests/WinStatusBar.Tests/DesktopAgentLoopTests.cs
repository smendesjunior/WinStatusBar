using WinStatusBar.Core.Ai;
using Xunit;
namespace WinStatusBar.Tests;
public class DesktopAgentLoopTests
{
    [Fact] public async Task ExecutesThenGivesResultBackBeforeFinalAnswer()
    {
        int calls=0; var actions=new List<string>();
        var final=await DesktopAgentLoop.RunAsync("abra o terminal", AgentPrompt.Options.None,
            (prompt, options, stream, ct) => Task.FromResult(++calls==1
                ? new AgentPrompt.Result("```winstatusbar-desktop\n{\"action\":\"launch\",\"application\":\"wt.exe\"}\n```", "session", null)
                : new AgentPrompt.Result(prompt.Contains("confirmado") ? "Concluído" : "Sem resultado", "session", null)),
            (request, ct) => { actions.Add(request.Action); return Task.FromResult(new DesktopActionResult("confirmado")); }, _=>{}, CancellationToken.None);
        // Retrato do PC antes da 1ª rodada, a ação única e a observação automática depois dela.
        Assert.Equal(new[] { "context", "launch", "observe_foreground" },actions); Assert.Equal(2,calls); Assert.Equal("Concluído",final.Text);
    }
    [Fact] public async Task CancellationNeverExecutesAnAction()
    {
        using var cancel=new CancellationTokenSource();cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=> DesktopAgentLoop.RunAsync("teste",AgentPrompt.Options.None,
            (_,_,_,_)=>throw new Exception("should not call"),(_,_)=>throw new Exception("should not execute"),_=>{},cancel.Token));
    }
    [Fact] public void LongCommandsGetTheirOwnDeadlineUpToThirtyMinutes()
    {
        var run = DesktopAgentLoop.Parse("```winstatusbar-desktop {\"action\":\"run\",\"application\":\"powershell.exe\",\"timeoutSeconds\":900}```")!;
        Assert.Equal(900, run.EffectiveTimeoutSeconds);
        Assert.Equal(DesktopActionRequest.MaxTimeoutSeconds, (run with { TimeoutSeconds = 99999 }).EffectiveTimeoutSeconds);
        Assert.Equal(DesktopActionRequest.DefaultTimeoutSeconds, (run with { TimeoutSeconds = null }).EffectiveTimeoutSeconds);
        // Só ações demoradas estendem o prazo; entrada de teclado/mouse continua com 60 s.
        Assert.Equal(DesktopActionRequest.DefaultTimeoutSeconds, new DesktopActionRequest("click", TimeoutSeconds: 900).EffectiveTimeoutSeconds);
    }
    [Fact] public void FullAccessUnlocksNativeToolsOnlyWithThePencilOn()
    {
        Assert.True(new AgentPrompt.Options([], false, true, null, [], FullAccess: true).Unrestricted);
        Assert.False(new AgentPrompt.Options([], false, false, null, [], FullAccess: true).Unrestricted);
        Assert.False(new AgentPrompt.Options([], false, true, null, []).Unrestricted);
        Assert.Contains("registro", DesktopAgentLoop.Instructions);
        Assert.Contains("timeoutSeconds", DesktopAgentLoop.Instructions);
    }
    [Fact] public void RejectsUnknownAndMultipleActionBlocks()
    {
        Assert.Throws<InvalidOperationException>(()=>DesktopAgentLoop.Parse("```winstatusbar-desktop {\"action\":\"unknown\"}```"));
        Assert.Throws<InvalidOperationException>(()=>DesktopAgentLoop.Parse("```winstatusbar-desktop {\"action\":\"list_windows\"}```\n```winstatusbar-desktop {\"action\":\"keys\"}```"));
    }

    // ---- lote, "final", contexto e catálogo (comuns a todas as IAs)
    private const string Batch = "```winstatusbar-desktop\n{\"plan\":[\"copiar\",\"colar\"],\"steps\":[{\"action\":\"copy_selection\"},{\"action\":\"focus\",\"target\":\"Bloco\"},{\"action\":\"paste\",\"text\":\"olá\"}],\"final\":\"Texto colado no Bloco de Notas.\"}\n```";

    [Fact] public void ParsesBatchPlanAndFinal()
    {
        var steps = DesktopAgentLoop.ParseAll(Batch, out var plan, out var final)!;
        Assert.Equal(new[] { "copy_selection", "focus", "paste" }, steps.Select(s => s.Action));
        Assert.Equal(new[] { "copiar", "colar" }, plan);
        Assert.Equal("Texto colado no Bloco de Notas.", final);
        Assert.Throws<InvalidOperationException>(() => DesktopAgentLoop.ParseAll("```winstatusbar-desktop\n{\"steps\":[" + string.Join(",", Enumerable.Repeat("{\"action\":\"wait\"}", DesktopAgentLoop.MaxBatch + 1)) + "]}\n```", out _));
    }

    [Fact] public async Task FinalAnswerSkipsTheConfirmationRoundOnlyWhenEverythingWasVerified()
    {
        int calls = 0; var actions = new List<string>();
        var result = await DesktopAgentLoop.RunAsync("copie e cole", AgentPrompt.Options.None,
            (p, o, s, ct) => Task.FromResult(new AgentPrompt.Result(++calls == 1 ? Batch : "não deveria chegar aqui", "s", null)),
            (r, ct) => { actions.Add(r.Action); return Task.FromResult(new DesktopActionResult("ok")); }, _ => { }, CancellationToken.None);
        Assert.Equal(1, calls);
        Assert.StartsWith("Texto colado no Bloco de Notas.", result.Text);
        Assert.Contains("Conferido pelo WinStatusBar", result.Text);
        Assert.Equal(new[] { "context", "copy_selection", "focus", "paste" }, actions); // sem observação: não houve outra rodada
    }

    [Fact] public async Task PartialStepDiscardsFinalAndReportsBack()
    {
        int calls = 0; string? second = null;
        var result = await DesktopAgentLoop.RunAsync("copie e cole", AgentPrompt.Options.None,
            (p, o, s, ct) => { if (++calls == 2) second = p; return Task.FromResult(new AgentPrompt.Result(calls == 1 ? Batch : "Conferi e está certo.", "s", null)); },
            (r, ct) => Task.FromResult(r.Action == "paste" ? new DesktopActionResult("Ctrl+V enviado; não verificável", Status: DesktopStatus.Partial) : new DesktopActionResult("ok")),
            _ => { }, CancellationToken.None);
        Assert.Equal(2, calls);
        Assert.Equal("Conferi e está certo.", result.Text);
        Assert.Contains("[parcial]", second);
    }

    [Fact] public async Task FailureStopsTheBatchAndListsWhatDidNotRun()
    {
        int calls = 0; string? second = null; var actions = new List<string>();
        await DesktopAgentLoop.RunAsync("copie e cole", AgentPrompt.Options.None,
            (p, o, s, ct) => { if (++calls == 2) second = p; return Task.FromResult(new AgentPrompt.Result(calls == 1 ? Batch : "Parei.", "s", null)); },
            (r, ct) => { actions.Add(r.Action); return Task.FromResult(r.Action == "copy_selection" ? new DesktopActionResult("sem seleção", Success: false) : new DesktopActionResult("ok")); },
            _ => { }, CancellationToken.None);
        Assert.Equal(new[] { "context", "copy_selection" }, actions);
        Assert.Contains("Não executadas", second);
        Assert.Contains("paste", second);
    }

    [Fact] public async Task ContextGoesToTheFirstRoundOnly()
    {
        var systems = new List<string?>(); int calls = 0;
        await DesktopAgentLoop.RunAsync("liste", AgentPrompt.Options.None,
            (p, o, s, ct) => { systems.Add(o.SystemPrompt); return Task.FromResult(new AgentPrompt.Result(++calls == 1 ? "```winstatusbar-desktop\n{\"action\":\"list_windows\"}\n```" : "Feito.", "s", null)); },
            (r, ct) => Task.FromResult(new DesktopActionResult(r.Action == "context" ? "Janela ativa: WINWORD — \"a.docx\"" : "lista")), _ => { }, CancellationToken.None);
        Assert.Contains(DesktopAgentLoop.ContextMarker, systems[0]);
        Assert.Contains("WINWORD", systems[0]);
        Assert.DoesNotContain(DesktopAgentLoop.ContextMarker, systems[1]);
        Assert.StartsWith(DesktopAgentLoop.Instructions, systems[1]);
    }

    [Fact] public void CatalogCoversEveryInstructedActionAndElevationRules()
    {
        foreach (var spec in DesktopActionCatalog.All.Where(a => a.Name is not ("launch" or "activate" or "observe_foreground" or "context")))
            Assert.Contains("\"" + spec.Name + "\"", DesktopAgentLoop.Instructions);
        Assert.True(DesktopActionCatalog.NeedsElevation(new("service", Op: "stop", Name: "Spooler")));
        Assert.True(DesktopActionCatalog.NeedsElevation(new("registry", Op: "write", Path: @"HKLM\Software\X")));
        Assert.False(DesktopActionCatalog.NeedsElevation(new("registry", Op: "write", Path: @"HKCU\Software\X")));
        Assert.True(DesktopActionCatalog.NeedsElevation(new("powershell", Text: "x", Elevated: true)));
        // Só operações atômicas são repetidas sozinhas como administrador; comandos e cópias exigem conferência.
        Assert.True(DesktopActionCatalog.RetryElevatedOnDenied(new("file", Op: "rename", Path: "a", To: "b")));
        Assert.False(DesktopActionCatalog.RetryElevatedOnDenied(new("file", Op: "copy", Path: "a", To: "b")));
        Assert.False(DesktopActionCatalog.RetryElevatedOnDenied(new("powershell", Text: "x")));
    }

    [Fact] public void ExtensionsKeepTheCatalogAsShortcutNotWhitelist()
    {
        Assert.Contains("não limita", DesktopAgentLoop.Extensions);
        Assert.Contains("network_map", DesktopAgentLoop.Extensions);
        Assert.Contains("recipe", DesktopAgentLoop.Extensions);
    }
}
