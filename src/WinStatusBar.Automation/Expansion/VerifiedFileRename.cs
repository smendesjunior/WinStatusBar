using System.Security.Cryptography;
using WinStatusBar.Core.Ai;

namespace WinStatusBar.ExecutorExpansion;

public static class VerifiedFileRename
{
    public static async Task<PlanStep[]> PrepareAsync(string[][]? pairs, CancellationToken ct)
    {
        if (pairs is not { Length: >= 1 and <= 15 }) throw new ArgumentException("Informe values com 1 a 15 pares [origem,destino].");
        var sources = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var destinations = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var normalized = new List<(string Source, string Destination)>();
        foreach (var row in pairs)
        {
            if (row is null || row.Length != 2 || row.Any(p => string.IsNullOrWhiteSpace(p) || !Path.IsPathFullyQualified(p))) throw new ArgumentException("Cada par precisa de dois caminhos absolutos.");
            string source = Path.GetFullPath(row[0]), destination = Path.GetFullPath(row[1]);
            if (!sources.Add(source) || !destinations.Add(destination) || source.Equals(destination, StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("Origem/destino repetido ou sem alteração.");
            if (!File.Exists(source) || File.Exists(destination) || Directory.Exists(destination))
                throw new ArgumentException("Origem ausente ou destino já existe; nada foi renomeado.");
            if (!string.Equals(Path.GetDirectoryName(source), Path.GetDirectoryName(destination), StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("Renomeação mantém a pasta; para mover entre pastas use file/move com destino explícito.");
            normalized.Add((source, destination));
        }
        var plan = new List<PlanStep>();
        foreach (var pair in normalized)
        {
            ct.ThrowIfCancellationRequested();
            string hash = await FingerprintAsync(pair.Source, ct);
            int index = plan.Count / 2; string mutationId = "rename" + index, verificationId = "verify" + index;
            plan.Add(new(mutationId, new("file", Op: "rename", Path: pair.Source, To: pair.Destination,
                Scope: "verified", Value: hash), Effect.Mutation, index == 0 ? [] : ["verify" + (index - 1)], 120));
            plan.Add(new(verificationId, new("file", Op: "info", Path: pair.Destination), Effect.Read, [mutationId]));
        }
        return plan.ToArray();
    }
    public static async Task<DesktopActionResult> ExecuteAsync(DesktopActionRequest r, CancellationToken ct)
    {
        if (r.Path is null || r.To is null || r.Value is null || !Path.IsPathFullyQualified(r.Path) || !Path.IsPathFullyQualified(r.To))
            throw new ArgumentException("Caminhos e fingerprint necessários.");
        if (File.Exists(r.To) || Directory.Exists(r.To)) return new("Destino já existe; nada foi sobrescrito.", Success: false, Status: DesktopStatus.Blocked);
        bool started = false;
        try
        {
            // Prevent concurrent writers; allow our own atomic rename while keeping the source content handle.
            await using var source = new FileStream(r.Path, FileMode.Open, FileAccess.Read,
                FileShare.Read | FileShare.Delete, 65536, FileOptions.Asynchronous | FileOptions.SequentialScan);
            string before = Convert.ToHexString(await SHA256.HashDataAsync(source, ct));
            if (!before.Equals(r.Value, StringComparison.Ordinal))
                return new("Origem mudou após planejamento; renomeação bloqueada.", Success: false, Status: DesktopStatus.Blocked);
            ct.ThrowIfCancellationRequested();
            started = true; File.Move(r.Path, r.To, false);
            string after = await FingerprintAsync(r.To, ct);
            return !File.Exists(r.Path) && before == after
                ? new("Arquivo renomeado; origem ausente e SHA-256 do destino conferidos.", Method: "File.Move+SHA256")
                : new("Renomeação ocorreu, mas a verificação não confere. Preserve o estado e não repita automaticamente.", Success: false, Status: DesktopStatus.Partial, Method: "File.Move+SHA256");
        }
        catch (OperationCanceledException) when (started)
        { return new("Interrompida após iniciar renomeação; consulte os dois caminhos antes de repetir.", Success: false, Status: DesktopStatus.Partial); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        { return new(started ? "Resultado da renomeação não confirmado; consulte origem e destino." : "Arquivo em uso/sem acesso ou destino indisponível; não foi renomeado.",
            Success: false, Status: started ? DesktopStatus.Partial : DesktopStatus.Blocked, Method: "File.Move+SHA256"); }
    }
    private static async Task<string> FingerprintAsync(string path, CancellationToken ct)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, ct));
    }
}
