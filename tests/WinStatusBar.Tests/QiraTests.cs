using WinStatusBar.Core.Ai;
using WinStatusBar.Core.Settings;
using Xunit;

namespace WinStatusBar.Tests;

public class QiraTests
{
    [Fact]
    public void SelectedModeMustBeConfirmedBeforeSending()
    {
        Assert.Equal("híbrido", QiraBridge.ModeLabel("HYBRID_MODE"));
        Assert.Equal("local", QiraBridge.ModeLabel("LOCAL_MODE"));
        Assert.Null(QiraBridge.ModeError("HYBRID_MODE"));
        Assert.Null(QiraBridge.ModeError("LOCAL_MODE", "LOCAL_MODE"));
        Assert.Contains("modo híbrido", QiraBridge.ModeError("LOCAL_MODE"));
        Assert.Contains("Nada foi enviado", QiraBridge.ModeError(null, "LOCAL_MODE"));
        var previous = QiraBridge.Runner;
        try
        {
            QiraBridge.Runner = (_, _, _, _, _) => Task.FromResult(new AgentPrompt.Result("", null, null));
            Assert.Equal(["hybrid"], QiraBridge.Catalog().Models.Select(m => m.Id).ToArray());
        }
        finally { QiraBridge.Runner = previous; }
    }

    [Fact]
    public void QiraIsAnInBarTargetOutsideAutomaticRouting()
    {
        Assert.Equal(AgentPrompt.Target.Qira, AgentPrompt.Parse("qira"));
        Assert.Equal("qira", AgentPrompt.Key(AgentPrompt.Target.Qira));
        Assert.Equal(5, (int)AgentPrompt.Target.Copilot); // valores anteriores preservados
        var ready = new AgentPrompt.Catalog([new("hybrid", "Híbrido", [], null)], "hybrid", null, null);
        Assert.False(AutoDestinationRouter.Eligible(AgentPrompt.Target.Qira, ready));
    }

    [Fact]
    public void StreamChunksAccumulateTextAndHandler()
    {
        static string Chunk(string status, string inner) => System.Text.Json.JsonSerializer.Serialize(new { jobId = 1, status, data = new { text = inner } });
        var acc = new QiraBridge.Accumulator();
        acc.Add(QiraBridge.Parse(Chunk("in_progress", """{"response":"Azure Agent","compact":false,"type":"handler"}"""))!);
        acc.Add(QiraBridge.Parse(Chunk("in_progress", """{"type":"status","response":"Counting tokens..."}"""))!);
        Assert.True(acc.Add(QiraBridge.Parse(Chunk("in_progress", """{"type":"text","response":"Dezessete"}"""))!));
        acc.Add(QiraBridge.Parse(Chunk("in_progress", """{"type":"timings","response":"{}"}"""))!);
        acc.Add(QiraBridge.Parse(Chunk("complete", """{"type":"text","response":"Dezessete vezes três é 51.","searchUrls":["https://example.com/a"]}"""))!);
        Assert.Equal("Azure Agent", acc.Handler);
        Assert.True(acc.Done);
        Assert.Null(acc.Failure);
        Assert.Equal("Dezessete vezes três é 51.", acc.Text);
        Assert.Contains("https://example.com/a", acc.Final());
    }

    [Fact]
    public void FailedEmptyChunkIsReportedAsFailure()
    {
        var acc = new QiraBridge.Accumulator();
        acc.Add(QiraBridge.Parse("""{"jobId":2,"status":"failed","data":{"text":"{\"response\":\"\"}"}}""")!);
        Assert.True(acc.Done);
        Assert.NotNull(acc.Failure);
        Assert.Equal("", acc.Text);
    }

    [Fact]
    public void CloudCreditsAreReadFromQiraQuota()
    {
        var now = DateTimeOffset.Parse("2026-11-07T21:00:00Z");
        var q = QiraBridge.ParseQuota("""[{"feature":"qira-credits","percentageRemaining":99.0,"billingCycleStart":"2026-11-01T00:00:00.000Z","renewDate":"2026-12-01T00:00:00.000Z","consumption":1,"limit":100,"unlimited":false}]""", now);
        Assert.NotNull(q);
        Assert.Equal(1, q!.Used);
        Assert.Equal(100, q.Limit);
        Assert.Equal(1, q.PercentUsed, 3);
        Assert.Equal(DateTimeOffset.Parse("2026-12-01T00:00:00Z"), q.Renews);
        Assert.StartsWith("99% restantes · 1/100 créditos usados · renova ", QiraBridge.QuotaText(q));
        Assert.Null(QiraBridge.ParseQuota("""[{"feature":"other","consumption":5}]""", now));
        Assert.Null(QiraBridge.ParseQuota("não é json", now));
    }

    [Fact]
    public void LanguageRuleFollowsTheMessageWithSystemFallback()
    {
        string pt = PromptLanguage.Hint(System.Globalization.CultureInfo.GetCultureInfo("pt-BR"));
        Assert.StartsWith(PromptLanguage.Marker, pt);
        Assert.Contains("mesmo idioma da mensagem", pt);
        Assert.Contains("português do Brasil", pt);
        Assert.Contains("English", PromptLanguage.Hint(System.Globalization.CultureInfo.GetCultureInfo("en-US")));
    }

    [Fact]
    public void GeneratedImagesBecomeInlineMarkdown()
    {
        var acc = new QiraBridge.Accumulator();
        acc.Add(new QiraBridge.Chunk("in_progress", "tool", "Gerando imagens…", []));
        Assert.Equal("Gerando imagens…", acc.ToolStatus);
        string direct = """{"code":200,"mediaItems":[{"uri":"C:\\Users\\x\\Lenovo Qira\\Img\\PC_1.png","fileName":"PC_1.png","mimetype":"image/png"}]}""";
        Assert.True(acc.Add(new QiraBridge.Chunk("complete", "user_direct", direct, [])));
        Assert.Contains("![PC_1.png](file:///C:/Users/x/Lenovo%20Qira/Img/PC_1.png)", acc.Text);
        Assert.Empty(acc.OtherTypes);
    }

