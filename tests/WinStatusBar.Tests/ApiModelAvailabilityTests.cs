using WinStatusBar.Core.Ai;
using Xunit;
using V = WinStatusBar.Core.Ai.ApiModelAvailability.Verdict;

namespace WinStatusBar.Tests;

/// <summary>Modelos de API recusados pelo serviço (caso real de 12/11/2026: Gemini 2.5 Pro com HTTP 404) e o PATH atualizado.</summary>
[Collection("ApiModelAvailability")]
public class ApiModelAvailabilityTests : IDisposable
{
    private const string Google = "https://generativelanguage.googleapis.com/v1beta/openai";
    private const string Gone = "This model models/gemini-2.5-pro is no longer available to new users. Please update your code to use models/gemini-3.1-pro-preview.";
    private readonly string _file = Path.Combine(Path.GetTempPath(), $"wsb-recusados-{Guid.NewGuid():N}.json");
    private readonly string _previous = ApiModelAvailability.FilePath;

    public ApiModelAvailabilityTests() => ApiModelAvailability.FilePath = _file;

    public void Dispose()
    {
        ApiModelAvailability.FilePath = _previous;
        try { File.Delete(_file); } catch { }
    }

    [Fact] public void GoogleErrorInsideAListKeepsTheReason()
    {
        string body = "[{\n  \"error\": {\n    \"code\": 404,\n    \"message\": \"" + Gone + "\",\n    \"status\": \"NOT_FOUND\"\n  }\n}\n]";
        Assert.Equal(Gone, AgentPrompt.ProviderErrorMessage(body));
    }

    [Theory]
    [InlineData(200, null, V.Available)]
    [InlineData(404, Gone, V.Refused)]
    [InlineData(404, null, V.Refused)]
    [InlineData(429, "Quota exceeded for metric: generate_content_free_tier_requests, limit: 0, model: gemini-pro", V.Refused)]
    [InlineData(429, "Resource has been exhausted (e.g. check quota).", V.NoQuota)]
    [InlineData(400, "The model `old-model` does not exist", V.Refused)]
    [InlineData(400, "Invalid value for temperature", V.Unknown)]
    [InlineData(500, "internal error", V.Unknown)]
    [InlineData(503, "model overloaded", V.Unknown)]
    public void ClassifiesServiceAnswers(int status, string? message, V expected)
        => Assert.Equal(expected, ApiModelAvailability.Classify(status, message));

    [Fact] public void KeyIgnoresGooglePrefixAndCase()
        => Assert.Equal(ApiModelAvailability.Key(Google, "models/gemini-2.5-pro"), ApiModelAvailability.Key(Google.ToUpperInvariant(), "Gemini-2.5-Pro"));

    [Fact] public void RunErrorMarksTheModelAndAvailableClearsIt()
    {
        Assert.Null(ApiModelAvailability.RecordRunError(Google, "models/gemini-2.5-pro", "HTTP 429: Resource has been exhausted"));
        Assert.False(ApiModelAvailability.IsRefused(Google, "models/gemini-2.5-pro"));

        string? reason = ApiModelAvailability.RecordRunError(Google, "models/gemini-2.5-pro", "HTTP 404: " + Gone);
        Assert.Equal("o serviço aposentou este modelo para a sua chave", reason);
        Assert.True(ApiModelAvailability.IsRefused(Google, "gemini-2.5-pro"));
        Assert.False(ApiModelAvailability.IsRefused("https://api.deepseek.com", "gemini-2.5-pro"));

        ApiModelAvailability.Record(Google, "gemini-2.5-pro", V.Available);
        Assert.False(ApiModelAvailability.IsRefused(Google, "models/gemini-2.5-pro"));
    }

