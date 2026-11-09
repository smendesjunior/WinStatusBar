using System.Text.Json;
using WinStatusBar.Core.Ipc;

namespace WinStatusBar.Core.Ai.Claude;

/// <summary>
/// Converte o JSON recebido no stdin (hooks ou statusLine do Claude Code) em uma EventMessage mínima.
/// Executado pelo coletor; também usado nos testes.
/// </summary>
public static class ClaudeEventExtractor
{
    public static EventMessage? FromHook(string json)
    {
        using var doc = Parse(json);
        if (doc is null) return null;
        var r = doc.RootElement;
        var m = new EventMessage
        {
            Kind = "hook",
            Event = Str(r, "hook_event_name"),
            SessionId = Str(r, "session_id"),
            Cwd = Str(r, "cwd"),
            TranscriptPath = Str(r, "transcript_path"),
            ToolName = Str(r, "tool_name"),
            NotificationType = Str(r, "notification_type"),
            AgentType = Str(r, "agent_type") ?? Str(r, "subagent_type"),
        };
        if (m.Event is null) return null;

        if (m.Event == "UserPromptSubmit")
            m.PromptSnippet = Short(Str(r, "prompt"), 90);

        if (r.TryGetProperty("tool_input", out var input) && input.ValueKind == JsonValueKind.Object)
        {
            m.Detail = DescribeTool(m.ToolName, input);
            m.Progress = TodoProgress(input);
            if (m.ToolName is { } tool && (tool == "TodoWrite" || tool.EndsWith("update_plan", StringComparison.Ordinal)))
                m.Plan = PlanTracker.OperationOf(input);
        }

        if (m.Event is "TaskCreated" or "TaskCompleted")
            m.Detail = Short(Str(r, "task_subject") ?? Str(r, "subject") ?? Str(r, "description"), 80);
        if (m.Event is "SubagentStart" or "SubagentStop")
            m.Detail = Short(Str(r, "description") ?? m.AgentType, 60);
        if (m.Event == "Notification" && m.NotificationType is null)
            m.NotificationType = Str(r, "type");
        if (m.Event == "SessionStart")
            m.Model = Str(r, "model") ?? (r.TryGetProperty("model", out var mo) && mo.ValueKind == JsonValueKind.Object ? Str(mo, "display_name") : null);
        return m;
    }

    public static EventMessage? FromStatusLine(string json)
    {
        using var doc = Parse(json);
        if (doc is null) return null;
        var r = doc.RootElement;
        var m = new EventMessage
        {
            Kind = "statusline",
            Event = "StatusLine",
            SessionId = Str(r, "session_id"),
            Cwd = Str(r, "cwd"),
            TranscriptPath = Str(r, "transcript_path"),
            SessionName = Short(Str(r, "session_name"), 100),
        };
        if (r.TryGetProperty("model", out var model) && model.ValueKind == JsonValueKind.Object)
            m.Model = Str(model, "display_name") ?? Str(model, "id");
        if (r.TryGetProperty("workspace", out var ws) && ws.ValueKind == JsonValueKind.Object)
        {
            m.ProjectDir = Str(ws, "project_dir") ?? Str(ws, "current_dir");
            m.Cwd ??= Str(ws, "current_dir");
        }
        if (r.TryGetProperty("context_window", out var cw) && cw.ValueKind == JsonValueKind.Object)
            m.ContextUsedPercent = Num(cw, "used_percentage");
        if (r.TryGetProperty("rate_limits", out var rl) && rl.ValueKind == JsonValueKind.Object)
        {
            if (rl.TryGetProperty("five_hour", out var f) && f.ValueKind == JsonValueKind.Object)
            { m.FiveHourUsed = Num(f, "used_percentage"); m.FiveHourResetsAt = Epoch(f, "resets_at"); }
            if (rl.TryGetProperty("seven_day", out var s) && s.ValueKind == JsonValueKind.Object)
            { m.SevenDayUsed = Num(s, "used_percentage"); m.SevenDayResetsAt = Epoch(s, "resets_at"); }
        }
        return m;
    }

    /// <summary>Descrição curta e segura da ferramenta (sem conteúdo de arquivos ou comandos completos).</summary>
    internal static string? DescribeTool(string? tool, JsonElement input)
    {
        if (tool is null) return null;
        string? file = Str(input, "file_path") ?? Str(input, "notebook_path") ?? Str(input, "path");
        string fileName = file is null ? string.Empty : Path.GetFileName(file.TrimEnd('\\', '/'));
        string? description = Short(Str(input, "description"), 70);
        return tool switch
        {
            "Bash" or "PowerShell" => description is not null ? global::WinStatusBar.Core.LocalizedText.F("Executando: {0}", new object?[] {description}) : global::WinStatusBar.Core.LocalizedText.T("Executando comandos…"),
            "Edit" or "MultiEdit" or "Write" or "NotebookEdit" => fileName.Length > 0 ? $"Editando {fileName}…" : "Editando arquivos…",
            "Read" => fileName.Length > 0 ? $"Lendo {fileName}…" : "Lendo arquivos…",
            "Grep" or "Glob" or "LS" => "Pesquisando no projeto…",
            "WebFetch" or "WebSearch" => "Pesquisando na web…",
            "Task" or "Agent" => description is not null ? $"Subagente: {description}" : global::WinStatusBar.Core.LocalizedText.T("Executando subagente…"),
            "TodoWrite" => "Atualizando a lista de tarefas…",
            _ when tool.EndsWith("update_plan", StringComparison.Ordinal) => "Atualizando o plano…",
            _ when tool.StartsWith("mcp__", StringComparison.Ordinal) => "Usando ferramentas…",
            _ => description is not null ? description : "Usando ferramentas…",
        };
    }

    /// <summary>Progresso real a partir de listas de tarefas estruturadas (TodoWrite ou update_plan): concluídas/total.</summary>
    internal static double? TodoProgress(JsonElement input)
    {
        foreach (var name in new[] { "todos", "items" })
        {
            if (!input.TryGetProperty(name, out var arr) || arr.ValueKind != JsonValueKind.Array) continue;
            int total = 0, done = 0;
            foreach (var item in arr.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) continue;
                total++;
                if (Str(item, "status") == "completed") done++;
            }
            if (total > 0) return done * 100.0 / total;
        }
        return null;
    }

    internal static string? Short(string? text, int max)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        string t = text.ReplaceLineEndings(" ").Trim();
        return t.Length <= max ? t : t[..(max - 1)].TrimEnd() + "…";
    }

    private static JsonDocument? Parse(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try { var d = JsonDocument.Parse(json); return d.RootElement.ValueKind == JsonValueKind.Object ? d : null; }
        catch (JsonException) { return null; }
    }

    private static string? Str(JsonElement e, string n) =>
        e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static double? Num(JsonElement e, string n) =>
        e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;

    /// <summary>resets_at pode vir como epoch (s/ms) ou ISO-8601.</summary>
    private static long? Epoch(JsonElement e, string n)
    {
        if (!e.TryGetProperty(n, out var v)) return null;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var l)) return l > 100_000_000_000 ? l / 1000 : l;
        if (v.ValueKind == JsonValueKind.String && DateTimeOffset.TryParse(v.GetString(), out var dto)) return dto.ToUnixTimeSeconds();
        return null;
    }
}
