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

    /// <summary>O mesmo Claude Code que a barra usa.</summary>
    public static string? FindExecutable() => WinStatusBar.Core.Ai.AgentPrompt.FindClaudeExecutable();

    private static ProcessStartInfo StartInfo(string exe, IEnumerable<string> args, string workDir)
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
        return new(ToModels(reading.Models));
    }

    /// <summary>Opções da barra → opções do Codex (mesma ordem do Claude Code).</summary>
    internal static IReadOnlyList<AiModel> ToModels(IReadOnlyList<AgentPrompt.ModelOption> options)
        => options.Select(o => new AiModel(
                "codexias-claude-" + Slug.From(o.Id.StartsWith("claude-", StringComparison.Ordinal) ? o.Id["claude-".Length..] : o.Id),
                "claude", o.Id, "Claude " + o.Label,
                "Claude Code pela conta, pela WinStatusBar. Responde em texto e pode ler a pasta da conversa.",
                o.Efforts, o.DefaultEffort, -6))
            .DistinctBy(m => m.Slug).ToList();

    public async Task<Answer> RunAsync(AiModel model, string? effort, Conversation conversation, Action<string> onText, CancellationToken ct)
    {
        string? exe = FindExecutable();
        if (exe is null) return new("", "Claude Code não encontrado neste PC.");
        string workDir = conversation.Cwd is { } wd && Directory.Exists(wd) ? wd : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var args = new List<string> { "-p", "--output-format", "stream-json", "--verbose", "--include-partial-messages" };
        if (model.Id != "default") args.AddRange(["--model", model.Id]);
        if (!string.IsNullOrEmpty(effort)) args.AddRange(["--effort", effort]);
        args.AddRange(Quiet);
        string prompt = ConversationReader.WithTranscript(conversation.Prompt, conversation.History);

        var text = new StringBuilder();
        string? final = null, error = null;
        Process? proc = null;
        try
        {
            proc = Process.Start(StartInfo(exe, args, workDir));
            if (proc is null) return new("", "Não foi possível iniciar o Claude Code.");
            // No grupo que encerra tudo junto com o componente (fechado ou atualizado no meio: o claude e os comandos param).
            if (Job.Value?.Add(proc) != true && Interlocked.Exchange(ref _jobWarned, 1) == 0)
                Log.Write("Claude: grupo de processos do Windows indisponível; os comandos do Claude não param junto com o componente");
            // O pedido vai pela entrada padrão: sem limite de tamanho da linha de comando.
            await proc.StandardInput.WriteAsync(prompt.AsMemory(), ct);
            proc.StandardInput.Close();
            var stderrTask = proc.StandardError.ReadToEndAsync(ct);
            using var reg = ct.Register(() => { try { if (!proc.HasExited) proc.Kill(entireProcessTree: true); } catch { } });
            while (await proc.StandardOutput.ReadLineAsync(ct) is { } line)
            {
                if (line.Length == 0 || line[0] != '{') continue;
                try
                {
                    using var doc = JsonDocument.Parse(line);
                    var root = doc.RootElement;
                    string type = Str(root, "type") ?? "";
                    if (type == "stream_event" && root.TryGetProperty("event", out var ev)
                        && Str(ev, "type") == "content_block_delta" && ev.TryGetProperty("delta", out var d) && Str(d, "type") == "text_delta")
                    {
                        text.Append(Str(d, "text"));
                        onText(text.ToString());
                    }
                    else if (type == "assistant" && text.Length > 0 && !text.ToString().EndsWith("\n\n"))
                        text.Append("\n\n"); // entre turnos (uso de ferramentas), separa os blocos
                    else if (type == "result")
                    {
                        final = Str(root, "result");
                        if (root.TryGetProperty("is_error", out var isErr) && isErr.ValueKind == JsonValueKind.True)
                            error = final ?? "O Claude retornou um erro.";
                    }
                }
                catch (JsonException) { }
            }
            await proc.WaitForExitAsync(ct);
            string stderr = await stderrTask;
            string answer = !string.IsNullOrWhiteSpace(final) ? final!.Trim() : text.ToString().Trim();
            if (error is null && answer.Length == 0 && proc.ExitCode != 0)
                error = stderr.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault() ?? "O Claude terminou sem resposta.";
            return new(error is null ? answer : text.ToString().Trim(), error);
        }
        catch (OperationCanceledException) { return new(text.ToString().Trim(), "Interrompido"); }
        catch (Exception ex) { return new(text.ToString().Trim(), ex.Message); }
        finally { proc?.Dispose(); }
    }

    private static string? Str(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
