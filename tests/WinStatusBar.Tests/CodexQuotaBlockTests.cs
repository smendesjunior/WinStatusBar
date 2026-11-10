using System.Text.Json;
using WinStatusBar.Core.Ai;
using WinStatusBar.Core.Ai.Codex;
using Xunit;

namespace WinStatusBar.Tests;

public class CodexQuotaBlockTests
{
    // Formato real de account/rateLimits/read com os créditos do workspace esgotados (codex-cli, 10/11/2026).
    internal const string CreditsDepleted = """
    {"ordinaryUsageAllowed":false,
     "rateLimits":{"limitId":"codex","limitName":null,"normalModelSlug":null,
       "primary":{"usedPercent":0,"windowDurationMins":300,"resetsAt":1794292362},
       "secondary":{"usedPercent":100,"windowDurationMins":10080,"resetsAt":1794535289},
       "credits":{"hasCredits":false,"unlimited":false,"balance":"0"},
       "individualLimit":null,"spendControlReached":false,"planType":"team",
       "rateLimitReachedType":"workspace_owner_credits_depleted"},
     "rateLimitResetCredits":{"availableCount":0,"credits":[]},
     "rateLimitUpsell":{"banner_type":"workspace_owner_credits_depleted","title":"You're out of credits",
       "description":"Your workspace is out of credits. Add credits to continue using Codex.",
       "ctas":[{"action":"add_credits","label":"Add Credits"}],"reset_at":1794535289,"referral":null,"request_url":null}}
    """;

    // rate_limits de um token_count do registro, sem janelas, com o motivo do bloqueio.
    internal const string RolloutBlock = """
    {"limit_id":"premium","limit_name":null,"primary":null,"secondary":null,
     "credits":{"has_credits":false,"unlimited":false,"balance":"0"},"individual_limit":null,
     "spend_control_reached":null,"plan_type":null,"rate_limit_reached_type":"workspace_owner_credits_depleted"}
    """;

    internal static readonly DateTimeOffset Seen = new(new DateTime(2026, 11, 10, 10, 37, 0, DateTimeKind.Local));
    internal static readonly DateTimeOffset Returns = DateTimeOffset.FromUnixTimeSeconds(1794535289).ToLocalTime();

    internal static CodexAccess Official(DateTimeOffset seen, bool? allowed, string? reached = null, bool? spend = null) =>
        new(allowed, reached, spend, HasCredits: null, CreditsBalance: null, BannerTitle: null, BannerResetAt: null, seen, Official: true);

    internal static QuotaSnapshot Snapshot(DateTimeOffset seen, CodexAccess? access, double used5h = 0, double used7d = 100, DateTimeOffset? resets7d = null) => new()
    {
        Primary = new QuotaWindow(AgentKind.Codex, "5h", used5h, seen.AddHours(5), 300, seen, "t"),
        Secondary = new QuotaWindow(AgentKind.Codex, "7d", used7d, resets7d ?? Returns, 10080, seen, "t"),
        ObservedAt = seen,
        Access = access,
    };

    private static QuotaSnapshot? Parse(string json, DateTimeOffset at)
    {
        using var doc = JsonDocument.Parse(json);
        return CodexParsers.ParseRateLimits(doc.RootElement, "t", at);
    }

