using WinStatusBar.Core.Ai;
using Xunit;
using T = WinStatusBar.Core.Ai.AgentPrompt.Target;

namespace WinStatusBar.Tests;

public class AiErrorExplainerTests
{
    [Fact]
    public void OllamaImageToTextOnlyModel_SaysTheModelOnlyReadsText()
    {
        // Caso real (08/11/2026): captura da janela enviada ao phi4-mini; antes a barra mostrava só "HTTP 400".
        var e = AiErrorExplainer.Explain(T.Local, "HTTP 400: Multimodal data provided, but model does not support multimodal requests.", "phi4-mini:latest");
        Assert.Equal("modelo só lê texto", e.Short);
        Assert.Contains("phi4-mini:latest", e.Message);
        Assert.Contains("Contexto", e.Message);
        Assert.Contains("Multimodal data provided", e.Technical);
    }

    [Fact]
    public void BareHttp400_IsStillExplained()
    {
        var e = AiErrorExplainer.Explain(T.Local, "HTTP 400", "phi4-mini:latest");
        Assert.Equal("pedido recusado", e.Short);
        Assert.StartsWith("A IA local recusou o pedido", e.Message);
        Assert.Equal("HTTP 400", e.Technical);
    }

    [Theory]
    [InlineData(T.Api, "HTTP 401: Incorrect API key provided: sk-abc.", "login recusado", "Configurações › IAs e contas")]
    [InlineData(T.Claude, "Invalid API key · Please run /login", "login recusado", "/login")]
    [InlineData(T.Codex, """{"message":"unexpected status 401 Unauthorized: Missing bearer","codexErrorInfo":"unauthorized"}""", "login recusado", "codex login")]
    [InlineData(T.Api, "HTTP 429: Rate limit reached for gpt-5 in organization", "muitos pedidos", "Aguarde um minuto")]
    [InlineData(T.Claude, "API Error: 529 {\"type\":\"error\",\"error\":{\"type\":\"overloaded_error\",\"message\":\"Overloaded\"}}", "serviço instável", "do Claude")]
    [InlineData(T.Api, "HTTP 402: Insufficient credits", "cota esgotada", "Cotas")]
    [InlineData(T.Claude, "Credit balance is too low", "cota esgotada", "no Claude")]
    [InlineData(T.Codex, "You've hit your usage limit. Upgrade to Pro or try again at 3:05 PM.", "cota esgotada", "Cotas")]
    [InlineData(T.Gemini, "[API Error: got status: 429 Too Many Requests. RESOURCE_EXHAUSTED]", "muitos pedidos", "o Gemini pediu")]
    [InlineData(T.Local, "HTTP 404: model \"llama9\" not found, try pulling it first", "modelo não instalado", "ollama pull")]
    [InlineData(T.Local, "No connection could be made because the target machine actively refused it. (127.0.0.1:11434)", "IA local fechada", "Abra o Ollama")]
    [InlineData(T.Api, "No such host is known. (api.exemplo.com:443)", "sem conexão", "com a API")]
    [InlineData(T.Api, "HTTP 400: This model's maximum context length is 8192 tokens.", "conversa longa demais", "nova conversa")]
    [InlineData(T.Local, "HTTP 500: model requires more system memory (12.0 GiB) than is available (8.1 GiB)", "falta memória", "modelo menor")]
    [InlineData(T.Local, "HTTP 500: CUDA error: out of memory", "falta memória", "modelo menor")]
    // Casos reais (09/11/2026): placa de vídeo com overclock instável errando cálculos do Ollama, e o motor dele reiniciando.
    [InlineData(T.Local, "HTTP 500: an error was encountered while running the model: CUDA error\nCUDA error: an illegal memory access was encountered", "falha da placa de vídeo", "overclock")]
    [InlineData(T.Local, "HTTP 500: {\"error\":\"an error was encountered while running the model: CUDA error: misaligned address\"}", "falha da placa de vídeo", "overclock")]
    [InlineData(T.Local, "HTTP 500: health resp: Get \"http://127.0.0.1:55478/health\": dial tcp 127.0.0.1:55478: connectex: No connection could be made because the target machine actively refused it.", "IA local reiniciando", "Tente de novo em alguns segundos")]
    [InlineData(T.Local, "HTTP 400: \"phi4-mini:latest\" does not support thinking", "modelo sem raciocínio", "Sem raciocínio")]
    [InlineData(T.Claude, "Claude Code não encontrado neste PC", "programa ausente", "Instale o Claude Code")]
    [InlineData(T.Local, "Sem resposta", "sem resposta", "terminou sem mandar")]
    [InlineData(T.Copilot, "Tempo limite do Copilot. A resposta não foi confirmada como concluída.", "tempo esgotado", "O Copilot demorou")]
    public void KnownProviderErrors_AreExplained(T target, string raw, string expectedShort, string expectedInMessage)
    {
        var e = AiErrorExplainer.Explain(target, raw, "modelo-x");
        Assert.Equal(expectedShort, e.Short);
        Assert.Contains(expectedInMessage, e.Message);
    }

