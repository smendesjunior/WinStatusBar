using System.Text.Json;

namespace WinStatusBar.ExecutorExpansion;

public sealed record DynamoNode(string Id, string Type, string? PythonEngine, string? Function);
public sealed record DynamoGraphReport(string? Version, int NodeCount, int ConnectorCount,
    IReadOnlyList<DynamoNode> Nodes, string[] PackageDependencies, bool HasBindings, string[] Diagnostics);
public static class DynamoGraphInspection
{
    public static async Task<DynamoGraphReport> ReadAsync(string path, CancellationToken ct)
    {
        if (!Path.IsPathFullyQualified(path) || !path.EndsWith(".dyn", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Informe arquivo .dyn absoluto.");
        var file = new FileInfo(path);
        if (!file.Exists || file.Length > 20 * 1024 * 1024) throw new ArgumentException("Arquivo ausente ou maior que 20 MiB.");
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
        return Inspect(document.RootElement);
    }
    public static DynamoGraphReport Inspect(JsonElement graph)
    {
        var diagnostics = new List<string>(); var nodes = new List<DynamoNode>(); var ids = new HashSet<string>();
        if (graph.TryGetProperty("Nodes", out var array) && array.ValueKind == JsonValueKind.Array)
            foreach (var n in array.EnumerateArray())
            {
                string id = Text(n, "Id") ?? ""; string type = Text(n, "ConcreteType") ?? Text(n, "NodeType") ?? "unknown";
                if (id.Length == 0 || !ids.Add(id)) diagnostics.Add("Nó sem ID ou ID duplicado: " + id);
                string? engine = Text(n, "Engine");
                if (type.Contains("Python", StringComparison.OrdinalIgnoreCase) && engine is null)
                    diagnostics.Add("Engine Python não declarada no nó " + id + "; não inferir compatibilidade.");
                nodes.Add(new(id, type, engine, Text(n, "FunctionSignature")));
            }
        else diagnostics.Add("Nodes ausente: formato antigo/XML ou esquema desconhecido requer adaptador.");
        int connectors = graph.TryGetProperty("Connectors", out var c) && c.ValueKind == JsonValueKind.Array ? c.GetArrayLength() : 0;
        string[] packages = graph.TryGetProperty("NodeLibraryDependencies", out var dependencies) && dependencies.ValueKind == JsonValueKind.Array
            ? dependencies.EnumerateArray().Select(x => x.GetRawText()).ToArray() : [];
        bool bindings = graph.TryGetProperty("Bindings", out var b) && b.ValueKind == JsonValueKind.Array && b.GetArrayLength() > 0;
        if (bindings) diagnostics.Add("Bindings existentes: preservar ao executar para evitar duplicação de elementos.");
        diagnostics.Add("Nós ausentes e packages instalados não foram verificados em runtime. Nenhum grafo executado.");
        return new(graph.TryGetProperty("View", out var view) && view.TryGetProperty("Dynamo", out var dyn)
            ? Text(dyn, "Version") : Text(graph, "Version"), nodes.Count, connectors, nodes, packages, bindings, diagnostics.ToArray());
    }
    private static string? Text(JsonElement element, string key) =>
        element.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
