using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using WinStatusBar.Core.Ai;

namespace WinStatusBar.ExecutorExpansion;

/// <summary>Called by the existing runtime before ordinary actions. Nested steps use that SAME runtime.</summary>
public sealed class RecipeDispatcher
{
    private readonly VerifiedPlanRunner runner = new();
    private readonly ConcurrentDictionary<string, (string Hash, PlanStep[] Steps)> prepared = new();
    public Action<string>? Trace { get => runner.Trace; init => runner = new VerifiedPlanRunner { Trace = value }; }
    public async Task<DesktopActionResult?> TryExecuteAsync(DesktopActionRequest r,
        Func<DesktopActionRequest, CancellationToken, Task<DesktopActionResult>> execute, CancellationToken ct)
    {
        if (r.Action != "system" || r.Op != "recipe") return null;
        if (r.Name is not ("word.justify" or "network.map" or "windows.setting" or "excel.format" or "files.rename"))
            return null; // Unknown recipes do not close the general executor to the AI.
        if (!Guid.TryParse(r.Observation, out _))
            return new("Receita exige observation=GUID estável da execução. Reuse esse GUID para consultar resultado; não reinicie mutações incertas.", Success: false, Status: DesktopStatus.Blocked);
        string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(r))));
        if (prepared.TryGetValue(r.Observation!, out var existing) && existing.Hash != hash)
            return new("GUID pertence a outra receita/parametrização.", Success: false, Status: DesktopStatus.Blocked);
        var steps = existing.Steps;
        if (steps is null)
        {
            steps = r.Name switch
            {
                "word.justify" => DeterministicRecipes.WordJustify(r.Target ?? throw new ArgumentException("Informe target do documento."), r.Scope ?? "selection", r.Find),
                "network.map" => DeterministicRecipes.MapNetwork(r.Value ?? "", r.Path ?? "", r.Scope == "persistent", r.Format == "prompt"),
                "windows.setting" => DeterministicRecipes.ApplySetting(r.Target ?? throw new ArgumentException("Informe target da configuração."), r.Value ?? throw new ArgumentException("Informe value.")),
                "excel.format" => ExcelFormat(r),
                "files.rename" => await VerifiedFileRename.PrepareAsync(r.Values, ct),
                _ => throw new InvalidOperationException()
            };
            if (!prepared.TryAdd(r.Observation!, (hash, steps)))
            {
                var concurrent = prepared[r.Observation!];
                if (concurrent.Hash != hash) return new("GUID em uso por outro pedido.", Success: false, Status: DesktopStatus.Blocked);
                steps = concurrent.Steps;
            }
        }
        var report = await runner.RunAsync(r.Observation!, steps, execute, ct);
        bool uncertain = report.Steps.Any(s => s.State == ReceiptState.Uncertain || s.Result.Outcome() == DesktopStatus.Partial);
        return new(report.HumanText + "\nExecutionId: " + r.Observation,
            Success: report.Verified, Status: report.Verified ? DesktopStatus.Done : uncertain ? DesktopStatus.Partial : DesktopStatus.Blocked,
            Method: "deterministic-recipe");
    }
    private static PlanStep[] ExcelFormat(DesktopActionRequest r)
    {
        if (string.IsNullOrWhiteSpace(r.Target) || string.IsNullOrWhiteSpace(r.Range))
            throw new ArgumentException("Informe target do documento e range explícito.");
        OfficeExpansion.ValidateFormat(r);
        return [
            new("before", new("office", Application: "excel", Op: "state", Target: r.Target), Effect.Read, []),
            new("format", r with { Action = "office", Application = "excel", Op = "format", Name = null, Observation = null }, Effect.Mutation, ["before"]),
            new("after", new("office", Application: "excel", Op: "state", Target: r.Target), Effect.Read, ["format"])
        ];
    }
}
