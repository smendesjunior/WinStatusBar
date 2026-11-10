using System.Globalization;

namespace WinStatusBar.Core.Ai.Codex;

/// <summary>Por que o Codex está bloqueado (motivo informado pela conta ou janela em 100 %).</summary>
public enum CodexBlockCause
{
    None, OwnerCreditsDepleted, MemberCreditsDepleted, OwnerUsageLimit, MemberUsageLimit,
    RateLimitReached, SpendControl, AccountBlocked, FiveHourWindow, WeeklyWindow,
}

/// <summary>Aviso do topo do menu da cota do Codex, linha por linha.</summary>
public sealed record CodexBlockBanner(string Title, string Reason, string Returns, string Effect, string Confirmed, string Technical);

/// <summary>Textos em português do bloqueio da conta do Codex (menu da cota, dica da barra, linha da conversa e log).</summary>
public static class CodexQuotaText
{
    private static readonly CultureInfo Pt = CultureInfo.GetCultureInfo("pt-BR");

    /// <summary>"Cota liberada · pode continuar no app".</summary>
    public static string Released => LocalizedText.T("Cota liberada · pode continuar no app");

    /// <summary>Turno parado sem modelo, nível ou acesso registrado.</summary>
    public static string MissingTurnData => LocalizedText.T("O registro não diz com que modelo, nível ou acesso o turno parou; a barra não adivinha. Continue pelo app quando a cota voltar.");

    public static CodexBlockCause CauseOf(string? reachedType) => reachedType switch
    {
        null or "" => CodexBlockCause.None,
        "workspace_owner_credits_depleted" => CodexBlockCause.OwnerCreditsDepleted,
        "workspace_member_credits_depleted" => CodexBlockCause.MemberCreditsDepleted,
        "workspace_owner_usage_limit_reached" => CodexBlockCause.OwnerUsageLimit,
        "workspace_member_usage_limit_reached" => CodexBlockCause.MemberUsageLimit,
        "rate_limit_reached" => CodexBlockCause.RateLimitReached,
        _ => CodexBlockCause.AccountBlocked,
    };

    public static CodexBlockCause Cause(QuotaSnapshot? quota, DateTimeOffset now)
    {
        if (quota is null || !quota.IsBlocked(now)) return CodexBlockCause.None;
        var reached = quota.Access?.ReachedType;
        if (reached?.StartsWith("workspace_", StringComparison.Ordinal) == true) return CauseOf(reached);
        if (quota.Access?.SpendControlReached == true) return CodexBlockCause.SpendControl;
        if (IsFull(quota.Primary, now)) return CodexBlockCause.FiveHourWindow;
        if (IsFull(quota.Secondary, now)) return CodexBlockCause.WeeklyWindow;
        var byType = CauseOf(reached);
        return byType == CodexBlockCause.None ? CodexBlockCause.AccountBlocked : byType;
    }

    private static bool IsFull(QuotaWindow? w, DateTimeOffset now) => w is not null && w.UsedPercent >= 100 && !w.IsExpired(now);

    /// <summary>Motivo por extenso (tabela "Motivo informado" do spec).</summary>
    public static string Reason(CodexBlockCause cause) => cause switch
    {
        CodexBlockCause.OwnerCreditsDepleted => LocalizedText.T("os créditos do workspace acabaram"),
        CodexBlockCause.MemberCreditsDepleted => LocalizedText.T("os créditos do workspace acabaram (o administrador precisa adicionar)"),
        CodexBlockCause.OwnerUsageLimit => LocalizedText.T("o workspace atingiu o limite de uso definido"),
        CodexBlockCause.MemberUsageLimit => LocalizedText.T("você atingiu o limite de uso definido pelo administrador"),
        CodexBlockCause.RateLimitReached => LocalizedText.T("o limite de uso foi atingido"),
        CodexBlockCause.SpendControl => LocalizedText.T("o limite de gastos do workspace foi atingido"),
        CodexBlockCause.AccountBlocked => LocalizedText.T("a conta bloqueou o uso"),
        CodexBlockCause.FiveHourWindow => LocalizedText.T("a cota de 5 horas acabou"),
        CodexBlockCause.WeeklyWindow => LocalizedText.T("a cota semanal acabou"),
        _ => "",
    };

