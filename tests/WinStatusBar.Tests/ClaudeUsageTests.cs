using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using WinStatusBar.Core.Ai.Claude;
using Xunit;
namespace WinStatusBar.Tests;
public class ClaudeUsageTests
{
    private const string Usage = """{"five_hour":{"utilization":100,"resets_at":"2026-11-04T17:40:00Z"},"seven_day":{"utilization":32,"resets_at":"2026-11-07T02:00:00Z"}}""";
    [Fact] public void ParsesRealAccountUsageAndKeepsItsSource()
    {
        var now = DateTimeOffset.Parse("2026-11-04T17:00:00Z");
        var message = ClaudeUsageClient.ParseUsage(Usage, now)!;
        var quota = ClaudeCodeProvider.ToQuota(message, now)!;
        Assert.Equal(100, quota.Primary!.UsedPercent);
        Assert.Equal(0, quota.Primary.RemainingPercent);
        Assert.Equal(68, quota.Secondary!.RemainingPercent);
        Assert.Equal(DateTimeOffset.Parse("2026-11-04T17:40:00Z"), quota.Primary.ResetsAt);
        Assert.Equal("Consulta da conta Claude", quota.Primary.Source);
    }
    [Theory]
    [InlineData("{}")]
    [InlineData("{\"five_hour\":null}")]
    [InlineData("{\"five_hour\":{\"utilization\":-1}}")]
    [InlineData("{\"five_hour\":{\"utilization\":101}}")]
    [InlineData("{\"five_hour\":{\"utilization\":\"unknown\"}}")]
    public void NeverInventsPercentages(string json) => Assert.Null(ClaudeUsageClient.ParseUsage(json, DateTimeOffset.UtcNow));

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => Task.FromResult(response(request));
    }
    private static string Credentials(bool expired = false)
    {
        string path = Path.GetTempFileName();
        File.WriteAllText(path, JsonSerializer.Serialize(new { claudeAiOauth = new { accessToken = "fake-test-token",
            expiresAt = DateTimeOffset.UtcNow.AddHours(expired ? -1 : 1).ToUnixTimeMilliseconds() } }));
        return path;
    }
    [Fact] public async Task UsesExistingLoginOnlyForTheAnthropicUsageEndpoint()
    {
        string path = Credentials();
        try
        {
            using var client = new ClaudeUsageClient(new HttpClient(new Handler(request => {
                Assert.Equal("https://api.anthropic.com/api/oauth/usage", request.RequestUri!.AbsoluteUri);
                Assert.Equal(HttpMethod.Get, request.Method);
                Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
                Assert.Equal("fake-test-token", request.Headers.Authorization.Parameter);
                return new(HttpStatusCode.OK) { Content = new StringContent(Usage) };
            })), path);
            var result = await client.FetchAsync(default);
            Assert.Null(result.Error); Assert.NotNull(result.Message);
            Assert.Equal(TimeSpan.FromMinutes(5), result.RetryDelay);
            Assert.DoesNotContain("fake-test-token", JsonSerializer.Serialize(result));
        }
        finally { File.Delete(path); }
    }
    [Fact] public async Task HonorsServerBackoffAndDoesNotExposeErrorBody()
    {
        string path = Credentials();
        try
        {
            using var client = new ClaudeUsageClient(new HttpClient(new Handler(_ => {
                var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests) { Content = new StringContent("private-error-body") };
                response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromMinutes(20)); return response;
            })), path);
            var result = await client.FetchAsync(default);
            Assert.Null(result.Message); Assert.True(result.RateLimited);
            Assert.Equal(TimeSpan.FromMinutes(20), result.RetryDelay);
            Assert.DoesNotContain("private-error-body", result.Error);
        }
        finally { File.Delete(path); }
    }
    [Fact] public async Task ExpiredLoginDoesNotSendRequestsOrModifyCredentials()
    {
        string path = Credentials(expired: true), before = File.ReadAllText(path);
        try
        {
            using var client = new ClaudeUsageClient(new HttpClient(new Handler(_ => throw new Exception("Must not query"))), path);
            var result = await client.FetchAsync(default);
            Assert.Null(result.Message); Assert.Contains("expirou", result.Error);
            Assert.Equal(before, File.ReadAllText(path));
        }
        finally { File.Delete(path); }
    }
    [Fact] public async Task ExpiredLoginIsRenewedLikeClaudeCodeAndSavedAtomically()
    {
        string path = Path.GetTempFileName();
        File.WriteAllText(path, JsonSerializer.Serialize(new
        {
            claudeAiOauth = new { accessToken = "old-token", refreshToken = "refresh-1", expiresAt = DateTimeOffset.UtcNow.AddHours(-1).ToUnixTimeMilliseconds(),
                scopes = new[] { "user:inference", "user:profile" }, subscriptionType = "pro" },
            other = "keep",
        }));
        string? usageToken = null; string? refreshBody = null;
        try
        {
            using var client = new ClaudeUsageClient(new HttpClient(new Handler(request =>
            {
                if (request.RequestUri!.AbsoluteUri == ClaudeUsageClient.TokenUrl)
                {
                    refreshBody = request.Content!.ReadAsStringAsync().Result;
                    return new(HttpStatusCode.OK) { Content = new StringContent("""{"access_token":"new-token","refresh_token":"refresh-2","expires_in":28800}""") };
                }
                usageToken = request.Headers.Authorization!.Parameter;
                return new(HttpStatusCode.OK) { Content = new StringContent(Usage) };
            })), path, allowRefresh: true);
            var result = await client.FetchAsync(default);
            Assert.Null(result.Error); Assert.NotNull(result.Message);
            Assert.Equal("new-token", usageToken);
            Assert.Contains("\"grant_type\":\"refresh_token\"", refreshBody);
            Assert.Contains(ClaudeUsageClient.ClaudeCodeClientId, refreshBody);
            Assert.Contains("user:inference user:profile", refreshBody);
            using var saved = JsonDocument.Parse(File.ReadAllText(path));
            var oauth = saved.RootElement.GetProperty("claudeAiOauth");
            Assert.Equal("new-token", oauth.GetProperty("accessToken").GetString());
            Assert.Equal("refresh-2", oauth.GetProperty("refreshToken").GetString());
            Assert.True(oauth.GetProperty("expiresAt").GetInt64() > DateTimeOffset.UtcNow.AddHours(7).ToUnixTimeMilliseconds());
            Assert.Equal("pro", oauth.GetProperty("subscriptionType").GetString());   // demais campos preservados
            Assert.Equal("keep", saved.RootElement.GetProperty("other").GetString());
            Assert.False(File.Exists(path + ".wsb-tmp"));
            Assert.DoesNotContain("new-token", JsonSerializer.Serialize(result));
        }
        finally { File.Delete(path); }
    }

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
    }
    [Fact] public async Task AutomaticQueriesFollowConfiguredInterval_RealTimeIsOneMinute()
    {
        string path = Credentials(); int requests = 0; var clock = new Clock();
        try
        {
            var client = new ClaudeUsageClient(new HttpClient(new Handler(_ => { requests++; return new(HttpStatusCode.OK) { Content = new StringContent(Usage) }; })), path);
            using var provider = new ClaudeCodeProvider(client, clock);
            Assert.Equal(TimeSpan.FromMinutes(3), provider.PollInterval); // padrão seguro do "tempo real"
            provider.PollInterval = TimeSpan.FromMinutes(1);
            // Intervalo de 1 minuto: imediatamente e depois a cada minuto
            await provider.RefreshUsageAsync(); Assert.Equal(1, requests);
            await provider.RefreshUsageAsync(); Assert.Equal(1, requests);
            clock.Now += TimeSpan.FromSeconds(50); await provider.RefreshUsageAsync(); Assert.Equal(1, requests);
            clock.Now += TimeSpan.FromSeconds(10); await provider.RefreshUsageAsync(); Assert.Equal(2, requests);
            // Intervalo configurado de 5 minutos
            provider.PollInterval = TimeSpan.FromMinutes(5);
            clock.Now += TimeSpan.FromMinutes(1); await provider.RefreshUsageAsync(); Assert.Equal(3, requests);
            clock.Now += TimeSpan.FromMinutes(4); await provider.RefreshUsageAsync(); Assert.Equal(3, requests);
            clock.Now += TimeSpan.FromMinutes(1); await provider.RefreshUsageAsync(); Assert.Equal(4, requests);
            Assert.NotNull(provider.Quota); Assert.Null(provider.LastQuotaError);
        }
        finally { File.Delete(path); }
    }
    [Fact] public async Task RateLimitBacksOffAndSurvivesRestart()
    {
        string path = Credentials(); int requests = 0; var clock = new Clock();
        string cache = Path.Combine(Path.GetTempPath(), $"wsb-test-{Guid.NewGuid():N}.json");
        try
        {
            var client = new ClaudeUsageClient(new HttpClient(new Handler(_ => {
                requests++; var r = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
                r.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(30)); return r; })), path);
            using (var p = new ClaudeCodeProvider(client, clock, cache))
            {
                await p.RefreshUsageAsync(); Assert.Equal(1, requests);
                clock.Now += TimeSpan.FromMinutes(4); await p.RefreshUsageAsync(); Assert.Equal(1, requests); // mínimo de 5 min
                clock.Now += TimeSpan.FromMinutes(1); await p.RefreshUsageAsync(); Assert.Equal(2, requests);
                clock.Now += TimeSpan.FromMinutes(9); await p.RefreshUsageAsync(); Assert.Equal(2, requests); // 2º bloqueio: 10 min
            }
            // Reabrir a barra durante o bloqueio não gera nova consulta
            var client2 = new ClaudeUsageClient(new HttpClient(new Handler(_ => { requests++; return new(HttpStatusCode.OK) { Content = new StringContent(Usage) }; })), path);
            using var reopened = new ClaudeCodeProvider(client2, clock, cache);
            await reopened.RefreshUsageAsync(); Assert.Equal(2, requests);
            clock.Now += TimeSpan.FromMinutes(1); await reopened.RefreshUsageAsync(); Assert.Equal(3, requests);
        }
        finally { File.Delete(path); File.Delete(cache); File.Delete(cache + ".next"); }
    }

    [Fact] public async Task ManualRefreshCannotBypassServerRateLimit()
    {
        string path = Credentials(); int requests = 0; var clock = new Clock();
        try
        {
            var client = new ClaudeUsageClient(new HttpClient(new Handler(_ => {
                requests++; var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
                response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromMinutes(20)); return response;
            })), path);
            using var provider = new ClaudeCodeProvider(client, clock);
            await provider.RefreshUsageAsync(); Assert.Equal(1, requests);
            clock.Now += TimeSpan.FromMinutes(1); await provider.RefreshUsageAsync(force: true); Assert.Equal(1, requests);
            clock.Now += TimeSpan.FromMinutes(19); await provider.RefreshUsageAsync(); Assert.Equal(2, requests);
        }
        finally { File.Delete(path); }
    }

}
