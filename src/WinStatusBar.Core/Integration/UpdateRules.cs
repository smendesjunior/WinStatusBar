using System.Security.Cryptography;
using System.Text.Json;

namespace WinStatusBar.Core.Integration;

/// <summary>Manifesto publicado junto do instalador (Release do GitHub ou pasta local deste PC).</summary>
/// <remarks>Versões 1.3.x leem o mesmo arquivo só com Build, Sha256 e Size; Version e Url são campos novos.</remarks>
public sealed record UpdateManifest(string? Version, string Build, string Sha256, long Size, string? Url);

/// <summary>Tentativas de instalar uma compilação, gravadas em <c>Updates\install-attempts.json</c>: o instalador fecha a
/// barra antes de trocar os arquivos, então a contagem em memória se perde quando a troca falha e é desfeita.</summary>
public sealed record InstallAttempts(string Build, int Count, DateTimeOffset Last);

/// <summary>Regras da atualização automática: onde procurar, o que aceitar e como conferir o download.</summary>
public static class UpdateRules
{
    public const string ManifestFileName = "WinStatusBar-update.json";
    public const string SetupFileName = "WinStatusBar-Setup.exe";

    /// <summary>Link fixo da Release mais recente (sem token e sem o limite de consultas da API do GitHub).</summary>
    public const string GitHubManifestUrl = "https://github.com/smendesjunior/WinStatusBar/releases/latest/download/" + ManifestFileName;

    /// <summary>Só instaladores das Releases deste repositório são aceitos.</summary>
    public const string GitHubDownloadPrefix = "https://github.com/smendesjunior/WinStatusBar/releases/download/";

    public static readonly TimeSpan FirstCheckDelay = TimeSpan.FromMinutes(1);
    public static readonly TimeSpan CheckInterval = TimeSpan.FromMinutes(15);

