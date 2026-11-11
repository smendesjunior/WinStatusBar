using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace CodexIAs;

/// <summary>
/// Ações pelo Codex para IAs que só conversam (Gemini e Copilot pela conta, APIs, modelos locais, Claude sem comandos): a IA
/// termina a resposta com um bloco de ação em texto e o encaminhador o entrega ao Codex como chamada da ferramenta de
/// comandos dele (exec_command / write_stdin). O Codex executa com as permissões da conversa, mostra na tela, guarda no
/// histórico e manda o resultado no pedido seguinte, que volta à mesma IA.
/// </summary>
public static partial class ActionProtocol
{
    /// <summary>Ações por mensagem da pessoa: evita que uma IA fique repetindo ações sem fim.</summary>
    public const int MaxSteps = 40;
    public enum Kind { Command, File, Wait }
    public sealed record Request(Kind Kind, string Text, string? Path = null, int Session = 0);

    // Marcadores entre colchetes duplos: os sites (Gemini, Copilot) mostram a resposta já formatada, e um marcador entre
    // "<" e ">" some da página (vira etiqueta de HTML), como no teste de 10/11/2026. "<<<…>>>" ainda vale (APIs e locais).
    [GeneratedRegex(@"(?:\[\[|<<<)[ \t]*(COMANDO|ARQUIVO|AGUARDAR)\b([^\]>\r\n]*)(?:\]\]|>>>)[ \t]*\r?\n?", RegexOptions.IgnoreCase)]
    private static partial Regex Marker();
    [GeneratedRegex(@"(?:\[\[|<<<)[ \t]*FIM[ \t]*(?:\]\]|>>>)", RegexOptions.IgnoreCase)]
    private static partial Regex EndMarker();
    [GeneratedRegex(@"^[ \t]*(`{3,}|~{3,})[^`\n]*$")]
    private static partial Regex FenceOpen();

    /// <summary>Instruções que vão junto do pedido (uma vez por conversa nova com a IA).</summary>
    public static string Instructions(string? cwd) => $$"""
        [Como agir no computador]
        Você está trabalhando dentro do app Codex{{(string.IsNullOrEmpty(cwd) ? "" : ", na pasta " + cwd)}}. Você não acessa o computador diretamente: para ler arquivos, rodar comandos ou gravar arquivos, termine a resposta com UMA ação, num dos formatos abaixo, e pare de escrever. O Codex executa a ação e devolve o resultado na próxima mensagem; aí você decide o passo seguinte. Escreva os marcadores [[...]] exatamente assim, cada um sozinho na linha, e ponha o conteúdo num bloco de código.

        Rodar um comando do PowerShell (Windows):
        [[COMANDO]]
        ```powershell
        Get-Content -LiteralPath 'relatorio.md' -Encoding utf8
        ```
        [[FIM]]

        Gravar um arquivo inteiro (cria ou substitui; caminho relativo à pasta da conversa ou completo):
        [[ARQUIVO relatorio.md]]
        ~~~~
        conteúdo completo do arquivo
        ~~~~
        [[FIM]]

        Esperar um comando que ainda está rodando (use o número da sessão informado no resultado):
        [[AGUARDAR 12]]

        Regras: uma ação por resposta, sempre no fim; nunca invente o resultado de uma ação; leia antes de alterar; para mudar parte de um arquivo, leia o arquivo e grave a versão completa; quando a tarefa terminar, responda só em texto, sem ação. Escreva a explicação no idioma da pessoa.
        """;

    /// <summary>
    /// Texto que pode ser mostrado enquanto a resposta chega: corta no início de um possível marcador ("[[", "&lt;&lt;&lt;" ou o
    /// começo deles no fim do texto). A resposta final sem ação volta inteira.
    /// </summary>
    public static string Visible(string text)
    {
        int at = MinIndex(text.IndexOf("[[", StringComparison.Ordinal), text.IndexOf("<<<", StringComparison.Ordinal));
        if (at >= 0) return text[..at].TrimEnd();
        foreach (var partial in new[] { "<<", "[", "<" })
            if (text.EndsWith(partial, StringComparison.Ordinal)) return text[..^partial.Length];
        return text;
    }

    private static int MinIndex(int a, int b) => a < 0 ? b : b < 0 ? a : Math.Min(a, b);

