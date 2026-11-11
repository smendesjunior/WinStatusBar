using System.Text.Json;
using WinStatusBar.Core.Metrics;
using WinStatusBar.Core.Settings;
using Xunit;

namespace WinStatusBar.Tests;

public class AuditRegressionTests
{
    [Fact]
    public void NullCollectionsFromSettingsAreRepairedWithoutLosingCustomOrder()
    {
        var settings = JsonSerializer.Deserialize<AppSettings>("""
            {"SchemaVersion":3,"ModuleOrder":null,"HiddenModules":null,
             "LocalServerProcesses":null,"DisabledApiActivityProviders":null}
            """);
        settings!.Normalize();
        Assert.Equal(AppSettings.DefaultOrder, settings.ModuleOrder);
        Assert.NotNull(settings.HiddenModules);
        Assert.NotNull(settings.LocalServerProcesses);
        Assert.NotNull(settings.DisabledApiActivityProviders);
    }

    [Fact]
    public void NullStringsAndConnectorListFromSettingsAreRepaired()
    {
        var settings = JsonSerializer.Deserialize<AppSettings>("""
            {"SchemaVersion":4,"AllowedConnectors":null,"CommandChatGptVoiceHotkey":null,"CommandClaudeVoiceHotkey":null,
             "ApiCredentialResource":null,"ApiCredentialEndpoint":null}
            """);
        settings!.Normalize();
        Assert.NotNull(settings.AllowedConnectors);
        Assert.Equal(string.Empty, settings.CommandChatGptVoiceHotkey);
        Assert.Equal(string.Empty, settings.CommandClaudeVoiceHotkey);
        Assert.Equal(new AppSettings().ApiCredentialResource, settings.ApiCredentialResource);
        Assert.Equal(string.Empty, settings.ApiCredentialEndpoint);
    }
}

/// <summary>Testes que trocam a pasta de dados do SettingsService (estático) rodam um por vez e nunca no perfil real.</summary>
[CollectionDefinition("SettingsServiceCollection", DisableParallelization = true)]
public class SettingsServiceCollection;

