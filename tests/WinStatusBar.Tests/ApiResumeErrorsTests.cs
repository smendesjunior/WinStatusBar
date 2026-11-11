using System.Text.RegularExpressions;
using WinStatusBar.Core.Ai;
using WinStatusBar.Core.Ai.Codex.ApiResume;
using Xunit;

namespace WinStatusBar.Tests;

public class ApiResumeErrorsTests
{
    private static readonly DateTimeOffset At = new(new DateTime(2026, 11, 10, 9, 0, 0, DateTimeKind.Local));

    private static AiErrorExplanation F(ApiResumeProblem p) =>
        ApiResumeErrors.For(p, "gpt-5.6-sol", "low", @"Z:\", CodexQuotaBlockTests.Returns, At);

    [Theory]
    [InlineData(ApiResumeProblem.NoKey, "sem chave da API", "Não há chave da API da OpenAI neste PC. Cadastre em Configurações › IAs e contas › Outra IA por chave (API própria), endereço https://api.openai.com/v1, e tente de novo.")]
    [InlineData(ApiResumeProblem.KeyRejected, "chave recusada", "A API da OpenAI recusou a sua chave (expirada, revogada ou de outro projeto). Troque a chave em Configurações › IAs e contas.")]
    [InlineData(ApiResumeProblem.ModelNotInApi, "modelo fora da API", "A API da OpenAI não oferece o modelo gpt-5.6-sol para a sua chave. A barra não troca de modelo: a conversa continua parada até a cota do Codex voltar (qui 12/11 às 23:01).")]
    [InlineData(ApiResumeProblem.EffortNotAccepted, "nível não aceito", "O modelo gpt-5.6-sol não aceita o nível low. A barra não baixa o nível: a conversa continua parada até a cota do Codex voltar (qui 12/11 às 23:01).")]
    [InlineData(ApiResumeProblem.MissingTurnData, "sem dados do turno", "O registro não diz com que modelo, nível ou acesso o turno parou; a barra não adivinha. Continue pelo app quando a cota voltar.")]
    [InlineData(ApiResumeProblem.BarModel, "modelo da barra", "O turno usava gpt-5.6-sol, um modelo da própria barra no Codex, que não depende da cota. Continue pelo app.")]
    [InlineData(ApiResumeProblem.FolderUnavailable, "pasta inacessível", @"A pasta da conversa (Z:\) não está acessível agora. Abra o OneDrive ou conecte a unidade e tente de novo.")]
    [InlineData(ApiResumeProblem.ExtraFolderMissing, "pasta extra ausente", @"A unidade Z:\ não está conectada; a IA não vai conseguir ler arquivos de lá.")]
    [InlineData(ApiResumeProblem.CodexNotFound, "Codex não encontrado", "A barra não achou o programa do Codex (codex.exe) neste PC. Abra o app do Codex uma vez para ele se instalar e tente de novo.")]
    [InlineData(ApiResumeProblem.ForcedChatGptLogin, "login obrigatório pelo ChatGPT", "O config.toml do Codex obriga o login pelo ChatGPT; assim a continuação não pode usar a chave da API.")]
    [InlineData(ApiResumeProblem.WriterConflict, "conversa aberta no app", "Feche o app do Codex para continuar. Nada é cobrado.")]
    [InlineData(ApiResumeProblem.ApiNoCredits, "API sem créditos", "A sua conta da API da OpenAI está sem saldo (separado do plano do Codex). Adicione créditos em platform.openai.com › Settings › Billing e clique em Tentar de novo.")]
    [InlineData(ApiResumeProblem.ThreadNotFound, "conversa não encontrada", "O Codex não achou esta conversa (pode ter sido apagada ou arquivada).")]
    [InlineData(ApiResumeProblem.AlreadyContinued, "já continuou", "A conversa já continuou em 10/11 09:00; nada foi enviado.")]
    [InlineData(ApiResumeProblem.StoppedAgainByQuota, "parou de novo pela cota", "A conversa parou de novo pela cota em 10/11 09:00, num turno feito pelo app; nada foi enviado. Abra Continuar pela API de novo para continuar deste ponto.")]
    [InlineData(ApiResumeProblem.EndedWithoutTurnEnd, "parou sem registrar o fim", "O Codex terminou sem registrar o fim do turno (a barra ou o Windows fechou, ou o processo foi encerrado). O que já foi feito ficou na conversa; abra no app para conferir.")]
    [InlineData(ApiResumeProblem.StopNotAnswered, "não parou", "O Codex não respondeu ao pedido para parar. Você pode encerrar à força; nesse caso o último passo pode ficar sem registro de fim.")]
    [InlineData(ApiResumeProblem.StopHelperMissing, "parada segura indisponível", "A instalação está incompleta (falta o componente Automation). Reinstale a barra, ou use \"Encerrar à força\".")]
    public void EveryProblemHasLabelAndMessage(ApiResumeProblem problem, string label, string message)
    {
        var e = F(problem);
        Assert.Equal((label, message), (e.Short, e.Message));
    }

    [Fact]
    public void EveryProblemIsCovered() =>
        Assert.Equal(18, Enum.GetValues<ApiResumeProblem>().Length);

    [Fact]
    public void ModelNotInApiNeverSuggestsAnotherModel()
    {
        string m = F(ApiResumeProblem.ModelNotInApi).Message;
        Assert.Contains("A barra não troca de modelo", m);
        Assert.Contains("qui 12/11 às 23:01", m);
        Assert.DoesNotContain("outro modelo", m, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Escolha", m);
        Assert.Contains("sem previsão informada pela conta", ApiResumeErrors.For(ApiResumeProblem.ModelNotInApi, "gpt-5.6-sol").Message);
    }

    [Theory]
    [InlineData("Error: thread 01aa0000-0000-7000-8000-000000000002 already has an active writer (code -32600)", "conversa aberta no app")]
    [InlineData("""{"error":{"message":"You exceeded your current quota","type":"insufficient_quota"}}""", "API sem créditos")]
    [InlineData("no rollout found for thread id 01aa0000-0000-7000-8000-000000000002", "conversa não encontrada")]
    [InlineData("The model `gpt-9` does not exist or you do not have access to it.", "modelo fora da API")]
    [InlineData("HTTP 403: Project proj_x does not have access to model gpt-5.6-sol", "modelo fora da API")]
    [InlineData("forced_login_method requires chatgpt", "login obrigatório pelo ChatGPT")]
    [InlineData("HTTP 401: Incorrect API key provided", "chave recusada")]
    [InlineData("HTTP 429: Rate limit reached for gpt-5.6-sol", "muitos pedidos")]
    [InlineData("HTTP 403: forbidden", "erro desconhecido")]
    public void ExplainApiResumeRecognizes(string raw, string label) =>
        Assert.Equal(label, AiErrorExplainer.ExplainApiResume(raw, "gpt-5.6-sol", CodexQuotaBlockTests.Returns).Short);

    [Theory]
    [InlineData("")]
    [InlineData("HTTP 400: bad request")]
    [InlineData("HTTP 403: forbidden")]
    [InlineData("falha xyz-123 sem padrão")]
    [InlineData("The model `gpt-9` does not exist or you do not have access to it.")]
    [InlineData("HTTP 403: Project proj_x does not have access to model gpt-5.6-sol")]
    public void NoResumeExplanationSuggestsAnotherModel(string raw) =>
        Assert.DoesNotContain("outro modelo", AiErrorExplainer.ExplainApiResume(raw, "gpt-5.6-sol", null).Message, StringComparison.OrdinalIgnoreCase);

    [Fact]
    public void TechnicalDetailHasNoKey()
    {
        string key = "sk-proj-" + new string('a', 40);
        var leak = new Regex(@"sk-[A-Za-z0-9_\-]{6,}");
        var explained = AiErrorExplainer.ExplainApiResume("HTTP 401: Incorrect API key provided: " + key, "gpt-5.6-sol", null);
        var forced = ApiResumeErrors.For(ApiResumeProblem.KeyRejected, technical: "Authorization: Bearer " + key);
        foreach (var technical in new[] { explained.Technical, forced.Technical })
        {
            Assert.NotNull(technical);
            Assert.DoesNotContain(key, technical);
            Assert.DoesNotMatch(leak, technical!);
        }
        Assert.Equal("CODEX_API_KEY=•••", ApiResumeErrors.Redact("CODEX_API_KEY=" + key));
        Assert.Equal("", ApiResumeErrors.Redact(null));
        Assert.Equal(601, ApiResumeErrors.Redact(new string('x', 900)).Length);
    }
}
