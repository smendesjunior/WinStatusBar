using System.Text.Json;
using WinStatusBar.Core.Ai;
using WinStatusBar.Core.Ai.Claude;
using WinStatusBar.Core.Ai.Codex;
using WinStatusBar.Core.Settings;
using Xunit;

namespace WinStatusBar.Tests;

public class QuotaDetailsTests
{
    // Estrutura real de account/rateLimits/read (codex-cli 0.166), com reset avulso disponível.
    private const string CodexResult = """
    {"ordinaryUsageAllowed":true,
     "rateLimits":{"limitId":"codex","primary":{"usedPercent":63,"windowDurationMins":300,"resetsAt":1793823088},
       "secondary":{"usedPercent":24,"windowDurationMins":10080,"resetsAt":1794333894},
       "credits":{"hasCredits":false,"unlimited":false,"balance":"0"},"planType":"team"},
     "rateLimitsByLimitId":{
       "codex":{"limitId":"codex","primary":{"usedPercent":63,"windowDurationMins":300,"resetsAt":1793823088}},
       "codex_other":{"limitId":"codex_other","limitName":"GPT-5.5","primary":{"usedPercent":10,"windowDurationMins":300,"resetsAt":1793823088}}},
     "rateLimitResetCredits":{"availableCount":1,"credits":[{"id":"x","resetType":"codexRateLimits","status":"available",
       "grantedAt":1793574464,"expiresAt":1796166464,"title":"Full reset (Weekly + 5 hr)","description":"Thanks for using Codex!"}]}}
    """;

    [Fact]
    public void Codex_ParsesResetCreditsCreditsAndOtherLimits()
    {
        using var doc = JsonDocument.Parse(CodexResult);
        var q = CodexParsers.ParseRateLimits(doc.RootElement, "t")!;
        Assert.Equal(1, q.ResetCreditsAvailable);
        var c = Assert.Single(q.ResetCredits);
        Assert.Equal("x", c.Id);
        Assert.Equal("Full reset (Weekly + 5 hr)", c.Title);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1796166464), c.ExpiresAt);
        Assert.Equal("Sem créditos extras", q.CreditsText);
        var extra = Assert.Single(q.AdditionalWindows); // o limite principal ("codex") não é repetido
        Assert.Equal("GPT-5.5 · 5h", extra.Label);
        Assert.Equal(10, extra.UsedPercent);
    }

    [Theory]
    [InlineData("reset", false)]
    [InlineData("alreadyRedeemed", false)]
    [InlineData("nothingToReset", true)]
    [InlineData("noCredit", true)]
    [InlineData(null, true)]
    public void ResetRequiresConfirmedOutcome(string? outcome, bool error)
        => Assert.Equal(error, CodexAppServerClient.ResetOutcomeError(outcome) is not null);

    [Fact]
    public void ResetRequiresIdAvailabilityAndUnexpiredCredit()
    {
        var now = DateTimeOffset.UtcNow;
        var credit = new ResetCredit("Limites", null, now.AddHours(1), "available") { Id = "credit-a" };
        Assert.True(credit.CanConsume(now));
        Assert.False((credit with { Id = null }).CanConsume(now));
        Assert.False((credit with { Status = "unknown" }).CanConsume(now));
        Assert.False((credit with { ExpiresAt = now.AddMinutes(-1) }).CanConsume(now));
    }

    [Fact]
    public void Codex_RolloutUpdateKeepsDetailsFromOfficialQuery()
    {
        using var doc = JsonDocument.Parse(CodexResult);
        var official = CodexParsers.ParseRateLimits(doc.RootElement, "app-server")!;
        var s = new AgentSession { Kind = AgentKind.Codex, Id = "t" };
        var live = CodexParsers.ApplyRolloutLine(s, """{"timestamp":"2026-11-04T18:00:00Z","type":"event_msg","payload":{"type":"token_count","rate_limits":{"primary":{"used_percent":70.0,"window_minutes":300,"resets_at":1793823088},"secondary":{"used_percent":25.0,"window_minutes":10080,"resets_at":1794333894}}}}""")!;
        var merged = live.WithDetailsFrom(official);
        Assert.Equal(70, merged.Primary!.UsedPercent);   // percentual em tempo real
        Assert.Equal(1, merged.ResetCreditsAvailable);   // detalhes da consulta oficial preservados
        Assert.Equal("team", merged.PlanType);
    }

    [Fact]
    public void Claude_ParsesExtraWindowsAdaptivelyAndExtraUsage()
    {
        string json = """
        {"five_hour":{"utilization":12.0,"resets_at":"2026-11-04T20:00:00+00:00"},
         "seven_day":{"utilization":33.0,"resets_at":"2026-11-08T10:00:00+00:00"},
         "seven_day_opus":{"utilization":40.0,"resets_at":"2026-11-08T10:00:00+00:00"},
         "seven_day_sonnet":{"utilization":5.0,"resets_at":null},
         "seven_day_novo_modelo":{"utilization":1.0},
         "extra_usage":{"is_enabled":true,"monthly_limit":50,"used_credits":12.5,"utilization":25.0}}
        """;
        var (windows, extra) = ClaudeUsageClient.ParseExtras(json, DateTimeOffset.Now);
        Assert.Equal(["Opus 7d", "Sonnet 7d", "Novo Modelo 7d"], windows.Select(w => w.Label));
        Assert.Equal(40, windows[0].UsedPercent);
        Assert.Equal("Uso extra: 12,5 de 50 créditos no mês", extra);
    }

    [Fact]
    public void Claude_ExtraUsageDisabled()
    {
        var (_, extra) = ClaudeUsageClient.ParseExtras("""{"extra_usage":{"is_enabled":false}}""", DateTimeOffset.Now);
        Assert.Equal("Uso extra: desativado", extra);
    }

    [Fact]
    public void Settings_MigratesQuotaRefreshToRealTime_AndClampsZero()
    {
        var old = new AppSettings { CodexQuotaIntervalMin = 10, ClaudeQuotaIntervalMin = 5, SchemaVersion = 1 };
        old.Normalize();
        Assert.Equal(0, old.CodexQuotaIntervalMin);
        Assert.Equal(0, old.ClaudeQuotaIntervalMin);
        var chosen = new AppSettings { CodexQuotaIntervalMin = 15, ClaudeQuotaIntervalMin = 0, SchemaVersion = 2 };
        chosen.Normalize();
        Assert.Equal(15, chosen.CodexQuotaIntervalMin); // escolha do usuário respeitada após a migração
        Assert.Equal(TimeSpan.FromMinutes(1), AppSettings.QuotaPoll(0));
        Assert.Equal(TimeSpan.FromMinutes(15), AppSettings.QuotaPoll(15));
    }

    [Fact]
    public void AgentNamesAndKeys()
    {
        Assert.Equal("Claude Code", AgentKind.Claude.Display());
        Assert.Equal("IA local", AgentKind.Local.Display());
        Assert.Equal("local", AgentKind.Local.Key());
    }
}
