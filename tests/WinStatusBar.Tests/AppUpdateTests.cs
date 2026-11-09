using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using WinStatusBar.Core.Integration;
using WinStatusBar.Services;
using Xunit;

namespace WinStatusBar.Tests;

/// <summary>Regras da atualização pelo GitHub: manifesto, origem por PC, conferência do download e textos de erro.</summary>
public class AppUpdateTests
{
    private const string Prefix = "https://github.com/smendesjunior/WinStatusBar/releases/download/";
    private const string Current = "20261109050307";
    private const string Sha = "E6320593919F5EE68F95CF9195BA3587074AB69DAAB40FE83FED7866A8BBAE75";

    private static UpdateManifest M(string build = "20261110000000", string? url = Prefix + "v1.4.0/WinStatusBar-Setup.exe",
        string sha = Sha, long size = 10) => new("1.4.0", build, sha, size, url);

    // ---------------------------------------------------------------- manifesto
    [Fact]
    public void InstallableWhenNewerAndFromRepository() =>
        Assert.True(UpdateRules.IsInstallable(M(), Current, UpdateSourceKind.GitHub));

    [Fact]
    public void RejectsSameOrOlderBuild()
    {
        Assert.False(UpdateRules.IsInstallable(M(Current), Current, UpdateSourceKind.GitHub));
        Assert.False(UpdateRules.IsInstallable(M("20261101000000"), Current, UpdateSourceKind.GitHub));
    }

    [Theory]
    [InlineData("https://github.com/outro/WinStatusBar/releases/download/v1.4.0/WinStatusBar-Setup.exe")]
    [InlineData("http://github.com/smendesjunior/WinStatusBar/releases/download/v1.4.0/WinStatusBar-Setup.exe")]
    [InlineData("https://github.com.evil.example/smendesjunior/WinStatusBar/releases/download/x.exe")]
    [InlineData(null)]
    public void RejectsUrlOutsideRepositoryOnGitHub(string? url) =>
        Assert.False(UpdateRules.IsInstallable(M(url: url), Current, UpdateSourceKind.GitHub));

    [Fact]
    public void LocalSourceIgnoresUrl() =>
        Assert.True(UpdateRules.IsInstallable(M(url: null), Current, UpdateSourceKind.Local));

    [Fact]
    public void RejectsBadHashBuildOrSize()
    {
        Assert.False(UpdateRules.IsInstallable(M(sha: "XYZ"), Current, UpdateSourceKind.GitHub));
        Assert.False(UpdateRules.IsInstallable(M(build: "2026"), Current, UpdateSourceKind.GitHub));
        Assert.False(UpdateRules.IsInstallable(M(size: 0), Current, UpdateSourceKind.GitHub));
    }

    [Fact]
    public void ParseAcceptsLegacyManifestWithoutVersionAndUrl()
    {
        // Arquivo publicado hoje no compartilhamento de rede e no OneDrive (Set-Content grava com BOM).
        var m = UpdateRules.Parse("\uFEFF{\"Sha256\":\"" + Sha + "\",\"Build\":\"20261109050307\",\"Size\":261073453}");
        Assert.NotNull(m);
        Assert.Null(m!.Url);
        Assert.Null(m.Version);
        Assert.Equal(261073453, m.Size);
        Assert.Equal("20261109050307", m.Build);
    }

    [Fact]
    public void ParseReadsNewManifest()
    {
        var m = UpdateRules.Parse("{\"Version\":\"1.4.0\",\"Build\":\"20261110000000\",\"Sha256\":\"" + Sha + "\",\"Size\":5,\"Url\":\"" + Prefix + "v1.4.0/WinStatusBar-Setup.exe\"}");
        Assert.Equal("1.4.0", m!.Version);
        Assert.Equal(Prefix + "v1.4.0/WinStatusBar-Setup.exe", m.Url);
    }

