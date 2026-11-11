using System.Globalization;
using WinStatusBar.Core.Ai;
using WinStatusBar.Core.Ai.Codex;
using WinStatusBar.Core.Ai.Codex.ApiResume;
using Xunit;
using static WinStatusBar.Tests.CodexResumeCommandTests;

namespace WinStatusBar.Tests;

/// <summary>Fixa as duas culturas em pt-BR durante o teste (números "3,1 mil", "US$ 0,21").</summary>
internal sealed class PtBr : IDisposable
{
    private readonly CultureInfo _culture = CultureInfo.CurrentCulture, _ui = CultureInfo.CurrentUICulture;
    public PtBr() { CultureInfo.CurrentCulture = CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("pt-BR"); }
    public void Dispose() { CultureInfo.CurrentCulture = _culture; CultureInfo.CurrentUICulture = _ui; }
}

public class ApiResumeTextTests
{
    private static readonly DateTimeOffset T1031 = new(new DateTime(2026, 11, 10, 10, 31, 0, DateTimeKind.Local));

    private static ApiResumeJob Job(ApiResumeState state, double minutes = 14, string title = Title1)
    {
        var job = ApiResumeStoreTests.Job(state);
        job.Title = title;
        job.StartedAt = T1031;
        job.EndedAt = T1031.AddMinutes(minutes);
        return job;
    }

    [Fact]
    public void RowTextsMatchTheSpec()
    {
        using var _ = new PtBr();
        Assert.Equal("Parou em ter 10/11 às 00:31 · gpt-5.6-sol · nível baixo · pasta PROJEÇÃO", ApiResumeText.StoppedLine(Stop()));
        Assert.Equal("Contexto: cerca de 79 mil tokens (cada chamada à API reenvia esse contexto)", ApiResumeText.ContextLine(79000));
        Assert.Equal("", ApiResumeText.ContextLine(null));
        Assert.Equal("Agora está marcada como gpt-5.6-sol · médio. Vai continuar com o modelo e o nível do turno que parou: gpt-5.6-sol · baixo. Depois, a conversa fica marcada assim no app.",
            ApiResumeText.RemarkedLine(Stop()));
        Assert.Null(ApiResumeText.RemarkedLine(Stop() with { CurrentEffort = "low" }));
        Assert.Equal("Não dá para continuar: modelo fora da API", ApiResumeText.Unavailable(ApiResumeErrors.For(ApiResumeProblem.ModelNotInApi, "x")));
    }

    [Fact]
    public void SummaryCountsStops()
    {
        Assert.Equal("Nenhuma conversa parou por falta de cota nos últimos 14 dias", ApiResumeText.Summary([]));
        Assert.Equal("1 conversa parou por falta de cota: Analisar processo de Fulano de Tal", ApiResumeText.Summary([Stop()]));
        Assert.Equal("2 conversas pararam por falta de cota: Analisar processo de Fulano de Tal; Responder saudação",
            ApiResumeText.Summary([Stop(), Stop(id: Id2, title: "Responder saudação")]));
    }

    [Fact]
    public void LiveLinesMatchTheSpec()
    {
        using var _ = new PtBr();
        Assert.Equal("Tokens deste turno: 120 mil de entrada (98 mil em cache) · 3,1 mil de saída", ApiResumeText.Tokens(new TokenUsage(120000, 98000, 3100, 0)));
        Assert.Equal("Tokens deste turno: aguardando o registro do Codex", ApiResumeText.Tokens(null));
        Assert.Equal("Gasto: US$ 0,21 desde 10:31 (painel da OpenAI às 10:42; atrasa alguns minutos e inclui outro uso da mesma organização)",
            ApiResumeText.Spend(0.21m, T1031, T1031.AddMinutes(11)));
        Assert.Equal("Gasto: aguardando o painel da OpenAI (desde 10:31)", ApiResumeText.Spend(null, T1031, null));
        Assert.Equal("US$ 0,62 (pode mudar alguns centavos nos próximos minutos)", ApiResumeText.FinalSpend(0.62m));
        Assert.Equal("US$: o painel da OpenAI ainda não informou o custo (a barra confere de novo até 3 h depois do fim)", ApiResumeText.FinalSpend(null));
        Assert.Equal("Fila: 1 de 2", ApiResumeText.Queue(1, 2));
        Assert.Equal("gpt-5.6-sol · baixo (o mesmo do turno que parou)", ApiResumeText.ConfirmModel("gpt-5.6-sol", "low"));
        Assert.Equal("Chave: arquivo protegido do Windows (openai-key.dpapi)", ApiResumeText.KeySource(OpenAiApiKey.FileSourceName));
        Assert.Equal("Chave: não encontrada", ApiResumeText.KeySource(null));
        Assert.Equal("Organização cobrada pela chave: org-x · conta do painel de faturamento: não informada", ApiResumeText.Organization("org-x", null));
    }

