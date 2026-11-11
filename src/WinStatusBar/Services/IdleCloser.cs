using Microsoft.UI.Dispatching;

namespace WinStatusBar.Services;

/// <summary>
/// Fecha uma página oculta do WebView2 pouco depois do último uso: o navegador dela ocupa centenas de MB enquanto
/// estiver aberto. Cada uso recomeça a contagem; a espera vem das configurações (Manter a IA aberta depois da resposta).
/// Se a página estiver ocupada na hora, tenta de novo depois. Chamado na thread da interface.
/// </summary>
internal sealed class IdleCloser(Func<TimeSpan> idle, Func<Task<bool>> close)
{
    private DispatcherQueueTimer? _timer;

    /// <summary>Espera escolhida pela pessoa para todas as IAs da barra.</summary>
    public static TimeSpan Configured()
    {
        try { return WinStatusBar.Core.Settings.AppSettings.AiRelease(App.Current.Settings.Current.AiReleaseSeconds); }
        catch { return TimeSpan.FromSeconds(WinStatusBar.Core.Settings.AppSettings.DefaultAiReleaseSeconds); }
    }

    public void Restart(DispatcherQueue? ui)
    {
        if (ui is null) return;
        if (_timer is null)
        {
            _timer = ui.CreateTimer();
            _timer.IsRepeating = false;
            _timer.Tick += async (_, _) =>
            {
                try
                {
                    // Ocupada (respondendo ou com a janela de login aberta): confere de novo em 30 s.
                    if (!await close()) { _timer!.Interval = TimeSpan.FromSeconds(30); _timer.Start(); }
                }
                catch (Exception ex) { App.Log("Página oculta parada: " + ex.Message); }
            };
        }
        _timer.Stop();
        _timer.Interval = idle();
        _timer.Start();
    }
}
