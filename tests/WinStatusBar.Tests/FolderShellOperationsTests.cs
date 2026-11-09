using WinStatusBar.Services;
using Xunit;

namespace WinStatusBar.Tests;

public class FolderShellOperationsTests
{
    [Fact]
    public async Task NativeCopyMoveAndRenamePreserveContents()
    {
        string root = Path.Combine(Path.GetTempPath(), "wsb-shell-" + Guid.NewGuid());
        string source = Path.Combine(root, "source"), destination = Path.Combine(root, "destination");
        Directory.CreateDirectory(source); Directory.CreateDirectory(destination);
        try
        {
            string file = Path.Combine(source, "example.txt"); File.WriteAllText(file, "native transfer");
            Assert.True(await FolderShellOperations.TransferAsync([file], destination, false));
            Assert.True(File.Exists(file));
            Assert.Equal("native transfer", File.ReadAllText(Path.Combine(destination, "example.txt")));
            string renamed = Path.Combine(source, "renamed.txt");
            Assert.True(await FolderShellOperations.RenameAsync(file, renamed));
            Assert.False(File.Exists(file));
            Assert.True(await FolderShellOperations.TransferAsync([renamed], destination, true));
            Assert.False(File.Exists(renamed));
            Assert.Equal("native transfer", File.ReadAllText(Path.Combine(destination, "renamed.txt")));
        }
        finally { Directory.Delete(root, true); }
    }
}