    [Fact]
    public void EndStatesAndNoticesMatchTheSpec()
    {
        using var _ = new PtBr();
        var now = T1031.AddMinutes(20);
        var done = Job(ApiResumeState.Completed);
        Assert.Equal("Concluída pela API em 14 min", ApiResumeText.State(done, now));
        done.Summary = "pronto";
        Assert.Equal((Title1, "Concluída pela API em 14 min\npronto"), ApiResumeText.Notice(done, now));
        done.EndedWhileBarClosed = true;
        Assert.StartsWith("Enquanto a barra estava fechada: Concluída", ApiResumeText.Notice(done, now).Message);

        Assert.Equal("Parada por você após 6 min; o que já foi feito ficou na conversa.", ApiResumeText.State(Job(ApiResumeState.Stopped, 6), now));
        var failed = Job(ApiResumeState.Failed);
        failed.Error = ApiResumeErrors.For(ApiResumeProblem.ApiNoCredits);
        Assert.Equal("Não continuou: API sem créditos", ApiResumeText.State(failed, now));
        Assert.EndsWith(failed.Error.Message, ApiResumeText.Notice(failed, now).Message);
        Assert.Equal("Cota do Codex liberada: aguardando você", ApiResumeText.State(Job(ApiResumeState.QuotaBackWaitingUser), now));
        Assert.Equal("A cota do Codex voltou. 3 conversa(s) esperam na fila da API; elas não vão continuar sozinhas.", ApiResumeText.QuotaBackNotice(3));
        Assert.StartsWith("Feche o app do Codex por completo", ApiResumeText.WaitingFor(true));
        Assert.StartsWith("Outro programa do Codex está com esta conversa aberta", ApiResumeText.WaitingFor(false));

        var running = Job(ApiResumeState.Running, title: "Responder saudação");
        Assert.Equal("Pela API: Responder saudação · Executando há 6 min", ApiResumeText.Banner(running, T1031.AddMinutes(6)));
        Assert.Equal("Na fila", ApiResumeText.State(Job(ApiResumeState.Queued), now));
    }

    [Theory]
    [InlineData(ApiResumeState.Queued, false, false, "RemoveFromQueue")]
    [InlineData(ApiResumeState.WaitingForApp, false, false, "RemoveFromQueue")]
    [InlineData(ApiResumeState.QuotaBackWaitingUser, false, false, "AllowWhenQuotaBack,RemoveFromQueue")]
    [InlineData(ApiResumeState.Starting, true, false, "Stop")]
    [InlineData(ApiResumeState.Running, true, false, "Stop")]
    [InlineData(ApiResumeState.Stopping, true, false, "")]
    [InlineData(ApiResumeState.Stopping, true, true, "ForceKill")]
    [InlineData(ApiResumeState.Completed, true, false, "OpenInCodex,ContinueAgain,Dismiss")]
    [InlineData(ApiResumeState.Completed, false, false, "OpenInCodex,Dismiss")]
    [InlineData(ApiResumeState.Stopped, true, false, "OpenInCodex,Dismiss")]
    [InlineData(ApiResumeState.Failed, true, false, "Retry,Dismiss")]
    [InlineData(ApiResumeState.Blocked, true, false, "Dismiss")]
    [InlineData(ApiResumeState.AlreadyContinued, true, false, "Dismiss")]
    public void ActionsFollowTheState(ApiResumeState state, bool blocked, bool forceOffered, string expected)
    {
        var job = Job(state);
        job.ForceOffered = forceOffered;
        Assert.Equal(expected, string.Join(",", ApiResumeText.Actions(job, blocked)));
        Assert.Equal("Encerrar à força", ApiResumeText.ActionLabel(ApiResumeAction.ForceKill));
        Assert.Equal("Continuar pela API mesmo assim", ApiResumeText.ActionLabel(ApiResumeAction.AllowWhenQuotaBack));
    }

