using System.Net;
using System.Text.Json.Nodes;
using WinStatusBar.Core.Ai;
using Xunit;
namespace WinStatusBar.Tests;
public class ApiUsageTests
{
    private sealed class Handler : HttpMessageHandler
    {
        public int Requests;
        public bool Deny;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            Assert.Equal("api.openai.com", request.RequestUri!.Host);
            Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
            if (Deny) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden));
            var query = request.RequestUri.Query.TrimStart('?').Split('&').Select(p => p.Split('=')).ToDictionary(p => p[0], p => p[1]);
            bool costs = request.RequestUri.AbsolutePath.EndsWith("costs");
            bool more = !costs && !query.ContainsKey("page");
            var result = costs ? new JsonObject { ["amount"] = new JsonObject { ["value"] = 0.125m, ["currency"] = "usd" } }
                : new JsonObject { ["input_tokens"] = 100, ["output_tokens"] = 25 };
            var body = new JsonObject { ["has_more"] = more, ["next_page"] = more ? "next-page" : null,
                ["data"] = new JsonArray(new JsonObject { ["start_time"] = long.Parse(query["start_time"]), ["results"] = new JsonArray(result) }) };
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body.ToJsonString()) });
        }
    }
    [Fact] public async Task ReadsAllPagesAndKeepsCostsSeparateFromTokens()
    {
        var handler = new Handler(); using var http = new HttpClient(handler);
        var data = await ApiUsageClient.ReadAsync("test-key", CancellationToken.None, http);
        Assert.Equal(3, handler.Requests); Assert.Equal(30, data.Count);
        Assert.Equal(200, data.Sum(d => d.Input)); Assert.Equal(50, data.Sum(d => d.Output)); Assert.Equal(0.125m, data.Sum(d => d.Cost));
    }
    [Fact] public async Task ExplainsMissingAdministrativePermission()
    {
        using var http = new HttpClient(new Handler { Deny = true });
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => ApiUsageClient.ReadAsync("test-key", CancellationToken.None, http));
        Assert.Contains("chave administrativa", ex.Message);
    }
}
