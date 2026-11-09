using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using WinStatusBar.Core.Ai;

namespace WinStatusBar.ExecutorExpansion;

public enum Effect { Read, Mutation }
public enum ReceiptState { Done, Failed, Blocked, Cancelled, Uncertain }
public sealed record PlanStep(string Id, DesktopActionRequest Request, Effect Effect,
    string[] DependsOn, int TimeoutSeconds = 60, int ReadAttempts = 2);
public sealed record StepReceipt(string Id, ReceiptState State, DesktopActionResult Result,
    long DurationMs, int Attempts, string Method, string RequestHash);
public sealed record PlanReport(string ExecutionId, IReadOnlyList<StepReceipt> Steps)
{
    public bool Verified => Steps.Count > 0 && Steps.All(s => s.State == ReceiptState.Done);
    public string HumanText => string.Join(Environment.NewLine, Steps.Select(s =>
        $"{s.Id}: {s.State} · {s.DurationMs} ms · {s.Method} · {s.Result.Text}"));
}
public sealed class TransientReadException(string message) : IOException(message);

/// <summary>
/// One runner per user request, behind AllowEdits. Reuse executionId only for the SAME plan.
/// Serializes execution, caches all outcomes, and never retries a mutation or an uncertain operation.
/// Does not terminate COM or third-party apps. The existing disposable broker owns hard termination.
/// </summary>
public sealed class VerifiedPlanRunner
{
    private readonly SemaphoreSlim serial = new(1, 1);
    private readonly ConcurrentDictionary<string, StepReceipt> receipts = new();
    private readonly ConcurrentDictionary<string, string> plans = new();
    private Task<DesktopActionResult>? unfinished;
    public Action<string>? Trace { get; init; }

