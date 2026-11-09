namespace WinStatusBar.Services;

/// <summary>
/// Limite de segurança da saída: depois de iniciado, espera o prazo numa thread própria (de segundo plano, que nunca
/// segura o processo) e chama a saída forçada. Se a saída normal terminar antes, o processo acaba e a vigia some com ele.
/// Iniciar de novo não faz nada (sinal de saída e menu da bandeja podem chegar juntos).
/// </summary>
internal sealed class ExitWatchdog(TimeSpan timeout, Action onTimeout)
{
    private int _started;

    public bool Started => Volatile.Read(ref _started) == 1;

    public void Start()
    {
        if (Interlocked.Exchange(ref _started, 1) == 1) return;
        new Thread(() => { Thread.Sleep(timeout); onTimeout(); }) { IsBackground = true, Name = "WinStatusBar.ExitWatchdog" }.Start();
    }
}
