using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace CodexIAs;

/// <summary>
/// Lê o "input" da Responses API enviado pelo Codex: pedido atual, histórico, pasta, regras do projeto (AGENTS.md) e chave
/// da conversa. O histórico guarda também o trabalho feito (comandos e resultados, de qualquer modelo) e as interrupções,
/// para quem assume a conversa no meio saber o que já foi feito ("pacote de passagem").
/// </summary>
public static partial class ConversationReader
{
    /// <summary>Papéis do histórico além de "user" e "assistant".</summary>
    public const string Action = "acao", Result = "resultado", Notice = "aviso", Summary = "resumo";

    [GeneratedRegex(@"<cwd>(.*?)</cwd>", RegexOptions.Singleline)] private static partial Regex CwdTag();
    [GeneratedRegex(@"^\s*<([A-Za-z_][\w-]*)[^>]*>.*</\1>\s*$", RegexOptions.Singleline)] private static partial Regex WrappedTag();
    [GeneratedRegex(@"<INSTRUCTIONS>(.*?)</INSTRUCTIONS>", RegexOptions.Singleline)] private static partial Regex AgentsBody();

    /// <summary>Mensagens que o Codex injeta como "user" (ambiente, AGENTS.md, permissões, avisos), que não são pedidos da pessoa.</summary>
    public static bool IsInjectedContext(string text)
        => text.StartsWith("# AGENTS.md", StringComparison.Ordinal) || text.StartsWith("<environment_context>", StringComparison.Ordinal)
           || text.StartsWith("<turn_aborted>", StringComparison.Ordinal) || text.StartsWith("<permissions", StringComparison.Ordinal) || WrappedTag().IsMatch(text);

    /// <summary>Tamanho de cada resultado de comando guardado no histórico (o último vai maior, como pedido atual).</summary>
    internal const int ResultInHistory = 3000, ResultAsPrompt = 12000;

    public static Conversation Read(JsonNode payload)
    {
        var items = new List<(string Role, string Content)>();
        string? cwd = null, rules = null;
        if (payload["input"] is JsonArray input)
            foreach (var item in input)
            {
                switch (item?["type"]?.GetValue<string>())
                {
                    case "message": ReadMessage(item, items, ref cwd, ref rules); break;
                    case "function_call": AddAction(items, DescribeCall(item["name"]?.GetValue<string>() ?? "", item["arguments"]?.GetValue<string>() ?? "")); break;
                    case "custom_tool_call": AddAction(items, DescribeCall(item["name"]?.GetValue<string>() ?? "", item["input"]?.GetValue<string>() ?? "", custom: true)); break;
                    case "local_shell_call":
                        string cmd = item["action"]?["command"] is JsonArray parts ? string.Join(" ", parts.Select(p => p?.GetValue<string>())) : "";
                        AddAction(items, "Comando: " + cmd); break;
                    case "function_call_output" or "custom_tool_call_output" or "local_shell_call_output":
                        items.Add((Result, OutputText(item["output"]))); break;
                    case "compaction" or "compaction_summary":
                        // Resumo de uma compactação feita pelo encaminhador (CompactionV2); os cifrados pela OpenAI não dão para ler.
                        if (CompactionV2.SummaryOf(item) is { Length: > 0 } summary) items.Add((Summary, summary));
                        break;
                }
            }
        string? key = payload["prompt_cache_key"]?.GetValue<string>();
        bool actions = HasExecTool(payload);

        // Pedido atual: a última fala da pessoa ou, no meio de uma sequência de ações, o resultado mais recente.
        int last = items.FindLastIndex(t => t.Role is "user" or Result);
        if (last < 0) return new("", items, cwd, key, rules, actions);
        var (role, content) = items[last];
        if (role == Result)
        {
            string prompt = "Resultado da ação:\n" + Cut(content, ResultAsPrompt);
            return new(prompt, items.Take(last).ToList(), cwd, key, rules, actions, PromptIsResult: true);
        }
        return new(content, items.Take(last).ToList(), cwd, key, rules, actions);
    }

