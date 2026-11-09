namespace CodexIAs;

/// <summary>Thread da interface (WebView2 só funciona nela). Os pedidos chegam de outras threads e são enfileirados aqui.</summary>
internal static class Ui
{
    private static SynchronizationContext? _context;
    private static int _threadId;

    /// <summary>Chamado uma vez na thread da interface, depois de criar o primeiro controle do WinForms.</summary>
    public static void Init()
    {
        _context = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();
        _threadId = Environment.CurrentManagedThreadId;
    }

    public static Task<T> Run<T>(Func<Task<T>> work)
    {
        if (_context is null) return Task.FromException<T>(new InvalidOperationException("Interface indisponível."));
        if (Environment.CurrentManagedThreadId == _threadId) return work();
        var done = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        _context.Post(async _ =>
        {
            try { done.SetResult(await work()); }
            catch (Exception ex) { done.SetException(ex); }
        }, null);
        return done.Task;
    }
}
