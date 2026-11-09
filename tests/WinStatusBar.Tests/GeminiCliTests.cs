using WinStatusBar.Core.Ai;
using WinStatusBar.Core.Ai.Gemini;
using Xunit;

namespace WinStatusBar.Tests;

public class GeminiCliTests
{
    [Fact] public void ArgumentsFollowTheAccessLevelAndKeepThePromptLast()
    {
        var full = GeminiCli.Arguments("olá", "gemini-x", new AgentPrompt.Options([], false, true, null, [], FullAccess: true));
        Assert.Equal(["--output-format", "stream-json", "--model", "gemini-x", "--approval-mode", "yolo", "--prompt", "olá"], full);
        Assert.Contains("auto_edit", GeminiCli.Arguments("x", null, new AgentPrompt.Options([], false, true, null, [])));
        var readOnly = GeminiCli.Arguments("x", null, AgentPrompt.Options.None);
        Assert.Contains("default", readOnly);
        Assert.DoesNotContain("--model", readOnly);
    }

    [Fact] public void ParsesStreamJsonEvents()
    {
        Assert.Equal("s1", GeminiCli.ParseEvent("""{"type":"init","session_id":"s1"}""").Session);
        Assert.Equal("Oi", GeminiCli.ParseEvent("""{"type":"message","role":"assistant","content":"Oi","delta":true}""").Delta);
        Assert.Null(GeminiCli.ParseEvent("""{"type":"message","role":"user","content":"pergunta"}""").Delta);
        Assert.Equal("falhou", GeminiCli.ParseEvent("""{"type":"error","message":"falhou"}""").Error);
        Assert.Null(GeminiCli.ParseEvent("texto solto").Delta);
    }

    [Fact] public void GeminiIsADestinationKey()
    {
        Assert.Equal("gemini", AgentPrompt.Key(AgentPrompt.Target.Gemini));
        Assert.Equal(AgentPrompt.Target.Gemini, AgentPrompt.Parse("gemini"));
    }

    [Fact] public void ChatModelsKeepsOnlyConversationModelsNewestFirst()
    {
        using var doc = System.Text.Json.JsonDocument.Parse("""
            {"models":[
              {"name":"models/gemini-2.5-flash","supportedGenerationMethods":["generateContent"]},
              {"name":"models/gemini-3.5-flash-lite","supportedGenerationMethods":["generateContent"]},
              {"name":"models/gemini-3.5-transcribe","supportedGenerationMethods":["generateContent"]},
              {"name":"models/gemini-embedding-001","supportedGenerationMethods":["embedContent"]},
              {"name":"models/gemini-nano-banana-2.1","supportedGenerationMethods":["generateContent"]},
              {"name":"models/gemini-3.6-flash","supportedGenerationMethods":["generateContent"]}
            ]}
            """);
        Assert.Equal(["gemini-3.6-flash", "gemini-3.5-flash-lite", "gemini-2.5-flash"], GeminiCli.ChatModels(doc.RootElement));
    }
}
