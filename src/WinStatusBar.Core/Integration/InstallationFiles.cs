using System.IO.Compression;
using System.Security.Cryptography;

namespace WinStatusBar.Core.Integration;

public static class InstallationFiles
{
    public static void Replace(Stream payload, string installDir, Action beforeReplace)
    {
        installDir = Path.GetFullPath(installDir).TrimEnd(Path.DirectorySeparatorChar);
        string stage = installDir + ".stage-" + Guid.NewGuid().ToString("N");
        string backup = installDir + ".previous-" + Guid.NewGuid().ToString("N");
        bool installed = false, rollbackFailed = false;
        var replaced = new List<(string Target, string? Backup)>();
        try
        {
            // Extract and validate before stopping the app or touching its current files.
            using (var zip = new ZipArchive(payload, ZipArchiveMode.Read, leaveOpen: true))
                zip.ExtractToDirectory(stage);
            if (!File.Exists(Path.Combine(stage, "WinStatusBar.exe")))
                throw new InvalidDataException("Pacote sem WinStatusBar.exe.");
            beforeReplace();
            Directory.CreateDirectory(installDir);
            var incoming = Directory.GetFiles(stage, "*", SearchOption.AllDirectories);
            var obsolete = Directory.GetFiles(installDir, "*", SearchOption.AllDirectories)
                .Where(file => !File.Exists(Path.Combine(stage, Path.GetRelativePath(installDir, file)))).ToArray();
            try
            {
                foreach (string file in incoming)
                {
                    string relative = Path.GetRelativePath(stage, file);
                    string target = Path.Combine(installDir, relative);
                    // Loaded sensor runtimes may be locked by an elevated worker.
                    // Byte-identical files need no replacement; changed files retain rollback.
                    if (File.Exists(target) && Identical(file, target)) continue;
                    string? previous = null;
                    if (File.Exists(target))
                    {
                        previous = Path.Combine(backup, relative);
                        Directory.CreateDirectory(Path.GetDirectoryName(previous)!);
                        File.Move(target, previous);
                    }
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    replaced.Add((target, previous));
                    File.Copy(file, target, overwrite: true);
                }
                installed = true;
            }
            catch
            {
                foreach (var (target, previous) in replaced.AsEnumerable().Reverse())
                {
                    try
                    {
                        if (previous is null) File.Delete(target);
                        else { File.Delete(target); File.Move(previous, target); }
                    }
                    catch { rollbackFailed = true; }
                }
                throw;
            }
            foreach (string file in obsolete)
                try { File.Delete(file); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
        finally
        {
            TryRemove(stage);
            // If rollback failed, preserve the old directory for recovery.
            if (installed || !rollbackFailed) TryRemove(backup);
        }
    }

    private static bool Identical(string incoming, string installed)
    {
        if (new FileInfo(incoming).Length != new FileInfo(installed).Length) return false;
        using var a=File.OpenRead(incoming);
        using var b=File.OpenRead(installed);
        return SHA256.HashData(a).AsSpan().SequenceEqual(SHA256.HashData(b));
    }
    private static void TryRemove(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}


