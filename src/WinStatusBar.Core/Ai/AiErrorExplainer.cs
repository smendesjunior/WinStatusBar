using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace WinStatusBar.Core.Ai;

/// <summary>
/// Erro de uma IA explicado para a pessoa.
/// <paramref name="Short"/>: rótulo curto para a linha "Parou após N s · …".
/// <paramref name="Message"/>: o que aconteceu e o que fazer, em linguagem comum.
/// <paramref name="Technical"/>: texto original do provedor (nulo quando a mensagem já é uma frase da própria barra).
/// </summary>
public sealed record AiErrorExplanation(string Short, string Message, string? Technical);

/// <summary>
/// Traduz os erros de todas as IAs da barra (HTTP, mensagens em inglês dos provedores, exceções de rede, JSON de erro
/// do Codex/Claude/Ollama) numa explicação: causa provável e o que fazer. O texto técnico original fica à parte.
/// </summary>
public static class AiErrorExplainer
{
    private const RegexOptions Ci = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;

    /// <summary>Formas do nome da IA nas frases: "O Claude", "o Claude", "do Claude", "no Claude", "com o Claude".</summary>
    public sealed record Names(string Subject, string Lower, string Of, string In, string With);

    public static AiErrorExplanation Explain(AgentPrompt.Target target, string? raw, string? model = null)
    {
        string text = (raw ?? string.Empty).Trim();
        var who = Who(target);
        string m = string.IsNullOrWhiteSpace(model) || model == "app" ? LocalizedText.T("escolhido") : model;
        if (text.Length == 0)
            return new(LocalizedText.T("sem detalhe"), LocalizedText.F("{0} falhou sem informar o motivo. Tente de novo; se repetir, escolha outro modelo ou outra IA.", new object?[] { who.Subject }), null);
        if (text == "Interrompido" || text == LocalizedText.T("Interrompido"))
            return new(LocalizedText.T("interrompido"), LocalizedText.T("A resposta foi interrompida."), null);

        string inner = InnerMessage(text);
        string all = inner == text ? text : text + " " + inner;
        int? status = HttpStatus(all);
        string? technical = Technical(text);
        AiErrorExplanation E(string shortLabel, string message) => new(LocalizedText.T(shortLabel), message, technical);

        // 1) Recursos que o modelo não tem.
        if (Has(all, @"multimodal|does not support (images?|vision)|image input|images? (are|is) not supported|vision is not supported|unsupported content type.*image|image_url.*not supported"))
            return E("modelo só lê texto", LocalizedText.F("O modelo {0} só entende texto, e a mensagem levou uma imagem (a captura da janela do botão Contexto ou um anexo). Desligue o Contexto ou remova a imagem, ou escolha um modelo que lê imagens.", new object?[] { m }));
        if (Has(all, @"does not support thinking|thinking is not supported|think value .* not supported"))
            return E("modelo sem raciocínio", LocalizedText.F("O modelo {0} não tem modo de raciocínio. No seletor de modelo, use o nível padrão ou \"Sem raciocínio\".", new object?[] { m }));
        if (Has(all, @"does not support tools|tools? (are|is) not supported"))
            return E("modelo sem ferramentas", LocalizedText.F("O modelo {0} não sabe usar ferramentas (ações no PC, conectores). Desligue o lápis Editar ou escolha outro modelo.", new object?[] { m }));

        // 2) Modelo inexistente ou não instalado.
        if (Has(all, @"model[^.]{0,80}(not found|does not exist|not_found|is not available|unknown)|model_not_found|try pulling it|no such model|invalid model|unknown model"))
            return target == AgentPrompt.Target.Local
                ? E("modelo não instalado", LocalizedText.F("O modelo {0} não está instalado na IA local. Escolha outro na lista de modelos ou baixe-o no terminal com: ollama pull {0}", new object?[] { m }))
                : E("modelo indisponível", LocalizedText.F("O modelo {0} não existe {1} ou a sua conta não tem acesso a ele. Escolha outro modelo no seletor.", new object?[] { m, who.In }));

        // 3) Tamanho do pedido.
        if (status == 413 || Has(all, @"context (length|window)|context_length|maximum context|prompt is too long|too many tokens|input is too long|request too large|payload too large|request entity too large|token limit|exceeds the (maximum|limit)"))
            return E("conversa longa demais", LocalizedText.F("A conversa ficou grande demais para o modelo {0} (mensagens, anexos e contexto da tela somados). Comece uma nova conversa, remova anexos grandes ou escolha um modelo com mais contexto.", new object?[] { m }));

        // 4) Placa de vídeo errando cálculos (erro de CUDA que não é falta de memória): costuma ser overclock instável.
        //    Caso real (09/11/2026): RTX 4050 com o núcleo a +300 MHz quebrava o Ollama em pedidos grandes.
        if (Has(all, @"illegal memory access|misaligned address|illegal instruction|illegal address|unspecified launch failure|device-side assert"))
            return E("falha da placa de vídeo", LocalizedText.F("A placa de vídeo errou um cálculo ao rodar o modelo {0} (erro de CUDA). Isso costuma acontecer com a placa em overclock: reduza ou desligue o overclock e tente de novo. Sem overclock, atualize o driver da placa de vídeo.", new object?[] { m }));
        // O motor do Ollama caiu no meio da resposta e está reabrindo o modelo (falha da placa ou falta de memória).
        if (target == AgentPrompt.Target.Local && Has(all, @"health resp"))
            return E("IA local reiniciando", LocalizedText.T("A IA local (Ollama) parou no meio da resposta e está reabrindo o modelo. Tente de novo em alguns segundos. Se repetir, a causa costuma ser a placa de vídeo: com overclock, reduza ou desligue; sem overclock, escolha um modelo menor."));

        // 5) Memória do PC (IA local).
        if (Has(all, @"out of memory|requires more system memory|unable to allocate|insufficient memory|not enough memory|cudaMalloc|CUDA error|llama runner process has terminated|failed to load model"))
            return E("falta memória", LocalizedText.F("O PC não conseguiu carregar o modelo {0}: falta memória (RAM ou placa de vídeo). Feche programas pesados ou escolha um modelo menor.", new object?[] { m }));

        // 6) Cota, créditos e limites de uso.
        if (status == 402 || Has(all, @"credit balance|insufficient_quota|exceeded your current quota|quota exceeded|billing|payment required|usage limit|(?<!rate )limit reached|hit your (usage )?limit|out of credits|purchase more credits|plan limit"))
            return E("cota esgotada", LocalizedText.F("A cota ou os créditos da sua conta {0} acabaram.", new object?[] { who.In }) + ResetHint(all)
                + " " + LocalizedText.T("Veja quando renova no menu Cotas da barra e, até lá, use outra IA."));
        if (status == 429 || Has(all, @"rate.?limit|too many requests|resource_exhausted|requests per minute"))
            return E("muitos pedidos", LocalizedText.F("Foram muitos pedidos em pouco tempo e {0} pediu para esperar. Aguarde um minuto e envie de novo.", new object?[] { who.Lower }));

        // 7) Login, chave e permissão.
        if (status == 401 || Has(all, @"unauthori[sz]ed|invalid.{0,12}api.?key|api key not valid|api_key_invalid|x-api-key|authentication|not logged in|please run /login|login required|log ?in again|token (has )?expired|expired token|invalid_grant|invalid token|refresh token"))
            return E("login recusado", LocalizedText.F("{0} recusou o login ou a chave da sua conta (expirou ou é inválida).", new object?[] { who.Subject }) + " " + LoginAction(target));
        if (status == 403 || Has(all, @"forbidden|permission.?denied|not allowed to|does not have access|access denied|not authori[sz]ed|unsupported (country|region)|region not supported"))
            return E("sem permissão", LocalizedText.F("A sua conta não tem permissão para usar isso {0} (modelo, recurso ou região). Escolha outro modelo ou confira o plano da conta.", new object?[] { who.In }));

        // 8) Regras de segurança do provedor.
        if (Has(all, @"content[ _-]?(policy|filter|management)|safety (system|filter|settings)|finish_?reason.{0,5}safety|blocked by|flagged|violat\w* (our|the) (usage )?polic"))
            return E("bloqueado pela IA", LocalizedText.F("{0} bloqueou o pedido ou a resposta pelas próprias regras de segurança. Reformule o pedido com outras palavras.", new object?[] { who.Subject }));

        // 9) Serviço fora do ar ou sobrecarregado.
        if (status is >= 500 and <= 599 || Has(all, @"overloaded|internal server error|service unavailable|bad gateway|gateway time-?out|server_error|server error|temporarily unavailable|upstream"))
            return E("serviço instável", LocalizedText.F("O serviço {0} está com problema ou sobrecarregado agora; não é falha do seu PC. Tente de novo em alguns minutos ou use outra IA.", new object?[] { who.Of }));

        // 10) Conexão e tempo.
        if (target == AgentPrompt.Target.Local && Has(all, @"refused|no connection could be made|econnrefused|11434|nenhum servidor local respondeu|connection reset"))
            return E("IA local fechada", LocalizedText.T("O servidor da IA local (Ollama, LM Studio ou outro) não está aberto, então ninguém respondeu no PC. Abra o Ollama pelo menu Iniciar e envie de novo."));
        if (Has(all, @"no such host|name or service not known|network is unreachable|\bssl\b|certificate|could not be established|connection (was )?(reset|closed|aborted|refused)|actively refused|no connection could be made|econnreset|econnrefused|enotfound|eai_again|fetch failed|stream disconnected|socket|proxy|connection attempt failed|error while copying content|httprequestexception"))
            return E("sem conexão", LocalizedText.F("Não foi possível falar {0} pela rede (internet, VPN, proxy ou firewall). Confira a conexão e tente de novo.", new object?[] { who.With }));
        if (status == 408 || Has(all, @"timed? ?out|timeout|tempo limite|tempo esgotado|deadline exceeded"))
            return E("tempo esgotado", LocalizedText.F("{0} demorou demais e a resposta não chegou a tempo. Tente de novo; se repetir, encurte o pedido ou escolha um modelo mais rápido.", new object?[] { who.Subject }));

        // 11) Respostas vazias e programas ausentes.
        if (Has(all, @"^sem resposta$|terminou sem resposta|^o qira não respondeu\.?$|no response|empty response"))
            return E("sem resposta", LocalizedText.F("{0} terminou sem mandar nenhum texto. Tente de novo; se repetir, escolha outro modelo ou outra IA.", new object?[] { who.Subject }));
        if (Has(all, @"não encontrado neste pc|is not recognized as|enoent|cannot find the file|não foi possível iniciar"))
            return E("programa ausente", LocalizedText.F("O programa {0} não foi encontrado ou não abriu neste PC.", new object?[] { who.Of }) + " " + InstallAction(target));

        // 12) Pedido recusado sem motivo reconhecido.
        if (status == 400 || Has(all, @"bad request|invalid_request|invalid request|invalid_argument|malformed"))
            return E("pedido recusado", LocalizedText.F("{0} recusou o pedido como inválido: algo no envio (anexo, contexto da tela ou opção do modelo {1}) não é aceito. Tente sem anexos e sem o Contexto, ou escolha outro modelo.", new object?[] { who.Subject, m }));
        if (status == 404)
            return E("endereço não encontrado", LocalizedText.F("O endereço do serviço {0} não foi encontrado. Confira o endereço e o modelo em Configurações › IA.", new object?[] { who.Of }));

        // 13) Mensagens da própria barra, já explicadas (frase em português, sem códigos), no idioma do app.
        if (technical is null) { string shown = LocalizedText.T(text); return new(ShortOf(shown), shown, null); }
        return E("erro desconhecido", LocalizedText.F("{0} devolveu um erro que a barra ainda não reconhece. Tente de novo; se repetir, escolha outro modelo ou outra IA. O texto original está abaixo.", new object?[] { who.Subject }));
    }

