using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace WinStatusBar.Core.Ai.Codex;

/// <summary>
/// Cliente JSON-RPC do protocolo oficial "codex app-server --listen stdio://".
/// A sessão é aberta sob demanda, consultada e encerrada — não mantém processo permanente.
/// A autenticação continua inteiramente a cargo do CLI (nenhum token é lido ou armazenado).
/// </summary>
public sealed class CodexAppServerClient
{
    public sealed record AccountInfo(string? Type, string? PlanType);
    public sealed record QueryResult(QuotaSnapshot? Quota, AccountInfo? Account, string? Error)
    {
        public bool ResetDefinitive { get; init; }
    }

    public static string? FindCodexExecutable()
    {
        var candidates = new List<string>();
        string bin = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OpenAI", global::WinStatusBar.Core.LocalizedText.T("Codex"), "bin");
        try
        {
            if (Directory.Exists(bin))
                candidates.AddRange(Directory.EnumerateFiles(bin, "codex.exe", SearchOption.AllDirectories)
                    .OrderByDescending(File.GetLastWriteTimeUtc));
        }
        catch { }
        // Instalações alternativas em outros PCs: winget (Links), npm global (%APPDATA%\npm), scoop, PATH do usuário e da máquina.
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var dirs = new List<string>
        {
            Path.Combine(local, "Microsoft", "WinGet", global::WinStatusBar.Core.LocalizedText.T("Links")), Path.Combine(roaming, "npm"), Path.Combine(home, "scoop", "shims"),
            Path.Combine(home, ".local", "bin"),
        };
        foreach (var scope in new[] { EnvironmentVariableTarget.Process, EnvironmentVariableTarget.User, EnvironmentVariableTarget.Machine })
            dirs.AddRange((Environment.GetEnvironmentVariable("PATH", scope) ?? string.Empty).Split(';', StringSplitOptions.RemoveEmptyEntries));
        foreach (var dir in dirs.Select(d => d.Trim()).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                foreach (var name in new[] { "codex.exe", "codex.cmd" })
                {
                    string p = Path.Combine(Environment.ExpandEnvironmentVariables(dir), name);
                    if (File.Exists(p)) candidates.Add(p);
                }
            }
            catch { }
        }
        return candidates.FirstOrDefault(File.Exists);
    }

    /// <summary>Codex considerado instalado neste PC (executável ou pasta de dados).</summary>
    public static bool IsInstalled() => FindCodexExecutable() is not null || Directory.Exists(CodexHome());

    public static string CodexHome()
    {
        string? env = Environment.GetEnvironmentVariable("CODEX_HOME");
        return !string.IsNullOrWhiteSpace(env) ? env : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex");
    }

    public async Task<QueryResult> QueryAsync(string executable, TimeSpan timeout, CancellationToken ct, string? resetAttempt = null, string? creditId = null)
    {
        // codex.cmd (instalação via npm) precisa do cmd.exe; codex.exe é executado diretamente.
        bool viaCmd = executable.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase);
        var psi = new ProcessStartInfo(viaCmd ? Path.Combine(Environment.SystemDirectory, "cmd.exe") : executable)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            StandardInputEncoding = new UTF8Encoding(false), // sem BOM: o app-server rejeita a primeira linha com BOM
        };
        if (viaCmd) { psi.ArgumentList.Add("/d"); psi.ArgumentList.Add("/c"); psi.ArgumentList.Add(executable); }
        psi.ArgumentList.Add("app-server");
        psi.ArgumentList.Add("--listen");
        psi.ArgumentList.Add("stdio://");

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        Process? proc = null;
        try
        {
            proc = Process.Start(psi);
            if (proc is null) return new(null, null, global::WinStatusBar.Core.LocalizedText.T("Não foi possível iniciar o Codex"));
            try { proc.PriorityClass = ProcessPriorityClass.BelowNormal; } catch { }
            _ = proc.StandardError.ReadToEndAsync(cts.Token); // drena stderr para não bloquear o processo

            var pending = new Dictionary<int, TaskCompletionSource<JsonElement>>();
            var reader = Task.Run(async () =>
            {
                while (!cts.IsCancellationRequested)
                {
                    string? line = await proc.StandardOutput.ReadLineAsync(cts.Token);
                    if (line is null) break;
                    if (line.Length == 0 || line[0] != '{') continue;
                    try
                    {
                        using var doc = JsonDocument.Parse(line);
                        var root = doc.RootElement;
                        if (root.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.Number)
                        {
                            TaskCompletionSource<JsonElement>? tcs;
                            lock (pending) pending.TryGetValue(idEl.GetInt32(), out tcs);
                            tcs?.TrySetResult(root.Clone());
                        }
                    }
                    catch (JsonException) { }
                }
            }, cts.Token);

            async Task<JsonElement> Call(int id, string method, JsonNode? @params)
            {
                var tcs = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
                lock (pending) pending[id] = tcs;
                var msg = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = method };
                if (@params is not null) msg["params"] = @params;
                await proc.StandardInput.WriteLineAsync(msg.ToJsonString().AsMemory(), cts.Token);
                await proc.StandardInput.FlushAsync(cts.Token);
                using (cts.Token.Register(() => tcs.TrySetCanceled()))
                    return await tcs.Task;
            }

            var init = await Call(1, "initialize", new JsonObject
            {
                ["clientInfo"] = new JsonObject { ["name"] = "winstatusbar", ["title"] = "WinStatusBar", ["version"] = "1.0.0" },
                ["capabilities"] = new JsonObject
                {
                    ["experimentalApi"] = resetAttempt is not null,
                    ["optOutNotificationMethods"] = new JsonArray("warning", "remoteControl/status/changed", "skills/changed", "mcpServer/startupStatus/updated"),
                },
            });
            if (init.TryGetProperty("error", out var initErr)) return new(null, null, initErr.ToString());
            await proc.StandardInput.WriteLineAsync("{\"jsonrpc\":\"2.0\",\"method\":\"initialized\"}".AsMemory(), cts.Token);
            await proc.StandardInput.FlushAsync(cts.Token);

            // Sequencial: duas escritas simultâneas no mesmo stdin geram
            // "The stream is currently in use by a previous operation" e a consulta oficial (com resets e plano) se perde.
            var account = await Call(2, "account/read", new JsonObject());
            var limits = await Call(3, "account/rateLimits/read", null);

            AccountInfo? info = null;
            if (account.TryGetProperty("result", out var ar) && ar.TryGetProperty("account", out var acc) && acc.ValueKind == JsonValueKind.Object)
                info = new AccountInfo(CodexParsers.Str(acc, "type"), CodexParsers.Str(acc, "planType"));

            if (limits.TryGetProperty("error", out var limErr))
                return new(null, info, CodexParsers.Str(limErr, "message") ?? global::WinStatusBar.Core.LocalizedText.T("Limites indisponíveis"));
            if (resetAttempt is not null)
            {
                var available = limits.TryGetProperty("result", out var before) ? CodexParsers.ParseRateLimits(before, "Codex app-server") : null;
                if (available?.ResetCreditsAvailable is not > 0) return new(available, info, "Nenhum reset disponível confirmado pela conta.");
                if (string.IsNullOrWhiteSpace(creditId) || !available.ResetCredits.Any(c => c.Id == creditId && c.CanConsume(DateTimeOffset.UtcNow)))
                    return new(available, info, "O reset escolhido não está mais disponível. Atualize a lista.");
                var consumed = await Call(4, "account/rateLimitResetCredit/consume", new JsonObject { ["idempotencyKey"] = resetAttempt, ["creditId"] = creditId });
                if (consumed.TryGetProperty("error", out var resetError)) return new(available, info, CodexParsers.Str(resetError, "message") ?? "O Codex não confirmou o reset.");
                string? outcome = consumed.TryGetProperty("result", out var resetResult) ? CodexParsers.Str(resetResult, "outcome") : null;
                if (ResetOutcomeError(outcome) is { } outcomeError) return new(available, info, outcomeError) { ResetDefinitive = outcome is "nothingToReset" or "noCredit" };
                limits = await Call(5, "account/rateLimits/read", null);
                if (limits.TryGetProperty("error", out _)) return new(null, info, "Reset aceito; não foi possível consultar o novo saldo. Atualize as cotas antes de tentar novamente.");
            }
            var quota = limits.TryGetProperty("result", out var lr) ? CodexParsers.ParseRateLimits(lr, global::WinStatusBar.Core.LocalizedText.T("Codex app-server")) : null;
            return new(quota, info, quota is null ? global::WinStatusBar.Core.LocalizedText.T("Limites indisponíveis para esta conta") : null);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new(null, null, global::WinStatusBar.Core.LocalizedText.T("Tempo esgotado ao consultar o Codex"));
        }
        catch (Exception ex)
        {
            return new(null, null, ex.Message);
        }
        finally
        {
            if (proc is not null)
            {
                try { proc.StandardInput.Close(); } catch { }
                try { if (!proc.WaitForExit(1500)) proc.Kill(entireProcessTree: true); } catch { }
                proc.Dispose();
            }
        }
    }

    internal static string? ResetOutcomeError(string? outcome) => outcome switch
    {
        "reset" or "alreadyRedeemed" => null,
        "nothingToReset" => "Nenhum limite está elegível para reset neste momento. Nenhum crédito foi consumido.",
        "noCredit" => "A conta não tem mais crédito de reset disponível.",
        _ => "O Codex não confirmou o resultado. Atualize as cotas antes de tentar novamente."
    };
}
