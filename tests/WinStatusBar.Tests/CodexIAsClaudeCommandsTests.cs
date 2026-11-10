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

public class CodexIAsClaudeCommandsRegistryTests
{
    private static readonly Conversation Conv = new("Liste os arquivos", [("user", "Oi"), ("assistant", "Olá")], @"C:\Projetos\X", "conv-registro");
    private const string Slug = "codexias-claude-opus-5-5";

    [Fact]
    public async Task Repeticao_durante_a_execucao_se_junta_e_nao_roda_de_novo()
    {
        var registry = new ClaudeRunRegistry();
        int starts = 0;
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sentFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Func<Action<string>, Action<string>, CancellationToken, Task<Answer>> start = async (text, status, ct) =>
        {
            Interlocked.Increment(ref starts);
            status("Executando: a"); text("Parcial");
            sentFirst.TrySetResult();
            await gate.Task;
            status("Lendo b");
            return new Answer("Parcial e fim", null);
        };
        string key = ClaudeRunRegistry.KeyOf(Slug, null, Conv)!;
        var first = registry.RunAsync(key, start, _ => { }, _ => { }, CancellationToken.None);
        await sentFirst.Task;
        var texts2 = new List<string>();
        var statuses2 = new List<string>();
        var second = registry.RunAsync(key, start, texts2.Add, statuses2.Add, CancellationToken.None);
        gate.SetResult();
        Assert.Equal("Parcial e fim", (await first).Text);
        Assert.Equal("Parcial e fim", (await second).Text);
        Assert.Equal(1, starts);
        Assert.Equal(["Executando: a", "Lendo b"], statuses2);
        Assert.Equal("Parcial", texts2[0]);
    }

    [Fact]
    public async Task Repeticao_depois_do_fim_recebe_a_resposta_pronta_por_10_minutos()
    {
        var now = new DateTime(2026, 11, 10, 12, 0, 0, DateTimeKind.Utc);
        var registry = new ClaudeRunRegistry { Clock = () => now };
        int starts = 0;
        Func<Action<string>, Action<string>, CancellationToken, Task<Answer>> start = (text, status, ct) =>
        {
            starts++;
            status("Executando: a"); text("Pronto");
            return Task.FromResult(new Answer("Pronto", null));
        };
        string key = ClaudeRunRegistry.KeyOf(Slug, null, Conv)!;
        await registry.RunAsync(key, start, _ => { }, _ => { }, CancellationToken.None);
        now = now.AddMinutes(9);
        var statuses = new List<string>();
        Assert.Equal("Pronto", (await registry.RunAsync(key, start, _ => { }, statuses.Add, CancellationToken.None)).Text);
        Assert.Equal(["Executando: a"], statuses);
        Assert.Equal(1, starts);
        now = now.AddMinutes(1).AddSeconds(1);
        await registry.RunAsync(key, start, _ => { }, _ => { }, CancellationToken.None);
        Assert.Equal(2, starts);
    }

    [Fact]
    public async Task Quando_todos_desistem_a_execucao_para_e_a_proxima_roda_de_novo()
    {
        var registry = new ClaudeRunRegistry();
        int starts = 0;
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Func<Action<string>, Action<string>, CancellationToken, Task<Answer>> start = async (text, status, ct) =>
        {
            Interlocked.Increment(ref starts);
            try { await Task.Delay(Timeout.Infinite, ct); }
            catch (OperationCanceledException) { cancelled.TrySetResult(); throw; }
            return new Answer("nunca", null);
        };
        string key = ClaudeRunRegistry.KeyOf(Slug, null, Conv)!;
        using var cts = new CancellationTokenSource();
        var run = registry.RunAsync(key, start, _ => { }, _ => { }, cts.Token);
        await Task.Delay(50);
        cts.Cancel();
        Assert.Equal("Interrompido", (await run).Error);
        Assert.True(await Task.WhenAny(cancelled.Task, Task.Delay(5000)) == cancelled.Task);
        using var cts2 = new CancellationTokenSource(200);
        await registry.RunAsync(key, start, _ => { }, _ => { }, cts2.Token);
        Assert.Equal(2, starts);
    }

    [Fact]
    public async Task Um_dos_dois_desistindo_nao_para_a_execucao()
    {
        var registry = new ClaudeRunRegistry();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        bool cancelledRun = false;
        Func<Action<string>, Action<string>, CancellationToken, Task<Answer>> start = async (text, status, ct) =>
        {
            ct.Register(() => cancelledRun = true);
            await gate.Task;
            return new Answer("ok", null);
        };
        string key = ClaudeRunRegistry.KeyOf(Slug, null, Conv)!;
        using var cts = new CancellationTokenSource();
        var first = registry.RunAsync(key, start, _ => { }, _ => { }, cts.Token);
        await Task.Delay(50);
        var second = registry.RunAsync(key, start, _ => { }, _ => { }, CancellationToken.None);
        cts.Cancel();
        Assert.Equal("Interrompido", (await first).Error);
        gate.SetResult();
        Assert.Equal("ok", (await second).Text);
        Assert.False(cancelledRun);
    }