    /// <summary>Motivo curto, para legendas e a dica da barra.</summary>
    public static string ShortReason(CodexBlockCause cause) => cause switch
    {
        CodexBlockCause.OwnerCreditsDepleted or CodexBlockCause.MemberCreditsDepleted => LocalizedText.T("créditos do workspace esgotados"),
        CodexBlockCause.OwnerUsageLimit => LocalizedText.T("limite de uso do workspace atingido"),
        CodexBlockCause.MemberUsageLimit => LocalizedText.T("limite de uso definido pelo administrador atingido"),
        CodexBlockCause.RateLimitReached => LocalizedText.T("limite de uso atingido"),
        CodexBlockCause.SpendControl => LocalizedText.T("limite de gastos do workspace atingido"),
        CodexBlockCause.AccountBlocked => LocalizedText.T("uso bloqueado pela conta"),
        CodexBlockCause.FiveHourWindow => LocalizedText.T("cota de 5 horas esgotada"),
        CodexBlockCause.WeeklyWindow => LocalizedText.T("cota semanal esgotada"),
        _ => "",
    };

    public static string ShortReason(QuotaSnapshot quota, DateTimeOffset now) => ShortReason(Cause(quota, now));

    /// <summary>Etiqueta curta da linha do Codex.</summary>
    public static string Pill(CodexBlockCause cause) => cause switch
    {
        CodexBlockCause.OwnerCreditsDepleted or CodexBlockCause.MemberCreditsDepleted => LocalizedText.T("Créditos esgotados"),
        CodexBlockCause.OwnerUsageLimit or CodexBlockCause.MemberUsageLimit or CodexBlockCause.SpendControl => LocalizedText.T("Limite do workspace"),
        CodexBlockCause.FiveHourWindow => LocalizedText.T("Cota de 5h esgotada"),
        CodexBlockCause.WeeklyWindow => LocalizedText.T("Cota semanal esgotada"),
        CodexBlockCause.RateLimitReached or CodexBlockCause.AccountBlocked => LocalizedText.T("Uso bloqueado"),
        _ => "",
    };

    public static string Pill(QuotaSnapshot quota, DateTimeOffset now) => Pill(Cause(quota, now));

    /// <summary>"qui 12/11", sempre no horário local e com o dia da semana em português.</summary>
    public static string DayDate(DateTimeOffset at)
    {
        var local = at.ToLocalTime();
        return Pt.DateTimeFormat.GetAbbreviatedDayName(local.DayOfWeek).TrimEnd('.') + " " + local.ToString("dd/MM", CultureInfo.InvariantCulture);
    }