    [Fact]
    public void NewManifestStillReadsInOldRecordShape()
    {
        // As versões 1.3.x desserializam em (Build, Sha256, Size): os campos novos não podem quebrar a leitura.
        var old = JsonSerializer.Deserialize<LegacyRelease>("{\"Version\":\"1.4.0\",\"Build\":\"20261110000000\",\"Sha256\":\"AA\",\"Size\":5,\"Url\":\"x\"}");
        Assert.Equal("20261110000000", old!.Build);
        Assert.Equal(5, old.Size);
    }

    private sealed record LegacyRelease(string Build, string Sha256, long Size);

    [Fact]
    public void ParseReturnsNullForGarbage()
    {
        Assert.Null(UpdateRules.Parse("<html>"));
        Assert.Null(UpdateRules.Parse("{\"Build\":5}"));
    }

    // ---------------------------------------------------------------- conferência do download
    [Fact]
    public void VerifyDownloadRejectsTruncatedFile()
    {
        string file = TempFile(new byte[5]);
        try
        {
            var ex = Assert.Throws<InvalidDataException>(() => UpdateRules.VerifyDownload(file, M(size: 10)));
            Assert.Equal("O download veio incompleto; a atualização fica para a próxima consulta.", ex.Message);
        }
        finally { File.Delete(file); }
    }

    [Fact]
    public void VerifyDownloadRejectsWrongHash()
    {
        string file = TempFile(new byte[10]);
        try
        {
            var ex = Assert.Throws<InvalidDataException>(() => UpdateRules.VerifyDownload(file, M(size: 10)));
            Assert.Equal("A cópia baixada não confere com a publicação; a atualização fica para a próxima consulta.", ex.Message);
        }
        finally { File.Delete(file); }
    }

    [Fact]
    public void VerifyDownloadAcceptsMatchingFile()
    {
        byte[] data = "WinStatusBar"u8.ToArray();
        string file = TempFile(data);
        try { UpdateRules.VerifyDownload(file, M(sha: Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant(), size: data.Length)); }
        finally { File.Delete(file); }
    }

    private static string TempFile(byte[] content)
    {
        string file = Path.Combine(Path.GetTempPath(), "WinStatusBar.Tests." + Guid.NewGuid().ToString("N") + ".part");
        File.WriteAllBytes(file, content);
        return file;
    }

    // ---------------------------------------------------------------- origem por PC
    [Fact]
    public void ResolveMissingFileIsGitHub() => Assert.Equal(UpdateSourceKind.GitHub, UpdateSource.Resolve(null).Kind);

    [Fact]
    public void ResolveTreatsLegacyFileWithBomAsGitHub() =>
        // Gravado pelo BAT do OneDrive CGM (só LocalDirectory): esses PCs passam a usar o GitHub.
        Assert.Equal(UpdateSourceKind.GitHub, UpdateSource.Resolve("\uFEFF{\r\n    \"LocalDirectory\":  \"C:\\\\Users\\\\x\\\\OneDrive - Empresa\\\\WinStatusBar\"\r\n}").Kind);

    [Fact]
    public void ResolveLocalModeKeepsFolder()
    {
        var s = UpdateSource.Resolve("{\"Mode\":\"Local\",\"LocalDirectory\":\"C:\\\\Apps\\\\Instalador\"}");
        Assert.Equal(UpdateSourceKind.Local, s.Kind);
        Assert.Equal(@"C:\Apps\Instalador", s.LocalDirectory);
    }

    [Fact]
    public void ResolveLocalModeRejectsNetworkPath()
    {
        var ex = Assert.Throws<InvalidDataException>(() => UpdateSource.Resolve("{\"Mode\":\"Local\",\"LocalDirectory\":\"\\\\\\\\srv\\\\x\"}"));
        Assert.Equal("A pasta local de atualização é inválida; a atualização automática fica desligada neste PC.", ex.Message);
    }

