using System.Security.Cryptography;
using System.Text;

namespace CodexIAs;

/// <summary>
/// Só com os comandos do Claude ligados: se o Codex repetir o mesmo pedido (mesma conversa e mesmo conteúdo) enquanto o
/// Claude ainda roda, ou até 10 min depois do fim, o pedido se junta à execução (ou recebe a resposta pronta) em vez de
/// rodar os comandos de novo. Fica só na memória.
/// </summary>
internal sealed class ClaudeRunRegistry
{
    public static ClaudeRunRegistry Shared { get; } = new();
    public static readonly TimeSpan ReadyWindow = TimeSpan.FromMinutes(10);
    internal Func<DateTime> Clock { get; init; } = () => DateTime.UtcNow;

    private readonly object _gate = new();
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);

    private sealed record Subscriber(Action<string> OnText, Action<string> OnStatus);

    private sealed class Entry
    {
        public readonly List<string> Statuses = [];
        public readonly List<Subscriber> Subscribers = [];
        public readonly CancellationTokenSource Cts = new();
        public string? LastText;
        public Answer? Result;
        public DateTime? FinishedAt;
        public Task<Answer> Run = Task.FromResult(new Answer("", null));
    }

    /// <summary>Chave do pedido: a conversa do Codex mais o resumo do conteúdo; null sem conversa (roda direto).</summary>
    public static string? KeyOf(string slug, string? effort, Conversation conversation)
    {
        if (string.IsNullOrEmpty(conversation.Key)) return null;
        string content = string.Join("\n", slug, effort ?? "", conversation.Cwd ?? "", conversation.Prompt,
            string.Join("\u0001", conversation.History.Select(h => h.Role + ":" + h.Content)));
        return conversation.Key + ":" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content)))[..32];
    }

    public async Task<Answer> RunAsync(string key, Func<Action<string>, Action<string>, CancellationToken, Task<Answer>> start,
        Action<string> onText, Action<string> onStatus, CancellationToken ct)
    {
        if (ct.IsCancellationRequested) return new("", "Interrompido");
        var me = new Subscriber(onText, onStatus);
        Entry entry;
        List<string> replayStatuses = [];
        string? replayText = null;
        Answer? ready = null;
        bool joined = false;
        lock (_gate)
        {
            var now = Clock();
            foreach (var old in _entries.Where(e => e.Value.FinishedAt is { } f && now - f > ReadyWindow).Select(e => e.Key).ToList())
                _entries.Remove(old);
            if (_entries.TryGetValue(key, out var existing))
            {
                entry = existing;
                replayStatuses = [.. entry.Statuses];
                replayText = entry.LastText;
                if (entry.Result is { } done) ready = done;
                else { entry.Subscribers.Add(me); joined = true; }
            }
            else
            {
                entry = new Entry();
                entry.Subscribers.Add(me);
                _entries[key] = entry;
                var created = entry;
                entry.Run = Task.Run(() => start(t => Fan(created, text: t), s => Fan(created, status: s), created.Cts.Token));
                _ = entry.Run.ContinueWith(t => Settle(key, created, t), TaskScheduler.Default);
            }
        }
        if (replayStatuses.Count > 0 || replayText is not null || ready is not null)
        {
            foreach (var s in replayStatuses) onStatus(s);
            if (replayText is not null) onText(replayText);
        }
        if (ready is not null)
        {
            Log.Write("Claude: o Codex repetiu o pedido; resposta pronta reaproveitada (nada roda de novo)");
            return ready;
        }
        if (joined)
            Log.Write("Claude: o Codex repetiu o pedido; juntado à execução em andamento");

        try
        {
            var answer = await entry.Run.WaitAsync(ct).ConfigureAwait(false);
            Settle(key, entry, entry.Run);
            lock (_gate) entry.Subscribers.Remove(me);
            return answer;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            string? partial;
            lock (_gate)
            {
                entry.Subscribers.Remove(me);
                partial = entry.LastText;
                // Ninguém mais espera: para a execução e esquece a entrada (a próxima roda de novo).
                if (entry.Subscribers.Count == 0 && entry.Result is null)
                {
                    entry.Cts.Cancel();
                    if (_entries.TryGetValue(key, out var current) && ReferenceEquals(current, entry)) _entries.Remove(key);
                }
            }
            return new(partial ?? "", "Interrompido");
        }
    }

    private void Fan(Entry entry, string? text = null, string? status = null)
    {
        List<Subscriber> subscribers;
        lock (_gate)
        {
            if (status is not null) entry.Statuses.Add(status);
            if (text is not null) entry.LastText = text;
            subscribers = [.. entry.Subscribers];
        }
        foreach (var s in subscribers)
        {
            if (status is not null) s.OnStatus(status);
            if (text is not null) s.OnText(text);
        }
    }

    /// <summary>Terminou bem: guarda a resposta por 10 min. Cancelada ou com falha: esquece a entrada.</summary>
    private void Settle(string key, Entry entry, Task<Answer> run)
    {
        lock (_gate)
        {
            if (run.IsCompletedSuccessfully)
            {
                if (entry.Result is null) { entry.Result = run.Result; entry.FinishedAt = Clock(); }
            }
            else if (run.IsCompleted && _entries.TryGetValue(key, out var current) && ReferenceEquals(current, entry))
                _entries.Remove(key);
        }
    }
}
