namespace WinStatusBar.Services;

/// <summary>Context remains attached while its chip is active, including later conversation turns.</summary>
internal static class ContextAttachmentPolicy
{
    public static void AfterSend(List<string> attachments, bool useContext, string? activeFile, string? textFile)
    {
        // Never re-add a removed attachment from stale capture metadata.
        attachments.RemoveAll(path => !useContext ||
            (!string.Equals(path, activeFile, StringComparison.OrdinalIgnoreCase) &&
             !string.Equals(path, textFile, StringComparison.OrdinalIgnoreCase)));
    }
}
