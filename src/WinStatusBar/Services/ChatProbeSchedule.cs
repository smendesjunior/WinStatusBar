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
}
