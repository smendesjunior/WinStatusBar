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

    private static AgentPrompt.Catalog One(string model, params string[] efforts) => new([new(model, model, efforts, efforts.FirstOrDefault())], model, efforts.FirstOrDefault(), null);

    [Theory]
    [InlineData("```json\n{\"target\":\"claude\",\"model\":\"haiku\",\"effort\":\"low\",\"reason\":\"rápido\"}\n```")]
    [InlineData("Escolha: {\"target\":\"claude\",\"model\":\"haiku\",\"effort\":\"low\",\"reason\":\"rápido\"} pronto.")]
    public void JsonWrappedInFencesOrTextIsStillRead(string response)
    {
        var choice = AutoDestinationRouter.Parse(response, Catalogs);
        Assert.NotNull(choice);
        Assert.Equal(AgentPrompt.Target.Claude, choice!.Target);
    }

    [Fact]
    public void InvalidAnswerIsNullSoTheNextAuxiliaryCanTry()
    {
        Assert.Null(AutoDestinationRouter.Parse("não sei", Catalogs));
        Assert.Null(AutoDestinationRouter.Parse("{\"target\":\"claude\",\"model\":\"inventado\",\"effort\":\"low\",\"reason\":\"x\"}", Catalogs));
    }

    [Fact]
    public void MiniInsideGeminiDoesNotMakeAProModelLight()
    {
        var catalogs = new Dictionary<AgentPrompt.Target, AgentPrompt.Catalog>
        {
            [AgentPrompt.Target.Gemini] = new([new("gemini-3-pro", "Gemini 3 Pro", [], null), new("gemini-3-flash", "Gemini 3 Flash", [], null)], "gemini-3-pro", null, null)
        };
        Assert.Equal("gemini-3-flash", Assert.Single(AutoDestinationRouter.Routers(catalogs)).Model);
        Assert.Equal("gemini-3-pro", InferredPlan.SimplestModel(new([new("gemini-3-pro", "Gemini 3 Pro", [], null)], "gemini-3-pro", null, null)).Model);
        Assert.Equal("gpt-5-mini", InferredPlan.SimplestModel(new([new("gpt-5", "GPT-5", [], null), new("gpt-5-mini", "GPT-5 mini", [], null)], "gpt-5", null, null)).Model);
    }

    [Fact]
    public void QiraCanBeChosenButNeverRoutesAndLocalIsNeverOffered()
    {
        var catalogs = new Dictionary<AgentPrompt.Target, AgentPrompt.Catalog>(Catalogs) { [AgentPrompt.Target.Qira] = QiraCatalog };
        string prompt = AutoDestinationRouter.Prompt("abra o Qira", catalogs);
        Assert.Contains("\"qira\"", prompt);
        Assert.DoesNotContain("local-model", prompt);
        Assert.DoesNotContain(AutoDestinationRouter.Routers(catalogs), r => r.Target == AgentPrompt.Target.Qira);
        var choice = AutoDestinationRouter.Parse("{\"target\":\"qira\",\"model\":\"hybrid\",\"effort\":null,\"reason\":\"pedido cita o Qira\"}", catalogs);
        Assert.Equal(AgentPrompt.Target.Qira, choice?.Target);
    }
    private static readonly AgentPrompt.Catalog QiraCatalog = new([new("hybrid", "Híbrido", [], null)], "hybrid", null, null);

    [Fact]
    public void LocalAndQiraAreLastResortOnlyWithoutAccountsOrApis()
    {
        var local = One("gemma:4b");
        Assert.Null(AutoDestinationRouter.LastResort(new Dictionary<AgentPrompt.Target, AgentPrompt.Catalog> { [AgentPrompt.Target.Local] = local, [AgentPrompt.Target.Copilot] = One("app") }));
        Assert.Equal(AgentPrompt.Target.Local, AutoDestinationRouter.LastResort(new Dictionary<AgentPrompt.Target, AgentPrompt.Catalog> { [AgentPrompt.Target.Local] = local, [AgentPrompt.Target.Qira] = QiraCatalog }));
        Assert.Equal(AgentPrompt.Target.Qira, AutoDestinationRouter.LastResort(new Dictionary<AgentPrompt.Target, AgentPrompt.Catalog> { [AgentPrompt.Target.Qira] = QiraCatalog }));
        // Codex com erro não conta como disponível: a IA local assume.
        Assert.Equal(AgentPrompt.Target.Local, AutoDestinationRouter.LastResort(new Dictionary<AgentPrompt.Target, AgentPrompt.Catalog>(Catalogs.Where(x => x.Key != AgentPrompt.Target.Claude))));
        Assert.Null(AutoDestinationRouter.LastResort(new Dictionary<AgentPrompt.Target, AgentPrompt.Catalog>()));
    }

    [Fact]
    public void PromptDescribesDestinationsAndSendsOnlyTheStartOfALongRequest()
    {
        string request = new string('a', AutoDestinationRouter.MaxRequestChars) + "FIM-DO-TEXTO";
        string prompt = AutoDestinationRouter.Prompt(request, Catalogs,
            new Dictionary<AgentPrompt.Target, string> { [AgentPrompt.Target.Claude] = "cota de 5 h: 90% usada" }, new(2, true, false));
        Assert.DoesNotContain("FIM-DO-TEXTO", prompt);
        Assert.Contains("\"requestChars\":" + request.Length, prompt);
        Assert.Contains("\"attachments\":2", prompt);
        Assert.Contains("\"mayEditFiles\":true", prompt);
        Assert.Contains("assinatura", prompt);
        Assert.Contains("cota de 5 h: 90% usada", prompt);
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