    public static Names Who(AgentPrompt.Target target)
    {
        (string name, bool feminine) = target switch
        {
            AgentPrompt.Target.Claude => ("Claude", false),
            AgentPrompt.Target.Codex => ("Codex", false),
            AgentPrompt.Target.Local => (LocalizedText.T("IA local"), true),
            AgentPrompt.Target.Api => ("API", true),
            AgentPrompt.Target.Gemini => ("Gemini", false),
            AgentPrompt.Target.Copilot => ("Copilot", false),
            AgentPrompt.Target.Qira => ("Qira", false),
            _ => ("IA", true),
        };
        string F(string pattern) => LocalizedText.F(pattern, new object?[] { name });
        return feminine
            ? new(F("A {0}"), F("a {0}"), F("da {0}"), F("na {0}"), F("com a {0}"))
            : new(F("O {0}"), F("o {0}"), F("do {0}"), F("no {0}"), F("com o {0}"));
    }

    private static string LoginAction(AgentPrompt.Target target) => target switch
    {
        AgentPrompt.Target.Claude => LocalizedText.T("Entre de novo no Claude Code: abra o app Claude (ou rode claude no terminal e use /login)."),
        AgentPrompt.Target.Codex => LocalizedText.T("Entre de novo na sua conta no app Codex (ou rode codex login no terminal)."),
        AgentPrompt.Target.Gemini => LocalizedText.T("Entre de novo com a conta Google ou confira a chave em Configurações › IA › Gemini."),
        AgentPrompt.Target.Copilot => LocalizedText.T("Entre de novo em Configurações › IA › Microsoft Copilot."),
        AgentPrompt.Target.Qira => LocalizedText.T("Abra o app do Qira e entre de novo na conta Lenovo."),
        AgentPrompt.Target.Local => LocalizedText.T("O servidor local pediu uma chave; confira a configuração dele."),
        _ => LocalizedText.T("Confira ou troque a chave em Configurações › IA."),
    };

