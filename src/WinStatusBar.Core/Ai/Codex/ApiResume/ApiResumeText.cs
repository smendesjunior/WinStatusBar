using System.Globalization;

namespace WinStatusBar.Core.Ai.Codex.ApiResume;

/// <summary>Textos da seção "Conversas paradas pela cota", da janela "Continuar pela API", do Acompanhamento e dos avisos
/// (spec, "Parte 2 › O que muda para você"; "(plano)" onde a spec não fixou o texto).</summary>
public static class ApiResumeText
{
    public static bool ShowSection(bool codexBlocked, bool hasUnfinished) => codexBlocked || hasUnfinished;
    public static bool ShowParallelChoice(int marked) => marked >= 2;

    public static string SectionTitle => LocalizedText.T("Conversas paradas pela cota");
    public static string ContinueButton => LocalizedText.T("Continuar pela API…");
    public static string NoKeyHint => LocalizedText.T("Cadastre a chave da API da OpenAI em Configurações › IA e tarefas (API própria, endereço https://api.openai.com/v1)");
    public static string StoreHint => LocalizedText.T("Disponível na versão do instalador tradicional: na versão da Loja, fechar ou atualizar a barra encerraria a continuação no meio.");
    public static string OpenInApp => LocalizedText.T("Aberta no app do Codex: começa sozinha assim que você fechar o app");
    public static string AlreadyQueued => LocalizedText.T("Já está na fila");
    public static string MessageSent => LocalizedText.T("Mensagem enviada: 'continue de onde parou'");
    public static string NoSpendLimit => LocalizedText.T("Sem limite de gasto. Você acompanha tokens e dólares no Acompanhamento e pode parar a qualquer momento; o que já foi feito fica na conversa.");
    public static string DontOpenInApp => LocalizedText.T("Durante a execução, não abra a conversa no app do Codex.");
    public static string QueueOption => LocalizedText.T("Uma de cada vez (fila)");
    public static string ParallelOption => LocalizedText.T("Todas ao mesmo tempo");
    public static string CardTitle => LocalizedText.T("Acompanhamento · Codex · pela API");
    public static string StopConfirm => LocalizedText.T("O que já foi feito fica na conversa");
    public static string Stopping => LocalizedText.T("Parando…");
    public static string QuotaBack => LocalizedText.T("Cota do Codex liberada: aguardando você");

    public static string Summary(IReadOnlyList<QuotaStop> stops) => stops.Count switch
    {
        0 => LocalizedText.T("Nenhuma conversa parou por falta de cota nos últimos 14 dias"),
        1 => LocalizedText.F("1 conversa parou por falta de cota: {0}", stops[0].Title),
        _ => LocalizedText.F("{0} conversas pararam por falta de cota: {1}", stops.Count, string.Join("; ", stops.Select(s => s.Title))),
    };

