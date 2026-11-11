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

    [Fact]
    public void CanReuseVerifiedInstallerFromPreviousAttempt()
    {
        byte[] data = "instalador"u8.ToArray();
        string file = TempFile(data);
        try { Assert.True(UpdateRules.CanReuse(file, M(sha: Convert.ToHexString(SHA256.HashData(data)), size: data.Length))); }
        finally { File.Delete(file); }
    }

    [Fact]
    public void CannotReuseMissingOrDifferentInstaller()
    {
        Assert.False(UpdateRules.CanReuse(Path.Combine(Path.GetTempPath(), "nao-existe-" + Guid.NewGuid().ToString("N") + ".exe"), M()));
        string file = TempFile(new byte[10]);
        try { Assert.False(UpdateRules.CanReuse(file, M(size: 10))); }
        finally { File.Delete(file); }
    }

    [Theory]
    [InlineData(0, 15)]
    [InlineData(1, 15)]
    [InlineData(2, 15)]
    [InlineData(3, 360)]
    [InlineData(9, 360)]
    public void AutomaticRetryBacksOffAfterThreeFailuresOfTheSameBuild(int failures, int minutes) =>
        // Sem isso, uma falha que se repete baixaria ~270 MB a cada 15 minutos (o botão manual não espera).
        Assert.Equal(TimeSpan.FromMinutes(minutes), UpdateRules.RetryDelay(failures));

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
    public void ExplainsConnectionDroppedDuringDownload()
    {
        // Queda da conexão no meio dos ~270 MB: o .NET lança HttpIOException ou IOException com SocketException dentro.
        const string Expected = "A conexão com o GitHub caiu durante o download. A barra tenta de novo sozinha em 15 minutos.";
        Assert.Equal(Expected, UpdateErrorText.Explain(new HttpIOException(HttpRequestError.ResponseEnded), UpdateSourceKind.GitHub));
        Assert.Equal(Expected, UpdateErrorText.Explain(new IOException("x", new System.Net.Sockets.SocketException(10054)), UpdateSourceKind.GitHub));
    }

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

    [Fact]
    public async Task ManualCheckJoinsTheRunningCheckToo()
    {
        var gate = new TaskCompletionSource<UpdateProgress>();
        int calls = 0;
        AppUpdater.CheckOverrideForTests = async () => { calls++; return await gate.Task; };
        try
        {
            var automatic = AppUpdater.CheckNowAsync();
            var manual = AppUpdater.CheckNowAsync(manual: true);
            Assert.Same(automatic, manual);
            Assert.Equal(1, calls);
            gate.SetResult(new UpdateProgress(UpdateStage.UpToDate, "ok"));
            await automatic;
        }
        finally { AppUpdater.CheckOverrideForTests = null; }
    }

    // ---------------------------------------------------------------- tentativas de instalação gravadas em disco
    private const string Newer = "20261110000000";
    private static readonly DateTimeOffset T0 = new(2026, 11, 11, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void MayInstallWithoutPreviousAttemptsOrForAnotherBuild()
    {
        Assert.True(UpdateRules.MayInstall(null, Newer, T0));
        Assert.True(UpdateRules.MayInstall(new InstallAttempts("20261109000000", 9, T0), Newer, T0));
    }

    [Theory]
    [InlineData(1, 0, true)]
    [InlineData(2, 1, true)]
    [InlineData(3, 1, false)]
    [InlineData(3, 359, false)]
    [InlineData(3, 360, true)]
    [InlineData(7, 30, false)]
    [InlineData(7, 361, true)]
    public void AutomaticInstallStopsForSixHoursAfterThreeAttemptsOfTheSameBuild(int count, int minutesSinceLast, bool allowed) =>
        Assert.Equal(allowed, UpdateRules.MayInstall(new InstallAttempts(Newer, count, T0), Newer, T0.AddMinutes(minutesSinceLast)));

    [Fact]
    public void ClockSetBackDoesNotBlockTheUpdateForever() =>
        Assert.True(UpdateRules.MayInstall(new InstallAttempts(Newer, 3, T0), Newer, T0.AddDays(-2)));

    [Fact]
    public void NextInstallAttemptCountsPerBuild()
    {
        Assert.Equal(new InstallAttempts(Newer, 1, T0), UpdateRules.NextInstallAttempt(null, Newer, T0));
        Assert.Equal(new InstallAttempts(Newer, 3, T0), UpdateRules.NextInstallAttempt(new InstallAttempts(Newer, 2, T0.AddHours(-1)), Newer, T0));
        Assert.Equal(new InstallAttempts(Newer, 1, T0), UpdateRules.NextInstallAttempt(new InstallAttempts("20261109000000", 5, T0), Newer, T0));
    }

    [Fact]
    public void RelaunchedBarStopsReinstallingABuildWhoseInstallKeepsRollingBack()
    {
        // O instalador fecha a barra, a troca dos arquivos falha e é desfeita, a barra reabre sem memória e consulta de
        // novo em 1 minuto. Antes do registro em disco, isso repetia a instalação a cada minuto, para sempre.
        string dir = TempDir();
        string file = Path.Combine(dir, "Updates", UpdateRules.InstallAttemptsFileName);
        try
        {
            int Launches(DateTimeOffset from, int restarts)
            {
                int launched = 0;
                for (int i = 0; i < restarts; i++)
                {
                    var now = from.AddMinutes(i);
                    var attempts = UpdateRules.LoadInstallAttempts(file); // processo novo: só o disco lembra
                    if (!UpdateRules.MayInstall(attempts, Newer, now)) continue;
                    UpdateRules.SaveInstallAttempts(file, UpdateRules.NextInstallAttempt(attempts, Newer, now));
                    launched++;
                }
                return launched;
            }
            Assert.Equal(3, Launches(T0, restarts: 30));
            Assert.Equal(1, Launches(T0.AddHours(7), restarts: 30)); // uma nova tentativa a cada 6 horas
            Assert.Equal(4, UpdateRules.LoadInstallAttempts(file)!.Count);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void InstallAttemptsRoundTripAndBrokenFilesCountAsNoAttempts()
    {
        var saved = new InstallAttempts(Newer, 2, T0.AddSeconds(0.5));
        Assert.Equal(saved, UpdateRules.ParseInstallAttempts(UpdateRules.SerializeInstallAttempts(saved)));
        Assert.Null(UpdateRules.ParseInstallAttempts(null));
        Assert.Null(UpdateRules.ParseInstallAttempts(""));
        Assert.Null(UpdateRules.ParseInstallAttempts("{oops"));
        Assert.Null(UpdateRules.ParseInstallAttempts("[1]"));
        Assert.Null(UpdateRules.ParseInstallAttempts("{\"Build\":\"" + Newer + "\",\"Count\":\"3\",\"Last\":\"2026-11-11T12:00:00+00:00\"}"));
        Assert.Null(UpdateRules.ParseInstallAttempts("{\"Build\":\"" + Newer + "\",\"Count\":3,\"Last\":\"ontem\"}"));
        Assert.Null(UpdateRules.ParseInstallAttempts("{\"Build\":\"" + Newer + "\",\"Count\":3}"));
        Assert.Null(UpdateRules.LoadInstallAttempts(Path.Combine(Path.GetTempPath(), "nao-existe-" + Guid.NewGuid().ToString("N"), "install-attempts.json")));
    }

    [Fact]
    public void UpToDateRemovesTheUsedInstallerAndTheAttemptsRecord()
    {
        string dir = TempDir();
        try
        {
            string setup = Path.Combine(dir, UpdateRules.SetupFileName), attempts = Path.Combine(dir, UpdateRules.InstallAttemptsFileName), other = Path.Combine(dir, "outro.txt");
            File.WriteAllText(setup, "instalador");
            UpdateRules.SaveInstallAttempts(attempts, new InstallAttempts(Newer, 1, T0));
            File.WriteAllText(other, "fica");
            UpdateRules.ClearFinishedUpdate(dir);
            Assert.False(File.Exists(setup));
            Assert.False(File.Exists(attempts));
            Assert.True(File.Exists(other));
            UpdateRules.ClearFinishedUpdate(dir); // nada para apagar
            UpdateRules.ClearFinishedUpdate(Path.Combine(dir, "nao-existe")); // pasta Updates ainda não criada
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void InstallerStillInUseIsLeftForTheNextCheck()
    {
        string dir = TempDir();
        try
        {
            string setup = Path.Combine(dir, UpdateRules.SetupFileName), attempts = Path.Combine(dir, UpdateRules.InstallAttemptsFileName);
            File.WriteAllText(setup, "instalador");
            UpdateRules.SaveInstallAttempts(attempts, new InstallAttempts(Newer, 1, T0));
            using (new FileStream(setup, FileMode.Open, FileAccess.Read, FileShare.Read))
                UpdateRules.ClearFinishedUpdate(dir); // o instalador ainda confere o WebView2 depois de abrir a barra
            Assert.True(File.Exists(setup));
            Assert.False(File.Exists(attempts));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void UpdaterGatesAndRecordsTheAttemptBeforeStartingTheInstaller()
    {
        string source = RepoSource.Read("src", "WinStatusBar", "Services", "AppUpdater.cs");
        int At(string text) => source.IndexOf(text, StringComparison.Ordinal);
        int clear = At("UpdateRules.ClearFinishedUpdate(UpdatesDir)"), installable = At("UpdateRules.IsInstallable(");
        int gate = At("!manual && !UpdateRules.MayInstall("), reuse = At("UpdateRules.CanReuse(");
        int defer = At("!manual && UpdateRules.ShouldDeferInstall("), save = At("UpdateRules.SaveInstallAttempts("), launch = At("Process.Start(start)");
        Assert.True(clear >= 0 && clear < installable, "em dia: apaga o instalador usado e o registro de tentativas");
        Assert.True(gate > installable && gate < reuse, "o limite de tentativas vale só para a consulta automática e antes de baixar");
        Assert.True(defer > reuse && defer < save, "a espera pela barra livre vale só para a consulta automática, com o instalador já conferido");
        Assert.True(save < launch, "a tentativa é gravada antes de o instalador abrir (ele fecha esta barra)");
        Assert.Equal(save, source.LastIndexOf("UpdateRules.SaveInstallAttempts(", StringComparison.Ordinal)); // a falha não conta de novo
        Assert.Contains("Task.Run(() => RunAsync(manual))", source);
    }

    // ---------------------------------------------------------------- barra ocupada (resposta de IA em andamento)
    [Fact]
    public void BusyHookIsOffByDefault() => Assert.Null(AppUpdater.IsBusy);

    [Fact]
    public void InstallWaitsWhileBusyForAtMostTwoHours()
    {
        Assert.False(UpdateRules.ShouldDeferInstall(busy: false, null, T0));
        Assert.False(UpdateRules.ShouldDeferInstall(busy: false, T0.AddMinutes(-5), T0));
        Assert.True(UpdateRules.ShouldDeferInstall(busy: true, null, T0));
        Assert.True(UpdateRules.ShouldDeferInstall(busy: true, T0.AddMinutes(-119), T0));
        Assert.False(UpdateRules.ShouldDeferInstall(busy: true, T0.AddHours(-2), T0));
        Assert.False(UpdateRules.ShouldDeferInstall(busy: true, T0.AddHours(-9), T0));
        Assert.False(UpdateRules.ShouldDeferInstall(busy: true, T0.AddHours(1), T0)); // relógio voltou: não espera sem fim
    }

    [Theory]
    [InlineData(true, 0, 1)]
    [InlineData(true, 5, 1)]
    [InlineData(false, 0, 15)]
    [InlineData(false, 3, 360)]
    public void DeferredInstallIsCheckedAgainInOneMinute(bool deferred, int failures, int minutes) =>
        Assert.Equal(TimeSpan.FromMinutes(minutes), UpdateRules.NextCheckDelay(deferred, failures));

    private static string TempDir()
    {
        string dir = Path.Combine(Path.GetTempPath(), "WinStatusBar.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }
}
