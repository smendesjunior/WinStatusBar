using System.Diagnostics;
using System.Text;
using System.Text.Json;
using WinStatusBar.Core.Ai;
using WinStatusBar.Core.Ai.Claude;

namespace CodexIAs.Providers;

/// <summary>
/// Claude pelo Claude Code (claude CLI). A lista de modelos vem do próprio Claude Code (pedido "initialize" do modo
/// stream-json, que não chama nenhum modelo), com os níveis de esforço de cada um. Cada pedido roda "claude -p" sem
/// sessão salva, com o histórico do Codex como contexto, só leitura na pasta da conversa.
/// </summary>
public sealed class ClaudeProvider : IProvider
{
    public string Name => "claude";
    public string Label => "Claude";

    /// <summary>Tamanho do pacote de passagem para o Claude (cabe com folga na janela de contexto dele).</summary>
    internal const int TranscriptChars = 60000;

    /// <summary>O mesmo Claude Code que a barra usa.</summary>
    public static string? FindExecutable() => WinStatusBar.Core.Ai.AgentPrompt.FindClaudeExecutable();

    /// <summary>Ferramentas liberadas com a opção ligada (WebFetch fica mesmo ausente no Claude Code 2.1.317: é ignorada sem erro).</summary>
    internal static readonly string[] CommandTools = ["Bash", "PowerShell", "Read", "Write", "Edit", "Glob", "Grep", "WebFetch", "WebSearch"];

    /// <summary>Argumentos do claude -p: os de hoje e, com a opção ligada, as permissões no fim (--allowedTools aceita vários valores).</summary>
    internal static List<string> Arguments(AiModel model, string? effort, bool commands)
    {
        var args = new List<string> { "-p", "--output-format", "stream-json", "--verbose", "--include-partial-messages" };
        if (model.Id != "default") args.AddRange(["--model", model.Id]);
        if (!string.IsNullOrEmpty(effort)) args.AddRange(["--effort", effort]);
        args.AddRange(Quiet);
        if (commands) { args.AddRange(["--permission-mode", "acceptEdits", "--allowedTools"]); args.AddRange(CommandTools); }
        return args;
    }

