using WinStatusBar.Services;
using Xunit;

namespace WinStatusBar.Tests;

public class ConversationStoreTests
{
    [Fact]
    public void OlderQueuedSaveCannotOverwriteNewerConversation()
    {
        string dir = Path.Combine(Path.GetTempPath(), "WinStatusBar.Tests", Guid.NewGuid().ToString());
        ConversationStore.CustomFolder = dir;
        try
        {
            var now = DateTimeOffset.UtcNow;
            ConversationStore.Save(new() { Id = "test", Title = "latest", Updated = now });
            ConversationStore.Save(new() { Id = "test", Title = "old", Updated = now.AddMinutes(-1) });
            Assert.Equal("latest", Assert.Single(ConversationStore.List()).Title);
        }
        finally
        {
            ConversationStore.CustomFolder = null;
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void ConcurrentSavesAllReachDiskWithoutTemporaryFileCollisions()
    {
        string dir = Path.Combine(Path.GetTempPath(), "WinStatusBar.Tests", Guid.NewGuid().ToString());
        ConversationStore.CustomFolder = dir;
        try
        {
            App.Messages.Clear();
            Parallel.For(0, 100, i => ConversationStore.Save(new() { Id = "test", Title = i.ToString() }));
            Assert.Empty(App.Messages);
            Assert.Single(ConversationStore.List());
            Assert.Empty(Directory.GetFiles(dir, "*.tmp", SearchOption.AllDirectories));
        }
        finally
        {
            ConversationStore.CustomFolder = null;
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void SavesIntoTheServiceFolderAndCategorizesOldRootFiles()
    {
        string dir = Path.Combine(Path.GetTempPath(), "WinStatusBar.Tests", Guid.NewGuid().ToString());
        ConversationStore.CustomFolder = dir;
        try
        {
            ConversationStore.Save(new() { Id = "a", Title = "gemini", Target = "gemini" });
            Assert.True(File.Exists(Path.Combine(dir, "Gemini", "a.json")));
            // Conversa antiga gravada na raiz (versões anteriores): a categorização move para a pasta do serviço.
            File.WriteAllText(Path.Combine(dir, "b.json"), System.Text.Json.JsonSerializer.Serialize(new SavedConversation { Id = "b", Title = "local", Target = "local" }));
            Assert.Equal(2, ConversationStore.List().Count);
            Assert.Equal(1, ConversationStore.Categorize());
            Assert.True(File.Exists(Path.Combine(dir, "IA local", "b.json")));
            Assert.False(File.Exists(Path.Combine(dir, "b.json")));
            ConversationStore.Delete("b");
            Assert.Single(ConversationStore.List());
        }
        finally
        {
            ConversationStore.CustomFolder = null;
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }
}
