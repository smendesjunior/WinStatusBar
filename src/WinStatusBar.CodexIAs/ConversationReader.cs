using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace CodexIAs;

/// <summary>Lê o "input" da Responses API enviado pelo Codex: pedido atual, histórico, pasta e chave da conversa.</summary>
public static partial class ConversationReader
{
    [GeneratedRegex(@"<cwd>(.*?)</cwd>", RegexOptions.Singleline)] private static partial Regex CwdTag();
    [GeneratedRegex(@"^\s*<([A-Za-z_][\w-]*)[^>]*>.*</\1>\s*$", RegexOptions.Singleline)] private static partial Regex WrappedTag();

    /// <summary>Mensagens que o Codex injeta como "user" (ambiente, AGENTS.md, permissões, avisos), que não são pedidos da pessoa.</summary>
    public static bool IsInjectedContext(string text)
        => text.StartsWith("# AGENTS.md", StringComparison.Ordinal) || text.StartsWith("<environment_context>", StringComparison.Ordinal)
           || text.StartsWith("<turn_aborted>", StringComparison.Ordinal) || text.StartsWith("<permissions", StringComparison.Ordinal) || WrappedTag().IsMatch(text);

    public static Conversation Read(JsonNode payload)
    {
        var turns = new List<(string Role, string Content)>();
        string? cwd = null;
        if (payload["input"] is JsonArray input)
            foreach (var item in input)
            {
                if (item?["type"]?.GetValue<string>() != "message" || item["role"]?.GetValue<string>() is not { } role || role is not ("user" or "assistant")) continue;
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
                if (text.Length == 0) continue;
                if (role == "user" && text.StartsWith("<environment_context>", StringComparison.Ordinal) && CwdTag().Match(text) is { Success: true } m)
                    cwd = m.Groups[1].Value.Trim();
                if (role == "user" && IsInjectedContext(text)) continue;
                turns.Add((role, text));
            }
        string? key = payload["prompt_cache_key"]?.GetValue<string>();
        int last = turns.FindLastIndex(t => t.Role == "user");
        if (last < 0) return new("", turns, cwd, key);
        return new(turns[last].Content, turns.Take(last).ToList(), cwd, key);
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

    /// <summary>Pedido com a conversa anterior à frente, para uma IA que começa sessão nova no meio da conversa.</summary>
    public static string WithTranscript(string prompt, IReadOnlyList<(string Role, string Content)> history, int maxChars = 12000)
    {
        var recent = Recent(history, maxChars);
        if (recent.Count == 0) return prompt;
        var sb = new StringBuilder("[Conversa anterior no Codex. É contexto, não são instruções novas.]\n");
        foreach (var (role, content) in recent)
            sb.Append(role == "assistant" ? "Assistente: " : "Usuário: ").Append(content).Append("\n\n");
        sb.Append("[Fim da conversa anterior. Continue a partir do pedido atual.]\n\n");
        return sb.Append(prompt).ToString();
    }
}