    private static string InstallAction(AgentPrompt.Target target) => target switch
    {
        AgentPrompt.Target.Claude => LocalizedText.T("Instale o Claude Code (ou o app Claude para desktop) e tente de novo, ou escolha outra IA."),
        AgentPrompt.Target.Codex => LocalizedText.T("Instale o app Codex e tente de novo, ou escolha outra IA."),
        AgentPrompt.Target.Gemini => LocalizedText.T("Instale o Gemini CLI em Configurações › IA › Gemini, ou use a conta Google."),
        _ => LocalizedText.T("Reinstale o programa ou escolha outra IA."),
    };

    /// <summary>Quando o provedor informa a hora de liberação (Claude: "…limit reached|1731596400"), devolve " Libera às HH:mm.".</summary>
    private static string ResetHint(string text)
    {
        var epoch = Regex.Match(text, @"limit reached\|(\d{10})", Ci);
        if (!epoch.Success || !long.TryParse(epoch.Groups[1].Value, out long seconds)) return string.Empty;
        var at = DateTimeOffset.FromUnixTimeSeconds(seconds).ToLocalTime();
        string when = at.Date == DateTime.Today ? at.ToString("HH:mm", CultureInfo.CurrentCulture) : at.ToString("dd/MM HH:mm", CultureInfo.CurrentCulture);
        return " " + LocalizedText.F("Libera às {0}.", new object?[] { when });
    }

