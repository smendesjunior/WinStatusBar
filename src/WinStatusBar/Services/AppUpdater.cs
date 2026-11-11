using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Reflection;
using WinStatusBar.Core.Integration;

namespace WinStatusBar.Services;

internal enum UpdateStage { Checking, UpToDate, Downloading, Installing, Failed, Disabled }

internal sealed record UpdateProgress(UpdateStage Stage, string Text);

/// <summary>
/// Atualização silenciosa da instalação por usuário: consulta a Release mais recente do GitHub (ou a pasta local deste
/// PC, com <c>"Mode": "Local"</c>), baixa, confere tamanho e SHA-256 e executa o instalador com <c>/quiet /update</c>.
/// Uma consulta por vez; o estado vai para Configurações › Sobre por <see cref="StatusChanged"/>.
/// </summary>
internal static class AppUpdater
{
    private const string DisabledText = "Compilação local: atualização automática desligada.";
    private static readonly object Gate = new();
    private static Task<UpdateProgress>? _current;
    private static bool _sourceLogged;
    // Falhas seguidas da mesma versão (para espaçar as consultas automáticas; ver UpdateRules.RetryDelay).
    private static string? _failedBuild;
    private static int _failures;
    // Instalação automática adiada porque a barra está ocupada (IsBusy): de qual compilação, desde quando e se a
    // última consulta terminou adiada (a próxima vem em 1 minuto, não em 15).
    private static string? _deferredBuild;
    private static DateTimeOffset? _deferredSince;
    private static bool _installDeferred;
    // Compilação cujo bloqueio por tentativas já foi registrado no log (uma linha por compilação, não a cada consulta).
    private static string? _blockedLogged;

    private static string LocalAppData => Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
    private static string UpdatesDir => Path.Combine(LocalAppData, "WinStatusBar", "Updates");
    private static readonly string? CurrentBuild = typeof(AppUpdater).Assembly
        .GetCustomAttributes<AssemblyMetadataAttribute>().FirstOrDefault(a => a.Key == "NetworkUpdateBuild")?.Value;
    private static readonly string CurrentVersion = typeof(AppUpdater).Assembly.GetName().Version is { } v ? $"{v.Major}.{v.Minor}.{v.Build}" : "1.0.0";
    private static readonly HttpClient Http = CreateClient();

    /// <summary>Instalação pelo EXE, fora do pacote MSIX, com compilação carimbada. Builds de desenvolvimento não se atualizam.</summary>
    public static bool IsEnabled { get; } = !PackageContext.IsPackaged && !string.IsNullOrEmpty(CurrentBuild)
        && AppContext.BaseDirectory.TrimEnd('\\').Equals(Path.Combine(LocalAppData, "Programs", "WinStatusBar"), StringComparison.OrdinalIgnoreCase);

    private static UpdateSourceKind? _sourceKind;

    /// <summary>Origem desta instalação; antes da primeira consulta, lida do update-source.json (para o texto de Sobre).</summary>
    public static UpdateSourceKind? SourceKind
    {
        get
        {
            if (_sourceKind is null && IsEnabled)
            {
                try
                {
                    string file = Path.Combine(LocalAppData, "WinStatusBar", "update-source.json");
                    _sourceKind = UpdateSource.Resolve(File.Exists(file) ? File.ReadAllText(file) : null).Kind;
                }
                catch { }
            }
            return _sourceKind;
        }
        private set => _sourceKind = value;
    }
    public static UpdateProgress? LastStatus { get; private set; }
    public static event Action<UpdateProgress>? StatusChanged;

    /// <summary>Só para testes: substitui a consulta real.</summary>
    internal static Func<Task<UpdateProgress>>? CheckOverrideForTests { get; set; }

    /// <summary>Barra ocupada (por exemplo, resposta de IA em andamento): a instalação automática espera, por no máximo
    /// 2 h (<see cref="UpdateRules.ShouldDeferInstall"/>). null = nunca ocupada. É chamado fora da thread da interface.</summary>
    internal static Func<bool>? IsBusy { get; set; }

    public static void Start()
    {
        if (!IsEnabled) return;
        _ = Task.Run(async () =>
        {
            await Task.Delay(UpdateRules.FirstCheckDelay);
            while (true)
            {
                // Installing só volta com o instalador concluído; normalmente ele já fechou esta barra antes.
                if ((await CheckNowAsync()).Stage == UpdateStage.Installing) return;
                await Task.Delay(UpdateRules.NextCheckDelay(_installDeferred, _failures));
            }
        });
    }

    /// <summary>Consulta agora. Se já houver uma consulta em andamento (automática ou do botão), devolve a mesma.</summary>
    /// <param name="manual">Pedido da pessoa (botão "Verificar atualizações"): instala na hora, sem o limite de
    /// tentativas da mesma compilação e sem esperar a barra ficar livre.</param>
    public static Task<UpdateProgress> CheckNowAsync(bool manual = false)
    {
        lock (Gate)
        {
            if (_current is { IsCompleted: false }) return _current;
            _current = CheckOverrideForTests?.Invoke() ?? Task.Run(() => RunAsync(manual));
            return _current;
        }
    }