    [Fact]
    public void ClaudeUsageLimit_ShowsWhenItResets()
    {
        long epoch = DateTimeOffset.Now.AddHours(2).ToUnixTimeSeconds();
        var e = AiErrorExplainer.Explain(T.Claude, $"Claude AI usage limit reached|{epoch}");
        Assert.Equal("cota esgotada", e.Short);
        Assert.Contains("Libera às", e.Message);
    }

    [Fact]
    public void BarOwnPortugueseMessage_PassesThroughWithoutTechnicalLine()
    {
        const string own = "O Copilot já está respondendo outra pergunta. Aguarde.";
        var e = AiErrorExplainer.Explain(T.Copilot, own);
        Assert.Equal(own, e.Message);
        Assert.Null(e.Technical);
        Assert.Equal("O Copilot já está respondendo outra pergunta", e.Short);
    }

    [Fact]
    public void UnknownEnglishError_GetsGenericExplanationAndKeepsOriginal()
    {
        const string raw = "Object reference not set to an instance of an object.";
        var e = AiErrorExplainer.Explain(T.Qira, "Falha ao conversar com o Qira: " + raw);
        Assert.Equal("erro desconhecido", e.Short);
        Assert.StartsWith("O Qira devolveu um erro", e.Message);
        Assert.Contains(raw, e.Technical);
    }

    [Fact]
    public void EmptyAndInterrupted_AreReadable()
    {
        Assert.StartsWith("O Codex falhou sem informar o motivo", AiErrorExplainer.Explain(T.Codex, "").Message);
        Assert.Equal("A resposta foi interrompida.", AiErrorExplainer.Explain(T.Claude, "Interrompido").Message);
    }

    [Theory]
    [InlineData("""{"error":"Multimodal data provided"}""", "Multimodal data provided")]
    [InlineData("""{"error":{"message":"Incorrect API key","type":"invalid_request_error"}}""", "Incorrect API key")]
    [InlineData("""{"type":"error","error":{"type":"overloaded_error","message":"Overloaded"}}""", "Overloaded")]
    [InlineData("Bad Gateway", "Bad Gateway")]
    [InlineData("<html><body>502</body></html>", null)]
    [InlineData("", null)]
    public void ProviderErrorBody_IsReadForOllamaOpenAiAndAnthropic(string body, string? expected)
        => Assert.Equal(expected, AgentPrompt.ProviderErrorMessage(body));

    // ---- Fluxo das APIs compatíveis com OpenAI e da Anthropic (linha a linha)
    [Theory]
    // Trecho normal.
    [InlineData("openai", """data: {"choices":[{"index":0,"delta":{"content":"Olá"}}]}""", "Olá")]
    // Primeiro bloco do Azure (filtros de conteúdo) e bloco final só com o uso: "choices" vazio. Antes interrompia a resposta.
    [InlineData("openai", """data: {"choices":[],"prompt_filter_results":[{"prompt_index":0}]}""", null)]
    [InlineData("openai", """data: {"choices":[],"usage":{"prompt_tokens":9,"completion_tokens":12}}""", null)]
    // Papel sem texto, conteúdo nulo, fim e linhas que não são dados.
    [InlineData("openai", """data: {"choices":[{"delta":{"role":"assistant"}}]}""", null)]
    [InlineData("openai", """data: {"choices":[{"delta":{"content":null},"finish_reason":"stop"}]}""", null)]
    [InlineData("openai", "data: [DONE]", null)]
    [InlineData("openai", ": keep-alive", null)]
    [InlineData("openai", "data: 123", null)]
    // Anthropic.
    [InlineData("anthropic", """data: {"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"Oi"}}""", "Oi")]
    [InlineData("anthropic", """data: {"type":"content_block_delta","index":0,"delta":{"type":"input_json_delta","partial_json":"{"}}""", null)]
    [InlineData("anthropic", """data: {"type":"message_start","message":{"id":"m1"}}""", null)]
    [InlineData("anthropic", "event: content_block_delta", null)]
    public void ApiStreamPiece_ReadsTheTextOfEachLine(string kind, string line, string? expected)
        => Assert.Equal(expected, AgentPrompt.ApiStreamPiece(kind, line));

