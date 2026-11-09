using WinStatusBar.Core.Ai;
using WinStatusBar.Core.Ai.Claude;
using Xunit;
namespace WinStatusBar.Tests;

public class ClaudeBootstrapTests
{
    [Fact]
    public void RunningTranscriptIsResumedAndFinishedOneIsNot()
    {
        string root = Path.Combine(Path.GetTempPath(), "wsb-claude-" + Guid.NewGuid().ToString("N"));
        string project = Path.Combine(root, "C--proj");
        Directory.CreateDirectory(project);
        try
        {
            File.WriteAllText(Path.Combine(project, "aaa.jsonl"),
                "{\"type\":\"user\",\"sessionId\":\"aaa\",\"cwd\":\"C:/proj\",\"message\":{\"role\":\"user\",\"content\":\"Faça o relatório\"}}\n" +
                "{\"type\":\"assistant\",\"sessionId\":\"aaa\",\"message\":{\"role\":\"assistant\",\"stop_reason\":\"tool_use\",\"content\":[]}}\n");
            File.WriteAllText(Path.Combine(project, "bbb.jsonl"),
                "{\"type\":\"user\",\"sessionId\":\"bbb\",\"message\":{\"role\":\"user\",\"content\":\"Oi\"}}\n" +
                "{\"type\":\"assistant\",\"sessionId\":\"bbb\",\"message\":{\"role\":\"assistant\",\"stop_reason\":\"end_turn\",\"content\":[]}}\n");
            var p = new ClaudeCodeProvider();
            Assert.Equal(1, p.Bootstrap(TimeSpan.FromMinutes(10), root));
            var s = Assert.Single(p.Sessions);
            Assert.Equal(AgentState.Running, s.State);
            Assert.Equal("C:/proj", s.ProjectDir);
        }
        finally { Directory.Delete(root, true); }
    }
}
