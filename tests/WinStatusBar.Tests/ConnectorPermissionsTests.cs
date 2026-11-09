using WinStatusBar.Core.Ai;
using Xunit;

namespace WinStatusBar.Tests;

public class ConnectorPermissionsTests
{
    [Theory]
    [InlineData("mcp__claude_ai_Google_Drive__list_recent_files", "mcp__claude_ai_Google_Drive")]
    [InlineData("mcp__plugin_pdf-viewer_pdf__display_pdf", "mcp__plugin_pdf-viewer_pdf")]
    [InlineData("Bash", null)]
    public void ServerRuleCoversTheWholeConnector(string tool, string? rule)
        => Assert.Equal(rule, ConnectorPermissions.ServerRule(tool));

    [Theory]
    [InlineData("mcp__claude_ai_Google_Drive", "Google Drive")]
    [InlineData("mcp__claude_ai_Trimble_SketchUp", "Trimble SketchUp")]
    [InlineData("mcp__plugin_pdf-viewer_pdf", "pdf-viewer · pdf")]
    [InlineData("mcp__node_repl", "node repl")]
    public void ConnectorNamesAreReadable(string rule, string name)
        => Assert.Equal(name, ConnectorPermissions.DisplayName(rule));

    [Fact]
    public void BlockedToolsAreGroupedByConnectorAndIgnoreNonConnectorTools()
    {
        var groups = ConnectorPermissions.Group([
            "mcp__claude_ai_Google_Drive__list_recent_files", "mcp__claude_ai_Google_Drive__search_files",
            "mcp__claude_ai_Google_Drive__list_recent_files", "Bash"]);
        var g = Assert.Single(groups);
        Assert.Equal("mcp__claude_ai_Google_Drive", g.Rule);
        Assert.Equal("Google Drive", g.Name);
        Assert.Equal(["list_recent_files", "search_files"], g.Tools);
    }

    [Theory]
    [InlineData("claude.ai Google Drive", "mcp__claude_ai_Google_Drive")]
    [InlineData("plugin:pdf-viewer:pdf", "mcp__plugin_pdf-viewer_pdf")]
    public void ServerNamesBecomeRules(string server, string rule) => Assert.Equal(rule, ConnectorPermissions.RuleForServer(server));

    [Fact]
    public void ConnectorFullAccessIsTheDefaultAndMigratedOnce()
    {
        Assert.True(new WinStatusBar.Core.Settings.AppSettings().ConnectorsFullAccess);
        var old = new WinStatusBar.Core.Settings.AppSettings { SchemaVersion = 3, ConnectorsFullAccess = false, CommandAllowEditsDefault = false };
        old.Normalize();
        Assert.True(old.ConnectorsFullAccess);
        Assert.False(old.CommandAllowEditsDefault); // Editar não muda
    }

    [Fact]
    public void AllowedConnectorsArePersistedInSettings()
    {
        var s = new WinStatusBar.Core.Settings.AppSettings();
        s.AllowedConnectors.Add("mcp__claude_ai_Google_Drive");
        var json = System.Text.Json.JsonSerializer.Serialize(s);
        var back = System.Text.Json.JsonSerializer.Deserialize<WinStatusBar.Core.Settings.AppSettings>(json)!;
        Assert.Equal(["mcp__claude_ai_Google_Drive"], back.AllowedConnectors);
    }
}
