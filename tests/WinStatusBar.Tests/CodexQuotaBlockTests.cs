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
