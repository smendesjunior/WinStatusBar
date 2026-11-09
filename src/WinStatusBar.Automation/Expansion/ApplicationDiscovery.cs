using Microsoft.Win32;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace WinStatusBar.ExecutorExpansion;

public sealed record ApplicationCandidate(string Id, string Name, string Source, string? Location,
    string Classification, string Evidence, bool CanLaunch);
public sealed record DiscoveryReport(IReadOnlyList<ApplicationCandidate> Applications,
    IReadOnlyList<ApplicationCandidate> Dependencies, IReadOnlyList<string> Gaps);

public static class ApplicationDiscovery
{
    private static readonly Regex Dependency = new(
        @"(?i)(?:^|[\s._-])(?:runtime|redistributable|framework|driver|sdk|targeting pack|language pack|resource pack|webview2|vclibs|net native|hostfxr)(?:$|[\s._-])|^Microsoft\.(?:NET|UI\.Xaml|VCLibs|WindowsAppRuntime)|^Windows (?:SDK|Software Development Kit)",
        RegexOptions.CultureInvariant);
    public static bool IsDependency(string name) => Dependency.IsMatch(name);
    public static DiscoveryReport Classify(IEnumerable<ApplicationCandidate> items, IEnumerable<string>? gaps = null)
    {
        var unique = items.DistinctBy(x => x.Id, StringComparer.OrdinalIgnoreCase).OrderBy(x => x.Name).ToArray();
        // Heuristic negatives retained in the report for review; no claims that name matching is exhaustive.
        return new(unique.Where(x => x.Classification != "dependency").ToArray(),
            unique.Where(x => x.Classification == "dependency").ToArray(), (gaps ?? []).ToArray());
    }
    public static async Task<DiscoveryReport> ReadAsync(IEnumerable<string>? portableExecutables, CancellationToken ct)
    {
        var items = new List<ApplicationCandidate>(); var gaps = new List<string>();
        foreach (var hive in new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine })
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                using var root = RegistryKey.OpenBaseKey(hive, view);
                using var uninstall = root.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Uninstall");
                foreach (string name in uninstall?.GetSubKeyNames() ?? [])
                {
                    using var key = uninstall!.OpenSubKey(name);
                    if (key?.GetValue("DisplayName") is not string title) continue;
                    bool dependency = key.GetValue("SystemComponent") is int hidden && hidden == 1 ||
                        key.GetValue("ParentKeyName") is string || IsDependency(title);
                    items.Add(new($"{hive}:{view}:uninstall:{name}", title, "uninstall",
                        key.GetValue("InstallLocation") as string, dependency ? "dependency" : "candidate",
                        dependency ? "Metadado de componente ou heurística de nome; revisar exclusão." :
                            "Registro de instalação; executável ainda não validado.", false));
                }
                using var appPaths = root.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\App Paths");
                foreach (string name in appPaths?.GetSubKeyNames() ?? [])
                {
                    using var key = appPaths!.OpenSubKey(name);
                    string? path = (key?.GetValue("") as string)?.Trim('"');
                    if (path is null) continue;
                    path = Environment.ExpandEnvironmentVariables(path);
                    bool exists = Path.IsPathFullyQualified(path) && File.Exists(path);
                    items.Add(new($"{hive}:{view}:apppath:{name}", Path.GetFileNameWithoutExtension(name),
                        "App Paths", path, IsDependency(name) ? "dependency" : exists ? "functional" : "candidate",
                        exists ? "Executável registrado existe; funcionalidade não executada." : "Caminho não confirmado.", exists));
                }
            }
            catch (Exception e) when (e is UnauthorizedAccessException or System.Security.SecurityException or IOException)
            { gaps.Add($"Inventário incompleto: {hive}/{view} indisponível."); }
        }
        // No user strings in the script. Get-StartApps provides registered launch identities, not just uninstallers.
        const string script = """
            $apps=@(Get-StartApps | Select-Object Name,AppID)
            $packages=@(Get-AppxPackage | Select-Object PackageFamilyName,IsFramework,IsResourcePackage,IsBundle)
            @{apps=$apps;packages=$packages}|ConvertTo-Json -Depth 5 -Compress
            """;
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(TimeSpan.FromSeconds(20));
            var result = await HiddenProcess.PowerShellAsync(script, deadline.Token);
            if (result.ExitCode != 0 || result.Truncated) gaps.Add("StartApps/Appx indisponível ou saída excedeu o limite.");
            else
            {
                using var json = JsonDocument.Parse(result.Stdout);
                var packages = json.RootElement.GetProperty("packages").EnumerateArray().ToArray();
                foreach (var app in json.RootElement.GetProperty("apps").EnumerateArray())
                {
                    string title = app.GetProperty("Name").GetString()!, appId = app.GetProperty("AppID").GetString()!;
                    string family = appId.Split('!')[0];
                    bool dependency = IsDependency(title) || packages.Any(p =>
                        p.GetProperty("PackageFamilyName").GetString() == family &&
                        (p.GetProperty("IsFramework").GetBoolean() || p.GetProperty("IsResourcePackage").GetBoolean() ||
                         p.GetProperty("IsBundle").GetBoolean()));
                    items.Add(new("start:" + appId, title, appId.Contains('!') ? "Store/StartApps" : "Win32/StartApps",
                        appId, dependency ? "dependency" : "functional", "Identidade registrada no Iniciar; abertura não testada.", !dependency));
                }
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        { gaps.Add("StartApps/Appx excedeu 20 s."); }
        catch (Exception e) when (e is JsonException or IOException or System.ComponentModel.Win32Exception)
        { gaps.Add("StartApps/Appx não pôde ser lido."); }
        foreach (string supplied in portableExecutables ?? [])
        {
            ct.ThrowIfCancellationRequested();
            if (!Path.IsPathFullyQualified(supplied) || !supplied.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            { gaps.Add("Caminho portátil ignorado: requer caminho absoluto .exe."); continue; }
            string path = Path.GetFullPath(supplied), title = Path.GetFileNameWithoutExtension(path);
            bool exists = File.Exists(path);
            items.Add(new("portable:" + path, title, "portable-explicit", path,
                IsDependency(title) ? "dependency" : exists ? "functional" : "candidate",
                exists ? "Executável fornecido existe; abertura não testada." : "Executável não encontrado.", exists));
        }
        gaps.Add("Sem varredura do disco: portáteis requerem paths explícitos. Heurísticas podem omitir apps; revisar Dependencies e candidates.");
        return Classify(items, gaps);
    }
}
