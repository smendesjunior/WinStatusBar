using WinStatusBar.Core.Metrics;
using Xunit;

namespace WinStatusBar.Tests;

public class RecentFolderItemsTests
{
    [Fact]
    public void ReturnsAllItemsNewestFirstIncludingFoldersWithoutRecursionOrHiddenItems()
    {
        string root = Path.Combine(Path.GetTempPath(), "wsb-recent-" + Guid.NewGuid());
        Directory.CreateDirectory(root);
        try
        {
            for (int i = 0; i < 20; i++)
            {
                string file = Path.Combine(root, $"file-{i:00}.txt");
                File.WriteAllText(file, "");
                File.SetLastWriteTimeUtc(file, new DateTime(2026, 1, 1, 0, i, 0, DateTimeKind.Utc));
            }
            string folder = Path.Combine(root, "folder"); Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, "nested.txt"), "");
            Directory.SetLastWriteTimeUtc(folder, new DateTime(2026, 1, 2));
            string hidden = Path.Combine(root, "hidden.txt"); File.WriteAllText(hidden, ""); File.SetAttributes(hidden, FileAttributes.Hidden);
            var result = RecentFolderItems.Read(root);
            Assert.Equal(21, result.Count);
            Assert.True(result[0].IsDirectory);
            Assert.Equal("file-19.txt", result[1].Name);
            Assert.Equal("file-00.txt", result[^1].Name);
            Assert.DoesNotContain(result, x => x.Name == "hidden.txt" || x.Name == "nested.txt");
            Assert.Throws<OperationCanceledException>(() => RecentFolderItems.Read(root, new CancellationToken(true)));
        }
        finally { Directory.Delete(root, true); }
    }
}
