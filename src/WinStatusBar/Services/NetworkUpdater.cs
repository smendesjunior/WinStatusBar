using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using WinStatusBar.Core.Integration;

namespace WinStatusBar.Services;

/// <summary>Atualização por usuário, exclusivamente do compartilhamento autorizado. Nenhuma varredura de PCs.</summary>
internal static class NetworkUpdater
{
    private const string Share = @"\\servidor\rede\Obras\Engenharia\WinStatusBar";
    private sealed record Release(string Build, string Sha256, long Size);
    private sealed record SourceConfiguration(string? LocalDirectory);

    private static string UpdateDirectory()
    {
        // Configuração exclusiva desta instalação; não faz parte das preferências sincronizadas.
        string file = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "WinStatusBar", "update-source.json");
        if (!File.Exists(file)) return Share;
        var config = JsonSerializer.Deserialize<SourceConfiguration>(File.ReadAllText(file));
        string? path = config?.LocalDirectory;
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path) || path.StartsWith(@"\\"))
            throw new InvalidDataException("A origem local de atualização é inválida; consulta de rede desativada neste PC.");
        return path; // sem fallback para a rede, mesmo se a pasta local estiver temporariamente indisponível
    }

    public static void Start()
    {
        if (PackageContext.IsPackaged || !AppContext.BaseDirectory.TrimEnd('\\').Equals(
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "WinStatusBar"), StringComparison.OrdinalIgnoreCase)) return;
        var build = typeof(NetworkUpdater).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => a.Key == "NetworkUpdateBuild")?.Value;
        if (string.IsNullOrEmpty(build)) return; // builds de desenvolvimento não se atualizam
        _ = Task.Run(async () =>
        {
            await Task.Delay(TimeSpan.FromSeconds(30));
            while (true)
            {
                try { if (await CheckAsync(build)) return; }
                catch (Exception ex) { App.Log("Atualização pela rede: " + ex.Message); }
                await Task.Delay(TimeSpan.FromMinutes(5));
            }
        });
    }

    private static async Task<bool> CheckAsync(string currentBuild)
    {
        string directorySource = UpdateDirectory();
        string manifest = Path.Combine(directorySource, "WinStatusBar-update.json");
        if (!File.Exists(manifest)) return false;
        var release = JsonSerializer.Deserialize<Release>(await File.ReadAllTextAsync(manifest));
        if (release is null || release.Build is not { Length: 14 } || !release.Build.All(char.IsAsciiDigit)
            || string.CompareOrdinal(release.Build, currentBuild) <= 0 || release.Size <= 0
            || release.Sha256 is not { Length: 64 } || !release.Sha256.All(Uri.IsHexDigit)) return false;
        string directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WinStatusBar", "Updates");
        Directory.CreateDirectory(directory);
        string local = Path.Combine(directory, "WinStatusBar-Setup.exe");
        // Leitura compartilhada sem escrita: não aceita o instalador enquanto ele estiver sendo publicado.
        using (var source = new FileStream(Path.Combine(directorySource, "WinStatusBar-Setup.exe"), FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            if (source.Length != release.Size) return false;
            using var destination = new FileStream(local, FileMode.Create, FileAccess.Write, FileShare.None);
            await source.CopyToAsync(destination);
        }
        using (var file = File.OpenRead(local))
            if (!Convert.ToHexString(await SHA256.HashDataAsync(file)).Equals(release.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Cópia do instalador não corresponde à publicação; atualização adiada.");
        var start = new ProcessStartInfo(local) { UseShellExecute = false, CreateNoWindow = true };
        start.ArgumentList.Add("/quiet");
        start.ArgumentList.Add("/update");
        using var process = Process.Start(start);
        if (process is null) return false;
        App.Log("Atualização pela rede iniciada: " + release.Build);
        await process.WaitForExitAsync();
        return process.ExitCode == 0;
    }
}
