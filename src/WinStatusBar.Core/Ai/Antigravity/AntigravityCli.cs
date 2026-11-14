using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace WinStatusBar.Core.Ai.Antigravity;

/// <summary>
/// Google Antigravity pelo CLI oficial (agy), com a conta Google da pessoa (login feito uma vez no próprio agy). Usado
/// pela barra (opções "Antigravity — …" do Gemini) e pelo encaminhador do Codex.
/// - Modelos: "agy models" (lista "id\tnome"), sem gerar texto.
/// - Pedido: "agy --input-format stream-json --output-format stream-json -p=" com o pedido pela entrada padrão (sem
///   limite de tamanho da linha de comando); a resposta chega em eventos step_update (text_delta) e termina em result.
/// - Permissões só desta execução (--settings, nunca gravado no settings.json do agy): sem escrita e sem comandos, a
///   não ser que a barra libere (lápis ou acesso completo).
/// </summary>
public static class AntigravityCli
{
    /// <summary>Prefixo das opções do Antigravity no seletor do Gemini da barra ("agy:gemini-3.8-flash-high").</summary>
    public const string BarPrefix = "agy:";

    public sealed record Model(string Id, string Label);
    public sealed record Event(string? Conversation, string? Delta, string? Final, string? Error, bool Done);

