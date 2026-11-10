using WinStatusBar.Core.Ai.Codex.ApiResume;
using Xunit;

namespace WinStatusBar.Tests;

public class CodexThreadLockTests
{
    private static string TempLock(bool create)
    {
        string dir = Path.Combine(Path.GetTempPath(), "wsb-lock-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, CodexResumeCommandTests.Id2 + ".lock");
        if (create) File.WriteAllBytes(path, []);
        return path;
    }

    [Fact]
    public void MissingFileIsFreeAndNotCreated()
    {
        string path = TempLock(create: false);
        try
        {
            Assert.Equal(ThreadLockState.Free, CodexThreadLock.Probe(path));
            Assert.False(File.Exists(path));
            Assert.Equal(ThreadLockState.Free, CodexThreadLock.Probe(Path.Combine(Path.GetDirectoryName(path)!, "nao-existe", "x.lock")));
        }
        finally { Directory.Delete(Path.GetDirectoryName(path)!, true); }
    }

    [Fact]
    public void LockHeldByAnotherHandleIsDetected()
    {
        string path = TempLock(create: true);
        try
        {
            using (var holder = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete))
            {
                holder.Lock(0, 1);
                Assert.Equal(ThreadLockState.Held, CodexThreadLock.Probe(path));
                holder.Unlock(0, 1);
            }
        }
        finally { Directory.Delete(Path.GetDirectoryName(path)!, true); }
    }

    [Fact]
    public void ProbeReleasesAtOnce()
    {
        string path = TempLock(create: true);
        try
        {
            Assert.Equal(ThreadLockState.Free, CodexThreadLock.Probe(path));
            using var next = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete);
            next.Lock(0, 1);
            next.Unlock(0, 1);
        }
        finally { Directory.Delete(Path.GetDirectoryName(path)!, true); }
    }

    [Fact]
    public void PathForUsesTheCodexFolder()
    {
        Assert.Equal(@"C:\h\thread-writer-locks\" + CodexResumeCommandTests.Id2 + ".lock", CodexThreadLock.PathFor(@"C:\h", CodexResumeCommandTests.Id2));
        Assert.Throws<ArgumentException>(() => CodexThreadLock.PathFor(@"C:\h", @"..\x"));
        Assert.Throws<ArgumentException>(() => CodexThreadLock.PathFor(@"C:\h", "abc"));
    }
}