    [Theory]
    [InlineData("openai", """data: {"error":{"message":"Rate limit reached","type":"rate_limit_error"}}""", "Rate limit reached")]
    // "error" como texto simples: antes a leitura de err["message"] lançava outra exceção e a mensagem se perdia.
    [InlineData("openai", """data: {"error":"model overloaded"}""", "model overloaded")]
    [InlineData("openai", """data: {"error":{"code":503}}""", """{"code":503}""")]
    [InlineData("anthropic", """data: {"type":"error","error":{"type":"overloaded_error","message":"Overloaded"}}""", "Overloaded")]
    public void ApiStreamPiece_ThrowsTheProviderMessage(string kind, string line, string expected)
        => Assert.Equal(expected, Assert.Throws<InvalidOperationException>(() => AgentPrompt.ApiStreamPiece(kind, line)).Message);

    // ---- "Interrompido" em qualquer idioma do app
    [Fact]
    public void Interrupted_IsRecognizedAsLiteralAndTranslated()
    {
        Assert.True(AiErrorExplainer.IsInterrupted("Interrompido"));
        Assert.False(AiErrorExplainer.IsInterrupted(null));
        Assert.False(AiErrorExplainer.IsInterrupted("HTTP 500"));
        using (TranslationScope.With(new Dictionary<string, string> { ["Interrompido"] = "Stopped", ["A resposta foi interrompida."] = "The answer was stopped." }))
        {
            // O Codex, o Gemini CLI e as sessões web devolvem o literal; o Claude e as APIs, o texto traduzido.
            Assert.True(AiErrorExplainer.IsInterrupted("Interrompido"));
            Assert.True(AiErrorExplainer.IsInterrupted("Stopped"));
            Assert.False(AiErrorExplainer.IsInterrupted("stopped by policy"));
            Assert.Equal("The answer was stopped.", AiErrorExplainer.Explain(T.Codex, "Interrompido").Message);
            Assert.Equal("The answer was stopped.", AiErrorExplainer.Explain(T.Claude, "Stopped").Message);
        }
        Assert.False(AiErrorExplainer.IsInterrupted("Stopped"));
    }

    // ---- Erro da consulta de cotas na tela
    [Theory]
    [InlineData(T.Codex, "Tempo esgotado ao consultar o Codex")]
    [InlineData(T.Codex, "Codex não encontrado")]
    [InlineData(T.Codex, "Limites indisponíveis para esta conta")]
    [InlineData(T.Claude, "O Claude limitou as consultas. A próxima tentativa será automática.")]
    [InlineData(T.Claude, "Consulta de cotas indisponível (HTTP 503). Nova tentativa automática em 5 minutos.")]
    [InlineData(T.Claude, "Não foi possível renovar o login do Claude. Entre novamente pelo Claude Code. (HTTP 400)")]
    [InlineData(T.Claude, "Reset aceito; não foi possível consultar o novo saldo. Atualize as cotas antes de tentar novamente.")]
    public void QuotaError_KeepsWhatTheBarItselfWrote(T target, string own)
        => Assert.Equal(own, AiErrorExplainer.QuotaError(target, own));

