using System.Globalization;
using System.Text.RegularExpressions;

namespace WinStatusBar.Core.Ai.Codex.ApiResume;

/// <summary>Por que uma continuação pela API não pode seguir (ou terminou mal).</summary>
public enum ApiResumeProblem
{
    NoKey, KeyRejected, ModelNotInApi, EffortNotAccepted, MissingTurnData, BarModel, FolderUnavailable,
    ExtraFolderMissing, CodexNotFound, ForcedChatGptLogin, WriterConflict, ApiNoCredits, ThreadNotFound, AlreadyContinued,
    StoppedAgainByQuota, EndedWithoutTurnEnd, StopNotAnswered, StopHelperMissing,
}

/// <summary>Rótulo, explicação simples e detalhe técnico mascarado dos problemas da continuação pela API (tabela "Parte 2 › Erros").
/// A explicação nunca sugere trocar de modelo nem baixar o nível.</summary>
public static class ApiResumeErrors
{
    private static readonly Regex SecretKey = new(@"sk-[A-Za-z0-9_\-]{6,}", RegexOptions.CultureInvariant);
    private static readonly Regex Bearer = new(@"Bearer\s+\S+", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    private static readonly Regex EnvKey = new(@"CODEX_API_KEY=\S+", RegexOptions.CultureInvariant);

    public static AiErrorExplanation For(ApiResumeProblem p, string? model = null, string? effort = null, string? folder = null,
        DateTimeOffset? quotaReturns = null, DateTimeOffset? at = null, string? technical = null)
    {
        string m = model ?? "";
        string when = When(quotaReturns);
        string stamp = at is { } a ? a.ToLocalTime().ToString("dd/MM HH:mm", CultureInfo.InvariantCulture) : "";
        (string label, string message) = p switch
        {
            ApiResumeProblem.NoKey => (LocalizedText.T("sem chave da API"),
                LocalizedText.T("Não há chave da API da OpenAI neste PC. Cadastre em Configurações › IA e tarefas › Outro serviço (API própria), endereço https://api.openai.com/v1, e tente de novo.")),
            ApiResumeProblem.KeyRejected => (LocalizedText.T("chave recusada"),
                LocalizedText.T("A API da OpenAI recusou a sua chave (expirada, revogada ou de outro projeto). Troque a chave em Configurações › IA e tarefas.")),
            ApiResumeProblem.ModelNotInApi => (LocalizedText.T("modelo fora da API"),
                LocalizedText.F("A API da OpenAI não oferece o modelo {0} para a sua chave. A barra não troca de modelo: a conversa continua parada até a cota do Codex voltar ({1}).", m, when)),
            ApiResumeProblem.EffortNotAccepted => (LocalizedText.T("nível não aceito"),
                LocalizedText.F("O modelo {0} não aceita o nível {1}. A barra não baixa o nível: a conversa continua parada até a cota do Codex voltar ({2}).", m, effort ?? "", when)),
            ApiResumeProblem.MissingTurnData => (LocalizedText.T("sem dados do turno"), CodexQuotaText.MissingTurnData),
            ApiResumeProblem.BarModel => (LocalizedText.T("modelo da barra"),
                LocalizedText.F("O turno usava {0}, um modelo da própria barra no Codex, que não depende da cota. Continue pelo app.", m)),
            ApiResumeProblem.FolderUnavailable => (LocalizedText.T("pasta inacessível"),
                LocalizedText.F("A pasta da conversa ({0}) não está acessível agora. Abra o OneDrive ou conecte a unidade e tente de novo.", folder ?? "")),
            ApiResumeProblem.ExtraFolderMissing => (LocalizedText.T("pasta extra ausente"),
                LocalizedText.F("A unidade {0} não está conectada; a IA não vai conseguir ler arquivos de lá.", folder ?? "")),
            ApiResumeProblem.CodexNotFound => (LocalizedText.T("Codex não encontrado"),
                LocalizedText.T("A barra não achou o programa do Codex (codex.exe) neste PC. Abra o app do Codex uma vez para ele se instalar e tente de novo.")),
            ApiResumeProblem.ForcedChatGptLogin => (LocalizedText.T("login obrigatório pelo ChatGPT"),
                LocalizedText.T("O config.toml do Codex obriga o login pelo ChatGPT; assim a continuação não pode usar a chave da API.")),
            ApiResumeProblem.WriterConflict => (LocalizedText.T("conversa aberta no app"),
                LocalizedText.T("Feche o app do Codex para continuar. Nada é cobrado.")),
            ApiResumeProblem.ApiNoCredits => (LocalizedText.T("API sem créditos"),
                LocalizedText.T("A sua conta da API da OpenAI está sem saldo (separado do plano do Codex). Adicione créditos em platform.openai.com › Settings › Billing e clique em Tentar de novo.")),
            ApiResumeProblem.ThreadNotFound => (LocalizedText.T("conversa não encontrada"),
                LocalizedText.T("O Codex não achou esta conversa (pode ter sido apagada ou arquivada).")),
            ApiResumeProblem.AlreadyContinued => (LocalizedText.T("já continuou"),
                LocalizedText.F("A conversa já continuou em {0}; nada foi enviado.", stamp)),
            ApiResumeProblem.StoppedAgainByQuota => (LocalizedText.T("parou de novo pela cota"),
                LocalizedText.F("A conversa parou de novo pela cota em {0}, num turno feito pelo app; nada foi enviado. Abra Continuar pela API de novo para continuar deste ponto.", stamp)),
            ApiResumeProblem.EndedWithoutTurnEnd => (LocalizedText.T("parou sem registrar o fim"),
                LocalizedText.T("O Codex terminou sem registrar o fim do turno (a barra ou o Windows fechou, ou o processo foi encerrado). O que já foi feito ficou na conversa; abra no app para conferir.")),
            ApiResumeProblem.StopNotAnswered => (LocalizedText.T("não parou"),
                LocalizedText.T("O Codex não respondeu ao pedido para parar. Você pode encerrar à força; nesse caso o último passo pode ficar sem registro de fim.")),
            ApiResumeProblem.StopHelperMissing => (LocalizedText.T("parada segura indisponível"),
                LocalizedText.T("A instalação está incompleta (falta o componente Automation). Reinstale a barra, ou use \"Encerrar à força\".")),
            _ => (LocalizedText.T("erro desconhecido"), ""),
        };
        return new AiErrorExplanation(label, message, string.IsNullOrEmpty(technical) ? null : Redact(technical));
    }

    /// <summary>Tira chaves do texto (sk-…, Bearer …, CODEX_API_KEY=…) e corta em 600 caracteres.</summary>
    public static string Redact(string? text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        string clean = SecretKey.Replace(text, "sk-•••");
        clean = Bearer.Replace(clean, "Bearer •••");
        clean = EnvKey.Replace(clean, "CODEX_API_KEY=•••");
        return clean.Length > 600 ? clean[..600] + "…" : clean;
    }

    /// <summary>"qui 12/11 às 23:01"; sem horário, "sem previsão informada pela conta".</summary>
    internal static string When(DateTimeOffset? at) => at is { } a
        ? LocalizedText.F("{0} às {1}", CodexQuotaText.DayDate(a), a.ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture))
        : LocalizedText.T("sem previsão informada pela conta");
}