    public async Task<PlanReport> RunAsync(string executionId, IReadOnlyList<PlanStep> steps,
        Func<DesktopActionRequest, CancellationToken, Task<DesktopActionResult>> execute,
        CancellationToken ct = default)
    {
        Validate(executionId, steps);
        await serial.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            string planHash = Hash(steps);
            if (plans.TryGetValue(executionId, out string? previous) && previous != planHash)
                throw new InvalidOperationException("executionId já pertence a outro plano.");
            plans.TryAdd(executionId, planHash);
            if (unfinished is { IsCompleted: false })
                throw new InvalidOperationException("Operação anterior ainda está encerrando. Reinicie o auxiliar antes de continuar.");
            unfinished = null;
            var report = new List<StepReceipt>();
            bool stop = false;
            foreach (var step in steps)
            {
                string key = executionId + "/" + step.Id, hash = Hash(step.Request);
                if (receipts.TryGetValue(key, out var saved))
                {
                    report.Add(saved);
                    stop |= saved.State != ReceiptState.Done;
                    continue;
                }
                if (stop || step.DependsOn.Any(id => report.Single(x => x.Id == id).State != ReceiptState.Done))
                {
                    report.Add(Store(key, new(step.Id, ReceiptState.Blocked,
                        new("Não executada: a etapa anterior/dependência não foi confirmada.", Success: false,
                            Status: DesktopStatus.Blocked), 0, 0, "planner", hash)));
                    continue;
                }
                if (ct.IsCancellationRequested)
                {
                    report.Add(Store(key, new(step.Id, ReceiptState.Cancelled,
                        new("Cancelada antes de iniciar.", Success: false, Status: DesktopStatus.Cancelled),
                        0, 0, "planner", hash)));
                    stop = true;
                    continue;
                }
                var clock = Stopwatch.StartNew();
                int attempts = 0;
                DesktopActionResult result;
                ReceiptState state;
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
                deadline.CancelAfter(TimeSpan.FromSeconds(step.TimeoutSeconds));
                while (true)
                {
                    attempts++;
                    try
                    {
                        deadline.Token.ThrowIfCancellationRequested();
                        // Task.Run isolates synchronous startup too, but cannot kill a native call.
                        var work = Task.Run(() => execute(step.Request, deadline.Token), CancellationToken.None);
                        unfinished = work;
                        result = await work.WaitAsync(deadline.Token).ConfigureAwait(false);
                        unfinished = null;
                        state = result.Success && result.Outcome() == DesktopStatus.Done
                            ? ReceiptState.Done : result.Outcome() == DesktopStatus.Blocked
                                ? ReceiptState.Blocked : ReceiptState.Failed;
                        break;
                    }
                    catch (TransientReadException) when (step.Effect == Effect.Read &&
                        attempts < step.ReadAttempts && !deadline.IsCancellationRequested)
                    {
                        try { await Task.Delay(100 * attempts, deadline.Token).ConfigureAwait(false); }
                        catch (OperationCanceledException) { /* handled at next attempt */ }
                    }
                    catch (OperationCanceledException)
                    {
                        state = step.Effect == Effect.Mutation ? ReceiptState.Uncertain : ReceiptState.Cancelled;
                        result = new(step.Effect == Effect.Mutation
                            ? "Interrompida após iniciar: efeito não confirmado. Observe antes de qualquer nova mutação."
                            : "Leitura cancelada ou prazo excedido.", Success: false,
                            Status: state == ReceiptState.Uncertain ? DesktopStatus.Partial : DesktopStatus.Cancelled);
                        break;
                    }
                    catch (Exception)
                    {
                        // Never echo arbitrary exception text: it can contain scripts, credentials or private data.
                        state = step.Effect == Effect.Mutation ? ReceiptState.Uncertain : ReceiptState.Failed;
                        result = new("Falha no adaptador; resultado " +
                            (state == ReceiptState.Uncertain ? "não confirmado." : "indisponível."),
                            Success: false, Status: state == ReceiptState.Uncertain ? DesktopStatus.Partial : DesktopStatus.Failed);
                        break;
                    }
                }
                var receipt = new StepReceipt(step.Id, state, result, clock.ElapsedMilliseconds,
                    attempts, result.Method ?? "adapter", hash);
                report.Add(Store(key, receipt));
                // Trace contains metadata only, never paths, data, credentials or model output.
                try { Trace?.Invoke($"step={step.Id};state={state};ms={receipt.DurationMs};attempts={attempts}"); }
                catch { /* observability must not interrupt execution */ }
                stop |= state != ReceiptState.Done;
            }
            return new(executionId, report);
        }
        finally { serial.Release(); }
    }
    private StepReceipt Store(string key, StepReceipt receipt) { receipts[key] = receipt; return receipt; }
    private static string Hash<T>(T value) => Convert.ToHexString(
        SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value))));
    private static void Validate(string id, IReadOnlyList<PlanStep> steps)
    {
        if (!Guid.TryParse(id, out _) || steps.Count is < 1 or > 30)
            throw new ArgumentException("executionId deve ser GUID; plano deve ter de 1 a 30 etapas.");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var s in steps)
        {
            if (!System.Text.RegularExpressions.Regex.IsMatch(s.Id, "^[a-zA-Z0-9_-]{1,40}$") ||
                !seen.Add(s.Id) || s.DependsOn.Contains(s.Id) || s.DependsOn.Any(d => !seen.Contains(d)) ||
                s.TimeoutSeconds is < 1 or > 1800 || s.ReadAttempts is < 1 or > 3 ||
                !DesktopActionCatalog.Contains(s.Request.Action))
                throw new ArgumentException("Plano inválido: ids, dependências, ação ou limites.");
            // Effects are derived/checked here, never trusted from model JSON.
            if (s.Effect == Effect.Read && !RequestSafety.IsRead(s.Request))
                throw new ArgumentException("Mutação não pode ser declarada leitura.");
        }
    }
}

public static class RequestSafety
{
    public static bool IsRead(DesktopActionRequest r) => r.Action switch
    {
        "list_windows" or "observe" or "observe_foreground" or "find" or "clipboard_get" => true,
        "file" => r.Op is "list" or "read" or "exists" or "info" or "search",
        "office" => r.Op is "state" or "read",
        "registry" => r.Op == "read",
        "service" => r.Op == "status",
        "process" => r.Op == "list",
        "system" => r.Op is "apps_discover" or "network_list" or "network_diagnose" or "network_access" or
            "windows_query" or "windows_query_catalog" or "revit_discover" or "dynamo_inspect" or
            "setting_read" or "settings_catalog" or "capabilities" or "info" or "battery" or "ip",
        _ => false
    };
}