    [Fact]
    public void ResolveLocalModeRejectsRelativePath() =>
        Assert.Throws<InvalidDataException>(() => UpdateSource.Resolve("{\"Mode\":\"Local\",\"LocalDirectory\":\"Instalador\"}"));

    [Fact]
    public void ResolveBrokenJsonIsGitHub() => Assert.Equal(UpdateSourceKind.GitHub, UpdateSource.Resolve("{oops").Kind);

    // ---------------------------------------------------------------- textos de erro
    [Fact]
    public void ExplainsOffline() =>
        Assert.Equal("Sem conexão com o GitHub. A barra tenta de novo sozinha em 15 minutos.",
            UpdateErrorText.Explain(new HttpRequestException("x"), UpdateSourceKind.GitHub));

    [Fact]
    public void ExplainsIncompleteRelease() =>
        Assert.Equal("A versão nova ainda está sendo publicada no GitHub. A barra tenta de novo sozinha em 15 minutos.",
            UpdateErrorText.Explain(new HttpRequestException("x", null, HttpStatusCode.NotFound), UpdateSourceKind.GitHub));

    [Fact]
    public void ExplainsTimeout() =>
        Assert.Equal("O GitHub demorou demais para responder. A barra tenta de novo sozinha em 15 minutos.",
            UpdateErrorText.Explain(new TaskCanceledException(), UpdateSourceKind.GitHub));

    [Fact]
    public void ExplainsStalledDownload() =>
        // Download parado por 2 minutos: o leitor do fluxo cancela com OperationCanceledException (não TaskCanceledException).
        Assert.Equal("O GitHub demorou demais para responder. A barra tenta de novo sozinha em 15 minutos.",
            UpdateErrorText.Explain(new OperationCanceledException(), UpdateSourceKind.GitHub));

    [Fact]
    public void ExplainsDiskFull() =>
        Assert.Equal("Falta espaço no disco para baixar a atualização (cerca de 300 MB).",
            UpdateErrorText.Explain(new IOException("x", unchecked((int)0x80070070)), UpdateSourceKind.GitHub));

    [Fact]
    public void ExplainsLocalFolderMissing() =>
        Assert.Equal("A pasta local de atualização não está acessível. A barra tenta de novo sozinha em 15 minutos.",
            UpdateErrorText.Explain(new DirectoryNotFoundException("x"), UpdateSourceKind.Local));

    [Fact]
    public void InvalidDataKeepsOwnMessage() =>
        Assert.Equal("Cópia diferente da publicação.",
            UpdateErrorText.Explain(new InvalidDataException("Cópia diferente da publicação."), UpdateSourceKind.GitHub));

    [Fact]
    public void UnknownErrorIsPrefixed() =>
        Assert.Equal("Não foi possível verificar atualizações: falha qualquer",
            UpdateErrorText.Explain(new InvalidOperationException("falha qualquer"), UpdateSourceKind.GitHub));

    // ---------------------------------------------------------------- uma consulta por vez
    [Fact]
    public async Task CheckNowWhileRunningReturnsSameTask()
    {
        // O botão "Verificar atualizações" durante a consulta automática acompanha a mesma consulta: nunca dois downloads.
        var gate = new TaskCompletionSource<UpdateProgress>();
        int calls = 0;
        AppUpdater.CheckOverrideForTests = async () => { calls++; return await gate.Task; };
        try
        {
            var first = AppUpdater.CheckNowAsync();
            var second = AppUpdater.CheckNowAsync();
            Assert.Same(first, second);
            Assert.Equal(1, calls);
            gate.SetResult(new UpdateProgress(UpdateStage.UpToDate, "ok"));
            Assert.Equal(UpdateStage.UpToDate, (await first).Stage);
            var third = AppUpdater.CheckNowAsync();
            Assert.NotSame(first, third);
            Assert.Equal(2, calls);
        }
        finally { AppUpdater.CheckOverrideForTests = null; }
    }
}
