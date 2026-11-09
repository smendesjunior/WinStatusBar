using System.Text.Json;
using WinStatusBar.Core.Ai.Codex;
using Xunit;
namespace WinStatusBar.Tests;
public class CodexComposerTests
{
    private static readonly CodexPluginMention[] Plugins = [new("google-drive@catalog", "google-drive", "Google Drive", "plugin_123"), new("computer-use@local", "computer-use", "Computer Use", null)];
    [Fact] public void MentionBecomesStructuredInput()
    {
        var input = CodexComposerProtocol.BuildInput("use @google-drive e @computer-use", [], Plugins);
        Assert.Equal(3, input.Count);
        Assert.Equal("mention", input[1]!["type"]!.GetValue<string>());
        Assert.Equal("plugin://plugin_123", input[1]!["path"]!.GetValue<string>());
        Assert.Equal("plugin://computer-use@local", input[2]!["path"]!.GetValue<string>());
    }
    [Fact] public void EmailAndPartialNamesAreNotPluginMentions()
        => Assert.Single(CodexComposerProtocol.BuildInput("pessoa@google-drive.com @google-drive-extra", [], Plugins));
    [Fact] public void RepeatedMentionIsSentOnce()
        => Assert.Equal(2, CodexComposerProtocol.BuildInput("@google-drive @google-drive", [], Plugins).Count);
    [Fact] public void LocalImagesUseNativeInputType()
        => Assert.Equal("localImage", CodexComposerProtocol.BuildInput("teste", ["C:/sample.png"], [])[1]!["type"]!.GetValue<string>());
    [Fact] public void DisabledPluginsAreNotExposed()
    {
        using var json = JsonDocument.Parse("""{"marketplaces":[{"plugins":[{"id":"x@local","name":"x","installed":true,"enabled":false},{"id":"y@local","name":"y","installed":true,"enabled":true}]}]}""");
        Assert.Equal("y", Assert.Single(CodexComposerProtocol.ReadPlugins(json.RootElement)).Name);
    }
}
