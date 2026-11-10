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

public class CodexIAsClaudeCommandsArgumentsTests
{
    internal static readonly AiModel Opus = new("codexias-claude-opus-5-5", "claude", "claude-opus-5-5", "Claude Opus 5.5", "", ["low", "medium", "high"], "medium", -6);
    private static readonly string[] Today = ["-p", "--output-format", "stream-json", "--verbose", "--include-partial-messages", "--model", "claude-opus-5-5",
        "--effort", "high", "--no-session-persistence", "--strict-mcp-config", "--settings", "{\"disableAllHooks\":true}"];
    private static readonly string[] Tools = ["Bash", "PowerShell", "Read", "Write", "Edit", "Glob", "Grep", "WebFetch", "WebSearch"];

    [Fact]
    public void Desligada_os_argumentos_nao_mudam() => Assert.Equal(Today, ClaudeProvider.Arguments(Opus, "high", false));

    [Fact]
    public void Ligada_entram_acceptEdits_e_as_ferramentas_uma_por_argumento()
    {
        Assert.Equal([.. Today, "--permission-mode", "acceptEdits", "--allowedTools", .. Tools], ClaudeProvider.Arguments(Opus, "high", true));
        var plain = ClaudeProvider.Arguments(Opus with { Id = "default" }, null, true);
        Assert.DoesNotContain("--model", plain);
        Assert.DoesNotContain("--effort", plain);
        Assert.Contains("acceptEdits", plain);
    }

    [Fact]
    public void Ligada_o_tempo_do_Bash_e_ampliado()
    {
        var on = ClaudeProvider.StartInfo(@"C:\x\claude.exe", [], Path.GetTempPath(), true);
        Assert.Equal("2400000", on.Environment["BASH_DEFAULT_TIMEOUT_MS"]);
        Assert.Equal("3600000", on.Environment["BASH_MAX_TIMEOUT_MS"]);
        var off = ClaudeProvider.StartInfo(@"C:\x\claude.exe", [], Path.GetTempPath(), false);
        Assert.Equal(Environment.GetEnvironmentVariable("BASH_DEFAULT_TIMEOUT_MS"), off.Environment.TryGetValue("BASH_DEFAULT_TIMEOUT_MS", out var v) ? v : null);
    }

    [Fact]
    public void Descricao_no_seletor_acompanha_a_opcao()
    {
        const string models = """[{"value":"opus","resolvedModel":"claude-opus-5-5","displayName":"Opus 5.5","description":"x","supportedEffortLevels":["low"]}]""";
        using var doc = JsonDocument.Parse(models);
        var options = WinStatusBar.Core.Ai.Claude.ClaudeModelList.Parse(doc.RootElement);
        Assert.Equal("Claude Code pela conta, pela WinStatusBar. Responde em texto e pode ler a pasta da conversa.", ClaudeProvider.ToModels(options)[0].Description);
        Assert.Equal("Claude Code pela conta, pela WinStatusBar. Pode executar comandos e editar arquivos na pasta da conversa.",
            ClaudeProvider.ToModels(options, commands: true)[0].Description);
    }
}

public class CodexIAsClaudeCommandsJobTests
{
    private static bool Alive(int pid)
    {
        try { using var p = System.Diagnostics.Process.GetProcessById(pid); return !p.HasExited; }
        catch (ArgumentException) { return false; }
    }

    [Fact]
    public async Task Fechar_o_grupo_encerra_o_processo_e_os_comandos_dele()
    {
        string pidFile = Path.Combine(Path.GetTempPath(), "wsb-job-" + Guid.NewGuid().ToString("N") + ".txt");
        var job = KillOnCloseJob.Create();
        Assert.NotNull(job);
        var psi = new System.Diagnostics.ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe"))
        { UseShellExecute = false, CreateNoWindow = true };
        foreach (var a in new[] { "-NoProfile", "-Command",
            $"$p = Start-Process -FilePath ping.exe -ArgumentList '-n','120','127.0.0.1' -NoNewWindow -PassThru; Set-Content -Path '{pidFile}' -Value $p.Id; Start-Sleep -Seconds 120" })
            psi.ArgumentList.Add(a);
        using var shell = System.Diagnostics.Process.Start(psi)!;
        int ping = 0;
        try
        {
            Assert.True(job!.Add(shell));
            for (int i = 0; i < 100 && ping == 0; i++)
            {
                await Task.Delay(100);
                try { if (File.Exists(pidFile) && int.TryParse(File.ReadAllText(pidFile).Trim(), out int p)) ping = p; } catch (IOException) { }
            }
            Assert.NotEqual(0, ping);
            job.Dispose();
            for (int i = 0; i < 50 && (Alive(shell.Id) || Alive(ping)); i++) await Task.Delay(100);
            Assert.False(Alive(shell.Id));
            Assert.False(Alive(ping));
        }
        finally
        {
            try { if (!shell.HasExited) shell.Kill(true); } catch { }
            if (ping != 0) try { using var p = System.Diagnostics.Process.GetProcessById(ping); p.Kill(); } catch { }
            try { File.Delete(pidFile); } catch { }
        }
    }

    [Fact]
    public void Depois_de_fechado_nao_aceita_processo_e_nao_lanca()
    {
        var job = KillOnCloseJob.Create()!;
        job.Dispose();
        job.Dispose();
        var psi = new System.Diagnostics.ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "PING.EXE"), "-n 3 127.0.0.1")
        { UseShellExecute = false, CreateNoWindow = true };
        using var ping = System.Diagnostics.Process.Start(psi)!;
        try { Assert.False(job.Add(ping)); }
        finally { try { ping.Kill(); } catch { } }
    }

    [Fact]
    public void Claude_entra_no_grupo_antes_de_receber_o_pedido()
    {
        string cp = RepoSource.Read("src", "WinStatusBar.CodexIAs", "Providers", "ClaudeProvider.cs");
        int add = cp.IndexOf("Job.Value?.Add(proc)", StringComparison.Ordinal);
        int write = cp.IndexOf("proc.StandardInput.WriteAsync", StringComparison.Ordinal);
        Assert.True(add >= 0 && add < write, $"add={add} write={write}");
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
