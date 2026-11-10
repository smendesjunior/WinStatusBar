using System.Text.Json;
using CodexIAs;
using CodexIAs.Providers;
using WinStatusBar.Core.Settings;
using Xunit;

namespace WinStatusBar.Tests;

/// <summary>Testes que mudam estáticos do componente (BarSettings, BridgeServer) rodam um por vez.</summary>
[CollectionDefinition("CodexIAsClaudeCommandsCollection", DisableParallelization = true)]
public class CodexIAsClaudeCommandsCollection;

[Collection("CodexIAsClaudeCommandsCollection")]
public class CodexIAsClaudeCommandsSettingsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "wsb-claude-cmd-" + Guid.NewGuid().ToString("N"));
    private readonly string _previous = BarSettings.FilePath;

    public CodexIAsClaudeCommandsSettingsTests()
    {
        Directory.CreateDirectory(_dir);
        BarSettings.FilePath = Path.Combine(_dir, "settings.json");
        CodexIAs.Log.Enabled = false;
    }

    public void Dispose()
    {
        BarSettings.FilePath = _previous;
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    private static string Json(bool on) => JsonSerializer.Serialize(new AppSettings { CodexClaudeCommands = on }, AppSettingsContext.Default.AppSettings);

    [Fact]
    public void Opcao_vem_desligada_e_e_gravada_pelo_nome()
    {
        Assert.False(new AppSettings().CodexClaudeCommands);
        Assert.Contains("\"CodexClaudeCommands\": true", Json(true));
    }

    [Fact]
    public void Le_a_opcao_do_settings_json_sem_reiniciar_o_componente()
    {
        File.WriteAllText(BarSettings.FilePath, Json(false));
        Assert.False(BarSettings.ClaudeCommands);
        File.WriteAllText(BarSettings.FilePath, Json(true));
        File.SetLastWriteTimeUtc(BarSettings.FilePath, DateTime.UtcNow.AddSeconds(5));
        Assert.True(BarSettings.ClaudeCommands);
    }

    [Fact]
    public void Arquivo_ausente_ou_ilegivel_vale_desligado()
    {
        Assert.False(BarSettings.ClaudeCommands);
        File.WriteAllText(BarSettings.FilePath, "{ quebrado");
        Assert.False(BarSettings.ClaudeCommands);
    }
}

public class CodexIAsClaudeCommandsToolStatusTests
{
    private static string D(string tool, string json) => ClaudeToolStatus.Describe(tool, JsonDocument.Parse(json).RootElement.Clone());

    [Fact]
    public void Cada_ferramenta_vira_uma_linha_de_andamento()
    {
        Assert.Equal("Executando: git status", D("Bash", """{"command":"git status"}"""));
        Assert.Equal("Executando: Get-ChildItem -Recurse | Select-Object -First 5", D("PowerShell", "{\"command\":\"Get-ChildItem -Recurse\\n  | Select-Object -First 5\"}"));
        Assert.Equal("Lendo README.md", D("Read", """{"file_path":"C:\\Projetos\\X\\README.md"}"""));
        Assert.Equal("Criando notas.txt", D("Write", """{"file_path":"C:\\Projetos\\X\\notas.txt","content":"oi"}"""));
        Assert.Equal("Editando App.cs", D("Edit", """{"file_path":"C:\\Projetos\\X\\App.cs"}"""));
        Assert.Equal("Procurando…", D("Grep", """{"pattern":"x"}"""));
        Assert.Equal("Procurando…", D("Glob", """{"pattern":"*.cs"}"""));
        Assert.Equal("Abrindo learn.microsoft.com", D("WebFetch", """{"url":"https://learn.microsoft.com/pt-br/dotnet/"}"""));
        Assert.Equal("Pesquisando: cota do Codex", D("WebSearch", """{"query":"cota do Codex"}"""));
        Assert.Equal("Usando TodoWrite", D("TodoWrite", """{"todos":[]}"""));
        Assert.Equal("Usando Bash", D("Bash", "{}"));
        Assert.Equal("Usando Bash", D("Bash", "[]"));
        Assert.Equal("Não liberado: WebFetch", ClaudeToolStatus.Denied("WebFetch"));
    }

    [Fact]
    public void Linha_longa_e_cortada_em_140_caracteres()
    {
        string line = D("Bash", "{\"command\":\"" + new string('a', 300) + "\"}");
        Assert.Equal(ClaudeToolStatus.MaxLength, line.Length);
        Assert.StartsWith("Executando: aaa", line);
        Assert.EndsWith("…", line);
    }
}
