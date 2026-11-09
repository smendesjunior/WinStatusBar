using System.Text.Json;
using WinStatusBar.Core.Ai;

namespace WinStatusBar.ExecutorExpansion;

/// <summary>Intercept before DesktopRuntime.SkillAsync; null means delegate unchanged to the existing runtime.</summary>
public sealed class ExtensionRouter
{
    private readonly WindowsQueryCatalog windowsQueries = new();
    private readonly ClipboardExpansion clipboard = new();
    private readonly Dictionary<string, Func<DesktopActionRequest, CancellationToken, Task<DesktopActionResult>>> operations = new(StringComparer.Ordinal);
    public ExtensionRouter(INetworkBackend? network = null, nint owner = 0, SettingsRegistry? settings = null)
    {
        var mappings = new NetworkMappings(network ?? new WindowsNetworkBackend(), owner);
        foreach (string op in new[] { "network_list", "network_map", "network_reconnect", "network_disconnect", "network_access" })
            Register(op, (r, ct) => { ct.ThrowIfCancellationRequested(); return Task.FromResult(mappings.Execute(r)); });
        Register("network_diagnose", (r, ct) => NetworkMappings.DiagnoseAsync(r.Path ?? "", ct));
        Register("revit_discover", RevitConnection.ExecuteAsync);
        Register("revit_request", RevitConnection.ExecuteAsync);
        settings ??= new SettingsRegistry([
            new ExplorerPreference("explorer.file_extensions", "HideFileExt", "visible", 0, "hidden", 1),
            new ExplorerPreference("explorer.hidden_files", "Hidden", "visible", 1, "hidden", 2),
            new ActivePowerPlan(), new AudioEndpointSetting(false), new AudioEndpointSetting(true)
        ]);
        Register("setting_read", settings.ExecuteAsync);
        Register("setting_apply", settings.ExecuteAsync);
        Register("apps_discover", async (r, ct) =>
        {
            var report = await ApplicationDiscovery.ReadAsync(r.Paths, ct);
            if (r.Scope == "all") return new(JsonSerializer.Serialize(report), Method: "registry+StartApps/Appx");
            var matches = report.Applications.Where(a => r.Name is null || a.Name.Contains(r.Name, StringComparison.OrdinalIgnoreCase)).ToArray();
            return new(JsonSerializer.Serialize(new { Applications = matches.Take(40), TotalMatches = matches.Length,
                Omitted = Math.Max(0, matches.Length - 40), Dependencies = report.Dependencies.Count,
                report.Gaps, Hint = "Use name para filtrar; scope=all retorna inventário completo somente quando necessário." }), Method: "registry+StartApps/Appx");
        });
        Register("dynamo_inspect", async (r, ct) => new(JsonSerializer.Serialize(
            await DynamoGraphInspection.ReadAsync(r.Path ?? "", ct)), Method: "Dynamo/JSON/read-only"));
        Register("capabilities", (r, _) => Task.FromResult(Capabilities(r, false)));
        Register("settings_catalog", (r, _) => Task.FromResult(Capabilities(r, true)));
    }
    public void Register(string operation, Func<DesktopActionRequest, CancellationToken, Task<DesktopActionResult>> handler)
    {
        if (!operations.TryAdd(operation, handler)) throw new ArgumentException("Operação já registrada: " + operation);
    }
    public async Task<DesktopActionResult?> TryExecuteAsync(DesktopActionRequest r, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (r.Action == "file" && r.Op == "rename" && r.Scope == "verified") return await VerifiedFileRename.ExecuteAsync(r, ct);
        if (r.Action is "clipboard_get" or "clipboard_set") return await clipboard.ExecuteAsync(r, ct);
        if (r.Action is "run" or "powershell") return await StructuredCommands.ExecuteAsync(r, ct);
        if (OfficeExpansion.Handles(r)) return await Wrap(OfficeExpansion.ExecuteAsync(r, ct));
        if (await windowsQueries.TryExecuteAsync(r, ct) is { } query) return query;
        return r.Action == "system" && r.Op is not null && operations.TryGetValue(r.Op, out var handler)
            ? await Wrap(handler(r, ct)) : null;
    }
    private static async Task<DesktopActionResult?> Wrap(Task<DesktopActionResult> work) => await work.ConfigureAwait(false);
    private static DesktopActionResult Capabilities(DesktopActionRequest r, bool windowsOnly)
    {
        var source = CapabilityCatalog.All.Where(x => !windowsOnly || x.Domain == "Windows");
        if (r.Scope == "all") return new(JsonSerializer.Serialize(source), Method: "capability-catalog");
        if (r.Name is null)
            return new(JsonSerializer.Serialize(source.GroupBy(x => x.Domain).Select(g => new { Domain = g.Key,
                Implemented = g.Count(x => x.State == Implementation.Implemented), RequiresAdapter = g.Count(x => x.State == Implementation.RequiresAdapter),
                Hint = "Use name=domínio ou trecho do id para consultar capacidades; ausência de receita preserva executor geral." })), Method: "capability-catalog/summary");
        var matches = source.Where(x => x.Domain.Equals(r.Name, StringComparison.OrdinalIgnoreCase) ||
            x.Id.Contains(r.Name, StringComparison.OrdinalIgnoreCase)).ToArray();
        return new(JsonSerializer.Serialize(new { Capabilities = matches.Take(40), TotalMatches = matches.Length,
            Omitted = Math.Max(0, matches.Length - 40) }), Method: "capability-catalog/filtered");
    }
}

public static class DeterministicRecipes
{
    public static PlanStep[] MapNetwork(string drive, string remote, bool persistent, bool credentials)
    {
        drive = NetworkMappings.Drive(drive); remote = NetworkMappings.Remote(remote);
        return [
            new("inventory", new("system", Op: "network_list"), Effect.Read, []),
            new("connect", new("system", Op: "network_map", Name: drive, Path: remote,
                Scope: persistent ? "persistent" : "session", Format: credentials ? "prompt" : null),
                Effect.Mutation, ["inventory"], 120),
            new("verify", new("system", Op: "network_list"), Effect.Read, ["connect"])
        ];
    }
    public static PlanStep[] WordJustify(string document, string scope = "selection", string? find = null)
    {
        if (scope is not ("selection" or "paragraph" or "document" or "find") ||
            string.IsNullOrWhiteSpace(document) || scope == "find" && string.IsNullOrEmpty(find))
            throw new ArgumentException("Documento e escopo explícitos necessários.");
        return [
            new("state", new("office", Application: "word", Op: "state", Target: document), Effect.Read, []),
            new("format", new("office", Application: "word", Op: "format", Target: document,
                Scope: scope, Find: find, Align: "justify"), Effect.Mutation, ["state"]),
            new("verify", new("office", Application: "word", Op: "state", Target: document), Effect.Read, ["format"])
        ];
    }
    public static PlanStep[] ApplySetting(string id, string value) => [
        new("before", new("system", Op: "setting_read", Name: id), Effect.Read, []),
        new("apply", new("system", Op: "setting_apply", Name: id, Value: value), Effect.Mutation, ["before"]),
        new("after", new("system", Op: "setting_read", Name: id), Effect.Read, ["apply"])
    ];
}