    /// <summary>Separa a resposta em texto e a primeira ação. Null quando não há ação válida.</summary>
    public static (string Text, Request Action)? Parse(string answer)
    {
        if (Marker().Match(answer) is not { Success: true } m) return null;
        string before = answer[..m.Index].TrimEnd();
        string kind = m.Groups[1].Value.ToUpperInvariant(), arg = m.Groups[2].Value.Trim();
        if (kind == "AGUARDAR")
            return int.TryParse(arg, out int session) && session >= 0 ? (before, new Request(Kind.Wait, "", null, session)) : null;
        string rest = answer[(m.Index + m.Length)..];
        var end = EndMarker().Match(rest);
        string body = (end.Success ? rest[..end.Index] : rest).Replace("\r\n", "\n");
        body = TrimFences(body);
        if (kind == "COMANDO")
        {
            body = body.Trim();
            return body.Length == 0 ? null : (before, new Request(Kind.Command, body));
        }
        if (arg.Length == 0) return null;
        if (body.EndsWith('\n')) body = body[..^1];
        return (before, new Request(Kind.File, body, arg.Trim('"', '\'', '`')));
    }

    /// <summary>Tira o bloco de código (``` ou ~~~, de qualquer tamanho) em volta do conteúdo da ação.</summary>
    private static string TrimFences(string body)
    {
        var lines = body.Split('\n').ToList();
        while (lines.Count > 0 && lines[0].Trim().Length == 0) lines.RemoveAt(0);
        while (lines.Count > 0 && lines[^1].Trim().Length == 0) lines.RemoveAt(lines.Count - 1);
        if (lines.Count >= 2 && FenceOpen().Match(lines[0]) is { Success: true } open)
        {
            string fence = open.Groups[1].Value, close = lines[^1].Trim();
            if (close.Length >= fence.Length && close.All(c => c == fence[0]))
                lines = lines.Skip(1).Take(lines.Count - 2).ToList();
        }
        return string.Join("\n", lines) + (body.EndsWith('\n') ? "\n" : "");
    }

    /// <summary>Ações já feitas desde a última fala da pessoa (o pedido atual conta como continuação).</summary>
    public static int StepsSinceUser(Conversation conversation)
    {
        int steps = 0;
        for (int i = conversation.History.Count - 1; i >= 0 && conversation.History[i].Role != "user"; i--)
            if (conversation.History[i].Role == ConversationReader.Action) steps++;
        return steps;
    }

    private static string Ps(string text) => "'" + text.Replace("'", "''") + "'";

    /// <summary>Comando do PowerShell que grava o arquivo em UTF-8 sem BOM, criando a pasta se faltar.</summary>
    internal static string FileCommand(string path, string content)
    {
        string text = content.Replace("\r\n", "\n").Replace("\n", "\r\n");
        var sb = new StringBuilder();
        // Texto literal (here-string com aspas simples); se o conteúdo quebrar o delimitador, vai em Base64.
        if (!text.Contains("\n'@", StringComparison.Ordinal) && !text.StartsWith("'@", StringComparison.Ordinal))
            sb.Append("$conteudo = @'\r\n").Append(text).Append("\r\n'@\r\n");
        else
            sb.Append("$conteudo = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String(").Append(Ps(Convert.ToBase64String(Encoding.UTF8.GetBytes(text)))).Append("))\r\n");
        sb.Append("$destino = ").Append(Ps(path)).Append("\r\n")
          .Append("if (-not [IO.Path]::IsPathRooted($destino)) { $destino = Join-Path (Get-Location).Path $destino }\r\n")
          .Append("$pasta = Split-Path -Parent $destino\r\n")
          .Append("if ($pasta -and -not (Test-Path -LiteralPath $pasta)) { New-Item -ItemType Directory -Force -Path $pasta | Out-Null }\r\n")
          .Append("[IO.File]::WriteAllText($destino, $conteudo, (New-Object Text.UTF8Encoding $false))\r\n")
          .Append("Write-Output (\"Arquivo gravado: \" + $destino + \" (\" + $conteudo.Length + \" caracteres)\")");
        return sb.ToString();
    }

    /// <summary>Item de chamada de ferramenta da Responses API para o Codex executar.</summary>
    public static JsonObject CallItem(Request action, string? cwd)
    {
        JsonObject args;
        string name;
        if (action.Kind == Kind.Wait)
        {
            name = "write_stdin";
            args = new JsonObject { ["session_id"] = action.Session, ["chars"] = "", ["yield_time_ms"] = 60000 };
        }
        else
        {
            name = "exec_command";
            args = new JsonObject
            {
                ["cmd"] = action.Kind == Kind.File ? FileCommand(action.Path!, action.Text) : action.Text,
                ["shell"] = "powershell.exe",
                ["yield_time_ms"] = 30000,
            };
            if (!string.IsNullOrEmpty(cwd)) args["workdir"] = cwd;
        }
        return new JsonObject
        {
            ["type"] = "function_call", ["id"] = "fc_cdx_" + Guid.NewGuid().ToString("N"), ["call_id"] = "call_cdx_" + Guid.NewGuid().ToString("N"),
            ["name"] = name, ["arguments"] = args.ToJsonString(), ["status"] = "completed",
        };
    }
}
