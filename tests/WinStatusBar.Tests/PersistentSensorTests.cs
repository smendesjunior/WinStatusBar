using System.Xml.Linq;
using WinStatusBar.Core.Integration;
using WinStatusBar.Core.Ipc;
using Xunit;
namespace WinStatusBar.Tests;
public class PersistentSensorTests
{
    [Fact] public void TaskOnlyRunsTheProtectedReaderForThisUserAtLogon()
    {
        const string sid = "S-1-5-21-111-222-333-1001";
        string path = Path.Combine(SensorPermissionTask.ProtectedRoot, "test-version", SensorPermissionTask.ExecutableName);
        var xml = XDocument.Parse(SensorPermissionTask.BuildTaskXml(path, sid));
        XNamespace ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";
        var action = Assert.Single(xml.Descendants(ns + "Exec"));
        Assert.Equal(path, action.Element(ns + "Command")!.Value);
        Assert.Equal("--worker", action.Element(ns + "Arguments")!.Value);
        Assert.Equal("HighestAvailable", xml.Descendants(ns + "RunLevel").Single().Value);
        Assert.All(xml.Descendants(ns + "UserId"), user => Assert.Equal(sid, user.Value));
        Assert.Equal("InteractiveToken", xml.Descendants(ns + "LogonType").Single().Value);
        Assert.Equal("false", xml.Descendants(ns + "DisallowStartIfOnBatteries").Single().Value);
        Assert.Equal("PT0S", xml.Descendants(ns + "ExecutionTimeLimit").Single().Value);
    }
    [Fact] public void UserWritableExecutablesAndPathEscapesAreRejected()
    {
        Assert.False(SensorPermissionTask.IsProtectedExecutable(Path.Combine(Path.GetTempPath(), SensorPermissionTask.ExecutableName)));
        Assert.False(SensorPermissionTask.IsProtectedExecutable(Path.Combine(SensorPermissionTask.ProtectedRoot, "..", "other", SensorPermissionTask.ExecutableName)));
        Assert.Throws<ArgumentException>(() => SensorPermissionTask.BuildTaskXml("cmd.exe", "S-1-5-21-111-222-333-1001"));
    }
    [Fact] public void StaleOrInvalidSensorReadingsAreRejected()
    {
        var now = DateTimeOffset.UtcNow;
        Assert.True(CpuSensorPipe.IsFresh(new(80, now), now));
        Assert.False(CpuSensorPipe.IsFresh(new(80, now.AddMinutes(-1)), now));
        Assert.False(CpuSensorPipe.IsFresh(new(80, now.AddMinutes(1)), now));
        Assert.False(CpuSensorPipe.IsFresh(new(null, now), now));
        Assert.False(CpuSensorPipe.IsFresh(new(255, now), now));
    }
}