    private static readonly ModelCheck Ok = new(ModelCheckResult.Ok, "org", null, T1031);

    [Fact]
    public void EvaluateMarksOnlyUsableStops()
    {
        var free = ApiResumeCandidates.Evaluate(Stop(), null, Ok, _ => true, ThreadLockState.Free, false, null);
        Assert.True(free.CanMark);
        Assert.False(free.OpenInApp);
        var held = ApiResumeCandidates.Evaluate(Stop(), null, Ok, _ => true, ThreadLockState.Held, false, null);
        Assert.True(held.OpenInApp && held.CanMark);
        Assert.False(ApiResumeCandidates.Evaluate(Stop(), null, Ok, _ => true, ThreadLockState.Free, true, null).CanMark);
        Assert.True(ApiResumeCandidates.Evaluate(Stop(), null, Ok with { Result = ModelCheckResult.OtherError }, _ => true, ThreadLockState.Free, false, null).CanMark);
        Assert.Equal("modelo fora da API", ApiResumeCandidates.Evaluate(Stop(), null, Ok with { Result = ModelCheckResult.ModelNotFound }, _ => true, ThreadLockState.Free, false, null).Unavailable!.Short);
        Assert.Equal("chave recusada", ApiResumeCandidates.Evaluate(Stop(), null, Ok with { Result = ModelCheckResult.KeyRejected }, _ => true, ThreadLockState.Free, false, null).Unavailable!.Short);
        Assert.Equal("pasta inacessível", ApiResumeCandidates.Evaluate(Stop(), null, Ok, d => d != Cwd, ThreadLockState.Free, false, null).Unavailable!.Short);
        Assert.Equal("modelo da barra", ApiResumeCandidates.Evaluate(Stop(model: "codexias-claude-opus"), null, Ok, _ => true, ThreadLockState.Free, false, null).Unavailable!.Short);
        Assert.Equal([@"Z:\"], ApiResumeCandidates.Evaluate(Stop(), null, Ok, d => d != @"Z:\", ThreadLockState.Free, false, null).MissingExtraDirs);
    }

    [Fact]
    public void CandidatesLogCounts()
    {
        var c = ApiResumeCandidates.Evaluate(Stop(), null, Ok, _ => true, ThreadLockState.Free, false, null);
        Assert.Equal("Continuar pela API: candidatas=1 (01aa0000 «Analisar processo de Fulano de Tal» gpt-5.6-sol/low parou 10/11 00:31); excluídas: interrompidas por você=3, exec/scripts=2, subagentes=1, barra=4, outras=0",
            ApiResumeCandidates.Log([c], new QuotaStopExclusions(3, 2, 1, 4, 0)));
    }

    [Fact]
    public void ThousandsAndMinutes()
    {
        using var _ = new PtBr();
        Assert.Equal(("443", "3,1 mil", "79 mil", "1,2 mi"),
            (ApiResumeText.Thousands(443), ApiResumeText.Thousands(3100), ApiResumeText.Thousands(79000), ApiResumeText.Thousands(1240000)));
        Assert.Equal(("menos de 1 min", "14 min", "1 h 15 min"),
            (ApiResumeText.Minutes(TimeSpan.FromSeconds(30)), ApiResumeText.Minutes(TimeSpan.FromMinutes(14)), ApiResumeText.Minutes(TimeSpan.FromMinutes(75))));
        Assert.True(ApiResumeText.ShowSection(true, false) && ApiResumeText.ShowSection(false, true) && !ApiResumeText.ShowSection(false, false));
        Assert.True(ApiResumeText.ShowParallelChoice(2) && !ApiResumeText.ShowParallelChoice(1));
    }

    [Fact]
    public void QueuePositionCountsOnlyUnfinished()
    {
        var a = Job(ApiResumeState.Running); a.CreatedAt = T1031;
        var b = Job(ApiResumeState.Queued); b.CreatedAt = T1031.AddMinutes(1);
        var c = Job(ApiResumeState.Completed); c.CreatedAt = T1031.AddMinutes(-5);
        Assert.Equal((2, 2), ApiResumeText.QueuePosition([c, b, a], b.Id));
    }
}
