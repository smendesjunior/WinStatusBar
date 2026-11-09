using WinStatusBar.Core.Ai;
using Xunit;

namespace WinStatusBar.Tests;

public class AutoDestinationRouterTests
{
    private static readonly IReadOnlyDictionary<AgentPrompt.Target, AgentPrompt.Catalog> Catalogs =
        new Dictionary<AgentPrompt.Target, AgentPrompt.Catalog>
        {
            [AgentPrompt.Target.Claude] = new([new("haiku", "Haiku", ["low", "high"], "low")], "haiku", "low", null),
            [AgentPrompt.Target.Codex] = new([new("mini", "Mini", ["low"], "low")], "mini", "low", "indisponível"),
            [AgentPrompt.Target.Local] = new([new("local-model", "Local", [], null)], "local-model", null, null)
        };
    private static readonly AutoDestinationRouter.Choice Fallback = new(AgentPrompt.Target.Codex, "default", "high", "fallback");

    [Fact]
    public void PromptContainsRequestAndOnlyAvailableCatalogs()
    {
        string prompt = AutoDestinationRouter.Prompt("pedido com \"aspas\"", Catalogs);
        Assert.Contains("haiku", prompt);
        Assert.DoesNotContain("local-model", prompt);
        Assert.DoesNotContain("mini", prompt);
        Assert.Contains("target", prompt);
        Assert.Contains("effort", prompt);
        Assert.Contains("pedido com", prompt);
    }

    [Theory]
    [InlineData("{\"target\":\"claude\",\"model\":\"haiku\",\"effort\":\"low\",\"reason\":\"rápido\"}", true)]
    [InlineData("{\"target\":\"local\",\"model\":\"local-model\",\"effort\":null,\"reason\":\"local\"}", false)]
    [InlineData("{\"target\":\"codex\",\"model\":\"mini\",\"effort\":\"low\",\"reason\":\"x\"}", false)]
    [InlineData("{\"target\":\"claude\",\"model\":\"inventado\",\"effort\":\"low\",\"reason\":\"x\"}", false)]
    [InlineData("{\"target\":\"claude\",\"model\":\"haiku\",\"effort\":\"max\",\"reason\":\"x\"}", false)]
    [InlineData("{\"target\":\"claude\",\"model\":\"haiku\",\"effort\":null,\"reason\":\"x\"}", false)]
    [InlineData("{\"target\":\"local\",\"model\":\"local-model\",\"effort\":\"high\",\"reason\":\"x\"}", false)]
    [InlineData("{\"target\":\"auto\",\"model\":\"haiku\",\"effort\":\"low\",\"reason\":\"x\"}", false)]
    [InlineData("{\"target\":\"claude\",\"model\":\"haiku\",\"effort\":\"low\",\"reason\":\"\"}", false)]
    [InlineData("```json\n{}\n```", false)]
    [InlineData("[]", false)]
    [InlineData(null, false)]
    public void ResolveValidatesOrReturnsExactFallback(string? response, bool valid)
    {
        var result = AutoDestinationRouter.Resolve(response, Catalogs, Fallback);
        Assert.Equal(valid, result != Fallback);
    }

    [Fact]
    public void ShortReasonCannotInjectMoreLines()
    {
        var result = AutoDestinationRouter.Resolve("{\"target\":\"claude\",\"model\":\"haiku\",\"effort\":\"low\",\"reason\":\"rápido\\npara isso\"}", Catalogs, Fallback);
        Assert.DoesNotContain('\n', result.Reason);
    }

    [Fact]
    public void RouterExcludesLocalAndUnavailableProviders()
    {
        var routers = AutoDestinationRouter.Routers(Catalogs);
        Assert.Equal(AgentPrompt.Target.Claude, routers[0].Target);
        Assert.DoesNotContain(routers, r => r.Target == AgentPrompt.Target.Local);
        Assert.DoesNotContain(routers, r => r.Target == AgentPrompt.Target.Codex);
    }

    [Theory]
    [InlineData(AgentPrompt.Target.Claude, "claude-haiku-5-5")]
    [InlineData(AgentPrompt.Target.Codex, "gpt-mini")]
    [InlineData(AgentPrompt.Target.Gemini, "gemini-flash-lite")]
    [InlineData(AgentPrompt.Target.Api, "gpt-nano")]
    public void RouterWorksWithOnlyOneAvailableProvider(AgentPrompt.Target target, string model)
    {
        var catalogs = new Dictionary<AgentPrompt.Target, AgentPrompt.Catalog> { [target] = new([new(model, model, [], null)], model, null, null) };
        var router = Assert.Single(AutoDestinationRouter.Routers(catalogs));
        Assert.Equal(target, router.Target);
        Assert.Equal(model, router.Model);
        Assert.Null(router.Effort);
    }
    [Fact]
    public void LocalIsExcludedFromAutoButRemainsManuallyAvailable()
    {
        var catalogs = new Dictionary<AgentPrompt.Target, AgentPrompt.Catalog> { [AgentPrompt.Target.Local] = new([new("gemma:26b", "26b", [], null), new("gemma:1b", "1b", [], null)], "gemma:26b", null, null) };
        Assert.Empty(AutoDestinationRouter.Routers(catalogs));
        Assert.True(AutoDestinationRouter.Available(catalogs[AgentPrompt.Target.Local]));
        Assert.False(AutoDestinationRouter.Eligible(AgentPrompt.Target.Local, catalogs[AgentPrompt.Target.Local]));
        Assert.Equal(AgentPrompt.Target.Local, AgentPrompt.Parse("local"));
    }
    [Fact]
    public void CopilotIsNeverAnAuxiliaryRouter()
    {
        var catalogs = new Dictionary<AgentPrompt.Target, AgentPrompt.Catalog> { [AgentPrompt.Target.Copilot] = new([new("app", "app", [], null)], "app", null, null) };
        Assert.Empty(AutoDestinationRouter.Routers(catalogs));
        Assert.True(AutoDestinationRouter.Eligible(AgentPrompt.Target.Copilot, catalogs[AgentPrompt.Target.Copilot]));
    }

    [Fact]
    public void CopilotWithoutRunnerReportsUnavailableInsteadOfAutomatingTheApp()
    {
        var previous = CopilotAppBridge.Runner;
        CopilotAppBridge.Runner = null;
        try
        {
            Assert.NotNull(CopilotAppBridge.Catalog().Error);
            var result = CopilotAppBridge.RunAsync("oi", AgentPrompt.Options.None, _ => { }, CancellationToken.None).Result;
            Assert.NotNull(result.Error);
        }
        finally { CopilotAppBridge.Runner = previous; }
    }
}