    /// <summary>agy.exe: o instalador oficial põe em %LOCALAPPDATA%\agy\bin; senão, o do PATH.</summary>
    public static string? FindExecutable()
    {
        try
        {
            string local = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "agy", "bin", "agy.exe");
            if (File.Exists(local)) return local;
            foreach (var scope in new[] { EnvironmentVariableTarget.Process, EnvironmentVariableTarget.User, EnvironmentVariableTarget.Machine })
                foreach (var dir in (Environment.GetEnvironmentVariable("PATH", scope) ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries))
                {
                    string p = Path.Combine(Environment.ExpandEnvironmentVariables(dir.Trim()), "agy.exe");
                    if (File.Exists(p)) return p;
                }
        }
        catch { }
        return null;
    }

    /// <summary>Saída de "agy models": linhas "id&lt;tab&gt;nome"; o resto (ex.: "Fetching available models...") é ignorado.</summary>
    public static IReadOnlyList<Model> ParseModels(string output)
    {
        var list = new List<Model>();
        foreach (var raw in output.Split('\n'))
        {
            var parts = raw.Trim().Split('\t', 2, StringSplitOptions.TrimEntries);
            if (parts.Length == 2 && parts[0].Length > 0 && parts[1].Length > 0 && !parts[0].Contains(' ')) list.Add(new(parts[0], parts[1]));
        }
        return list.DistinctBy(m => m.Id).ToList();
    }

    private static IReadOnlyList<Model>? _cache;
    private static DateTime _cacheAt;
    /// <summary>Última lista lida (sem chamar o agy); null antes da primeira leitura.</summary>
    public static IReadOnlyList<Model>? Cached => _cache;

    /// <summary>
    /// Modelos da conta ("agy models", até 60 s). Null: o agy não respondeu; lista vazia: respondeu sem modelos (sem login).
    /// Guarda por 1 h para o seletor da barra.
    /// </summary>
    public static async Task<IReadOnlyList<Model>?> ModelsAsync(CancellationToken ct, bool fresh = false)
    {
        if (!fresh && _cache is not null && DateTime.UtcNow - _cacheAt < TimeSpan.FromHours(1)) return _cache;
        if (FindExecutable() is not { } exe) return null;
        var psi = StartInfo(exe, ["models"], WorkFolder());
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(TimeSpan.FromSeconds(60));
        Process? proc = null;
        try
        {
            proc = Process.Start(psi);
            if (proc is null) return null;
            proc.StandardInput.Close();
            using var reg = limit.Token.Register(() => { try { if (!proc.HasExited) proc.Kill(entireProcessTree: true); } catch { } });
            var stderr = proc.StandardError.ReadToEndAsync(limit.Token);
            string output = await proc.StandardOutput.ReadToEndAsync(limit.Token);
            await proc.WaitForExitAsync(limit.Token);
            await stderr;
            var models = ParseModels(output);
            if (proc.ExitCode != 0 && models.Count == 0) return [];
            _cache = models; _cacheAt = DateTime.UtcNow;
            return models;
        }
        catch (Exception ex) when (ex is OperationCanceledException or InvalidOperationException or System.ComponentModel.Win32Exception or IOException)
        {
            if (ct.IsCancellationRequested) throw new OperationCanceledException(ct);
            return null;
        }
        finally
        {
            try { if (proc is { HasExited: false }) proc.Kill(entireProcessTree: true); } catch { }
            proc?.Dispose();
        }
    }

    /// <summary>Nome do modelo para as listas: "Claude Sonnet 4.6 (Thinking)" → "Antigravity — Claude Sonnet 4.6 (Thinking)".</summary>
    public static string DisplayName(Model m) => "Antigravity — " + m.Label;

    /// <summary>Ações bloqueadas só nesta execução: escrita, comandos, navegador e conectores (resposta só em texto).</summary>
    public static readonly string[] ReadOnlyDeny =
        ["write_file(*)", "edit_file(*)", "command(*)", "browser(*)", "mcp(*)"];
    /// <summary>Lápis ligado na barra: edita arquivos, mas sem comandos, navegador e conectores.</summary>
    public static readonly string[] EditDeny = ["command(*)", "browser(*)", "mcp(*)"];

    /// <summary>Argumentos do agy (testável). Sem <paramref name="deny"/> (acesso completo da barra), aprova tudo.</summary>
    public static List<string> Arguments(string? model, string? effort, string? conversation, IReadOnlyList<string>? deny, IEnumerable<string>? extraDirs = null)
    {
        var args = new List<string> { "--input-format", "stream-json", "--output-format", "stream-json" };
        if (!string.IsNullOrEmpty(model)) args.AddRange(["--model", model]);
        if (!string.IsNullOrEmpty(effort)) args.AddRange(["--effort", effort]);
        if (!string.IsNullOrEmpty(conversation)) args.AddRange(["--conversation", conversation]);
        foreach (var dir in extraDirs ?? []) args.AddRange(["--add-dir", dir]);
        if (deny is null) args.Add("--dangerously-skip-permissions");
        else args.AddRange(["--settings", JsonSerializer.Serialize(new { permissions = new { deny } })]);
        args.Add("--disable-slash-commands");
        // O pedido vem pela entrada padrão; "-p=" (vazio, colado) liga o modo sem janela sem pegar o próximo argumento.
        args.Add("-p=");
        return args;
    }

    /// <summary>Linha da entrada padrão com o pedido (um turno).</summary>
    public static string InputLine(string prompt) => JsonSerializer.Serialize(new { @event = "user", message = new { content = prompt } });

    /// <summary>Uma linha do stream-json do agy: conversa, trecho da resposta, resposta final, erro e fim.</summary>
    public static Event ParseEvent(string line)
    {
        if (line.Length == 0 || line[0] != '{') return new(null, null, null, null, false);
        try
        {
            using var doc = JsonDocument.Parse(line);
            var r = doc.RootElement;
            switch (Str(r, "event"))
            {
                case "init":
                    return new(Str(r, "conversation_id"), null, null, null, false);
                case "step_update" when r.TryGetProperty("step_update", out var s):
                    return new(Str(s, "conversation_id"), Str(s, "step_type") == "agent_response" ? Str(s, "text_delta") : null, null, null, false);
                case "result" when r.TryGetProperty("result", out var res):
                    string? status = Str(res, "status");
                    string? error = status is "SUCCESS" ? null : Str(res, "error") is { Length: > 0 } e ? e : $"O Antigravity terminou com estado {status ?? "desconhecido"}";
                    return new(Str(res, "conversation_id"), null, Str(res, "response"), error, true);
            }
        }
        catch (JsonException) { }
        return new(null, null, null, null, false);
    }

    /// <summary>Pasta de trabalho neutra quando não há pasta da conversa (o agy lê a pasta em que roda).</summary>
    public static string WorkFolder()
    {
        string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WinStatusBar", "Antigravity");
        try { Directory.CreateDirectory(dir); } catch { }
        return dir;
    }

    public static ProcessStartInfo StartInfo(string exe, IEnumerable<string> args, string workDir)
    {
        var psi = new ProcessStartInfo(exe)
        {
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = workDir,
            StandardInputEncoding = new UTF8Encoding(false), StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        FreshPath.Apply(psi);
        return psi;
    }

    /// <summary>Resultado de uma execução: texto, conversa do agy (para continuar) e erro.</summary>
    public sealed record Run(string Text, string? Conversation, string? Error);

    /// <summary>
    /// Roda um pedido. O texto acumulado vai por <paramref name="onText"/>. <paramref name="onStart"/> recebe o processo
    /// (o encaminhador o põe no grupo que encerra junto com ele).
    /// </summary>
    public static async Task<Run> RunAsync(string prompt, IReadOnlyList<string> args, string workDir, Action<string> onText,
        CancellationToken ct, Action<Process>? onStart = null)
    {
        if (FindExecutable() is not { } exe) return new("", null, "Antigravity (agy) não encontrado neste PC.");
        var text = new StringBuilder();
        string? conversation = null, final = null, error = null;
        Process? proc = null;
        try
        {
            proc = Process.Start(StartInfo(exe, args, workDir));
            if (proc is null) return new("", null, "Não foi possível iniciar o Antigravity (agy).");
            onStart?.Invoke(proc);
            using var reg = ct.Register(() => { try { if (!proc.HasExited) proc.Kill(entireProcessTree: true); } catch { } });
            var stderrTask = proc.StandardError.ReadToEndAsync(ct);
            await proc.StandardInput.WriteLineAsync(InputLine(prompt).AsMemory(), ct);
            proc.StandardInput.Close();
            while (await proc.StandardOutput.ReadLineAsync(ct) is { } line)
            {
                var ev = ParseEvent(line);
                conversation ??= ev.Conversation;
                if (ev.Delta is { } delta) { text.Append(delta); onText(text.ToString()); }
                if (ev.Done) { final = ev.Final; error = ev.Error; }
            }
            await proc.WaitForExitAsync(ct);
            string stderr = (await stderrTask).Trim();
            if (text.Length == 0 && final is { Length: > 0 }) { text.Append(final); onText(final); }
            if (error is null && text.Length == 0)
                error = stderr.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Where(l => !l.StartsWith("warning:", StringComparison.OrdinalIgnoreCase)).LastOrDefault() ?? "O Antigravity terminou sem resposta.";
            return new(text.ToString().Trim(), conversation, Friendly(error));
        }
        catch (OperationCanceledException) { return new(text.ToString().Trim(), conversation, "Interrompido"); }
        catch (Exception ex) { return new(text.ToString().Trim(), conversation, ex.Message); }
        finally
        {
            try { if (proc is { HasExited: false }) proc.Kill(entireProcessTree: true); } catch { }
            proc?.Dispose();
        }
    }

    /// <summary>Erros conhecidos em português simples (o resto passa como veio).</summary>
    public static string? Friendly(string? error)
    {
        if (string.IsNullOrEmpty(error)) return error;
        if (System.Text.RegularExpressions.Regex.IsMatch(error, @"not (logged|signed) in|unauthenticated|login required|please (log|sign) in", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
            return "O Antigravity está sem login. Abra um terminal, rode \"agy\" e entre com a conta Google.";
        if (System.Text.RegularExpressions.Regex.IsMatch(error, @"quota|rate limit|resource.?exhausted|\b429\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
            return "O Antigravity está sem cota agora para este modelo. Tente outro modelo ou mais tarde. (" + error + ")";
        return error;
    }

    private static string? Str(JsonElement e, string n) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