    [Fact]
    public void ReadsTheRealCreditsDepletedResponse()
    {
        var q = Parse(CreditsDepleted, Seen)!;
        var a = q.Access!;
        Assert.True(a.Official);
        Assert.False(a.OrdinaryUsageAllowed);
        Assert.Equal("workspace_owner_credits_depleted", a.ReachedType);
        Assert.False(a.SpendControlReached);
        Assert.False(a.HasCredits);
        Assert.Equal("0", a.CreditsBalance);
        Assert.Equal("You're out of credits", a.BannerTitle);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1794535289), a.BannerResetAt);
        Assert.Equal(Seen, a.ObservedAt);
        Assert.Equal(100, q.Secondary!.UsedPercent);
        Assert.True(q.IsBlocked(Seen));
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1794535289), q.BlockedUntil(Seen));
    }

    [Fact]
    public void BlockedEvenWithFreeWindows()
    {
        var q = Snapshot(Seen, Official(Seen, false), used5h: 0, used7d: 10);
        Assert.True(q.IsBlocked(Seen));
        Assert.Null(q.BlockedUntil(Seen));
    }

    [Fact]
    public void BlockOutlivesTheExpectedReturnUntilAnOfficialReadClearsIt()
    {
        var blocked = Parse(CreditsDepleted, Seen)!;
        var later = Returns.AddHours(1);
        Assert.True(blocked.IsBlocked(later));
        Assert.Equal(Returns, blocked.BlockedUntil(later));

        var windowsOnly = Snapshot(later, access: null, used7d: 0).WithDetailsFrom(blocked);
        Assert.True(windowsOnly.IsBlocked(later));

        var freed = Snapshot(later, Official(later, true), used7d: 0).WithDetailsFrom(blocked);
        Assert.False(freed.IsBlocked(later));
        Assert.Null(freed.BlockedUntil(later));
    }

    [Fact]
    public void RolloutBlockWithoutWindowsKeepsTheKnownWindows()
    {
        var at = Seen.AddMinutes(5);
        var rollout = Parse(RolloutBlock, at)!;
        Assert.Null(rollout.Primary);
        Assert.Null(rollout.Secondary);
        Assert.False(rollout.Access!.Official);
        Assert.Equal(at, rollout.Access.ObservedAt);
        Assert.Equal("workspace_owner_credits_depleted", rollout.Access.ReachedType);

        var merged = rollout.WithDetailsFrom(Snapshot(Seen, Official(Seen, true), used7d: 40));
        Assert.Equal(40, merged.Secondary!.UsedPercent);
        Assert.Same(rollout.Access, merged.Access);
        Assert.True(merged.IsBlocked(at));
    }

    [Fact]
    public void RolloutWithoutBlockFieldsCreatesNoAccess()
    {
        const string windows = """
        {"limit_id":"codex","primary":{"used_percent":12,"window_minutes":300,"resets_at":1794292362},
         "secondary":{"used_percent":30,"window_minutes":10080,"resets_at":1794535289},
         "credits":null,"spend_control_reached":null,"plan_type":null,"rate_limit_reached_type":null}
        """;
        var q = Parse(windows, Seen)!;
        Assert.Null(q.Access);
        Assert.False(q.IsBlocked(Seen));

        const string empty = """
        {"limit_id":"codex","limit_name":null,"primary":null,"secondary":null,"credits":null,"individual_limit":null,
         "spend_control_reached":null,"plan_type":null,"rate_limit_reached_type":null}
        """;
        Assert.Null(Parse(empty, Seen));
    }

    [Fact]
    public void OlderRolloutBlockDoesNotBeatANewerOfficialRelease()
    {
        var oldRollout = Parse(RolloutBlock, Seen)!;
        var official = Snapshot(Seen.AddMinutes(10), Official(Seen.AddMinutes(10), true), used7d: 0);
        var merged = oldRollout.WithDetailsFrom(official);
        Assert.Same(official.Access, merged.Access);
        Assert.False(merged.IsBlocked(Seen.AddMinutes(10)));
    }

    [Theory]
    [InlineData(false, "workspace_owner_credits_depleted", null, 0, 10, "os créditos do workspace acabaram")]
    [InlineData(false, "workspace_member_credits_depleted", null, 0, 10, "os créditos do workspace acabaram (o administrador precisa adicionar)")]
    [InlineData(false, "workspace_owner_usage_limit_reached", null, 0, 10, "o workspace atingiu o limite de uso definido")]
    [InlineData(false, "workspace_member_usage_limit_reached", null, 0, 10, "você atingiu o limite de uso definido pelo administrador")]
    [InlineData(false, "rate_limit_reached", null, 0, 10, "o limite de uso foi atingido")]
    [InlineData(true, null, true, 0, 10, "o limite de gastos do workspace foi atingido")]
    [InlineData(false, null, null, 0, 10, "a conta bloqueou o uso")]
    [InlineData(true, null, null, 100, 10, "a cota de 5 horas acabou")]
    [InlineData(true, null, null, 0, 100, "a cota semanal acabou")]
    public void ReasonForEachCause(bool allowed, string? reached, bool? spend, double used5h, double used7d, string expected)
    {
        var q = Snapshot(Seen, Official(Seen, allowed, reached, spend), used5h, used7d);
        Assert.Equal(expected, CodexQuotaText.Reason(CodexQuotaText.Cause(q, Seen)));
    }

    [Theory]
    [InlineData(false, null, null, 0, 10, "Uso bloqueado")]
    [InlineData(false, "workspace_member_usage_limit_reached", null, 0, 10, "Limite do workspace")]
    [InlineData(true, null, true, 0, 10, "Limite do workspace")]
    [InlineData(true, null, null, 100, 10, "Cota de 5h esgotada")]
    [InlineData(true, null, null, 0, 100, "Cota semanal esgotada")]
    public void PillForEachKindOfBlock(bool allowed, string? reached, bool? spend, double used5h, double used7d, string expected)
    {
        var q = Snapshot(Seen, Official(Seen, allowed, reached, spend), used5h, used7d);
        Assert.Equal(expected, CodexQuotaText.Pill(q, Seen));
    }

    [Fact]
    public void PillLineAndBannerForTheRealBlock()
    {
        var q = Parse(CreditsDepleted, Seen)!;
        Assert.Equal("Créditos esgotados", CodexQuotaText.Pill(q, Seen));
        Assert.Equal("Codex bloqueado: créditos do workspace esgotados · volta qui 12/11 às 23:01", CodexQuotaText.BlockedLine(q, Seen));

        var b = CodexQuotaText.Banner(q, Seen)!;
        Assert.Equal("O Codex está bloqueado pela conta", b.Title);
        Assert.Equal("Motivo: os créditos do workspace acabaram.", b.Reason);
        Assert.Equal("Volta qui 12/11 às 23:01 (informado pela conta).", b.Returns);
        Assert.Equal("Enquanto isso, o app do Codex não envia mensagens, com nenhum modelo.", b.Effect);
        Assert.Equal("Confirmado pela conta às 10:37.", b.Confirmed);
        Assert.Equal("ordinaryUsageAllowed=false · rateLimitReachedType=workspace_owner_credits_depleted · saldo de créditos 0", b.Technical);

        var fromRollout = Parse(RolloutBlock, Seen)!;
        Assert.Equal("Visto no registro de uma conversa às 10:37; a conta ainda não confirmou.", CodexQuotaText.Banner(fromRollout, Seen)!.Confirmed);

        var free = Snapshot(Seen, Official(Seen, true), used7d: 10);
        Assert.Null(CodexQuotaText.Banner(free, Seen));
        Assert.Equal("", CodexQuotaText.BlockedLine(free, Seen));
    }

    [Fact]
    public void ReturnsTextHasThreeForms()
    {
        var q = Parse(CreditsDepleted, Seen)!;
        var passed = Returns.AddMinutes(5);
        Assert.Equal("O horário previsto (12/11 às 23:01) já passou; aguardando a conta confirmar a liberação.", CodexQuotaText.ReturnsText(q, passed));
        Assert.EndsWith("· aguardando a conta confirmar a liberação", CodexQuotaText.BlockedLine(q, passed));

        var noForecast = Snapshot(Seen, Official(Seen, false, "workspace_owner_credits_depleted"), used7d: 10);
        Assert.Equal("A conta não informou quando volta.", CodexQuotaText.ReturnsText(noForecast, Seen));
        Assert.EndsWith("· sem previsão informada pela conta", CodexQuotaText.BlockedLine(noForecast, Seen));
    }

    [Fact]
    public void StoppedCaptionRefusedSendAndLog()
    {
        Assert.Equal("Parada pela cota do Codex · créditos do workspace esgotados", CodexQuotaText.StoppedCaption("workspace_owner_credits_depleted"));
        Assert.Equal("Parada pela cota do Codex", CodexQuotaText.StoppedCaption(null));
        Assert.Equal("O Codex está sem cota (créditos do workspace esgotados); o app não confirmou resposta",
            CodexQuotaText.ChatRefused("créditos do workspace esgotados"));
        Assert.Equal("Codex cota: uso bloqueado (workspace_owner_credits_depleted) até 12/11 23:01",
            CodexQuotaText.BlockLog(Parse(CreditsDepleted, Seen)!, Seen));
        Assert.Equal("Codex cota: uso bloqueado (ordinaryUsageAllowed=false) até horário não informado",
            CodexQuotaText.BlockLog(Snapshot(Seen, Official(Seen, false), used7d: 10), Seen));
    }

    [Fact]
    public void DayDateIsPortugueseAndLocal()
    {
        Assert.Equal("ter 10/11", CodexQuotaText.DayDate(new DateTimeOffset(new DateTime(2026, 11, 10, 0, 31, 0, DateTimeKind.Local))));
        Assert.Equal("qui 12/11", CodexQuotaText.DayDate(DateTimeOffset.FromUnixTimeSeconds(1794535289)));
    }

    // ---- Barra: menu da cota, dica e etiqueta (A10; WinUI, por isso lê o código) ----

    [Fact]
    public void BarShowsTheBlockInTheMenuTheTooltipAndThePill()
    {
        string view = RepoSource.Read("src", "WinStatusBar", "Views", "QuotaDetailsView.cs");
        int banner = view.IndexOf("CodexQuotaText.Banner(q, now)", StringComparison.Ordinal);
        int noWindows = view.IndexOf("if (q?.Primary is null && q?.Secondary is null)", StringComparison.Ordinal);
        Assert.True(banner >= 0 && banner < noWindows, $"aviso={banner} semJanelas={noWindows}");
        Assert.Contains("Há saldo nesta janela, mas o bloqueio da conta vale para todos os modelos", view);

        Assert.Contains("CodexQuotaText.BlockedLine(codex, now)", RepoSource.Read("src", "WinStatusBar", "ViewModels", "BarViewModel.cs"));
        Assert.Contains("kind == AgentKind.Codex && quota?.IsBlocked(now) == true ? CodexQuotaText.Pill(quota, now)",
            RepoSource.Read("src", "WinStatusBar", "Services", "MonitoringHub.cs"));
    }

    // ---- CodexProvider (A6) ----

    [Fact]
    public void OldRolloutLineReadAgainDoesNotBeatTheOfficialRead()
    {
        var at = DateTimeOffset.Parse("2026-11-10T13:37:00Z");
        var official = Parse(CreditsDepleted, at)!;
        const string oldLine = """
        {"limit_id":"codex","primary":{"used_percent":87,"window_minutes":300,"resets_at":1794292362},
         "secondary":{"used_percent":100,"window_minutes":10080,"resets_at":1794535289},"rate_limit_reached_type":null}
        """;
        var old = Parse(oldLine, DateTimeOffset.Parse("2026-11-10T03:30:59Z"))!;
        Assert.Same(official, CodexProvider.MergeQuota(official, old, official: false));

        var newer = Parse(RolloutBlock, DateTimeOffset.Parse("2026-11-10T13:42:00Z"))!;
        var merged = CodexProvider.MergeQuota(official, newer, official: false);
        Assert.Equal(100, merged.Secondary!.UsedPercent);
        Assert.Same(newer.Access, merged.Access);
    }

    [Fact]
    public void AsksForAnOfficialReadRightWhenTheExpectedReturnPasses()
    {
        var blocked = Parse(CreditsDepleted, Seen)!;
        Assert.True(CodexProvider.ReturnDue(blocked, Returns.AddMinutes(-5), Returns.AddSeconds(1)));
        Assert.True(CodexProvider.ReturnDue(Snapshot(Seen, access: null), Returns.AddMinutes(-5), Returns.AddSeconds(1)));
        Assert.False(CodexProvider.ReturnDue(blocked, Returns.AddMinutes(1), Returns.AddMinutes(2)));
        Assert.False(CodexProvider.ReturnDue(blocked, Returns.AddMinutes(-5), Returns.AddMinutes(-1)));
        Assert.False(CodexProvider.ReturnDue(Snapshot(Seen, Official(Seen, true), used7d: 10), Returns.AddMinutes(-5), Returns.AddSeconds(1)));
        Assert.False(CodexProvider.ReturnDue(null, Returns.AddMinutes(-5), Returns.AddSeconds(1)));
    }

    [Fact]
    public void ProviderLogsWhenTheBlockStartsAndEnds()
    {
        using var provider = new CodexProvider();
        var log = new List<string>();
        provider.Diagnostic += log.Add;
        provider.ApplyQuota(Parse(CreditsDepleted, Seen)!, official: true, Seen);
        provider.ApplyQuota(Parse(CreditsDepleted, Seen.AddMinutes(10))!, official: true, Seen.AddMinutes(10));
        provider.ApplyQuota(Snapshot(Seen.AddHours(1), Official(Seen.AddHours(1), true), used7d: 10), official: true, Seen.AddHours(1));
        Assert.Equal(["Codex cota: uso bloqueado (workspace_owner_credits_depleted) até 12/11 23:01", "Codex cota: uso liberado"], log);
    }

    [Fact]
    public void FailedQueryKeepsTheKnownBlock()
    {
        using var provider = new CodexProvider();
        provider.ApplyQueryResult(new CodexAppServerClient.QueryResult(Parse(CreditsDepleted, Seen), null, null), Seen);
        provider.ApplyQueryResult(new CodexAppServerClient.QueryResult(null, null, "tempo esgotado"), Seen.AddMinutes(10));
        Assert.True(provider.Quota!.IsBlocked(Seen.AddMinutes(10)));
        Assert.Equal("tempo esgotado", provider.LastError);
        Assert.Equal("Confirmado pela conta às 10:37.", CodexQuotaText.Banner(provider.Quota, Seen.AddMinutes(10))!.Confirmed);
    }

    [Fact]
    public void FullWindowBlocksOnlyUntilItRenewsAndSpendControlBlocks()
    {
        var full = Snapshot(Seen, access: null);
        Assert.True(full.IsBlocked(Seen));
        Assert.Equal(Returns, full.BlockedUntil(Seen));
        Assert.False(full.IsBlocked(Returns));
        Assert.Null(full.BlockedUntil(Returns));

        Assert.True(Snapshot(Seen, Official(Seen, true, spend: true), used7d: 0).IsBlocked(Seen));
        Assert.False(Snapshot(Seen, Official(Seen, true, "rate_limit_reached"), used7d: 0).IsBlocked(Seen));
    }
}
