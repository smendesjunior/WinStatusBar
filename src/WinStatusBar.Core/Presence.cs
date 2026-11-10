namespace WinStatusBar.Core;

/// <summary>
/// Se alguém pode ver a barra agora: tela da sessão ligada e sessão desbloqueada. Com a tela desligada ou a sessão
/// bloqueada, os laços periódicos (sensores, janelas das IAs, cotas lidas pelo navegador, cota do Codex) param e voltam
/// assim que a tela acende, em vez de trabalhar a noite toda para ninguém (medido em 10/11/2026: ~3% de um núcleo
/// com a tela apagada, mais um navegador oculto aberto a cada 5 a 6 minutos). A barra informa o estado a partir dos
/// avisos do Windows (GUID_SESSION_DISPLAY_STATUS e WTS_SESSION_LOCK/UNLOCK).
/// </summary>
public static class Presence
{
    private static readonly Lock Gate = new();
    private static bool _displayOff, _locked;
    private static TaskCompletionSource _visible = Visible();

    /// <summary>Ninguém pode ver a barra agora (tela da sessão desligada ou sessão bloqueada).</summary>
    public static bool Away { get { lock (Gate) return _displayOff || _locked; } }

    /// <summary>Mudou entre visível e ausente; o argumento é o novo <see cref="Away"/>. Chamado fora do bloqueio interno.</summary>
    public static event Action<bool>? Changed;

    /// <summary>Tela da sessão: ligada (também esmaecida) ou desligada.</summary>
    public static void SetDisplay(bool on) => Update(() => _displayOff = !on);

    /// <summary>Sessão bloqueada (tela de bloqueio) ou desbloqueada.</summary>
    public static void SetLocked(bool locked) => Update(() => _locked = locked);

    /// <summary>Espera a barra voltar a ser vista; termina na hora se ela já é.</summary>
    public static Task WhenVisibleAsync(CancellationToken ct)
    {
        Task task;
        lock (Gate) task = _visible.Task;
        return task.IsCompleted ? Task.CompletedTask : task.WaitAsync(ct);
    }

    /// <summary>Versão bloqueante para threads próprias; devolve falso se <paramref name="ct"/> foi cancelado.</summary>
    public static bool WaitVisible(CancellationToken ct)
    {
        try { WhenVisibleAsync(ct).Wait(ct); return true; }
        catch (OperationCanceledException) { return false; }
        catch (AggregateException) { return !ct.IsCancellationRequested; }
    }

    private static void Update(Action change)
    {
        bool before, after;
        TaskCompletionSource? release = null;
        lock (Gate)
        {
            before = _displayOff || _locked;
            change();
            after = _displayOff || _locked;
            if (before == after) return;
            if (after) _visible = new(TaskCreationOptions.RunContinuationsAsynchronously);
            else release = _visible;
        }
        release?.TrySetResult();
        Changed?.Invoke(after);
    }

    private static TaskCompletionSource Visible()
    {
        var source = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        source.SetResult();
        return source;
    }

    /// <summary>Volta ao estado inicial (testes).</summary>
    internal static void Reset()
    {
        TaskCompletionSource? release;
        lock (Gate) { _displayOff = false; _locked = false; release = _visible; _visible = Visible(); }
        release.TrySetResult();
    }
}
