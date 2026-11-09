using WinStatusBar.Core.Metrics;
using Xunit;

namespace WinStatusBar.Tests;

public class WindowsUpdateActionTests
{
    [Fact]
    public void OnlyIdentifiedPendingUpdatesOfferInstallation()
    {
        var entry = new WindowsUpdateEntry("Update", "Disponível");
        Assert.False(entry.CanInstall);
        Assert.False((entry with { UpdateId = "not-an-id" }).CanInstall);
        Assert.True((entry with { UpdateId = Guid.NewGuid().ToString(), Revision = 5 }).CanInstall);
        Assert.Equal("Baixar e instalar", entry.ActionLabel);
        Assert.Equal("Instalar", (entry with { Downloaded = true }).ActionLabel);
    }

    [Theory]
    [InlineData("invalid", 1)]
    [InlineData("00000000-0000-0000-0000-000000000001", -1)]
    public void InvalidSelectionNeverStartsInstallation(string id, int revision)
        => Assert.Equal(2, WindowsUpdateInstallation.Install(id, revision));
}