    /// <summary>"Parou em ter 10/11 às 00:31 · gpt-5.6-sol · nível baixo · pasta PROJEÇÃO".</summary>
    public static string StoppedLine(QuotaStop stop) => LocalizedText.F("Parou em {0} às {1} · {2} · nível {3} · pasta {4}",
        CodexQuotaText.DayDate(stop.StoppedAt), stop.StoppedAt.ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture),
        stop.Model ?? "?", Level(stop.Effort ?? "?"), FolderName(stop.Cwd));

    public static string ContextLine(long? tokens) => tokens is { } t
        ? LocalizedText.F("Contexto: cerca de {0} tokens (cada chamada à API reenvia esse contexto)", Thousands(t)) : "";

    public static string? RemarkedLine(QuotaStop stop) => !stop.Remarked ? null
        : LocalizedText.F("Agora está marcada como {0} · {1}. Vai continuar com o modelo e o nível do turno que parou: {2} · {3}. Depois, a conversa fica marcada assim no app.",
            stop.CurrentModel ?? stop.Model ?? "", Level(stop.CurrentEffort ?? stop.Effort ?? ""), stop.Model ?? "", Level(stop.Effort ?? ""));

    public static string Unavailable(AiErrorExplanation e) => LocalizedText.F("Não dá para continuar: {0}", e.Short);

    public static string ExcludedFooter(QuotaStopExclusions x) => LocalizedText.F(
        "Só aparecem conversas paradas por falta de cota. De fora: {0} interrompidas por você, {1} de scripts, {2} de subagentes, {3} da barra, {4} outras; as arquivadas nunca aparecem.",
        x.Interrupted, x.ExecOrScripts, x.Subagents, x.Bar, x.Other);

    public static string ConfirmModel(string model, string effort) => LocalizedText.F("{0} · {1} (o mesmo do turno que parou)", model, Level(effort));

    public static string KeySource(string? sourceName) => sourceName is { Length: > 0 }
        ? LocalizedText.F("Chave: {0}", sourceName) : LocalizedText.T("Chave: não encontrada");

    public static string Organization(string? organization, string? dashboardAccount) => LocalizedText.F(
        "Organização cobrada pela chave: {0} · conta do painel de faturamento: {1}",
        organization is { Length: > 0 } o ? o : LocalizedText.T("não informada"),
        dashboardAccount is { Length: > 0 } d ? d : LocalizedText.T("não informada"));

    public static string Tokens(TokenUsage? t) => t is null
        ? LocalizedText.T("Tokens deste turno: aguardando o registro do Codex")
        : LocalizedText.F("Tokens deste turno: {0} de entrada ({1} em cache) · {2} de saída", Thousands(t.Input), Thousands(t.CachedInput), Thousands(t.Output));

    public static string Spend(decimal? spend, DateTimeOffset startedAt, DateTimeOffset? spendAt) => spend is { } s
        ? LocalizedText.F("Gasto: US$ {0} desde {1} (painel da OpenAI às {2}; atrasa alguns minutos e inclui outro uso da mesma organização)",
            Money(s), Time(startedAt), Time(spendAt ?? startedAt))
        : LocalizedText.F("Gasto: aguardando o painel da OpenAI (desde {0})", Time(startedAt));

    public static string FinalSpend(decimal? spend) => spend is { } s
        ? LocalizedText.F("US$ {0} (pode mudar alguns centavos nos próximos minutos)", Money(s))
        : LocalizedText.T("US$: o painel da OpenAI ainda não informou o custo (a barra confere de novo até 3 h depois do fim)");

    public static string Queue(int position, int total) => LocalizedText.F("Fila: {0} de {1}", position, total);

    /// <summary>Posição (1 em diante) entre as continuações não terminadas, pela ordem de criação.</summary>
    public static (int Position, int Total) QueuePosition(IReadOnlyList<ApiResumeJob> jobs, string id)
    {
        var open = jobs.Where(j => !j.IsFinished).OrderBy(j => j.CreatedAt).ToList();
        return (open.FindIndex(j => j.Id == id) + 1, open.Count);
    }

    public static string State(ApiResumeJob job, DateTimeOffset now) => job.State switch
    {
        ApiResumeState.Queued => LocalizedText.T("Na fila"),
        ApiResumeState.WaitingForApp => job.Detail ?? WaitingFor(true),
        ApiResumeState.QuotaBackWaitingUser => QuotaBack,
        ApiResumeState.Starting => LocalizedText.T("Iniciando…"),
        ApiResumeState.Running => LocalizedText.F("Executando há {0}", Minutes(now - (job.StartedAt ?? now))),
        ApiResumeState.Stopping => Stopping,
        ApiResumeState.Completed => LocalizedText.F("Concluída pela API em {0}", Minutes(Duration(job, now))),
        ApiResumeState.Stopped => LocalizedText.F("Parada por você após {0}; o que já foi feito ficou na conversa.", Minutes(Duration(job, now))),
        ApiResumeState.Failed or ApiResumeState.Blocked => LocalizedText.F("Não continuou: {0}", job.Error?.Short ?? LocalizedText.T("erro desconhecido")),
        ApiResumeState.AlreadyContinued => job.Error?.Message ?? LocalizedText.T("Já continuou"),
        ApiResumeState.Removed => LocalizedText.T("Tirada da fila"),
        _ => "",
    };

    private static TimeSpan Duration(ApiResumeJob job, DateTimeOffset now) => (job.EndedAt ?? now) - (job.StartedAt ?? job.EndedAt ?? now);

    public static string Banner(ApiResumeJob job, DateTimeOffset now) => LocalizedText.F("Pela API: {0} · {1}", job.Title, State(job, now));

    public static (string Title, string Message) Notice(ApiResumeJob job, DateTimeOffset now)
    {
        string message = (job.EndedWhileBarClosed ? LocalizedText.T("Enquanto a barra estava fechada: ") : "") + State(job, now);
        if (job.State == ApiResumeState.Completed && !string.IsNullOrWhiteSpace(job.Summary)) message += "\n" + job.Summary;
        if (job.State is ApiResumeState.Failed or ApiResumeState.Blocked && job.Error is { } e) message += "\n" + e.Message;
        return (job.Title, message);
    }

    public static string WaitingFor(bool chatGptRunning) => chatGptRunning
        ? LocalizedText.T("Feche o app do Codex por completo (inclusive o ícone perto do relógio). A barra começa sozinha assim que ele soltar a conversa.")
        : LocalizedText.T("Outro programa do Codex está com esta conversa aberta (um terminal com o Codex, a extensão do VS Code ou o chat da barra). Feche-o para continuar.");

    public static string QuotaBackNotice(int waiting) =>
        LocalizedText.F("A cota do Codex voltou. {0} conversa(s) esperam na fila da API; elas não vão continuar sozinhas.", waiting);

    public static IReadOnlyList<ApiResumeAction> Actions(ApiResumeJob job, bool codexBlocked) => job.State switch
    {
        ApiResumeState.Queued or ApiResumeState.WaitingForApp => [ApiResumeAction.RemoveFromQueue],
        ApiResumeState.QuotaBackWaitingUser => [ApiResumeAction.AllowWhenQuotaBack, ApiResumeAction.RemoveFromQueue],
        ApiResumeState.Starting or ApiResumeState.Running => [ApiResumeAction.Stop],
        ApiResumeState.Stopping => job.ForceOffered ? [ApiResumeAction.ForceKill] : [],
        ApiResumeState.Completed => codexBlocked
            ? [ApiResumeAction.OpenInCodex, ApiResumeAction.ContinueAgain, ApiResumeAction.Dismiss]
            : [ApiResumeAction.OpenInCodex, ApiResumeAction.Dismiss],
        ApiResumeState.Stopped => [ApiResumeAction.OpenInCodex, ApiResumeAction.Dismiss],
        ApiResumeState.Failed => [ApiResumeAction.Retry, ApiResumeAction.Dismiss],
        _ => [ApiResumeAction.Dismiss],
    };

    public static string ActionLabel(ApiResumeAction a) => a switch
    {
        ApiResumeAction.Stop => LocalizedText.T("Parar"),
        ApiResumeAction.ForceKill => LocalizedText.T("Encerrar à força"),
        ApiResumeAction.AllowWhenQuotaBack => LocalizedText.T("Continuar pela API mesmo assim"),
        ApiResumeAction.RemoveFromQueue => LocalizedText.T("Tirar da fila"),
        ApiResumeAction.Retry => LocalizedText.T("Tentar de novo"),
        ApiResumeAction.ContinueAgain => LocalizedText.T("Continuar de novo pela API"),
        ApiResumeAction.OpenInCodex => LocalizedText.T("Abrir no Codex"),
        _ => LocalizedText.T("OK"),
    };

    /// <summary>"443", "3,1 mil", "79 mil", "1,2 mi" (cultura atual).</summary>
    internal static string Thousands(long n) => n switch
    {
        < 1000 => n.ToString(CultureInfo.CurrentCulture),
        < 1_000_000 => LocalizedText.F("{0} mil", (n / 1000.0).ToString("0.#", CultureInfo.CurrentCulture)),
        _ => LocalizedText.F("{0} mi", (n / 1_000_000.0).ToString("0.#", CultureInfo.CurrentCulture)),
    };

    /// <summary>"menos de 1 min", "14 min", "1 h 15 min".</summary>
    internal static string Minutes(TimeSpan t)
    {
        if (t < TimeSpan.FromMinutes(1)) return LocalizedText.T("menos de 1 min");
        int total = (int)t.TotalMinutes;
        return total < 60 ? LocalizedText.F("{0} min", total)
            : total % 60 == 0 ? LocalizedText.F("{0} h", total / 60)
            : LocalizedText.F("{0} h {1} min", total / 60, total % 60);
    }

    internal static string Level(string effort) => AgentPrompt.EffortLabel(effort).ToLower(CultureInfo.CurrentCulture);

    private static string Money(decimal value) => value.ToString("0.00", CultureInfo.CurrentCulture);
    private static string Time(DateTimeOffset at) => at.ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture);

    private static string FolderName(string? path)
    {
        if (string.IsNullOrEmpty(path)) return "?";
        string trimmed = path.TrimEnd('\\', '/');
        string name = Path.GetFileName(trimmed);
        return name.Length > 0 ? name : path; // raiz de unidade (Z:\) fica inteira
    }
}
