namespace WinStatusBar.Core.Ai;

/// <summary>
/// Conectores (servidores MCP da conta claude.ai, de plugins ou locais) usados pelo Claude Code no chat da barra.
/// No modo não interativo (-p) o Claude nega ferramentas sem permissão prévia; a barra lê esses bloqueios,
/// pede autorização à pessoa e repete o pedido com o conector liberado (--allowedTools no nível do servidor).
/// </summary>
public static class ConnectorPermissions
{
    /// <summary>Regra de permissão do servidor: "mcp__claude_ai_Google_Drive__list_recent_files" → "mcp__claude_ai_Google_Drive".</summary>
    public static string? ServerRule(string toolName)
    {
        if (!toolName.StartsWith("mcp__", StringComparison.Ordinal)) return null;
        int end = toolName.IndexOf("__", 5, StringComparison.Ordinal);
        return end < 0 ? toolName : toolName[..end];
    }

    /// <summary>Regra a partir do nome do servidor no Claude: "claude.ai Google Drive" → "mcp__claude_ai_Google_Drive".</summary>
    public static string RuleForServer(string serverName)
        => "mcp__" + System.Text.RegularExpressions.Regex.Replace(serverName.Trim(), "[^A-Za-z0-9_-]", "_");

    /// <summary>Nome legível: "mcp__claude_ai_Google_Drive" → "Google Drive"; "mcp__plugin_pdf-viewer_pdf" → "pdf-viewer · pdf".</summary>
    public static string DisplayName(string rule)
    {
        string name = rule.StartsWith("mcp__", StringComparison.Ordinal) ? rule[5..] : rule;
        int tool = name.IndexOf("__", StringComparison.Ordinal);
        if (tool >= 0) name = name[..tool];
        if (name.StartsWith("claude_ai_", StringComparison.Ordinal)) return name["claude_ai_".Length..].Replace('_', ' ');
        if (name.StartsWith("plugin_", StringComparison.Ordinal))
        {
            var parts = name["plugin_".Length..].Split('_', 2);
            return parts.Length == 2 ? parts[0] + " · " + parts[1] : parts[0];
        }
        return name.Replace('_', ' ');
    }

    /// <summary>Ferramenta curta para o pedido de autorização: "list_recent_files".</summary>
    public static string ToolName(string toolName)
    {
        int end = toolName.LastIndexOf("__", StringComparison.Ordinal);
        return end > 4 ? toolName[(end + 2)..] : toolName;
    }

    public enum ServerState { Connected, NeedsSignIn, Failed, Unknown }

    /// <summary>Um servidor da lista do Claude ("claude mcp list"): nome do servidor, nome para exibir, origem, endereço ou comando e estado.</summary>
    public sealed record Server(string Name, string Display, string Origin, string Detail, ServerState State);

    /// <summary>
    /// Lê as linhas de "claude mcp list" ("claude.ai Google Drive: https://… - ✓ Connected"). Linhas fora desse formato
    /// (cabeçalhos, avisos) ficam de fora.
    /// </summary>
    public static IReadOnlyList<Server> ParseMcpList(string output)
    {
        var list = new List<Server>();
        foreach (string raw in output.Split('\n'))
        {
            string line = raw.Trim();
            int status = line.LastIndexOf(" - ", StringComparison.Ordinal), colon = line.IndexOf(": ", StringComparison.Ordinal);
            if (status < 0 || colon <= 0 || colon > status) continue;
            string name = line[..colon].Trim(), detail = line[(colon + 2)..status].Trim(), state = line[(status + 3)..];
            var parsed = state.Contains("Connected", StringComparison.OrdinalIgnoreCase) ? ServerState.Connected
                : state.Contains("auth", StringComparison.OrdinalIgnoreCase) ? ServerState.NeedsSignIn
                : state.Contains("fail", StringComparison.OrdinalIgnoreCase) || state.Contains("error", StringComparison.OrdinalIgnoreCase) ? ServerState.Failed
                : ServerState.Unknown;
            string origin = name.StartsWith("claude.ai ", StringComparison.Ordinal) ? "Conta claude.ai" : name.StartsWith("plugin:", StringComparison.Ordinal) ? "Plugin" : "Servidor MCP";
            list.Add(new(name, DisplayName(RuleForServer(name)), origin, detail, parsed));
        }
        return list;
    }

    /// <summary>Bloqueios de conectores agrupados por servidor (outras ferramentas, como Bash, ficam com o modo Editar).</summary>
    public static IReadOnlyList<(string Rule, string Name, IReadOnlyList<string> Tools)> Group(IEnumerable<string> blocked)
        => blocked.Select(t => (Tool: t, Rule: ServerRule(t))).Where(x => x.Rule is not null)
            .GroupBy(x => x.Rule!, StringComparer.Ordinal)
            .Select(g => (g.Key, DisplayName(g.Key), (IReadOnlyList<string>)g.Select(x => ToolName(x.Tool)).Distinct().ToList()))
            .ToList();
}