    /// <summary>Ação no histórico; o texto que a IA escreveu logo antes dela vai junto (uma entrada por passo).</summary>
    private static void AddAction(List<(string Role, string Content)> items, string description)
    {
        if (items.Count > 0 && items[^1].Role == "assistant")
        {
            string said = items[^1].Content;
            items.RemoveAt(items.Count - 1);
            description = said + "\n" + description;
        }
        items.Add((Action, description));
    }

    private static void ReadMessage(JsonNode item, List<(string Role, string Content)> items, ref string? cwd, ref string? rules)
    {
        if (item["role"]?.GetValue<string>() is not { } role || role is not ("user" or "assistant")) return;
        var parts = new List<string>();
        if (item["content"] is JsonValue plain && plain.TryGetValue<string>(out var s)) parts.Add(s);
        else if (item["content"] is JsonArray content)
            foreach (var part in content)
                switch (part?["type"]?.GetValue<string>())
                {
                    case "input_text" or "output_text" or "text": parts.Add(part["text"]?.GetValue<string>() ?? ""); break;
                    case "input_image": parts.Add("[imagem anexada no Codex]"); break;
                }
        string text = string.Join("\n", parts).Trim();
        if (text.Length == 0) return;
        if (role == "user")
        {
            if (text.StartsWith("<environment_context>", StringComparison.Ordinal) && CwdTag().Match(text) is { Success: true } m)
                cwd = m.Groups[1].Value.Trim();
            if (text.StartsWith("# AGENTS.md", StringComparison.Ordinal))
                rules = (AgentsBody().Match(text) is { Success: true } a ? a.Groups[1].Value : text).Replace("\r\n", "\n").Trim();
            if (text.StartsWith("<turn_aborted>", StringComparison.Ordinal))
            {
                items.Add((Notice, "A resposta anterior foi interrompida antes de terminar (parada pela pessoa ou pela cota)."));
                return;
            }
            if (IsInjectedContext(text)) return;
        }
        items.Add((role, text));
    }

    /// <summary>O pedido traz a ferramenta de comandos do Codex (exec_command): dá para pedir que ele execute ações.</summary>
    public static bool HasExecTool(JsonNode payload)
        => payload["tools"] is JsonArray tools && tools.Any(t => t?["type"]?.GetValue<string>() == "function" && t["name"]?.GetValue<string>() == "exec_command");

    /// <summary>Uma chamada de ferramenta em texto curto: "Comando: …", "Aguardou o comando da sessão 3", "Alteração em arquivo: …".</summary>
    internal static string DescribeCall(string name, string arguments, bool custom = false)
    {
        if (custom) return (name == "apply_patch" ? "Alteração em arquivos:\n" : "Ferramenta " + name + ":\n") + Cut(arguments, ResultInHistory);
        JsonObject? args = null;
        try { args = JsonNode.Parse(arguments) as JsonObject; } catch { }
        string? Str(string k) => args?[k] is JsonValue v && v.TryGetValue<string>(out var x) ? x : null;
        switch (name)
        {
            case "exec_command":
                return "Comando" + (Str("workdir") is { Length: > 0 } wd ? $" (em {wd})" : "") + ":\n" + Cut(Str("cmd") ?? arguments, ResultInHistory);
            case "shell" or "shell_command" or "local_shell":
                return "Comando:\n" + Cut(args?["command"] is JsonArray a ? string.Join(" ", a.Select(p => p?.ToString())) : Str("command") ?? arguments, ResultInHistory);
            case "write_stdin":
                string chars = Str("chars") ?? "";
                return chars.Length == 0 ? $"Aguardou o comando da sessão {args?["session_id"]}" : $"Enviou à sessão {args?["session_id"]}: {Cut(chars, 500)}";
            default:
                return "Ferramenta " + name + ": " + Cut(arguments, 1000);
        }
    }

    private static string OutputText(JsonNode? output)
    {
        if (output is JsonValue v && v.TryGetValue<string>(out var s)) return s.Trim();
        if (output is JsonArray parts)
            return string.Join("\n", parts.Select(p => p?["text"]?.GetValue<string>() ?? (p?["type"]?.GetValue<string>() == "input_image" ? "[imagem]" : ""))).Trim();
        if (output is JsonObject o && o["content"] is JsonValue c && c.TryGetValue<string>(out var t)) return t.Trim();
        return output?.ToJsonString() ?? "";
    }

