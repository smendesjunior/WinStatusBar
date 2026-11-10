using WinStatusBar.Core.Ai;
using WinStatusBar.Core.Ai.Codex.ApiResume;
using Xunit;

namespace WinStatusBar.Tests;

public class CodexExecEventsTests
{
    [Fact]
    public void CommandWithoutPowerShellPath()
    {
        const string cmd = "\"C:\\WINDOWS\\System32\\WindowsPowerShell\\v1.0\\powershell.exe\" -Command \"Get-ChildItem -Recurse\"";
        string line = "{\"type\":\"item.started\",\"item\":{\"id\":\"item_1\",\"type\":\"command_execution\",\"command\":"
            + System.Text.Json.JsonSerializer.Serialize(cmd) + ",\"aggregated_output\":\"\",\"status\":\"in_progress\"}}";
        var e = CodexExecEvents.Parse(line);
        Assert.Equal((ExecEventKind.Status, "Executando: Get-ChildItem -Recurse"), (e.Kind, e.Text));

        string longText = CodexExecEvents.CommandText(new string('a', 200));
        Assert.True(longText.Length <= CodexExecEvents.CommandMax);
        Assert.EndsWith("…", longText);
        Assert.True(("Executando: " + longText).Length <= 102);
        Assert.Equal("dir", CodexExecEvents.CommandText("cmd.exe /c dir"));
        Assert.Equal("ls -la", CodexExecEvents.CommandText("pwsh -c 'ls -la'"));
    }

    [Fact]
    public void RecordedModelWarningIsNotFailure()
    {
        var e = CodexExecEvents.Parse("""{"type":"item.completed","item":{"id":"item_0","type":"error","message":"This session was recorded with model `gpt-5.6-sol` but is resuming with `gpt-6.1-sol`."}}""");
        Assert.Equal(ExecEventKind.Warning, e.Kind);
        Assert.Equal(ExecEventKind.Error, CodexExecEvents.Parse("""{"type":"item.completed","item":{"type":"error","message":"boom"}}""").Kind);
        Assert.Equal(ExecEventKind.Error, CodexExecEvents.Parse("""{"type":"error","message":"stream disconnected"}""").Kind);
    }

    [Fact]
    public void TokensFromTurnCompleted()
    {
        var e = CodexExecEvents.Parse("""{"type":"turn.completed","usage":{"input_tokens":435000,"cached_input_tokens":398000,"output_tokens":443}}""");
        Assert.Equal(ExecEventKind.TurnCompleted, e.Kind);
        Assert.Equal(new TokenUsage(435000, 398000, 443, 0), e.Usage);
        var failed = CodexExecEvents.Parse("""{"type":"turn.failed","error":{"message":"HTTP 401: Incorrect API key"}}""");
        Assert.Equal((ExecEventKind.TurnFailed, "HTTP 401: Incorrect API key"), (failed.Kind, failed.Text));
        Assert.Equal(ExecEventKind.TurnStarted, CodexExecEvents.Parse("""{"type":"turn.started"}""").Kind);
        Assert.Equal(ExecEventKind.ThreadStarted, CodexExecEvents.Parse("""{"type":"thread.started","thread_id":"x"}""").Kind);
        Assert.Equal(ExecEventKind.Other, CodexExecEvents.Parse("not json").Kind);
    }

    [Fact]
    public void RealWriterConflictText()
    {
        Assert.True(CodexExecEvents.IsWriterConflict("Error: thread 01aa0000-0000-7000-8000-000000000002 already has an active writer (code -32600)"));
        Assert.False(CodexExecEvents.IsWriterConflict("rate limit"));
        Assert.False(CodexExecEvents.IsWriterConflict(null));
    }

    [Theory]
    [InlineData("""{"type":"item.started","item":{"type":"file_change","changes":[{"path":"a"},{"path":"b"}]}}""", ExecEventKind.Status, "Editando 2 arquivo(s)…")]
    [InlineData("""{"type":"item.started","item":{"type":"mcp_tool_call","server":"github","tool":"x"}}""", ExecEventKind.Status, "Usando github…")]
    [InlineData("""{"type":"item.started","item":{"type":"reasoning","text":""}}""", ExecEventKind.Status, "Pensando…")]
    [InlineData("""{"type":"item.started","item":{"type":"web_search","query":"x"}}""", ExecEventKind.Status, "Pesquisando na web…")]
    [InlineData("""{"type":"item.completed","item":{"type":"agent_message","text":"Pronto, terminei."}}""", ExecEventKind.AgentMessage, "Pronto, terminei.")]
    public void OtherItems(string line, ExecEventKind kind, string text)
    {
        var e = CodexExecEvents.Parse(line);
        Assert.Equal((kind, text), (e.Kind, e.Text));
    }

    [Fact]
    public void ReadNewSkipsIncompleteLastLine()
    {
        string file = Path.Combine(Path.GetTempPath(), "wsb-events-" + Guid.NewGuid().ToString("N") + ".jsonl");
        try
        {
            Assert.Empty(CodexExecEvents.ReadNew(file, 0, out long none));
            Assert.Equal(0, none);
            File.WriteAllText(file, "{\"type\":\"turn.started\"}\n{\"type\":\"tu");
            var first = CodexExecEvents.ReadNew(file, 0, out long offset);
            Assert.Equal(ExecEventKind.TurnStarted, Assert.Single(first).Kind);
            Assert.Equal("{\"type\":\"turn.started\"}\n".Length, offset);
            File.AppendAllText(file, "rn.completed\",\"usage\":{}}\n");
            var second = CodexExecEvents.ReadNew(file, offset, out long end);
            Assert.Equal(ExecEventKind.TurnCompleted, Assert.Single(second).Kind);
            Assert.Equal(new FileInfo(file).Length, end);
        }
        finally { File.Delete(file); }
    }
}