    /// <summary>Lê o manifesto; null se não for um manifesto (HTML de erro, JSON quebrado, campos com tipo errado).</summary>
    public static UpdateManifest? Parse(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json.TrimStart('﻿'));
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            string? build = Text(root, "Build"), sha = Text(root, "Sha256");
            if (build is null || sha is null || !root.TryGetProperty("Size", out var size) || size.ValueKind != JsonValueKind.Number) return null;
            return new UpdateManifest(Text(root, "Version"), build, sha, size.GetInt64(), Text(root, "Url"));
        }
        catch (Exception ex) when (ex is JsonException or FormatException or InvalidOperationException) { return null; }
    }

    private static string? Text(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    /// <summary>Compilação mais nova, tamanho e hash bem formados e, no GitHub, instalador deste repositório.</summary>
    public static bool IsInstallable(UpdateManifest m, string currentBuild, UpdateSourceKind kind)
    {
        if (m.Build is not { Length: 14 } || !m.Build.All(char.IsAsciiDigit) || string.CompareOrdinal(m.Build, currentBuild) <= 0) return false;
        if (m.Size <= 0 || m.Sha256 is not { Length: 64 } || !m.Sha256.All(char.IsAsciiHexDigit)) return false;
        return kind == UpdateSourceKind.Local || (m.Url is not null && m.Url.StartsWith(GitHubDownloadPrefix, StringComparison.Ordinal));
    }

    /// <summary>Espera da consulta automática: 15 min; depois de 3 falhas seguidas da mesma versão, 6 h (evita baixar
    /// ~270 MB a cada 15 min quando a instalação falha sempre do mesmo jeito). O botão "Verificar atualizações" não espera.</summary>
    public static TimeSpan RetryDelay(int consecutiveFailures) => consecutiveFailures >= 3 ? TimeSpan.FromHours(6) : CheckInterval;

    // ---------------------------------------------------------------- tentativas de instalação (gravadas em disco)
    public const string InstallAttemptsFileName = "install-attempts.json";
    public const int MaxInstallAttempts = 3;
    public static readonly TimeSpan InstallRetryInterval = TimeSpan.FromHours(6);

    /// <summary>Instalação automática permitida: outra compilação, menos de 3 tentativas ou 6 h desde a última. Sem isto,
    /// uma troca de arquivos que falha sempre reabriria a barra e repetiria a instalação a cada minuto. O botão
    /// "Verificar atualizações" não passa por aqui.</summary>
    public static bool MayInstall(InstallAttempts? attempts, string build, DateTimeOffset now) =>
        attempts is null || attempts.Build != build || attempts.Count < MaxInstallAttempts
        || now - attempts.Last >= InstallRetryInterval
        || now < attempts.Last; // relógio do PC voltou: não segura a atualização para sempre

    /// <summary>Registro a gravar antes de abrir o instalador: mais uma tentativa da mesma compilação, ou a primeira de outra.</summary>
    public static InstallAttempts NextInstallAttempt(InstallAttempts? attempts, string build, DateTimeOffset now) =>
        new(build, attempts is not null && attempts.Build == build ? attempts.Count + 1 : 1, now);

    /// <summary>Lê o registro de tentativas; null se o texto não for um registro válido (nesse caso a instalação segue).</summary>
    public static InstallAttempts? ParseInstallAttempts(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            using var doc = JsonDocument.Parse(json.TrimStart('﻿'));
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            if (Text(root, "Build") is not { Length: > 0 } build) return null;
            if (!root.TryGetProperty("Count", out var count) || count.ValueKind != JsonValueKind.Number || !count.TryGetInt32(out int tries)) return null;
            if (!root.TryGetProperty("Last", out var last) || last.ValueKind != JsonValueKind.String || !last.TryGetDateTimeOffset(out var when)) return null;
            return new InstallAttempts(build, tries, when);
        }
        catch (Exception ex) when (ex is JsonException or FormatException or InvalidOperationException) { return null; }
    }

    public static string SerializeInstallAttempts(InstallAttempts attempts)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("Build", attempts.Build);
            writer.WriteNumber("Count", attempts.Count);
            writer.WriteString("Last", attempts.Last);
            writer.WriteEndObject();
        }
        return System.Text.Encoding.UTF8.GetString(buffer.ToArray());
    }

    /// <summary>Registro de tentativas no disco; arquivo ausente, ilegível ou inválido conta como nenhuma tentativa.</summary>
    public static InstallAttempts? LoadInstallAttempts(string file)
    {
        try { return File.Exists(file) ? ParseInstallAttempts(File.ReadAllText(file)) : null; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    public static void SaveInstallAttempts(string file, InstallAttempts attempts)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, SerializeInstallAttempts(attempts));
    }

    /// <summary>Barra em dia: apaga o instalador já usado (~270 MB) e o registro de tentativas. O que não puder ser
    /// apagado agora (instalador ainda em execução) fica para a próxima consulta.</summary>
    public static void ClearFinishedUpdate(string updatesDir)
    {
        foreach (string name in new[] { SetupFileName, InstallAttemptsFileName })
        {
            try
            {
                string file = Path.Combine(updatesDir, name);
                if (File.Exists(file)) File.Delete(file);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    // ---------------------------------------------------------------- barra ocupada (resposta de IA em andamento)
    public static readonly TimeSpan BusyRecheckDelay = TimeSpan.FromMinutes(1);
    public static readonly TimeSpan MaxInstallDeferral = TimeSpan.FromHours(2);

    /// <summary>A instalação automática espera enquanto a barra está ocupada, por no máximo 2 h desde o primeiro adiamento
    /// (depois disso instala mesmo ocupada). O botão "Verificar atualizações" não espera.</summary>
    public static bool ShouldDeferInstall(bool busy, DateTimeOffset? firstDeferredAt, DateTimeOffset now) =>
        busy && (firstDeferredAt is not { } first || (now >= first && now - first < MaxInstallDeferral));

    /// <summary>Espera até a próxima consulta automática: 1 min com a instalação adiada pela barra ocupada; senão, <see cref="RetryDelay"/>.</summary>
    public static TimeSpan NextCheckDelay(bool installDeferred, int consecutiveFailures) =>
        installDeferred ? BusyRecheckDelay : RetryDelay(consecutiveFailures);

    /// <summary>Instalador de uma tentativa anterior que ainda confere com o manifesto: dispensa novo download.</summary>
    public static bool CanReuse(string file, UpdateManifest m)
    {
        if (!File.Exists(file)) return false;
        try { VerifyDownload(file, m); return true; }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException) { return false; }
    }

    /// <summary>Confere tamanho e SHA-256 do arquivo baixado antes de qualquer execução.</summary>
    public static void VerifyDownload(string file, UpdateManifest m)
    {
        if (new FileInfo(file).Length != m.Size)
            throw new InvalidDataException("O download veio incompleto; a atualização fica para a próxima consulta.");
        using var stream = File.OpenRead(file);
        if (!Convert.ToHexString(SHA256.HashData(stream)).Equals(m.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("A cópia baixada não confere com a publicação; a atualização fica para a próxima consulta.");
    }
}
