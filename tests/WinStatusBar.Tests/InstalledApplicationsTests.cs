using WinStatusBar.Core.Integration;
using WinStatusBar.Core.Metrics;
using Xunit;

namespace WinStatusBar.Tests;

public class InstalledApplicationsTests
{
    [Theory]
    [InlineData("\"C:\\Program Files\\App\\remove.exe\" /remove", "C:\\Program Files\\App\\remove.exe", "/remove")]
    [InlineData("C:\\Program Files\\App\\remove.exe /remove", "C:\\Program Files\\App\\remove.exe", "/remove")]
    public void PreservesExecutableAndArguments(string value, string file, string arguments)
    {
        var parsed = InstalledApplications.ParseUninstallCommand(value);
        Assert.NotNull(parsed); Assert.Equal(file, parsed.Value.File); Assert.Equal(arguments, parsed.Value.Arguments);
    }
    [Theory]
    [InlineData(null)] [InlineData("cmd /c remove.bat")] [InlineData("remove.exe /q")] [InlineData("\"missing")]
    public void RejectsAmbiguousCommands(string? value) => Assert.Null(InstalledApplications.ParseUninstallCommand(value));
    [Fact]
    public void MsiMaintenanceBecomesUninstall()
    {
        var parsed = InstalledApplications.ParseUninstallCommand("MsiExec.exe /I{12345678-1234-1234-1234-123456789012}");
        Assert.Equal(Path.Combine(Environment.SystemDirectory, "msiexec.exe"), parsed!.Value.File);
        Assert.StartsWith("/x{", parsed.Value.Arguments);
    }
    [Fact]
    public void EntireUpdateSelectionIsValidatedBeforeExecution()
        => Assert.Equal(2, WindowsUpdateInstallation.InstallSelection("12345678-1234-1234-1234-123456789012:1,invalid"));
}