    private static async Task<UpdateProgress> RunAsync(bool manual)
    {
        if (!IsEnabled) return Report(new(UpdateStage.Disabled, DisabledText));
        var kind = UpdateSourceKind.GitHub;
        string part = Path.Combine(UpdatesDir, UpdateRules.SetupFileName + ".part");
        UpdateManifest? manifest = null;
        bool deferred = false;
        try
        {
            Report(new(UpdateStage.Checking, "Procurando…"));
            string sourceFile = Path.Combine(LocalAppData, "WinStatusBar", "update-source.json");
            var source = UpdateSource.Resolve(File.Exists(sourceFile) ? await File.ReadAllTextAsync(sourceFile) : null);
            kind = source.Kind;
            SourceKind = kind;
            if (!_sourceLogged)
            {
                _sourceLogged = true;
                App.Log(kind == UpdateSourceKind.Local ? "Atualização: pela pasta local " + source.LocalDirectory : "Atualização: pelo GitHub (" + UpdateRules.GitHubManifestUrl + ")");
            }

            string json = kind == UpdateSourceKind.Local
                ? await File.ReadAllTextAsync(Path.Combine(source.LocalDirectory!, UpdateRules.ManifestFileName))
                : await GetManifestAsync();
            manifest = UpdateRules.Parse(json)
                ?? throw new InvalidDataException("O arquivo de controle da atualização está inválido. A barra tenta de novo sozinha em 15 minutos.");
            bool newer = manifest.Build is { Length: 14 } && string.CompareOrdinal(manifest.Build, CurrentBuild) > 0;
            if (!newer)
            {
                _failures = 0; _failedBuild = null; _deferredBuild = null; _deferredSince = null;
                // Nada pendente: o instalador já usado (~270 MB) e o registro de tentativas não servem mais.
                UpdateRules.ClearFinishedUpdate(UpdatesDir);
                return Report(new(UpdateStage.UpToDate, $"Você já tem a versão mais recente ({CurrentVersion})"));
            }
            if (!UpdateRules.IsInstallable(manifest, CurrentBuild!, kind))
                throw new InvalidDataException("A versão encontrada não passou na conferência de segurança e foi ignorada.");

            string label = manifest.Version ?? manifest.Build;
            // Tentativas desta compilação, gravadas em disco: o instalador fecha a barra antes de trocar os arquivos e,
            // se a troca falha e é desfeita, a barra reaberta não lembraria de nada e repetiria a instalação a cada minuto.
            string attemptsFile = Path.Combine(UpdatesDir, UpdateRules.InstallAttemptsFileName);
            var attempts = UpdateRules.LoadInstallAttempts(attemptsFile);
            if (!manual && !UpdateRules.MayInstall(attempts, manifest.Build, DateTimeOffset.UtcNow))
            {
                var blocked = Report(new(UpdateStage.Failed, $"A instalação da versão {label} não foi concluída em {attempts!.Count} tentativas. A barra tenta de novo sozinha em até 6 horas; o botão \"Verificar atualizações\" tenta agora."));
                if (_blockedLogged != manifest.Build)
                {
                    _blockedLogged = manifest.Build;
                    App.Log($"Atualização: {blocked.Text} Detalhes em {Path.Combine(LocalAppData, "WinStatusBar", "setup.log")}.");
                }
                return blocked;
            }
            Directory.CreateDirectory(UpdatesDir);
            string exe = Path.Combine(UpdatesDir, UpdateRules.SetupFileName);
            // Instalador de uma tentativa anterior que ainda confere: não baixa ~270 MB de novo.
            if (UpdateRules.CanReuse(exe, manifest)) App.Log($"Atualização: reaproveitando o instalador já baixado da versão {label}.");
            else
            {
                Report(new(UpdateStage.Downloading, $"Baixando a versão {label}… 0%"));
                if (kind == UpdateSourceKind.Local)
                {
                    // Leitura compartilhada sem escrita: não aceita o instalador enquanto ele ainda está sendo gravado.
                    await using var file = new FileStream(Path.Combine(source.LocalDirectory!, UpdateRules.SetupFileName), FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, useAsync: true);
                    await CopyWithProgressAsync(file, part, manifest.Size, label, stall: null);
                }
                else await DownloadAsync(manifest.Url!, part, manifest.Size, label);
                UpdateRules.VerifyDownload(part, manifest);
                File.Move(part, exe, overwrite: true);
            }

            // Barra ocupada (resposta de IA em andamento): a instalação automática espera, com o instalador já conferido
            // guardado, e a próxima consulta vem em 1 minuto. O botão instala na hora.
            var now = DateTimeOffset.UtcNow;
            if (_deferredBuild != manifest.Build) { _deferredBuild = manifest.Build; _deferredSince = null; }
            if (!manual && UpdateRules.ShouldDeferInstall(Busy(), _deferredSince, now))
            {
                if (_deferredSince is null)
                {
                    _deferredSince = now;
                    App.Log($"Atualização: versão {label} pronta; a instalação espera a barra ficar livre (no máximo 2 horas).");
                }
                deferred = true;
                // Sem etapa nova: Failed mantém o botão "Verificar atualizações" ligado, e ele instala na hora.
                return Report(new(UpdateStage.Failed, $"A versão {label} já foi baixada e será instalada quando a resposta em andamento terminar. O botão \"Verificar atualizações\" instala agora."));
            }
            _deferredSince = null;

            var installing = Report(new(UpdateStage.Installing, $"Instalando a versão {label}. A barra vai fechar e abrir de novo sozinha."));
            App.Log($"Atualização: instalando {label} (compilação {manifest.Build}).");
            // A tentativa é gravada ANTES de abrir o instalador: esta barra não sobrevive para registrar o resultado.
            try { UpdateRules.SaveInstallAttempts(attemptsFile, UpdateRules.NextInstallAttempt(attempts, manifest.Build, now)); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { App.Log("Atualização: tentativa não registrada (" + ex.Message + ")"); }
            var start = new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true };
            start.ArgumentList.Add("/quiet");
            start.ArgumentList.Add("/update");
            using var process = Process.Start(start) ?? throw new InvalidOperationException("o instalador não iniciou.");
            await process.WaitForExitAsync();
            if (process.ExitCode != 0)
                throw new InvalidOperationException($"o instalador terminou com o código {process.ExitCode}. Detalhes em {Path.Combine(LocalAppData, "WinStatusBar", "setup.log")}.");
            return installing;
        }
        catch (Exception ex)
        {
            try { if (File.Exists(part)) File.Delete(part); } catch { }
            // Só a contagem em memória (intervalo das consultas): a tentativa de instalação já foi gravada em disco
            // antes de o instalador abrir e não é contada de novo aqui.
            if (manifest is not null)
            {
                if (_failedBuild == manifest.Build) _failures++;
                else { _failedBuild = manifest.Build; _failures = 1; }
            }
            var failed = Report(new(UpdateStage.Failed, UpdateErrorText.Explain(ex, kind)));
            App.Log($"Atualização: {failed.Text} ({ex.GetType().Name}: {ex.Message})");
            return failed;
        }
        finally { _installDeferred = deferred; }
    }

