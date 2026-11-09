using System.Text.Json.Nodes;
using WinStatusBar.Core.Ai;
using Xunit;

namespace WinStatusBar.Tests;

public class QiraSettingsGuardTests
{
    private static string Body(string language, bool withCredentials = true) => new JsonObject
    {
        ["Data"] = new JsonObject
        {
            ["generalData"] = new JsonArray(
                Item("language", language), Item("saveAllRecordingsToNotes", "true"), Item("promptBar", "true"), Item("wakeWord", "false"),
                Item("voiceTone", "AvaNeural"), Item("interactionMode", "true"), Item("alwaysOnTop", "false")),
            ["dataControl"] = new JsonArray(Item("syncData", "true")),
            ["WakeWordCredentials"] = withCredentials ? new JsonArray(Item("aiSpeechProductSecret", "segredo")) : null,
        },
    }.ToJsonString();

    private static JsonObject Item(string key, string value) => new() { ["Key"] = key, ["Value"] = value, ["LocalType"] = 2 };

    [Fact]
    public void CredentialsAreNeverKept()
    {
        var groups = QiraSettingsGuard.Groups(Body("pt-BR"))!;
        Assert.False(groups.ContainsKey("WakeWordCredentials"));
        Assert.DoesNotContain("segredo", groups.ToJsonString());
    }

    [Fact]
    public void ChoicesAreRememberedAndRestoredAfterFactoryReset()
    {
        var mine = QiraSettingsGuard.Groups(Body("pt-BR"))!;
        var factory = QiraSettingsGuard.Groups(Body("en-US"))!;
        Assert.False(QiraSettingsGuard.IsFactory(mine));
        Assert.True(QiraSettingsGuard.IsFactory(factory));
        Assert.Equal(QiraSettingsGuard.Action.Remember, QiraSettingsGuard.Decide(mine, null));
        Assert.Equal(QiraSettingsGuard.Action.None, QiraSettingsGuard.Decide(mine, mine.DeepClone().AsObject()));
        Assert.Equal(QiraSettingsGuard.Action.Restore, QiraSettingsGuard.Decide(factory, mine));
        Assert.Equal(QiraSettingsGuard.Action.None, QiraSettingsGuard.Decide(factory, null));
        Assert.Contains("pt-BR", QiraSettingsGuard.SavePayload(mine));
    }

    [Fact]
    public void EmptyOrInvalidConfigIsIgnored()
    {
        Assert.Null(QiraSettingsGuard.Groups("{\"Data\":{}}"));
        Assert.Null(QiraSettingsGuard.Groups("não é json"));
        Assert.Null(QiraSettingsGuard.Groups(null));
    }
}
