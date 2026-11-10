namespace WinStatusBar.Services;

/// <summary>
/// Quando consultar a janela do ChatGPT, do Claude ou do Qira (UI Automation, em <see cref="ChatActivityProvider"/>):
/// a cada volta (2,5 s) com uma conversa em andamento ou com o app em uso (em primeiro plano agora ou nos últimos
/// 2 min); fora disso, a cada 10 s. Cada consulta percorre a janela inteira e faz o app consultado trabalhar também
/// (~20 ms no ChatGPT e ~17 ms no Claude por consulta, medidos em 10/11/2026); ver se o app está em primeiro plano
/// não custa nada.
/// </summary>
internal static class ChatProbeSchedule
{
    public static readonly TimeSpan Calm = TimeSpan.FromSeconds(10), RecentUse = TimeSpan.FromMinutes(2);

    public static bool ShouldProbe(DateTime now, DateTime lastProbe, DateTime lastUsed, bool running)
        => running || now - lastUsed < RecentUse || now - lastProbe >= Calm;

    /// <summary>O título da janela é o de uma conversa do Codex: igual, ou o título seguido de espaço
    /// (a barra lateral cola a mensagem pendente no nome). Ignora maiúsculas e espaços nas pontas.</summary>
    public static bool MatchesThread(string? windowTitle, IEnumerable<string?> codexTitles)
    {
        string? title = windowTitle?.Trim();
        if (string.IsNullOrEmpty(title)) return false;
        foreach (var candidate in codexTitles)
        {
            string? known = candidate?.Trim();
            if (string.IsNullOrEmpty(known)) continue;
            if (title.Equals(known, StringComparison.OrdinalIgnoreCase)
                || (title.Length > known.Length && title.StartsWith(known + " ", StringComparison.OrdinalIgnoreCase)))
                return true;
        }
        return false;
    }

    /// <summary>O indicador "Processando" sumiu: o registro do Codex manda; sem registro e com o Codex bloqueado, foi a cota;
    /// senão, o comportamento de antes (conclusão não confirmada).</summary>
    public static IndicatorGoneAction OnIndicatorGone(bool trackedByCodex, string? blockedReason) =>
        trackedByCodex ? IndicatorGoneAction.Remove
        : !string.IsNullOrEmpty(blockedReason) ? IndicatorGoneAction.BlockedByQuota
        : IndicatorGoneAction.Unconfirmed;
}

/// <summary>O que fazer com uma conversa do app quando o indicador de andamento some.</summary>
internal enum IndicatorGoneAction { Remove, BlockedByQuota, Unconfirmed }
