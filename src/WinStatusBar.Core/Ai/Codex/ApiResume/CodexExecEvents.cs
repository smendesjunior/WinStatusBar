using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace WinStatusBar.Core.Ai.Codex.ApiResume;

public enum ExecEventKind { ThreadStarted, TurnStarted, Status, AgentMessage, Warning, TurnCompleted, TurnFailed, Error, Other }

/// <summary>Um evento do codex exec --json traduzido: tipo, texto curto (ou a mensagem/erro) e uso do turno.</summary>
public sealed record ExecEvent(ExecEventKind Kind, string? Text = null, TokenUsage? Usage = null);

/// <summary>Traduz os eventos --json do codex exec (gravados em events.jsonl) em linhas curtas em português.</summary>
public static class CodexExecEvents
{
    public const int CommandMax = 90;

    private static readonly Regex Wrapper = new(
        @"^\s*(?:""[^""]*?(?:powershell|pwsh|cmd)(?:\.exe)?""|\S*?(?:powershell|pwsh|cmd)(?:\.exe)?)\s+(?:-\S+\s+)*?(?:-Command|-c|/c)\s+",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>Nunca lança: JSON inválido vira Other.</summary>
    public static ExecEvent Parse(string line)
    {
        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return new(ExecEventKind.Other);
            switch (CodexParsers.Str(root, "type"))
            {
                case "thread.started": return new(ExecEventKind.ThreadStarted, CodexParsers.Str(root, "thread_id"));
                case "turn.started": return new(ExecEventKind.TurnStarted);
                case "turn.completed":
                    return new(ExecEventKind.TurnCompleted, null,
                        root.TryGetProperty("usage", out var usage) ? CodexParsers.Tokens(usage) ?? new TokenUsage(0, 0, 0, 0) : null);
                case "turn.failed":
                    return new(ExecEventKind.TurnFailed, root.TryGetProperty("error", out var err) ? CodexParsers.Str(err, "message") ?? err.ToString() : null);
                case "error":
                    return Error(CodexParsers.Str(root, "message"));
                case "item.started":
                case "item.updated":
                case "item.completed":
                    return root.TryGetProperty("item", out var item) ? Item(item, CodexParsers.Str(root, "type") == "item.completed") : new(ExecEventKind.Other);
                default: return new(ExecEventKind.Other);
            }
        }
        catch (Exception) { return new(ExecEventKind.Other); }
    }

    private static ExecEvent Error(string? message) =>
        IsRecordedModelWarning(message) ? new(ExecEventKind.Warning, message) : new(ExecEventKind.Error, message);

    private static ExecEvent Item(JsonElement item, bool completed)
    {
        switch (CodexParsers.Str(item, "type"))
        {
            case "command_execution":
                return new(ExecEventKind.Status, LocalizedText.F("Executando: {0}", CommandText(CodexParsers.Str(item, "command") ?? "")));
            case "reasoning": return new(ExecEventKind.Status, LocalizedText.T("Pensando…"));
            case "file_change":
                int n = item.TryGetProperty("changes", out var changes) && changes.ValueKind == JsonValueKind.Array ? changes.GetArrayLength() : 0;
                return new(ExecEventKind.Status, LocalizedText.F("Editando {0} arquivo(s)…", Math.Max(1, n)));
            case "web_search": return new(ExecEventKind.Status, LocalizedText.T("Pesquisando na web…"));
            case "mcp_tool_call":
                return new(ExecEventKind.Status, LocalizedText.F("Usando {0}…", CodexParsers.Str(item, "server") ?? CodexParsers.Str(item, "tool") ?? "MCP"));
            case "agent_message":
                return completed ? new(ExecEventKind.AgentMessage, CodexParsers.Str(item, "text")) : new(ExecEventKind.Other);
            case "error": return Error(CodexParsers.Str(item, "message"));
            default: return new(ExecEventKind.Other);
        }
    }

    /// <summary>Eventos das linhas completas a partir de <paramref name="offset"/>; uma última linha sem '\n' fica para a próxima leitura.</summary>
    public static IReadOnlyList<ExecEvent> ReadNew(string path, long offset, out long newOffset)
    {
        newOffset = offset;
        var events = new List<ExecEvent>();
        try
        {
            if (!File.Exists(path)) return events;
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (offset > fs.Length) offset = 0;
            fs.Position = offset;
            var bytes = new byte[fs.Length - offset];
            fs.ReadExactly(bytes);
            int start = 0;
            for (int i = 0; i < bytes.Length; i++)
            {
                if (bytes[i] != (byte)'\n') continue;
                string line = Encoding.UTF8.GetString(bytes, start, i - start).TrimEnd('\r');
                if (line.Length > 0) events.Add(Parse(line));
                start = i + 1;
            }
            newOffset = offset + start;
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        return events;
    }

    public static bool IsWriterConflict(string? stderr) =>
        stderr?.Contains("active writer", StringComparison.OrdinalIgnoreCase) == true;

    public static bool IsRecordedModelWarning(string? message) =>
        message?.Contains("This session was recorded with model", StringComparison.OrdinalIgnoreCase) == true;

    /// <summary>O comando sem o invólucro powershell/pwsh/cmd e as aspas, em até 90 caracteres.</summary>
    internal static string CommandText(string command)
    {
        string text = Wrapper.Replace(command.Trim(), "", 1).Trim();
        if (text.Length >= 2 && (text[0] == '"' || text[0] == '\'') && text[^1] == text[0]) text = text[1..^1].Trim();
        text = Regex.Replace(text, @"\s+", " ");
        return text.Length > CommandMax ? text[..(CommandMax - 1)] + "…" : text;
    }
}