    [Fact]
    public void QuotaError_ExplainsWhatTheProviderReturned()
    {
        // JSON do "initialize" do app-server, mensagem em inglês do provedor e exceção do .NET: nada disso aparece cru.
        string login = AiErrorExplainer.QuotaError(T.Codex, """{"code":-32600,"message":"Not logged in. Please run codex login."}""");
        Assert.StartsWith("O Codex recusou o login", login);
        Assert.DoesNotContain("{", login);
        Assert.StartsWith("A cota ou os créditos da sua conta no Codex acabaram.", AiErrorExplainer.QuotaError(T.Codex, "You've hit your usage limit."));
        // Erro não reconhecido: a explicação promete "o texto original está abaixo", então ele vai junto.
        string unknown = AiErrorExplainer.QuotaError(T.Codex, "The pipe has been ended.");
        Assert.StartsWith("O Codex devolveu um erro que a barra ainda não reconhece", unknown);
        Assert.EndsWith("\nThe pipe has been ended.", unknown);
    }

    [Fact]
    public void QuotaError_KeepsTheBarSentencesInOtherLanguagesToo()
    {
        // Em outro idioma a frase da barra chega já traduzida (parece texto do provedor); ainda assim passa sem mudar.
        using var _ = TranslationScope.With(new Dictionary<string, string>
        {
            ["Tempo esgotado ao consultar o Codex"] = "Timed out while querying Codex",
            ["Consulta de cotas indisponível (HTTP {0}). Nova tentativa automática em 5 minutos."] = "Quota query unavailable (HTTP {0}). Automatic retry in 5 minutes.",
        });
        Assert.Equal("Timed out while querying Codex", AiErrorExplainer.QuotaError(T.Codex, "Timed out while querying Codex"));
        Assert.Equal("Quota query unavailable (HTTP 503). Automatic retry in 5 minutes.",
            AiErrorExplainer.QuotaError(T.Claude, "Quota query unavailable (HTTP 503). Automatic retry in 5 minutes."));
    }

    [Fact]
    public void QuotaError_KnowsEverySentenceTheQuotaClientsWrite()
    {
        // Frase nova nos clientes de cota precisa entrar em OwnQuotaSentences (senão, em outro idioma, viraria conselho de chat).
        string claude = RepoSource.Read("src", "WinStatusBar.Core", "Ai", "Claude", "ClaudeUsageClient.cs");
        var written = System.Text.RegularExpressions.Regex.Matches(claude, @"new\(null, global::WinStatusBar\.Core\.LocalizedText\.[TF]\(""([^""]+)""")
            .Select(m => m.Groups[1].Value).ToList();
        Assert.True(written.Count >= 9, $"só achei {written.Count} frases no ClaudeUsageClient");
        written.Add(System.Text.RegularExpressions.Regex.Match(claude, @"const string relogin = ""([^""]+)""").Groups[1].Value);
        string codex = RepoSource.Read("src", "WinStatusBar.Core", "Ai", "Codex", "CodexAppServerClient.cs");
        var codexWritten = System.Text.RegularExpressions.Regex.Matches(codex, @"return new\((?:null|quota), (?:null|info), [^;]*?LocalizedText\.T\(""([^""]+)""\)")
            .Select(m => m.Groups[1].Value).ToList();
        Assert.Equal(4, codexWritten.Count);
        written.AddRange(codexWritten);
        written.Add(System.Text.RegularExpressions.Regex.Match(RepoSource.Read("src", "WinStatusBar.Core", "Ai", "Codex", "CodexProvider.cs"),
            @"LastError = global::WinStatusBar\.Core\.LocalizedText\.T\(""([^""]+)""\)").Groups[1].Value);
        Assert.All(written, sentence => Assert.Contains(sentence, AiErrorExplainer.OwnQuotaSentences));
    }

    [Fact]
    public void QuotaError_IsUsedWhereTheQuotaErrorIsShown()
    {
        // O que fica guardado (CodexProvider.LastError, ClaudeCodeProvider.LastQuotaError) continua o original.
        Assert.Contains("AiErrorExplainer.QuotaError(isCodex ? AgentPrompt.Target.Codex : AgentPrompt.Target.Claude, rawError)",
            RepoSource.Read("src", "WinStatusBar", "Views", "QuotaDetailsView.cs"));
        string vm = RepoSource.Read("src", "WinStatusBar", "ViewModels", "BarViewModel.cs");
        Assert.Contains("AiErrorExplainer.QuotaError(global::WinStatusBar.Core.Ai.AgentPrompt.Target.Codex, codexError)", vm);
        Assert.Contains("AiErrorExplainer.QuotaError(global::WinStatusBar.Core.Ai.AgentPrompt.Target.Claude, claudeError)", vm);
    }
}