    internal static ProcessStartInfo StartInfo(string exe, IEnumerable<string> args, string workDir, bool commands)
    {
        bool viaCmd = exe.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase);
        var psi = new ProcessStartInfo(viaCmd ? Path.Combine(Environment.SystemDirectory, "cmd.exe") : exe)
        {
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = workDir,
            StandardInputEncoding = new UTF8Encoding(false), StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
        };
        if (viaCmd) { psi.ArgumentList.Add("/d"); psi.ArgumentList.Add("/c"); psi.ArgumentList.Add(exe); }
        foreach (var a in args) psi.ArgumentList.Add(a);
        // PATH de agora, não o de quando o encaminhador abriu: programas instalados depois (adb, git…) ficam visíveis.
        FreshPath.Apply(psi);
        if (commands)
        {
            // Sem isso o Claude Code corta cada comando em 2 a 10 min; etapas de 20 a 40 min precisam caber.
            psi.Environment["BASH_DEFAULT_TIMEOUT_MS"] = "2400000";
            psi.Environment["BASH_MAX_TIMEOUT_MS"] = "3600000";
        }
        return psi;
    }

    // Sem sessão salva, sem MCPs e sem hooks: a chamada do Codex não vira tarefa da barra nem carrega conectores.
    private static readonly Lazy<KillOnCloseJob?> Job = new(KillOnCloseJob.Create);
    private static int _jobWarned;

    private static readonly string[] Quiet =["--no-session-persistence", "--strict-mcp-config", "--settings", "{\"disableAllHooks\":true}"];

    /// <summary>
    /// Só com o Claude Code instalado e com conta conectada. A lista é a mesma que a barra mostra (ClaudeModelList, lida
    /// do próprio Claude Code e guardada para os dois).
    /// </summary>
    public async Task<Listing> ListAsync(CancellationToken ct)
    {
        string? exe = FindExecutable();
        if (exe is null) return Listing.Off("Claude Code não instalado neste PC");
        var reading = await ClaudeModelList.RefreshNowAsync(exe, ct);
        if (reading is null) return Listing.Fail("o Claude Code não respondeu");
        if (!reading.SignedIn) return Listing.Off("Claude Code sem conta conectada (entre na conta no Claude Code)");
        return new(ToModels(reading.Models, BarSettings.ClaudeCommands));
    }

    /// <summary>Opções da barra → opções do Codex (mesma ordem do Claude Code).</summary>
    internal static IReadOnlyList<AiModel> ToModels(IReadOnlyList<AgentPrompt.ModelOption> options, bool commands = false)
        => options.Select(o => new AiModel(
                "codexias-claude-" + Slug.From(o.Id.StartsWith("claude-", StringComparison.Ordinal) ? o.Id["claude-".Length..] : o.Id),
                "claude", o.Id, "Claude " + o.Label,
                commands ? "Claude Code pela conta, pela WinStatusBar. Pode executar comandos e editar arquivos na pasta da conversa."
                    : "Claude Code pela conta, pela WinStatusBar. Responde em texto e pode ler a pasta da conversa.",
                o.Efforts, o.DefaultEffort, -6))
            .DistinctBy(m => m.Slug).ToList();

    public Task<Answer> RunAsync(AiModel model, string? effort, Conversation conversation, Action<string> onText, CancellationToken ct)
        => RunAsync(model, effort, conversation, onText, _ => { }, ct);

    /// <summary>Roda o claude -p. O texto vai por <paramref name="onText"/>; com a opção de comandos ligada, cada uso de
    /// ferramenta (e cada recusa) vira uma linha de andamento por <paramref name="onStatus"/>, nunca no texto.</summary>
    public async Task<Answer> RunAsync(AiModel model, string? effort, Conversation conversation, Action<string> onText, Action<string> onStatus, CancellationToken ct)
    {
        string? exe = FindExecutable();
        if (exe is null) return new("", "Claude Code não encontrado neste PC.");
        string workDir = conversation.Cwd is { } wd && Directory.Exists(wd) ? wd : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        bool commands = BarSettings.ClaudeCommands;
        var args = Arguments(model, effort, commands);
        string prompt = ConversationReader.WithTranscript(conversation, TranscriptChars, workDir);

        var output = new ClaudeOutput(commands, onText, onStatus);
        Process? proc = null;
        try
        {
            proc = Process.Start(StartInfo(exe, args, workDir, commands));
            if (proc is null) return new("", "Não foi possível iniciar o Claude Code.");
            // No grupo que encerra tudo junto com o componente (fechado ou atualizado no meio: o claude e os comandos param).
            if (Job.Value?.Add(proc) != true && Interlocked.Exchange(ref _jobWarned, 1) == 0)
                Log.Write("Claude: grupo de processos do Windows indisponível; os comandos do Claude não param junto com o componente");
            // Antes de enviar o pedido: cancelar no meio do envio (o claude ainda abrindo) também encerra o processo, que
            // senão ficaria rodando com meio pedido.
            using var reg = ct.Register(() => { try { if (!proc.HasExited) proc.Kill(entireProcessTree: true); } catch { } });
            // O pedido vai pela entrada padrão: sem limite de tamanho da linha de comando.
            await proc.StandardInput.WriteAsync(prompt.AsMemory(), ct);
            proc.StandardInput.Close();
            var stderrTask = proc.StandardError.ReadToEndAsync(ct);
            while (await proc.StandardOutput.ReadLineAsync(ct) is { } line) output.Line(line);
            await proc.WaitForExitAsync(ct);
            string stderr = await stderrTask;
            string? error = output.Error;
            string answer = commands ? FinalText(output.Streamed, output.Result)
                : !string.IsNullOrWhiteSpace(output.Result) ? output.Result!.Trim() : output.Streamed.Trim();
            if (error is null && answer.Length == 0 && proc.ExitCode != 0)
                error = stderr.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault() ?? "O Claude terminou sem resposta.";
            return new(error is null ? answer : output.Streamed.Trim(), error);
        }
        catch (OperationCanceledException) { return new(output.Streamed.Trim(), "Interrompido"); }
        catch (Exception ex) { return new(output.Streamed.Trim(), ex.Message); }
        finally
        {
            // Saída por erro ou cancelamento com o claude ainda aberto: nunca fica um processo sem dono.
            try { if (proc is { HasExited: false }) proc.Kill(entireProcessTree: true); } catch { }
            proc?.Dispose();
        }
    }

    /// <summary>Resposta final que começa pelo que já foi mostrado (o Codex só recebe o que falta).</summary>
    internal static string FinalText(string streamed, string? result)
    {
        string shown = streamed.TrimEnd();
        string r = result?.Trim() ?? "";
        if (r.Length == 0) return shown;
        if (shown.Length == 0) return r;
        if (r.StartsWith(shown, StringComparison.Ordinal)) return r;
        if (shown.EndsWith(r, StringComparison.Ordinal)) return shown;
        return shown + "\n\n" + r;
    }

    private static string? Str(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}