    private static bool Busy()
    {
        try { return IsBusy?.Invoke() == true; }
        catch (Exception ex) { App.Log("Atualização: não foi possível saber se a barra está ocupada (" + ex.Message + ")"); return false; }
    }

    private static async Task<string> GetManifestAsync()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, UpdateRules.GitHubManifestUrl);
        request.Headers.CacheControl = new CacheControlHeaderValue { NoCache = true };
        using var response = await Http.SendAsync(request);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync();
    }

    private static async Task DownloadAsync(string url, string target, long size, string label)
    {
        // Sem limite de tempo total (são ~261 MB); só desiste se ficar 2 minutos sem receber nada.
        using var stall = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        using var response = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, stall.Token);
        response.EnsureSuccessStatusCode();
        await using var source = await response.Content.ReadAsStreamAsync(stall.Token);
        await CopyWithProgressAsync(source, target, size, label, stall);
    }

    private static async Task CopyWithProgressAsync(Stream source, string target, long total, string label, CancellationTokenSource? stall)
    {
        await using var destination = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true);
        var buffer = new byte[1 << 20];
        long done = 0;
        int lastPercent = 0;
        while (true)
        {
            stall?.CancelAfter(TimeSpan.FromMinutes(2));
            int read = await source.ReadAsync(buffer, stall?.Token ?? CancellationToken.None);
            if (read == 0) break;
            await destination.WriteAsync(buffer.AsMemory(0, read));
            done += read;
            if (done > total) throw new InvalidDataException("O download veio maior que a publicação; a atualização fica para a próxima consulta.");
            int percent = (int)(done * 100 / total);
            if (percent != lastPercent) { lastPercent = percent; Report(new(UpdateStage.Downloading, $"Baixando a versão {label}… {percent}%")); }
        }
    }

    private static UpdateProgress Report(UpdateProgress progress)
    {
        LastStatus = progress;
        try { StatusChanged?.Invoke(progress); }
        catch (Exception ex) { App.Log("Atualização: aviso de estado falhou: " + ex.Message); }
        return progress;
    }

    private static HttpClient CreateClient()
    {
        // Proxy do sistema e redirecionamentos (github.com → servidor de arquivos do GitHub) ficam no padrão.
        var http = new HttpClient(new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All, PooledConnectionLifetime = TimeSpan.FromMinutes(10),
            // Proxy corporativo com login do Windows (NTLM/Kerberos): sem isto, toda consulta falharia com 407.
            DefaultProxyCredentials = CredentialCache.DefaultCredentials,
        })
            { Timeout = TimeSpan.FromSeconds(30) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("WinStatusBar-Updater/" + CurrentVersion);
        return http;
    }
}
