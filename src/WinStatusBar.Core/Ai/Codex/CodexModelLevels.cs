using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace WinStatusBar.Core.Ai.Codex;

/// <summary>
/// Níveis de pensamento de cada modelo da OpenAI, tirados da lista do Codex instalado ("codex debug models --bundled").
/// O Codex se atualiza sozinho; um modelo novo da API (ex.: gpt-6.2-sol) aparece no seletor da barra com os níveis
/// certos, sem tabela fixa, quando a pessoa clica em "Verificar e atualizar" (Configurações › Codex e modelos).
/// </summary>
public static class CodexModelLevels
{
    public sealed record Levels(IReadOnlyList<string> Efforts, string? Default);

    private static string CacheFile => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WinStatusBar", "cache", "codex-modelos.json");
    private static readonly SemaphoreSlim Refreshing = new(1, 1);
    private static Dictionary<string, Levels>? _levels;

    private sealed record Cache(DateTime ReadAtUtc, string Stamp, Dictionary<string, Levels> Models);

    private static string StampOf(string exe) { try { return exe + "|" + File.GetLastWriteTimeUtc(exe).Ticks; } catch { return exe; } }

    /// <summary>Níveis conhecidos do modelo, ou null quando o Codex não o conhece.</summary>
    public static Levels? Get(string id) => _levels is { } l && l.TryGetValue(id, out var v) ? v : null;

    /// <summary>Carrega a lista guardada; sem nenhuma ainda, lê agora uma vez. Não relê sozinho (verificação manual).</summary>
    public static async Task EnsureAsync(CancellationToken ct)
    {
        if (_levels is not null) return;
        try
        {
            if (File.Exists(CacheFile) && JsonSerializer.Deserialize<Cache>(File.ReadAllText(CacheFile)) is { } cache) { _levels = cache.Models; return; }
        }
        catch { }
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(TimeSpan.FromSeconds(30));
        try { await RefreshNowAsync(limit.Token); } catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
    }

    /// <summary>Lê de novo a lista do Codex e guarda (verificação manual). Devolve quantos modelos leu (0: não deu).</summary>
    public static async Task<int> RefreshNowAsync(CancellationToken ct)
    {
        string? exe = CodexAppServerClient.FindCodexExecutable();
        if (exe is null) return 0;
        await Refreshing.WaitAsync(ct);
        try
        {
            bool viaCmd = exe.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase);
            var psi = new ProcessStartInfo(viaCmd ? Path.Combine(Environment.SystemDirectory, "cmd.exe") : exe)
            {
                RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8, WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            };
            if (viaCmd) { psi.ArgumentList.Add("/d"); psi.ArgumentList.Add("/c"); psi.ArgumentList.Add(exe); }
            foreach (var a in new[] { "debug", "models", "--bundled" }) psi.ArgumentList.Add(a);
            if (await OutputAsync(psi, ct) is not { } output) return 0;
            using var doc = JsonDocument.Parse(output);
            var levels = Parse(doc.RootElement);
            if (levels.Count == 0) return 0;
            _levels = levels;
            Directory.CreateDirectory(Path.GetDirectoryName(CacheFile)!);
            string temp = CacheFile + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(new Cache(DateTime.UtcNow, StampOf(exe), levels)));
            File.Move(temp, CacheFile, overwrite: true);
            return levels.Count;
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { return 0; }
        finally { Refreshing.Release(); }
    }

    /// <summary>
    /// Roda um programa sem janela até o fim e devolve a saída padrão (null se não iniciou). Se o prazo vencer ou o pedido
    /// for cancelado com o programa ainda aberto, ele e os filhos são encerrados antes de o cancelamento seguir: um
    /// "codex debug models" travado não fica rodando sem dono. <paramref name="psi"/> precisa redirecionar a saída.
    /// </summary>
    public static async Task<string?> OutputAsync(ProcessStartInfo psi, CancellationToken ct, Action<int>? onStarted = null)
    {
        using var proc = Process.Start(psi);
        if (proc is null) return null;
        try
        {
            onStarted?.Invoke(proc.Id);
            var stdout = proc.StandardOutput.ReadToEndAsync(ct);
            if (psi.RedirectStandardError) _ = proc.StandardError.ReadToEndAsync(ct);
            await proc.WaitForExitAsync(ct).ConfigureAwait(false);
            return await stdout.ConfigureAwait(false);
        }
        finally
        {
            try { if (!proc.HasExited) proc.Kill(entireProcessTree: true); } catch { }
        }
    }

    /// <summary>{"models":[{"slug","supported_reasoning_levels":[{"effort"}],"default_reasoning_level"}]} → níveis por modelo.</summary>
    public static Dictionary<string, Levels> Parse(JsonElement root)
    {
        var result = new Dictionary<string, Levels>(StringComparer.Ordinal);
        if (!root.TryGetProperty("models", out var models) || models.ValueKind != JsonValueKind.Array) return result;
        foreach (var m in models.EnumerateArray())
        {
            if (!m.TryGetProperty("slug", out var s) || s.ValueKind != JsonValueKind.String) continue;
            var efforts = m.TryGetProperty("supported_reasoning_levels", out var lv) && lv.ValueKind == JsonValueKind.Array
                ? lv.EnumerateArray().Select(x => x.TryGetProperty("effort", out var e) && e.ValueKind == JsonValueKind.String ? e.GetString() : null).OfType<string>().ToList() : [];
            string? def = m.TryGetProperty("default_reasoning_level", out var d) && d.ValueKind == JsonValueKind.String ? d.GetString() : null;
            result[s.GetString()!] = new(efforts, def);
        }
        return result;
    }

    /// <summary>Para testes: define os níveis conhecidos sem rodar o Codex.</summary>
    public static void Set(Dictionary<string, Levels>? levels) => _levels = levels;
}