/// <summary>
/// Lê o stream-json do claude -p, linha a linha: o texto (text_delta) acumula em <see cref="Streamed"/> e vai por onText; com
/// os comandos ligados, cada tool_use novo (pelo id, no evento assistant que traz o input completo) e cada recusa de
/// permissão viram uma linha de andamento por onStatus.
/// </summary>
internal sealed class ClaudeOutput(bool commands, Action<string> onText, Action<string> onStatus)
{
    private readonly StringBuilder _text = new();
    private readonly HashSet<string> _toolUses = new(StringComparer.Ordinal);

    public string Streamed => _text.ToString();
    public string? Result { get; private set; }
    public string? Error { get; private set; }

    public void Line(string line)
    {
        if (line.Length == 0 || line[0] != '{') return;
        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            string type = Str(root, "type") ?? "";
            if (type == "stream_event" && root.TryGetProperty("event", out var ev)
                && Str(ev, "type") == "content_block_delta" && ev.TryGetProperty("delta", out var d) && Str(d, "type") == "text_delta")
            {
                _text.Append(Str(d, "text"));
                onText(_text.ToString());
            }
            else if (type == "assistant")
            {
                if (commands && root.TryGetProperty("message", out var message) && message.TryGetProperty("content", out var content)
                    && content.ValueKind == JsonValueKind.Array)
                    foreach (var block in content.EnumerateArray())
                        if (Str(block, "type") == "tool_use" && Str(block, "id") is { } id && _toolUses.Add(id))
                            onStatus(ClaudeToolStatus.Describe(Str(block, "name") ?? "ferramenta",
                                block.TryGetProperty("input", out var input) ? input : default));
                if (_text.Length > 0 && !_text.ToString().EndsWith("\n\n")) _text.Append("\n\n"); // entre turnos, separa os blocos
            }
            else if (type == "result")
            {
                Result = Str(root, "result");
                if (root.TryGetProperty("is_error", out var isErr) && isErr.ValueKind == JsonValueKind.True)
                    Error = Result ?? "O Claude retornou um erro.";
                if (commands && root.TryGetProperty("permission_denials", out var denials) && denials.ValueKind == JsonValueKind.Array)
                    foreach (var tool in denials.EnumerateArray().Select(x => Str(x, "tool_name")).Where(t => t is not null).Distinct())
                        onStatus(ClaudeToolStatus.Denied(tool!));
            }
        }
        catch (JsonException) { }
    }

    private static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
