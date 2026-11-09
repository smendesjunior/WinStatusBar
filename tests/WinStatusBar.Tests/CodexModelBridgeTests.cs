using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using WinStatusBar.Core.Ai;
using Xunit;
namespace WinStatusBar.Tests;

public class CodexModelBridgeTests
{
    private static JsonObject Message(string role, string text) => new()
    {
        ["type"] = "message", ["role"] = role,
        ["content"] = new JsonArray(new JsonObject { ["type"] = role == "assistant" ? "output_text" : "input_text", ["text"] = text }),
    };

    private static JsonObject Payload(string model, bool stream = true) => new()
    {
        ["model"] = model, ["stream"] = stream, ["instructions"] = "You are Codex",
        ["input"] = new JsonArray(
            Message("developer", "<permissions instructions>sandbox</permissions instructions>"),
            Message("user", "# AGENTS.md instructions for C:\\proj\n\n<INSTRUCTIONS>x</INSTRUCTIONS>"),
            Message("user", "<environment_context>\n  <cwd>C:\\proj</cwd>\n</environment_context>"),
            Message("user", "Quanto é 2+2?"),
            Message("assistant", "4"),
            Message("user", "<external_codex_apps_writing_block_edits>x</external_codex_apps_writing_block_edits>"),
            Message("user", "E 3+3?"),
            new JsonObject { ["type"] = "function_call", ["name"] = "shell", ["arguments"] = "{}" }),
    };

    [Fact]
    public void ConversationKeepsOnlyPersonAndAnswers()
    {
        var c = CodexModelBridge.ReadConversation(Payload("winstatusbar-qira"));
        Assert.Equal("E 3+3?", c.Prompt);
        Assert.Equal([("user", "Quanto é 2+2?"), ("assistant", "4")], c.History);
        Assert.Equal("C:\\proj", c.Cwd);
    }

    [Fact]
    public async Task StreamsResponsesEventsAndRejectsWrongSecret()
    {
        string info = Path.Combine(Path.GetTempPath(), "wsb-bridge-" + Guid.NewGuid() + ".json");
        string? seenPrompt = null;
        using var bridge = CodexModelBridge.Start(async (model, prompt, options, onText, ct) =>
        {
            seenPrompt = prompt;
            onText("*Contando tokens...*"); // linha de estado: não pode virar resposta no Codex
            await Task.Delay(20, ct);
            onText("Seis");
            await Task.Delay(20, ct);
            onText("Seis.");
            return new("Seis.", null, null);
        }, info)!;
        try
        {
            var data = JsonNode.Parse(File.ReadAllText(info))!;
            using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{(int)data["port"]!}/") };
            var wrong = new HttpRequestMessage(HttpMethod.Post, "v1/responses") { Content = new StringContent(Payload("winstatusbar-qira").ToJsonString(), Encoding.UTF8, "application/json") };
            wrong.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "x");
            Assert.Equal(HttpStatusCode.Unauthorized, (await http.SendAsync(wrong)).StatusCode);

            var ok = new HttpRequestMessage(HttpMethod.Post, "v1/responses") { Content = new StringContent(Payload("winstatusbar-qira").ToJsonString(), Encoding.UTF8, "application/json") };
            ok.Headers.Authorization = new AuthenticationHeaderValue("Bearer", (string)data["token"]!);
            var response = await http.SendAsync(ok);
            string body = await response.Content.ReadAsStringAsync();
            Assert.Equal("E 3+3?", seenPrompt);
            var events = body.Split("\n").Where(l => l.StartsWith("data: ")).Select(l => JsonNode.Parse(l[6..])!).ToList();
            Assert.Equal("response.created", (string)events[0]["type"]!);
            Assert.Equal("Seis.", string.Concat(events.Where(e => (string)e["type"]! == "response.output_text.delta").Select(e => (string)e["delta"]!)));
            var done = events.Single(e => (string)e["type"]! == "response.output_item.done");
            Assert.Equal("Seis.", (string)done["item"]!["content"]![0]!["text"]!);
            Assert.Equal("response.completed", (string)events[^1]["type"]!);
            Assert.True((int)events[^1]["response"]!["usage"]!["total_tokens"]! > 0);
        }
        finally { bridge.Dispose(); Assert.False(File.Exists(info)); }
    }

    [Fact]
    public void InjectedContextIsRecognized()
    {
        Assert.True(CodexModelBridge.IsInjectedContext("<environment_context>\n<cwd>x</cwd>\n</environment_context>"));
        Assert.True(CodexModelBridge.IsInjectedContext("# AGENTS.md instructions for C:\\x"));
        Assert.False(CodexModelBridge.IsInjectedContext("<b>negrito</b> no meio de um pedido normal"));
        Assert.False(CodexModelBridge.IsInjectedContext("Explique o código"));
    }
}
