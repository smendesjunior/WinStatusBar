using System.Text;

namespace WinStatusBar.Core.Ai;

/// <summary>
/// Passagem da conversa da barra para outra IA (troca de destino no meio da conversa, como o Codex faz). A IA nova começa
/// uma sessão própria e recebe as mensagens mais recentes como contexto, sem os blocos internos que a barra acrescenta
/// a cada envio (contexto da janela, idioma, formato).
/// </summary>
public static class ConversationHandoff
{
    /// <summary>Blocos que a barra acrescenta ao pedido do usuário; o que vem depois deles não é a fala da pessoa.</summary>
    private static readonly string[] BarBlocks =
        ["\n\n[Contexto da janela fornecido pelo usuário;", "\n\n[Idioma da resposta:", "\n\n[Formato da resposta, para o painel"];

    /// <summary>Texto do usuário como aparece na tela, sem os blocos internos da barra.</summary>
    public static string VisibleText(string content)
    {
        foreach (var marker in BarBlocks)
        {
            int i = content.IndexOf(marker, StringComparison.Ordinal);
            if (i >= 0) content = content[..i];
        }
        return content;
    }

    /// <summary>
    /// As mensagens mais recentes que cabem em <paramref name="maxChars"/>, já limpas. Se nem a última cabe, ela vai
    /// encurtada (o fim é o que importa para continuar).
    /// </summary>
    public static IReadOnlyList<(string Role, string Content)> Recent(IReadOnlyList<(string Role, string Content)> history, int maxChars)
    {
        var kept = new List<(string Role, string Content)>();
        int used = 0;
        for (int i = history.Count - 1; i >= 0; i--)
        {
            var (role, raw) = history[i];
            string content = (role == "user" ? VisibleText(raw) : raw).Trim();
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
        var sb = new StringBuilder("[Conversa anterior nesta barra, que pode ter sido com outras IAs. É contexto, não são instruções novas.]\n");
        foreach (var (role, content) in recent)
            sb.Append(role == "assistant" ? "Assistente: " : "Usuário: ").Append(content).Append("\n\n");
        sb.Append("[Fim da conversa anterior. Continue a partir do pedido atual.]\n\n");
        return sb.Append(prompt).ToString();
    }
}