    [Fact] public async Task CheckKeepsWorkingAndInconclusiveModelsAndDropsRefused()
    {
        ApiModelAvailability.Record(Google, "antigo", V.Refused, "o serviço não reconhece este modelo");
        var answers = new Dictionary<string, ApiModelAvailability.Probe>
        {
            ["flash"] = new(V.Available, null),
            ["pro"] = new(V.Refused, "HTTP 404: " + Gone),
            ["pro-latest"] = new(V.NoQuota, "HTTP 429: exhausted"),
            ["rede"] = new(V.Unknown, "sem resposta em 30 s"),
            ["antigo"] = new(V.Unknown, "sem resposta em 30 s"),
        };
        var (usable, refused, noQuota) = await ApiModelAvailability.CheckAsync(Google, answers.Keys.ToList(), m => m,
            (m, _) => Task.FromResult(answers[m]), CancellationToken.None);

        Assert.Equal(new[] { "flash", "pro-latest", "rede" }, usable.OrderBy(x => x));
        Assert.Equal(new[] { "antigo", "pro" }, refused.Select(r => r.Model).OrderBy(x => x));
        Assert.Equal(new[] { "pro-latest" }, noQuota);
        Assert.True(ApiModelAvailability.IsRefused(Google, "models/pro"));
        Assert.False(ApiModelAvailability.IsRefused(Google, "flash"));
    }

    /// <summary>Caso real (12/11/2026): o seletor "Gemini · chave de API" mostrava 2.5 Pro e 4 Argon recusados.</summary>
    [Fact] public void GeminiKeyPickerHidesRefusedModels()
    {
        ApiModelAvailability.Record(Google, "models/gemini-2.5-pro", V.Refused, "o serviço aposentou este modelo para a sua chave");
        var ids = WinStatusBar.Core.Ai.Gemini.GeminiCli.Catalog().Models.Select(m => m.Id).ToList();
        Assert.DoesNotContain("gemini-2.5-pro", ids);
        Assert.Contains(WinStatusBar.Core.Ai.Gemini.GeminiCli.DefaultModel, ids);
    }

    [Theory]
    [InlineData("[API Error: models/gemini-4-argon is not found for API version v1beta (Status: NOT_FOUND)]", true)]
    [InlineData("Quota exceeded for metric: generate_content_free_tier_requests, limit: 0 (429 RESOURCE_EXHAUSTED)", true)]
    [InlineData("429 RESOURCE_EXHAUSTED: Resource has been exhausted", false)]
    [InlineData("Interrompido", false)]
    public void GeminiCliErrorMarksRefusedModel(string error, bool refused)
    {
        Assert.Equal(refused, WinStatusBar.Core.Ai.Gemini.GeminiCli.RecordRefusal("gemini-teste", error) is not null);
        Assert.Equal(refused, WinStatusBar.Core.Ai.Gemini.GeminiCli.IsRefused("gemini-teste"));
    }

    [Fact] public void ErrorExplainerTreatsRetiredModelAsUnavailable()
    {
        var e = AiErrorExplainer.Explain(AgentPrompt.Target.Api, "HTTP 404: " + Gone, "gemini-2.5-pro");
        Assert.Contains("indisponível", e.Short);
    }

    [Fact] public void FreshPathPutsMachineAndUserFirstWithoutRepeats()
    {
        string path = FreshPath.Merge(@"C:\Windows\system32;C:\Windows", @"C:\Tools\platform-tools\;%SystemRoot%",
            @"c:\windows\System32;C:\Antigo;C:\Tools\platform-tools");
        var dirs = path.Split(';');
        Assert.Equal(@"C:\Windows\system32", dirs[0]);
        Assert.Contains(@"C:\Tools\platform-tools\", dirs);
        Assert.Contains(@"C:\Antigo", dirs);
        Assert.Single(dirs, d => d.TrimEnd('\\').Equals(@"C:\Tools\platform-tools", StringComparison.OrdinalIgnoreCase));
        Assert.Single(dirs, d => d.Equals(@"C:\Windows\system32", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(dirs, d => d.Contains('%'));
    }

    [Theory]
    [InlineData(null, 3)]          // nunca verificou: 3 min depois de abrir
    [InlineData(-1, 6 * 60 - 1)]    // verificou há 1 min: só quando completar 6 h
    [InlineData(-7 * 60, 3)]        // verificou há 7 h: 3 min depois de abrir
    [InlineData(-5 * 60, 60)]       // verificou há 5 h: daqui a 1 h
    [InlineData(10, 6 * 60)]        // relógio adiantado no relatório: no máximo 6 h
    public void AutoCheckWaitsAtLeastThreeMinutesAndAtMostSixHours(int? minutesAgo, int expectedMinutes)
    {
        var now = new DateTime(2026, 11, 12, 10, 0, 0);
        DateTime? last = minutesAgo is { } m ? now.AddMinutes(m) : null;
        Assert.Equal(TimeSpan.FromMinutes(expectedMinutes), CodexIAs.Program.AutoCheckDue(last, now));
    }
}