    private static string Time(DateTimeOffset at) => at.ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture);
    private static string DayMonth(DateTimeOffset at) => at.ToLocalTime().ToString("dd/MM", CultureInfo.InvariantCulture);

    /// <summary>Primeira linha da dica da cota na barra; vazia quando o Codex está livre.</summary>
    public static string BlockedLine(QuotaSnapshot quota, DateTimeOffset now)
    {
        if (!quota.IsBlocked(now)) return "";
        var until = quota.BlockedUntil(now);
        string tail = until is null ? LocalizedText.T("sem previsão informada pela conta")
            : until.Value <= now ? LocalizedText.T("aguardando a conta confirmar a liberação")
            : LocalizedText.F("volta {0} às {1}", DayDate(until.Value), Time(until.Value));
        return LocalizedText.F("Codex bloqueado: {0} · {1}", ShortReason(quota, now), tail);
    }

    /// <summary>Linha "Volta…" do aviso, nas três formas do spec.</summary>
    public static string ReturnsText(QuotaSnapshot quota, DateTimeOffset now)
    {
        var until = quota.BlockedUntil(now);
        if (until is null) return LocalizedText.T("A conta não informou quando volta.");
        if (until.Value <= now)
            return LocalizedText.F("O horário previsto ({0} às {1}) já passou; aguardando a conta confirmar a liberação.", DayMonth(until.Value), Time(until.Value));
        return LocalizedText.F("Volta {0} às {1} (informado pela conta).", DayDate(until.Value), Time(until.Value));
    }

    /// <summary>Legenda da conversa parada pela cota.</summary>
    public static string StoppedCaption(string? reachedType)
    {
        var cause = CauseOf(reachedType);
        return cause == CodexBlockCause.None ? LocalizedText.T("Parada pela cota do Codex")
            : LocalizedText.F("Parada pela cota do Codex · {0}", ShortReason(cause));
    }

    /// <summary>Envio recusado no app do ChatGPT para uma conversa sem registro do Codex.</summary>
    public static string ChatRefused(string shortReason) => LocalizedText.F("O Codex está sem cota ({0}); o app não confirmou resposta", shortReason);

    /// <summary>Detalhe técnico, em letra pequena: só os campos informados.</summary>
    public static string Technical(CodexAccess? access)
    {
        if (access is null) return "";
        var parts = new List<string>();
        if (access.OrdinaryUsageAllowed is { } allowed) parts.Add("ordinaryUsageAllowed=" + (allowed ? "true" : "false"));
        if (!string.IsNullOrEmpty(access.ReachedType)) parts.Add("rateLimitReachedType=" + access.ReachedType);
        if (access.SpendControlReached == true) parts.Add("spendControlReached=true");
        if (access.CreditsBalance is { } balance) parts.Add(LocalizedText.F("saldo de créditos {0}", balance));
        return string.Join(" · ", parts);
    }

    /// <summary>Aviso do topo do menu da cota; null quando o Codex está livre.</summary>
    public static CodexBlockBanner? Banner(QuotaSnapshot? quota, DateTimeOffset now)
    {
        if (quota is null || !quota.IsBlocked(now)) return null;
        var access = quota.Access;
        string confirmed = access is null ? LocalizedText.F("Último dado às {0}.", Time(quota.ObservedAt))
            : access.Official ? LocalizedText.F("Confirmado pela conta às {0}.", Time(access.ObservedAt))
            : LocalizedText.F("Visto no registro de uma conversa às {0}; a conta ainda não confirmou.", Time(access.ObservedAt));
        return new CodexBlockBanner(
            LocalizedText.T("O Codex está bloqueado pela conta"),
            LocalizedText.F("Motivo: {0}.", Reason(Cause(quota, now))),
            ReturnsText(quota, now),
            LocalizedText.T("Enquanto isso, o app do Codex não envia mensagens, com nenhum modelo."),
            confirmed,
            Technical(access));
    }

    /// <summary>Linha do log quando o bloqueio começa: "Codex cota: uso bloqueado (motivo) até 12/11 23:01". Sem tradução.</summary>
    internal static string BlockLog(QuotaSnapshot quota, DateTimeOffset now)
    {
        var a = quota.Access;
        string why = !string.IsNullOrEmpty(a?.ReachedType) ? a!.ReachedType!
            : a?.SpendControlReached == true ? "spendControlReached"
            : IsFull(quota.Primary, now) ? "janela 5h em 100%"
            : IsFull(quota.Secondary, now) ? "janela 7d em 100%"
            : "ordinaryUsageAllowed=false";
        var until = quota.BlockedUntil(now);
        string when = until is null ? "horário não informado" : $"{DayMonth(until.Value)} {Time(until.Value)}";
        return $"Codex cota: uso bloqueado ({why}) até {when}";
    }
}