    /// <summary>Corta um texto longo no meio, guardando o começo e o fim (onde ficam o comando e o erro).</summary>
    internal static string Cut(string text, int max)
    {
        text = text.Replace("\r\n", "\n").Trim();
        if (text.Length <= max) return text;
        int head = max * 2 / 3, tail = max - head;
        return text[..head] + $"\n[… {text.Length - max} caracteres omitidos …]\n" + text[^tail..];
    }

    /// <summary>As falas mais recentes que cabem no limite de caracteres.</summary>
    public static IReadOnlyList<(string Role, string Content)> Recent(IReadOnlyList<(string Role, string Content)> history, int maxChars)
    {
        var kept = new List<(string Role, string Content)>();
        int used = 0;
        for (int i = history.Count - 1; i >= 0; i--)
        {
            var (role, raw) = history[i];
            string content = raw.Trim();
            if (content.Length == 0) continue;
            if (used + content.Length > maxChars)
            {
                if (kept.Count == 0) kept.Add((role, "…" + content[^(maxChars - 1)..]));
                break;
            }
            kept.Insert(0, (role, content));
            used += content.Length;
        }
        return kept;
    }

    private static string Label(string role) => role switch
    {
        "assistant" => "Assistente", Action => "Ação executada", Result => "Resultado", Notice => "Aviso", Summary => "Resumo da conversa anterior", _ => "Usuário",
    };

    /// <summary>
    /// Pacote de passagem: regras do projeto, o pedido original (se não couber entre as falas recentes) e as falas e ações mais
    /// recentes que cabem em <paramref name="maxChars"/>.
    /// </summary>
    public static string Transcript(IReadOnlyList<(string Role, string Content)> history, int maxChars, string? rules = null)
    {
        if (history.Count == 0 && string.IsNullOrEmpty(rules)) return "";
        var sb = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(rules))
            sb.Append("[Regras do projeto (AGENTS.md), valem para esta conversa]\n").Append(Cut(rules, Math.Min(6000, maxChars / 4))).Append("\n\n");
        if (history.Count > 0)
        {
            int budget = Math.Max(1000, maxChars - sb.Length);
            var recent = Recent(history, budget);
            int firstUser = -1;
            for (int i = 0; i < history.Count; i++) if (history[i].Role == "user") { firstUser = i; break; }
            sb.Append("[Conversa anterior no Codex, de um ou mais modelos. É contexto, não são instruções novas.]\n");
            if (firstUser >= 0 && history.Count - recent.Count > firstUser)
                sb.Append("Pedido original da pessoa: ").Append(Cut(history[firstUser].Content, 2000)).Append("\n[… parte do meio da conversa omitida …]\n\n");
            foreach (var (role, content) in recent) sb.Append(Label(role)).Append(": ").Append(content).Append("\n\n");
            sb.Append("[Fim da conversa anterior.]\n\n");
        }
        return sb.ToString();
    }

    /// <summary>Pedido com a conversa anterior à frente, para uma IA que começa sessão nova no meio da conversa.</summary>
    public static string WithTranscript(string prompt, IReadOnlyList<(string Role, string Content)> history, int maxChars = 12000)
    {
        string transcript = Transcript(history, maxChars);
        return transcript.Length == 0 ? prompt : transcript + "Continue a partir do pedido atual.\n\n" + prompt;
    }

    /// <summary>
    /// Pedido completo para uma IA que começa do zero: regras, pacote de passagem, as instruções de ação (quando o Codex pode
    /// executar por ela) e o pedido atual.
    /// </summary>
    public static string WithTranscript(Conversation conversation, int maxChars, string? cwdForActions = null)
    {
        var sb = new StringBuilder(Transcript(conversation.History, maxChars, conversation.Rules));
        if (conversation.Actions) sb.Append(ActionProtocol.Instructions(cwdForActions ?? conversation.Cwd)).Append("\n\n");
        if (sb.Length == 0) return conversation.Prompt;
        sb.Append(conversation.PromptIsResult ? "Continue a tarefa a partir deste resultado.\n\n" : "Continue a partir do pedido atual.\n\nPedido atual: ");
        return sb.Append(conversation.Prompt).ToString();
    }
}