/// <summary>
/// Um settings.json ilegível não pode mais zerar as preferências em silêncio nem levar os padrões para o OneDrive.
/// </summary>
[Collection("SettingsServiceCollection")]
public class SettingsRecoveryTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "wsb-settings-" + Guid.NewGuid().ToString("N"));
    private readonly string _data;
    private readonly string _cloud;
    private string Local => Path.Combine(_data, "settings.json");
    private string Bak => Local + ".bak";

    public SettingsRecoveryTests()
    {
        _data = Path.Combine(_root, "dados");
        _cloud = Path.Combine(_root, "OneDrive", "WinStatusBar", "configuracoes.json");
        Directory.CreateDirectory(_data);
        Directory.CreateDirectory(Path.GetDirectoryName(_cloud)!);
        SettingsService.DataDirOverride = _data;
        SettingsService.CloudSettingsPathOverride = () => _cloud;
    }

    public void Dispose()
    {
        SettingsService.DataDirOverride = null;
        SettingsService.CloudSettingsPathOverride = null;
        try { Directory.Delete(_root, true); } catch (IOException) { }
    }

    private static string Json(AppSettings s) => JsonSerializer.Serialize(s, AppSettingsContext.Default.AppSettings);
    private static AppSettings Parse(string file) => JsonSerializer.Deserialize(File.ReadAllText(file), AppSettingsContext.Default.AppSettings)!;
    private string[] CorruptCopies() => Directory.GetFiles(_data, "settings.corrupt-*.json");
    private string[] CloudCopies() => Directory.GetFiles(Path.GetDirectoryName(_cloud)!, "configuracoes.copia-*.json");

    /// <summary>Um settings.json bom com escala 1,25 e o .bak (versão anterior, 1,1) deixados por duas gravações normais.</summary>
    private void TwoNormalSaves()
    {
        var service = new SettingsService();
        service.Load();
        service.Current.SyncSettings = false; // estes cenários não usam o OneDrive
        service.Current.Scale = 1.1;
        service.Save();
        service.Current.Scale = 1.25;
        service.Save();
    }

    [Fact]
    public void TheTestsNeverPointAtTheRealProfile()
    {
        Assert.StartsWith(_root, SettingsService.SettingsPath);
        Assert.StartsWith(_root, SettingsService.CloudSettingsPath!);
        Assert.StartsWith(_root, SettingsService.CacheDir);
    }

    [Fact]
    public void Save_KeepsTheFileFormatAndLeavesThePreviousVersionInBak()
    {
        var service = new SettingsService();
        service.Load();
        service.ExpireRestoreWait();
        service.Current.Scale = 1.1;
        service.Save();
        string first = File.ReadAllText(Local);
        Assert.Equal(Json(service.Current), first);       // mesmo texto de sempre
        Assert.Equal((byte)'{', File.ReadAllBytes(Local)[0]); // UTF-8 sem BOM
        Assert.False(File.Exists(Bak));                   // ainda não havia versão anterior

        service.Current.Scale = 1.25;
        service.Save();
        Assert.Equal(first, File.ReadAllText(Bak));
        Assert.Equal(File.ReadAllText(Local), File.ReadAllText(_cloud));
        Assert.False(File.Exists(Local + ".tmp"));

        var again = new SettingsService();
        Assert.Equal(1.25, again.Load().Scale);
        Assert.Null(SettingsService.LoadNote);
        Assert.False(again.PendingRestore);
        Assert.Empty(CorruptCopies());
    }

    [Fact]
    public void ConcurrentSaves_DoNotCollideOnTheTemporaryFile()
    {
        var service = new SettingsService();
        service.Load();
        service.Current.SyncSettings = false;
        var errors = new System.Collections.Concurrent.ConcurrentQueue<Exception>();
        Parallel.For(0, 40, _ => { try { service.Save(); } catch (Exception ex) { errors.Enqueue(ex); } });
        Assert.Empty(errors);
        Assert.Equal(1.0, Parse(Local).Scale);
    }

    [Fact]
    public void CorruptFile_IsRestoredFromBakAndTheCorruptCopyIsKept()
    {
        TwoNormalSaves();
        string good = File.ReadAllText(Local);
        string truncated = good[..(good.Length / 2)]; // gravação interrompida
        File.WriteAllText(Local, truncated);

        var service = new SettingsService();
        var loaded = service.Load();

        Assert.Equal(1.1, loaded.Scale);                   // a versão anterior, não os padrões
        Assert.Contains("settings.json.bak", SettingsService.LoadNote);
        Assert.Equal(truncated, File.ReadAllText(Assert.Single(CorruptCopies())));
        Assert.Equal(1.1, Parse(Bak).Scale);               // o arquivo ruim não virou o .bak
        Assert.Equal(1.1, Parse(Local).Scale);             // e o settings.json voltou a ser legível
        Assert.False(service.PendingRestore);
    }

    [Fact]
    public void CorruptFile_WithoutBak_IsRestoredFromTheCloudCopy()
    {
        File.WriteAllText(_cloud, Json(new AppSettings { Scale = 1.4, NetworkInterface = "Ethernet 9" }));
        File.WriteAllText(Local, "{ \"Scale\": 1.3, \"Them");

        var service = new SettingsService();
        var loaded = service.Load();

        Assert.Equal(1.4, loaded.Scale);
        Assert.Null(loaded.NetworkInterface);              // como em toda restauração do OneDrive
        Assert.True(service.RestoredFromCloud);
        Assert.Contains("OneDrive", SettingsService.LoadNote);
        Assert.Single(CorruptCopies());
        Assert.False(File.Exists(Bak));                    // o arquivo ruim não virou o .bak
        Assert.Equal(1.4, Parse(Local).Scale);
        Assert.Empty(CloudCopies());                       // a cópia boa do OneDrive não precisou ser duplicada
    }

    [Theory]
    [InlineData(false)] // cópia do OneDrive ainda baixando / danificada
    [InlineData(true)]  // cópia do OneDrive legível, mas de quem desligou a sincronização
    public void CorruptFile_WithNothingUsable_FallsBackToDefaultsWithoutOverwritingTheCloudCopy(bool cloudReadable)
    {
        string cloudBefore = cloudReadable ? Json(new AppSettings { Scale = 1.4, SyncSettings = false }) : "{ \"Scale\": 1.4, \"Them";
        File.WriteAllText(_cloud, cloudBefore);
        File.WriteAllText(Local, "isto não é JSON");

        var service = new SettingsService();
        var loaded = service.Load();

        Assert.Equal(1.0, loaded.Scale);
        Assert.Contains("padrões", SettingsService.LoadNote);
        Assert.Single(CorruptCopies());
        Assert.True(service.PendingRestore);               // a espera de restauração bloqueia a gravação no OneDrive

        service.Current.Scale = 1.2;
        service.Save();                                    // a pessoa mexe numa opção durante a espera
        Assert.Equal(cloudBefore, File.ReadAllText(_cloud));
        Assert.Empty(CloudCopies());
        Assert.False(File.Exists(Bak));

        service.ExpireRestoreWait();                       // fim da espera sem cópia utilizável
        Assert.False(service.TryLateRestore());
        Assert.Equal(1.2, Parse(_cloud).Scale);            // agora sim a cópia nova vai para o OneDrive…
        Assert.Equal(cloudBefore, File.ReadAllText(Assert.Single(CloudCopies()))); // …com a anterior guardada ao lado

        service.Save();
        Assert.Single(CloudCopies());                      // só uma vez
    }

    [Fact]
    public void CleanInstall_UnreadableCloudFile_IsKeptBeforeTheFirstCloudCopy()
    {
        const string partial = "{ \"Scale\": 1.4, \"Them";
        File.WriteAllText(_cloud, partial);

        var service = new SettingsService();
        service.Load();
        Assert.Null(SettingsService.LoadNote);             // instalação limpa: nada a relatar
        Assert.True(service.PendingRestore);
        service.Save();
        Assert.Equal(partial, File.ReadAllText(_cloud));

        service.ExpireRestoreWait();
        Assert.False(service.TryLateRestore());
        Assert.Equal(1.0, Parse(_cloud).Scale);
        Assert.Equal(partial, File.ReadAllText(Assert.Single(CloudCopies())));
    }

    [Fact]
    public void CleanInstall_WithoutCloudFile_StillWritesTheFirstCopyAfterTheWait()
    {
        var service = new SettingsService();
        service.Load();
        Assert.True(service.PendingRestore);
        service.Save();
        Assert.False(File.Exists(_cloud));
        service.ExpireRestoreWait();
        Assert.False(service.TryLateRestore());
        Assert.True(File.Exists(_cloud));
        Assert.Empty(CloudCopies());
    }

    [Fact]
    public void UnknownEnumValue_OnlyThatOptionGoesBackToDefault()
    {
        // Ex.: arquivo gravado por uma versão mais nova e lido por uma anterior. Antes: tudo voltava ao padrão.
        string json = Json(new AppSettings { Scale = 1.25, CommandHotkey = "Ctrl+Space", Backdrop = BackdropKind.Acrylic, SyncSettings = false });
        Assert.Contains("\"Theme\": \"System\"", json);
        File.WriteAllText(Local, json.Replace("\"Theme\": \"System\"", "\"Theme\": \"Neon\""));

        var service = new SettingsService();
        var loaded = service.Load();

        Assert.Equal(1.25, loaded.Scale);
        Assert.Equal("Ctrl+Space", loaded.CommandHotkey);
        Assert.Equal(BackdropKind.Acrylic, loaded.Backdrop);
        Assert.Equal(ThemeMode.System, loaded.Theme);
        Assert.Contains("Theme", SettingsService.LoadNote);
        Assert.Contains("\"Neon\"", File.ReadAllText(Assert.Single(CorruptCopies())));
        Assert.Equal(1.25, Parse(Local).Scale);            // regravado legível (os outros processos também leem)
        Assert.False(service.PendingRestore);
    }
}
