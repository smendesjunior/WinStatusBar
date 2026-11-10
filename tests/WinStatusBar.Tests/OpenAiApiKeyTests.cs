using WinStatusBar.Core.Ai.Codex.ApiResume;
using Xunit;

namespace WinStatusBar.Tests;

public class OpenAiApiKeyTests
{
    private static readonly string FakeKey = "sk-" + new string('k', 30);

    private static string TempFile(string content)
    {
        string dir = Path.Combine(Path.GetTempPath(), "wsb-key-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string file = Path.Combine(dir, "openai-key.dpapi");
        File.WriteAllText(file, content);
        return file;
    }

    [Fact]
    public void ReadsTheHexProtectedUtf16File()
    {
        string file = TempFile("﻿" + OpenAiApiKey.ToProtectedHex(FakeKey) + "\r\n");
        try
        {
            Assert.True(OpenAiApiKey.Exists(file));
            Assert.Equal(FakeKey, OpenAiApiKey.Read(file));
            Assert.DoesNotContain(FakeKey, File.ReadAllText(file));
        }
        finally { Directory.Delete(Path.GetDirectoryName(file)!, true); }
    }

    [Fact]
    public void MissingFileIsNull()
    {
        string file = Path.Combine(Path.GetTempPath(), "wsb-key-" + Guid.NewGuid().ToString("N"), "openai-key.dpapi");
        Assert.False(OpenAiApiKey.Exists(file));
        Assert.Null(OpenAiApiKey.Read(file));
    }

    [Theory]
    [InlineData("zz-not-hex")]
    [InlineData("00ff")]
    [InlineData("")]
    public void GarbageIsNullWithoutThrowing(string content)
    {
        string file = TempFile(content);
        try { Assert.Null(OpenAiApiKey.Read(file)); }
        finally { Directory.Delete(Path.GetDirectoryName(file)!, true); }
    }

    [Fact]
    public void DefaultFileIsTheGatewayFile() =>
        Assert.EndsWith(Path.Combine("OpenAI", "CodexApiGateway", "openai-key.dpapi"), OpenAiApiKey.DefaultFile);
}
