using System.Runtime.InteropServices;

namespace WinStatusBar.Core.Metrics;

public sealed record RecentFolderItem(string Path, string Name, bool IsDirectory, DateTime Modified);

public static class RecentFolderItems
{
    public static string DownloadsPath()
    {
        var id = new Guid("374DE290-123F-4565-9164-39C4925E467B");
        int result = SHGetKnownFolderPath(ref id, 0, IntPtr.Zero, out var pointer);
        try { Marshal.ThrowExceptionForHR(result); return Marshal.PtrToStringUni(pointer)!; }
        finally { if (pointer != IntPtr.Zero) Marshal.FreeCoTaskMem(pointer); }
    }

    public static IReadOnlyList<RecentFolderItem> Read(string path, CancellationToken cancellationToken = default)
    {
        // List the whole folder without recursing or hydrating file contents.
        var items = new List<RecentFolderItem>();
        foreach (var entry in new DirectoryInfo(path).EnumerateFileSystemInfos())
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                if ((entry.Attributes & (FileAttributes.Hidden | FileAttributes.System)) != 0) continue;
                // Pastas e arquivos de configuração de programas (.dotnet, .gemini, .claude.json…): mudam o tempo todo e
                // empurravam os arquivos da pessoa para baixo na lista "mais recentes primeiro".
                if (entry.Name.StartsWith('.')) continue;
                items.Add(new(entry.FullName, entry.Name, (entry.Attributes & FileAttributes.Directory) != 0, entry.LastWriteTimeUtc));
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        cancellationToken.ThrowIfCancellationRequested();
        items.Sort((a, b) => { int date = b.Modified.CompareTo(a.Modified); return date != 0 ? date : StringComparer.OrdinalIgnoreCase.Compare(a.Name, b.Name); });
        return items;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHGetKnownFolderPath(ref Guid id, uint flags, IntPtr token, out IntPtr path);
}
