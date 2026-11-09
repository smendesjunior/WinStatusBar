using Microsoft.Win32;
using System.Diagnostics;
using System.Text.RegularExpressions;
using Windows.Management.Deployment;

namespace WinStatusBar.Core.Integration;

public sealed record InstalledApplication(string Id, string Name, string Publisher, string Version,
    string? UninstallCommand, string? PackageName, string? IconPath, bool Removable)
{
    public string Details => string.Join(" · ", new[] { Publisher, Version }.Where(s => !string.IsNullOrWhiteSpace(s)));
}

public static class InstalledApplications
{
    public static IReadOnlyList<InstalledApplication> Read()
    {
        var apps = new List<InstalledApplication>();
        foreach (var hive in new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine })
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            try
            {
                using var root = RegistryKey.OpenBaseKey(hive, view);
                using var uninstall = root.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Uninstall");
                foreach (string keyName in uninstall?.GetSubKeyNames() ?? [])
                {
                    try
                    {
                        using var key = uninstall!.OpenSubKey(keyName);
                        if (key?.GetValue("DisplayName") is not string name || string.IsNullOrWhiteSpace(name)
                            || key.GetValue("SystemComponent") is int hidden && hidden == 1
                            || key.GetValue("ParentKeyName") is string) continue;
                        string? command = key.GetValue("UninstallString") as string;
                        if (key.GetValue("WindowsInstaller") is int msi && msi == 1 && Guid.TryParse(keyName, out var product))
                            command = $"\"{Path.Combine(Environment.SystemDirectory, "msiexec.exe")}\" /x {{{product:D}}}";
                        string? icon = key.GetValue("DisplayIcon") as string;
                        if (icon is not null)
                        {
                            icon = Regex.Replace(icon, @",\s*-?\d+\s*$", "").Trim().Trim('"');
                            icon = Environment.ExpandEnvironmentVariables(icon);
                            if (!File.Exists(icon)) icon = null;
                        }
                        apps.Add(new($"{hive}:{view}:{keyName}", name.Trim(), key.GetValue("Publisher") as string ?? "",
                            key.GetValue("DisplayVersion") as string ?? "", command, null, icon,
                            key.GetValue("NoRemove") is not int noRemove || noRemove != 1));
                    }
                    catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException) { }
                }
            }
            catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException) { }
        }
        try
        {
        foreach (var package in new PackageManager().FindPackagesForUser(string.Empty))
        {
            try
            {
                if (package.IsFramework || package.IsResourcePackage || package.IsBundle) continue;
                string name = string.IsNullOrWhiteSpace(package.DisplayName) ? package.Id.Name : package.DisplayName;
                var v = package.Id.Version;
                string? icon = package.Logo.IsFile && File.Exists(package.Logo.LocalPath) ? package.Logo.LocalPath : null;
                apps.Add(new(package.Id.FullName, name, package.PublisherDisplayName, $"{v.Major}.{v.Minor}.{v.Build}.{v.Revision}",
                    null, package.Id.FullName, icon, true));
            }
            catch { } // a package can disappear while another installer is running
        }
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or UnauthorizedAccessException)
        { System.Diagnostics.Trace.WriteLine("Installed package enumeration: " + ex.Message); }
        return apps.DistinctBy(x => (x.Name.ToUpperInvariant(), x.Version, x.UninstallCommand, x.PackageName))
            .OrderBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();
    }

    public static (string File, string Arguments)? ParseUninstallCommand(string? command)
    {
        if (string.IsNullOrWhiteSpace(command)) return null;
        command = Environment.ExpandEnvironmentVariables(command.Trim());
        string file, arguments;
        if (command.StartsWith('"'))
        {
            int end = command.IndexOf('"', 1);
            if (end <= 1) return null;
            file = command[1..end]; arguments = command[(end + 1)..].TrimStart();
        }
        else
        {
            var executable = Regex.Match(command, @"^(.+?\.exe)(?=\s|$)", RegexOptions.IgnoreCase);
            if (!executable.Success) return null;
            file = executable.Groups[1].Value; arguments = command[executable.Length..].TrimStart();
        }
        if (!file.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) return null;
        if (Path.GetFileName(file).Equals("msiexec.exe", StringComparison.OrdinalIgnoreCase))
        {
            file = Path.Combine(Environment.SystemDirectory, "msiexec.exe");
            arguments = Regex.Replace(arguments, @"(?i)(^|\s)/i(?=\s|\{)", "$1/x");
        }
        else if (!Path.IsPathFullyQualified(file)) return null;
        return (file, arguments);
    }

    public static async Task UninstallAsync(InstalledApplication app)
    {
        if (!app.Removable) throw new InvalidOperationException("O Windows não permite remover este aplicativo.");
        // Re-read before executing, so a stale list cannot run an obsolete uninstall command.
        var current = (await Task.Run(Read)).FirstOrDefault(x => x.Id == app.Id)
            ?? throw new InvalidOperationException("O aplicativo não está mais na lista. Atualize o painel.");
        if (!current.Removable) throw new InvalidOperationException("Este aplicativo não pode ser removido.");
        if (current.PackageName is { } package)
        {
            var result = await new PackageManager().RemovePackageAsync(package);
            if (result.ExtendedErrorCode is { } error && error.HResult < 0) throw new InvalidOperationException(result.ErrorText, error);
            return;
        }
        if (ParseUninstallCommand(current.UninstallCommand) is not { } command)
            throw new InvalidOperationException("Desinstalador indisponível. Use Aplicativos instalados no Windows.");
        var start = new ProcessStartInfo(command.File, command.Arguments) { UseShellExecute = true };
        using var process = Process.Start(start);
        if (process is not null)
        {
            await process.WaitForExitAsync();
            if (process.ExitCode is not (0 or 3010)) throw new InvalidOperationException("O desinstalador foi cancelado ou não concluiu a operação.");
        }
    }
}
