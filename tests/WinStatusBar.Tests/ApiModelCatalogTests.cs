using WinStatusBar.Core.Ai;
using Xunit;
namespace WinStatusBar.Tests;
public class ApiModelCatalogTests
{
    [Fact] public void RecentMenuExcludesSnapshotsSpecialistsAndOldFamilies()
    {
        string[] ids = ["gpt-4o", "gpt-5", "gpt-6-sol", "gpt-6-luna", "gpt-6.1-sol", "gpt-6.1-terra", "gpt-6.1-sol-2026-10-20", "gpt-image-1", "gpt-6-transcribe", "chat-latest"];
        var menu = ApiModelCatalog.Recent(ids.Select(id => new AgentPrompt.ModelOption(id,id,[],null)));
        Assert.Equal(5, menu.Count);
        Assert.DoesNotContain(menu, m => m.Id == "gpt-4o" || m.Id.Contains("2026") || m.Id.Contains("transcribe"));
        Assert.Equal(new[] { "low", "medium", "high", "xhigh", "max" }, menu.Single(m => m.Id == "gpt-6.1-sol").Efforts);
        Assert.Contains("none", menu.Single(m => m.Id == "gpt-6.1-terra").Efforts);
        Assert.Empty(menu.Single(m => m.Id == "chat-latest").Efforts);
    }
}
