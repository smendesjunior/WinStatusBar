using WinStatusBar.Services;
using Xunit;
namespace WinStatusBar.Tests;
public class ContextAttachmentPolicyTests
{
    [Fact] public void KeepsContextAvailableForFollowupButConsumesOrdinaryAttachments()
    {
        var attachments = new List<string> { "window.png", "selection.txt", "user.pdf" };
        ContextAttachmentPolicy.AfterSend(attachments, true, "window.png", "selection.txt");
        Assert.Equal(new[] { "window.png", "selection.txt" }, attachments);
        ContextAttachmentPolicy.AfterSend(attachments, true, "window.png", "selection.txt");
        Assert.Equal(2, attachments.Count);
    }
    [Fact] public void DisabledContextIsNotResent()
    {
        var attachments = new List<string> { "window.png", "selection.txt" };
        ContextAttachmentPolicy.AfterSend(attachments, false, "window.png", "selection.txt");
        Assert.Empty(attachments);
    }
    [Fact] public void RemovedContextIsNotRestoredFromStaleMetadata()
    {
        var attachments = new List<string> { "user.pdf" };
        ContextAttachmentPolicy.AfterSend(attachments, true, "window.png", "selection.txt");
        Assert.Empty(attachments);
    }
}