    [Fact]
    public void Chave_muda_com_conteudo_modelo_nivel_e_conversa()
    {
        string key = ClaudeRunRegistry.KeyOf(Slug, null, Conv)!;
        Assert.Equal(key, ClaudeRunRegistry.KeyOf(Slug, null, Conv with { History = [("user", "Oi"), ("assistant", "Olá")] }));
        Assert.NotEqual(key, ClaudeRunRegistry.KeyOf(Slug, null, Conv with { Prompt = "Outra coisa" }));
        Assert.NotEqual(key, ClaudeRunRegistry.KeyOf("codexias-claude-sonnet-5-5", null, Conv));
        Assert.NotEqual(key, ClaudeRunRegistry.KeyOf(Slug, "low", Conv));
        Assert.NotEqual(key, ClaudeRunRegistry.KeyOf(Slug, null, Conv with { Key = "outra-conversa" }));
        Assert.Null(ClaudeRunRegistry.KeyOf(Slug, null, Conv with { Key = null }));
    }
}

public class CodexIAsClaudeCommandsOutputTests
{
    private static readonly string[] Fixture =
    [
        """{"type":"system","subtype":"init","tools":["Bash","PowerShell"]}""",
        """{"type":"stream_event","event":{"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"Vou listar."}}}""",
        """{"type":"assistant","message":{"content":[{"type":"text","text":"Vou listar."}]}}""",
        """{"type":"assistant","message":{"content":[{"type":"tool_use","id":"toolu_1","name":"PowerShell","input":{"command":"Get-ChildItem -Name"}}]}}""",
        """{"type":"assistant","message":{"content":[{"type":"tool_use","id":"toolu_1","name":"PowerShell","input":{"command":"Get-ChildItem -Name"}}]}}""",
        "não é json",
        """{"type":"stream_event","event":{"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"São 3 arquivos."}}}""",
        """{"type":"result","subtype":"success","is_error":false,"result":"São 3 arquivos.","permission_denials":[{"tool_name":"WebFetch","tool_use_id":"a","tool_input":{}},{"tool_name":"WebFetch","tool_use_id":"b","tool_input":{}}]}""",
    ];

    private static (ClaudeOutput Output, List<string> Texts, List<string> Statuses) Read(bool commands)
    {
        var texts = new List<string>();
        var statuses = new List<string>();
        var output = new ClaudeOutput(commands, texts.Add, statuses.Add);
        foreach (var line in Fixture) output.Line(line);
        return (output, texts, statuses);
    }

    [Fact]
    public void Uso_de_ferramenta_e_recusa_viram_andamento_fora_do_texto()
    {
        var (output, texts, statuses) = Read(commands: true);
        Assert.Equal(["Executando: Get-ChildItem -Name", "Não liberado: WebFetch"], statuses);
        Assert.Equal(["Vou listar.", "Vou listar.\n\nSão 3 arquivos."], texts);
        Assert.Equal("São 3 arquivos.", output.Result);
        Assert.Null(output.Error);
    }

    [Fact]
    public void Desligada_nao_ha_andamento()
    {
        var (_, texts, statuses) = Read(commands: false);
        Assert.Empty(statuses);
        Assert.Equal(2, texts.Count);
    }

    [Theory]
    [InlineData("A\n\nB", "B", "A\n\nB")]
    [InlineData("Olá", "Olá, tudo bem?", "Olá, tudo bem?")]
    [InlineData("", "R", "R")]
    [InlineData("Parcial", "Outro texto", "Parcial\n\nOutro texto")]
    [InlineData("Texto\n", null, "Texto")]
    public void Resposta_final_comeca_pelo_que_ja_foi_mostrado(string streamed, string? result, string expected)
    {
        string final = ClaudeProvider.FinalText(streamed, result);
        Assert.Equal(expected, final);
        Assert.StartsWith(streamed.TrimEnd(), final);
    }

    private sealed class TextOnly : IProvider
    {
        public string Name => "x";
        public string Label => "X";
        public Task<Listing> ListAsync(CancellationToken ct) => Task.FromResult(new Listing([]));
        public Task<Answer> RunAsync(AiModel model, string? effort, Conversation conversation, Action<string> onText, CancellationToken ct)
        {
            onText("x");
            return Task.FromResult(new Answer("x", null));
        }
    }

    [Fact]
    public async Task Provedor_sem_andamento_continua_so_com_texto()
    {
        IProvider provider = new TextOnly();
        var texts = new List<string>();
        var statuses = new List<string>();
        var answer = await provider.RunAsync(CodexIAsClaudeCommandsArgumentsTests.Opus, null, new Conversation("oi", [], null, null), texts.Add, statuses.Add, CancellationToken.None);
        Assert.Equal("x", answer.Text);
        Assert.Equal(["x"], texts);
        Assert.Empty(statuses);
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