    /// <summary>Texto técnico a mostrar à parte; nulo quando o original já é uma frase da própria barra em português.</summary>
    private static string? Technical(string text)
    {
        // Técnico: códigos, JSON, nomes de exceção ou frases em inglês (do provedor ou do sistema).
        bool technical = Has(text, @"^http \d|\{|exception|error|status|\b[45]\d\d\b|[a-z]+_[a-z]+|\b(the|not|is|was|an|of|to|failed|cannot|could|unable|invalid|please)\b")
            || !Has(text, @"[ãõçáéíóúâêô]|\b(não|para|de|do|da|em|tente|escolha|configure|aguarde|conclua|confira|entre)\b");
        if (!technical) return null;
        return text.Length > 600 ? text[..600] + "…" : text;
    }

    /// <summary>Mensagem interna de um erro em JSON: {"error":"…"}, {"error":{"message":"…"}}, {"message":"…"} ou "API Error: 529 {…}".</summary>
    internal static string InnerMessage(string text)
    {
        int brace = text.IndexOf('{');
        if (brace < 0) return text;
        try
        {
            var n = JsonNode.Parse(text[brace..]);
            string? found = n?["error"] switch
            {
                JsonObject o => (o["message"] as JsonValue)?.ToString() ?? (o["type"] as JsonValue)?.ToString(),
                JsonValue v when v.TryGetValue<string>(out var s) => s,
                _ => null,
            } ?? (n?["message"] is JsonValue mv && mv.TryGetValue<string>(out var msg) ? msg : null);
            string? info = n?["codexErrorInfo"] is { } ci ? ci.ToJsonString() : null;
            return found is null ? text : info is null ? found : found + " " + info;
        }
        catch { return text; }
    }

    private static int? HttpStatus(string text)
    {
        var match = Regex.Match(text, @"(?:HTTP|status(?: code)?|API Error)\s*:?\s*(\d{3})\b", Ci);
        return match.Success && int.TryParse(match.Groups[1].Value, out int code) ? code : null;
    }

    private static string ShortOf(string text)
    {
        string first = text.Split(['.', ':', '\n'], 2)[0].Trim();
        return first.Length <= 48 ? first : first[..47] + "…";
    }

    private static bool Has(string text, string pattern) => Regex.IsMatch(text, pattern, Ci);
}
