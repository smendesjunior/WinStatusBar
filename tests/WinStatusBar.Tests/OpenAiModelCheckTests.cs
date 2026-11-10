using System.Net;
using WinStatusBar.Core.Ai.Codex.ApiResume;
using Xunit;

namespace WinStatusBar.Tests;

public class OpenAiModelCheckTests
{
    private static readonly string FakeKey = "sk-" + new string('k', 30);

    private sealed class FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public readonly List<HttpRequestMessage> Requests = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(request);
            return Task.FromResult(respond(request));
        }
    }

    private static HttpResponseMessage Response(HttpStatusCode code, string body = "{}", string? org = null)
    {
        var r = new HttpResponseMessage(code) { Content = new StringContent(body) };
        if (org is not null) r.Headers.Add("openai-organization", org);
        return r;
    }

    [Fact]
    public async Task OkReadsOrganizationHeader()
    {
        var handler = new FakeHandler(_ => Response(HttpStatusCode.OK, """{"id":"gpt-5.6-sol","object":"model"}""", "org-teste"));
        var check = await new OpenAiModelCheck(handler).CheckAsync("gpt-5.6-sol", FakeKey, fresh: false, CancellationToken.None);
        Assert.Equal(ModelCheckResult.Ok, check.Result);
        Assert.Equal("org-teste", check.Organization);
        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal("https://api.openai.com/v1/models/gpt-5.6-sol", request.RequestUri!.ToString());
        Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
        Assert.Equal(FakeKey, request.Headers.Authorization.Parameter);
    }

    [Theory]
    [InlineData(404, """{"error":{"code":"model_not_found"}}""", ModelCheckResult.ModelNotFound)]
    [InlineData(403, """{"error":{"message":"Project proj_x does not have access to model gpt-5.6-sol"}}""", ModelCheckResult.ModelNotFound)]
    [InlineData(403, """{"error":{"message":"forbidden"}}""", ModelCheckResult.OtherError)]
    [InlineData(401, """{"error":{"message":"Incorrect API key provided"}}""", ModelCheckResult.KeyRejected)]
    [InlineData(500, """{"error":{"message":"server"}}""", ModelCheckResult.OtherError)]
    public async Task MapsStatuses(int status, string body, ModelCheckResult expected)
    {
        var handler = new FakeHandler(_ => Response((HttpStatusCode)status, body));
        var check = await new OpenAiModelCheck(handler).CheckAsync("gpt-5.6-sol", FakeKey, fresh: false, CancellationToken.None);
        Assert.Equal(expected, check.Result);
        Assert.StartsWith($"HTTP {status}: ", check.Detail);
    }

    [Fact]
    public async Task DetailNeverCarriesTheKey()
    {
        var handler = new FakeHandler(_ => Response(HttpStatusCode.Unauthorized, "{\"error\":{\"message\":\"Incorrect API key provided: " + FakeKey + "\"}}"));
        var check = await new OpenAiModelCheck(handler).CheckAsync("gpt-5.6-sol", FakeKey, fresh: false, CancellationToken.None);
        Assert.DoesNotContain(FakeKey, check.Detail);
        Assert.Contains("•••", check.Detail);
    }

    [Fact]
    public async Task NetworkFailureIsOtherError()
    {
        var handler = new FakeHandler(_ => throw new HttpRequestException("sem rede"));
        var check = await new OpenAiModelCheck(handler).CheckAsync("gpt-5.6-sol", FakeKey, fresh: false, CancellationToken.None);
        Assert.Equal(ModelCheckResult.OtherError, check.Result);
        Assert.Contains("sem rede", check.Detail);
    }

    [Fact]
    public async Task CachesTenMinutesAndFreshBypasses()
    {
        var clock = DateTimeOffset.Parse("2026-11-10T14:00:00Z");
        var handler = new FakeHandler(_ => Response(HttpStatusCode.OK));
        var check = new OpenAiModelCheck(handler, () => clock);
        await check.CheckAsync("gpt-5.6-sol", FakeKey, false, CancellationToken.None);
        await check.CheckAsync("gpt-5.6-sol", FakeKey, false, CancellationToken.None);
        Assert.Single(handler.Requests);
        clock = clock.AddMinutes(9);
        await check.CheckAsync("gpt-5.6-sol", FakeKey, false, CancellationToken.None);
        Assert.Single(handler.Requests);
        clock = clock.AddMinutes(2);
        await check.CheckAsync("gpt-5.6-sol", FakeKey, false, CancellationToken.None);
        Assert.Equal(2, handler.Requests.Count);
        await check.CheckAsync("gpt-5.6-sol", FakeKey, true, CancellationToken.None);
        Assert.Equal(3, handler.Requests.Count);
        await check.CheckAsync("gpt-5.6-sol", "sk-" + new string('o', 30), false, CancellationToken.None);
        Assert.Equal(4, handler.Requests.Count);
    }

    [Fact]
    public async Task OtherErrorIsNotCached()
    {
        var handler = new FakeHandler(_ => Response(HttpStatusCode.InternalServerError));
        var check = new OpenAiModelCheck(handler);
        await check.CheckAsync("gpt-5.6-sol", FakeKey, false, CancellationToken.None);
        await check.CheckAsync("gpt-5.6-sol", FakeKey, false, CancellationToken.None);
        Assert.Equal(2, handler.Requests.Count);
    }
}