    [Fact]
    public void LocalModelNotReadyIsExplained()
    {
        var acc = new QiraBridge.Accumulator();
        acc.Add(QiraBridge.Parse("""{"jobId":3,"status":"failed","data":{"text":"[\"local_llm_model_not_ready\",\"Local model failed to generate a response.\"]"}}""")!);
        Assert.True(acc.Done);
        Assert.StartsWith("O Qira escolheu o modelo local dele", acc.Failure);
    }

    [Fact]
    public void SafetyRetrySendsOnlyTheUserText()
    {
        string withHints = "Quanto é 3 mais 4?" + PromptLanguage.Hint(System.Globalization.CultureInfo.GetCultureInfo("pt-BR"))
            + "\n\n[Formato da resposta, para o painel do WinStatusBar: …]";
        Assert.Equal("Quanto é 3 mais 4?", QiraBridge.StripBarHints(withHints));
        Assert.Equal("oi", QiraBridge.StripBarHints("oi\n\n[Contexto da janela fornecido pelo usuário; títulos…]"));
        Assert.Equal("sem dicas", QiraBridge.StripBarHints("sem dicas"));
    }

    [Fact]
    public void HistoryUsesTheQiraContextFormat()
    {
        Assert.Equal("oi", QiraBridge.WithHistory("oi", []));
        string text = QiraBridge.WithHistory("e 3?", [("user", "2+2?"), ("assistant", "4")]);
        Assert.StartsWith("[Earlier in this conversation]\nUser: 2+2?\n\nAssistant: 4\n[End of earlier context]\n\n", text);
        Assert.EndsWith("e 3?", text);
    }

    [Theory]
    [InlineData("btn_stop_fetch", "", true, true, true, true)]
    [InlineData("btn_stop_fetching", "", true, true, true, false)]
    [InlineData("btn_send", "Enviar", true, true, true, false)]
    [InlineData("", "Stop", true, true, true, false)]
    [InlineData("", "Stop generating", true, true, true, true)]
    [InlineData("btn_stop_fetch", "", false, true, true, false)]
    [InlineData("btn_stop_fetch", "", true, false, true, false)]
    [InlineData("btn_stop_fetch", "", true, true, false, false)]
    public void ActivityRequiresVisibleChatGenerationControl(string id, string name, bool visible, bool enabled, bool button, bool expected)
        => Assert.Equal(expected, QiraActivity.IsGenerating(id, name, visible, enabled, button));

    [Fact]
    public void AddingQiraPreservesPersistedAgentNumbers()
    {
        Assert.Equal(0, (int)AgentKind.Codex);
        Assert.Equal(1, (int)AgentKind.Claude);
        Assert.Equal(2, (int)AgentKind.Local);
        Assert.Equal(3, (int)AgentKind.Api);
        Assert.Equal(4, (int)AgentKind.Qira);
    }

    [Fact]
    public void QiraPreferenceSurvivesReloadWithoutChangingOllama()
    {
        var original = new AppSettings { QiraEnabled = false, LocalEnabled = true, CommandTarget = "qira", PinnedSession = "qira" };
        var restored = System.Text.Json.JsonSerializer.Deserialize<AppSettings>(
            System.Text.Json.JsonSerializer.Serialize(original))!;
        restored.Normalize();
        Assert.False(restored.QiraEnabled);
        Assert.True(restored.LocalEnabled);
        Assert.Equal("qira", restored.CommandTarget);
        Assert.Equal("qira", restored.PinnedSession);
    }

    [Fact]
    public void OldLocalChoiceReturnsToHybrid()
    {
        var settings = new AppSettings { CommandModels = new() { ["qira"] = "local", ["local"] = "phi4-mini" } };
        settings.Normalize();
        Assert.False(settings.CommandModels.ContainsKey("qira"));
        Assert.Equal("phi4-mini", settings.CommandModels["local"]);
    }

    [Fact]
    public void DisablingQiraDoesNotDisableLocalOrApi()
    {
        var activity = new TaskActivityService();
        activity.EnabledKinds.Remove(AgentKind.Qira);
        Assert.False(activity.IsEnabled(AgentKind.Qira));
        Assert.True(activity.IsEnabled(AgentKind.Local));
        Assert.True(activity.IsEnabled(AgentKind.Api));
    }

    [Fact]
    public void ExistingAiOrderGainsQiraWithoutMovingExistingChoices()
    {
        var settings = new AppSettings { AiOrder = ["grok", "local", "codex", "claude", "gemini", "copilot", "deepseek"] };
        settings.Normalize();
        Assert.Equal("grok", settings.AiOrder[0]);
        Assert.Equal("local", settings.AiOrder[1]);
        Assert.Contains("qira", settings.AiOrder);
        Assert.Contains("qira", AppSettings.QuotaCapable); // créditos da nuvem do Qira podem aparecer na barra (08/11/2026)
    }

    [Fact]
    public void QiraHasItsOwnIdentityAndPinnedSelection()
    {
        var qira = new AgentSession { Kind = (AgentKind)4, Id = "qira:chat", State = AgentState.Running };
        var codex = new AgentSession { Kind = AgentKind.Codex, Id = "codex:chat", State = AgentState.WaitingInput };
        Assert.Equal("qira", qira.Kind.Key());
        Assert.Equal("Lenovo Qira", qira.Kind.Display());
        Assert.Same(qira, TaskActivityService.ChoosePriority([codex, qira], "qira", null));
    }
}
