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
    [InlineData(T.Api, "HTTP 401: Incorrect API key provided: sk-abc.", "login recusado", "Configurações › IA")]
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
}
