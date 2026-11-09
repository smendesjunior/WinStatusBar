using System.IO.Compression;
using WinStatusBar.Core.Integration;
using Xunit;

namespace WinStatusBar.Tests;

public class InstallationFilesTests
{
    [Fact]
    public void LoadedFileWithDeleteSharingCanBeReplaced()
    {
        string dir=Path.Combine(Path.GetTempPath(),"WinStatusBar.Tests",Guid.NewGuid().ToString());
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir,"WinStatusBar.exe"),"old");
            using(var locked=new FileStream(Path.Combine(dir,"WinStatusBar.exe"),FileMode.Open,FileAccess.Read,FileShare.Read|FileShare.Delete))
            {
                using var payload=new MemoryStream();
                using(var zip=new ZipArchive(payload,ZipArchiveMode.Create,true))
                using(var w=new StreamWriter(zip.CreateEntry("WinStatusBar.exe").Open()))w.Write("new");
                payload.Position=0;
                InstallationFiles.Replace(payload,dir,()=>{});
                Assert.Equal("new",File.ReadAllText(Path.Combine(dir,"WinStatusBar.exe")));
                using var reader=new StreamReader(locked);
                Assert.Equal("old",reader.ReadToEnd());
            }
        }
        finally {Directory.Delete(dir,true);}
    }
    [Fact]
    public void IdenticalRuntimeMayStayLoadedDuringUpdate()
    {
        string dir=Path.Combine(Path.GetTempPath(),"WinStatusBar.Tests",Guid.NewGuid().ToString());
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir,"runtime.dll"),"identical");
            using var locked=new FileStream(Path.Combine(dir,"runtime.dll"),FileMode.Open,FileAccess.Read,FileShare.Read);
            using var payload=new MemoryStream();
            using(var zip=new ZipArchive(payload,ZipArchiveMode.Create,true))
            {
                using(var w=new StreamWriter(zip.CreateEntry("WinStatusBar.exe").Open()))w.Write("new");
                using(var w=new StreamWriter(zip.CreateEntry("runtime.dll").Open()))w.Write("identical");
            }
            payload.Position=0;
            InstallationFiles.Replace(payload,dir,()=>{});
            Assert.Equal("new",File.ReadAllText(Path.Combine(dir,"WinStatusBar.exe")));
        }
        finally {Directory.Delete(dir,true);}
    }
    [Fact]
    public void StopFailureLeavesOldInstallationAndRemovesStaging()
    {
        string root = Path.Combine(Path.GetTempPath(), "WinStatusBar.Tests", Guid.NewGuid().ToString());
        string dir = Path.Combine(root, "app");
        try
        {
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "WinStatusBar.exe"), "old");
            using var payload = new MemoryStream();
            using (var zip = new ZipArchive(payload, ZipArchiveMode.Create, true))
            using (var writer = new StreamWriter(zip.CreateEntry("WinStatusBar.exe").Open())) writer.Write("new");
            payload.Position = 0;
            Assert.Throws<IOException>(() => InstallationFiles.Replace(payload, dir, () => throw new IOException("locked")));
            Assert.Equal("old", File.ReadAllText(Path.Combine(dir, "WinStatusBar.exe")));
            Assert.Single(Directory.GetDirectories(root));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public void InvalidPayloadLeavesExistingAppUntouchedAndRunning()
    {
        string dir = Path.Combine(Path.GetTempPath(), "WinStatusBar.Tests", Guid.NewGuid().ToString());
        try
        {
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "WinStatusBar.exe"), "old");
            bool stopped = false;
            using var payload = new MemoryStream([1, 2, 3]);
            Assert.Throws<InvalidDataException>(() => InstallationFiles.Replace(payload, dir, () => stopped = true));
            Assert.False(stopped);
            Assert.Equal("old", File.ReadAllText(Path.Combine(dir, "WinStatusBar.exe")));
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }

    [Fact]
    public void SuccessfulReplacementRemovesObsoleteFiles()
    {
        string dir = Path.Combine(Path.GetTempPath(), "WinStatusBar.Tests", Guid.NewGuid().ToString());
        try
        {
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "obsolete.dll"), "old");
            using var payload = new MemoryStream();
            using (var zip = new ZipArchive(payload, ZipArchiveMode.Create, true))
            using (var writer = new StreamWriter(zip.CreateEntry("WinStatusBar.exe").Open())) writer.Write("new");
            payload.Position = 0;
            InstallationFiles.Replace(payload, dir, () => { });
            Assert.Equal("new", File.ReadAllText(Path.Combine(dir, "WinStatusBar.exe")));
            Assert.False(File.Exists(Path.Combine(dir, "obsolete.dll")));
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }
}


