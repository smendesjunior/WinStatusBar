using Microsoft.UI.Dispatching;

namespace WinStatusBar.Services;

/// <summary>
/// Fecha uma página oculta do WebView2 depois de um tempo sem uso: o navegador dela ocupa centenas de MB enquanto
/// estiver aberto. Cada uso recomeça a contagem. Chamado na thread da interface.
/// </summary>
internal sealed class IdleCloser(TimeSpan idle, Func<Task> close)
{
    private DispatcherQueueTimer? _timer;

    public void Restart(DispatcherQueue? ui)
    {
        if (ui is null) return;
        if (_timer is null)
        {
            _timer = ui.CreateTimer();
            _timer.IsRepeating = false;
            _timer.Interval = idle;
            _timer.Tick += async (_, _) => { try { await close(); } catch (Exception ex) { App.Log("Página oculta parada: " + ex.Message); } };
        }
        _timer.Stop();
        _timer.Start();
    }
}
