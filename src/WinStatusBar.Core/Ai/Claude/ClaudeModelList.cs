using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace WinStatusBar.Core.Ai.Claude;

/// <summary>
/// Modelos do Claude lidos do próprio Claude Code (pedido "initialize" do modo stream-json, que não chama nenhum
/// modelo), com os níveis de esforço de cada um e se há conta conectada. Sem lista fixa no código: um modelo novo
/// aparece quando a pessoa clica em "Verificar e atualizar" (Configurações › Modelos). A lista fica guardada e é a mesma
/// para a barra e para as IAs no Codex.
/// </summary>
public static class ClaudeModelList
{
    private static string CacheFile => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WinStatusBar", "cache", "claude-modelos.json");
    private static readonly SemaphoreSlim Refreshing = new(1, 1);

    /// <summary>Uma leitura do Claude Code: modelos e se há conta conectada (assinatura ou e-mail).</summary>
    public sealed record Reading(IReadOnlyList<AgentPrompt.ModelOption> Models, bool SignedIn);

    private sealed record Cache(DateTime ReadAtUtc, List<AgentPrompt.ModelOption> Models, bool SignedIn = true);

    private static Cache? Load()
    {
        try { return File.Exists(CacheFile) ? JsonSerializer.Deserialize<Cache>(File.ReadAllText(CacheFile)) : null; } catch { return null; }
    }

    /// <summary>Data da última leitura guardada (null: nunca leu).</summary>
    public static DateTime? ReadAtUtc => Load()?.ReadAtUtc;

    /// <summary>
    /// Lista guardada. Sem nenhuma ainda, lê agora uma vez (até 30 s). Null quando o Claude Code não respondeu (quem chama
    /// usa a lista de reserva). Não relê sozinho: modelos novos entram pela verificação manual.
    /// </summary>
    public static async Task<IReadOnlyList<AgentPrompt.ModelOption>?> GetAsync(string? exe, CancellationToken ct)
    {
        if (Load() is { } cache) return cache.Models;
        if (exe is null) return null;
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(TimeSpan.FromSeconds(30));
        return (await RefreshNowAsync(exe, limit.Token))?.Models;
    }

    /// <summary>Lê de novo do Claude Code e guarda (verificação manual). Null quando não respondeu.</summary>
    public static async Task<Reading?> RefreshNowAsync(string exe, CancellationToken ct)
    {
        await Refreshing.WaitAsync(ct);
        try
        {
            var reading = await ReadFromCliAsync(exe, ct);
            if (reading is not { Models.Count: > 0 }) return reading;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(CacheFile)!);
                string temp = CacheFile + ".tmp";
                File.WriteAllText(temp, JsonSerializer.Serialize(new Cache(DateTime.UtcNow, reading.Models.ToList(), reading.SignedIn)));
                File.Move(temp, CacheFile, overwrite: true);
            }
            catch { }
            return reading;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return null; }
        finally { Refreshing.Release(); }
    }

    private static async Task<Reading?> ReadFromCliAsync(string exe, CancellationToken ct)
    {
        bool viaCmd = exe.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase);
        var psi = new ProcessStartInfo(viaCmd ? Path.Combine(Environment.SystemDirectory, "cmd.exe") : exe)
        {
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true,
            StandardInputEncoding = new UTF8Encoding(false), StandardOutputEncoding = Encoding.UTF8,
            WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        };
        if (viaCmd) { psi.ArgumentList.Add("/d"); psi.ArgumentList.Add("/c"); psi.ArgumentList.Add(exe); }
        // Sem sessão salva, sem MCPs e sem hooks: a leitura não vira tarefa na barra.
        foreach (var a in new[] { "-p", "--input-format", "stream-json", "--output-format", "stream-json", "--verbose",
                     "--no-session-persistence", "--strict-mcp-config", "--settings", "{\"disableAllHooks\":true}" })
            psi.ArgumentList.Add(a);
        using var proc = Process.Start(psi);
        if (proc is null) return null;
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(TimeSpan.FromSeconds(45));
        try
        {
            _ = proc.StandardError.ReadToEndAsync(limit.Token);
            await proc.StandardInput.WriteLineAsync("""{"type":"control_request","request_id":"winstatusbar-modelos","request":{"subtype":"initialize"}}""");
            await proc.StandardInput.FlushAsync(limit.Token);
            while (await proc.StandardOutput.ReadLineAsync(limit.Token) is { } line)
            {
                if (!line.Contains("control_response", StringComparison.Ordinal)) continue;
                using var doc = JsonDocument.Parse(line);
                if (!doc.RootElement.TryGetProperty("response", out var r) || !r.TryGetProperty("response", out var inner)) return null;
                var models = inner.TryGetProperty("models", out var m) && m.ValueKind == JsonValueKind.Array ? Parse(m) : [];
                return new(models, SignedIn(inner));
            }
            return null;
        }
        finally { try { proc.StandardInput.Close(); if (!proc.HasExited) proc.Kill(entireProcessTree: true); } catch { } }
    }

    /// <summary>Conta conectada: "account" com e-mail ou assinatura; ou uma chave da API da Anthropic no ambiente.</summary>
    public static bool SignedIn(JsonElement initialize)
    {
        static bool Filled(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String && v.GetString()!.Trim().Length > 0;
        if (initialize.TryGetProperty("account", out var account) && account.ValueKind == JsonValueKind.Object
            && (Filled(account, "email") || Filled(account, "subscriptionType"))) return true;
        return !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY"));
    }

    /// <summary>
    /// Lista "models" do initialize → opções do seletor da barra. "default" fica de fora (a barra já tem "Igual ao app");
    /// apelidos ("opus") viram o modelo de verdade ("claude-opus-5-5"), para a escolha ficar fixa até a pessoa trocar.
    /// </summary>
    public static IReadOnlyList<AgentPrompt.ModelOption> Parse(JsonElement models)
    {
        static string? Str(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        var list = new List<AgentPrompt.ModelOption>();
        foreach (var m in models.EnumerateArray())
        {
            if (m.ValueKind != JsonValueKind.Object || Str(m, "value") is not { Length: > 0 } value || value == "default") continue;
            string id = Str(m, "resolvedModel") is { Length: > 0 } resolved ? resolved : value;
            if (list.Any(x => x.Id == id)) continue;
            var efforts = m.TryGetProperty("supportedEffortLevels", out var e) && e.ValueKind == JsonValueKind.Array
                ? e.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!).ToList() : [];
            list.Add(new(id, Str(m, "displayName") ?? AgentPrompt.Pretty(id), efforts, efforts.Contains("medium") ? "medium" : efforts.FirstOrDefault()));
        }
        return list;
    }
}
