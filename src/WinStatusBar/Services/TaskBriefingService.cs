using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using WinStatusBar.Core.Ai;

namespace WinStatusBar.Services;

/// <summary>
/// Etapas da sessão selecionada para o acompanhamento. Prioridade: o plano registrado pela própria IA
/// (TodoWrite / update_plan, lido do transcript local). Sem plano registrado, uma chamada efêmera e leve
/// deduz o roteiro a partir da conversa (marcado como deduzido). O resumo em texto foi removido em 07/11/2026.
/// </summary>
internal sealed class TaskBriefingService : IDisposable
{
    public sealed record Briefing(string Session, string Text, string Status, DateTimeOffset? UpdatedAt)
    {
        public IReadOnlyList<ActivityStep> Steps { get; init; } = [];
        /// <summary>Etapas deduzidas por IA (a sessão não registrou um plano).</summary>
        public bool Inferred { get; init; }
        /// <summary>Início da conversa lido do transcript (vale também depois de a barra reiniciar).</summary>
        public DateTimeOffset? StartedAt { get; init; }
    }
    private sealed class Entry
    {
        public string? Path;
        public long Length;
        public DateTime WriteTime;
        public ActivityDigest? Digest;
        public DateTimeOffset? UpdatedAt;
        public IReadOnlyList<ActivityStep> Inferred = [];
        public string Fingerprint = "";
        public DateTimeOffset Attempt;
        public bool Loaded;
    }
    private sealed record Saved(List<ActivityStep> Steps, string Fingerprint, DateTimeOffset? UpdatedAt);
    private static string CacheFile(string id) => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "WinStatusBar", "TaskBriefings", Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(id))) + ".plan.json");
    private readonly Func<AgentSession?> _session;
    private readonly Func<bool> _enabled;
    private readonly Func<int> _interval;
    private readonly CancellationTokenSource _stop = new();
    private readonly Dictionary<string, Entry> _cache = new();
    private int _started;
    private Briefing _currentValue = new("", "", "Aguardando uma tarefa selecionada.", null);
    private string _currentKey = "";
    public Briefing Current => _currentValue;
    /// <summary>Etapas/estado mudaram: a barra recalcula o donut na hora, em vez de esperar o próximo ciclo de 5 s.</summary>
    public event EventHandler? Changed;
    /// <summary>Verdadeiro para sessões cuja dedução por IA deve ficar parada (ex.: Codex com a cota bloqueada).</summary>
    public Func<AgentSession, bool>? SkipInference { get; set; }
    private Briefing _current
    {
        get => _currentValue;
        set
        {
            _currentValue = value;
            string key = value.Session + "|" + value.Inferred + "|" + string.Join(";", value.Steps.Select(s => s.Status + ":" + s.Text));
            if (key == _currentKey) return;
            _currentKey = key;
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <param name="enabled">Permite deduzir o roteiro por IA quando não há plano registrado.</param>
    /// <param name="interval">Intervalo mínimo, em segundos, entre deduções por IA da mesma sessão.</param>
    public TaskBriefingService(Func<AgentSession?> session, Func<bool> enabled, Func<int> interval)
    { _session = session; _enabled = enabled; _interval = interval; }

    /// <summary>
    /// Deduz o roteiro com a mesma IA que executa a tarefa (Claude ou Codex), no modelo mais simples e no menor
    /// esforço disponíveis, numa chamada efêmera que não entra no histórico da conversa.
    /// </summary>
    private static async Task<IReadOnlyList<ActivityStep>> InferAsync(AgentSession session, string prompt, CancellationToken ct)
    {
        var target = session.Kind == AgentKind.Claude ? AgentPrompt.Target.Claude : AgentPrompt.Target.Codex;
        var catalog = await AgentPrompt.CatalogAsync(target, null, ct);
        var (model, effort) = InferredPlan.SimplestModel(catalog, target == AgentPrompt.Target.Codex ? CodexDescriptions() : null);
        var result = await AgentPrompt.RunAsync(target, prompt, model, effort, null,
            new AgentPrompt.Options([], false, false, null, [], Ephemeral: true), _ => { }, ct);
        if (result.Error is not null) App.Log($"Acompanhamento: roteiro por {model ?? "padrão"} falhou: {result.Error}");
        return result.Error is null ? InferredPlan.Parse(StripThinking(result.Text)) : [];
    }

    /// <summary>Descrições do catálogo do Codex ("Fast and affordable…"), para achar o modelo mais leve sem nome fixo.</summary>
    private static IReadOnlyDictionary<string, string> CodexDescriptions()
    {
        var map = new Dictionary<string, string>();
        try
        {
            string home = Environment.GetEnvironmentVariable("CODEX_HOME") is { Length: > 0 } h ? h
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex");
            string file = Path.Combine(home, "models_cache.json");
            if (!File.Exists(file)) return map;
            using var doc = JsonDocument.Parse(File.ReadAllText(file));
            if (doc.RootElement.TryGetProperty("models", out var models) && models.ValueKind == JsonValueKind.Array)
                foreach (var m in models.EnumerateArray())
                    if (m.TryGetProperty("slug", out var s) && s.GetString() is { } slug
                        && m.TryGetProperty("description", out var d) && d.GetString() is { } desc) map[slug] = desc;
        }
        catch (Exception ex) { App.Log("Acompanhamento: catálogo do Codex: " + ex.Message); }
        return map;
    }

    private static string StripThinking(string text) => System.Text.RegularExpressions.Regex.Replace(text ?? "", @"(?s)<think>.*?</think>", string.Empty);

    public void Start()
    {
        if (Interlocked.Exchange(ref _started, 1) != 0) return;
        _ = Task.Run(Loop);
    }

    private async Task Loop()
    {
        while (!_stop.IsCancellationRequested)
        {
            // Tela apagada ou sessão bloqueada: o acompanhamento espera; ao acender, lê só o que a conversa acrescentou.
            try { await WinStatusBar.Core.Presence.WhenVisibleAsync(_stop.Token); } catch (OperationCanceledException) { return; }
            try { await Refresh(); }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested) { return; }
            catch (Exception ex) { App.Log("Acompanhamento da tarefa: " + ex.Message); }
            try { await Task.Delay(1000, _stop.Token); } catch (OperationCanceledException) { return; }
        }
    }

    private async Task Refresh()
    {
        var session = _session()?.Clone();
        if (session is null) { _current = new("", "", "Nenhuma tarefa selecionada.", null); return; }
        string id = session.Kind + ":" + session.Id;
        if (!_cache.TryGetValue(id, out var entry))
        {
            if (_cache.Count >= 20) _cache.Remove(_cache.Keys.First());
            _cache[id] = entry = new();
        }
        if (!entry.Loaded)
        {
            entry.Loaded = true;
            try
            {
                if (File.Exists(CacheFile(id)) && JsonSerializer.Deserialize<Saved>(File.ReadAllText(CacheFile(id))) is { } saved)
                { entry.Inferred = saved.Steps; entry.Fingerprint = saved.Fingerprint; entry.UpdatedAt = saved.UpdatedAt; }
            }
            catch (Exception ex) { App.Log("Cache de acompanhamento: " + ex.Message); }
        }
        var fileInfo = string.IsNullOrEmpty(session.TranscriptPath) ? null : new FileInfo(session.TranscriptPath);
        bool fileChanged = fileInfo is { Exists: true } && (entry.Path != fileInfo.FullName
            || entry.Length != fileInfo.Length || entry.WriteTime != fileInfo.LastWriteTimeUtc);
        // Leitura local e barata: acompanha cada mudança do arquivo (antes ficava presa ao intervalo do resumo por IA).
        if (entry.Digest is null || fileChanged)
        {
            entry.Digest = ActivityDigest.Read(session.TranscriptPath);
            entry.Path = fileInfo?.FullName;
            entry.Length = fileInfo is { Exists: true } ? fileInfo.Length : 0;
            entry.WriteTime = fileInfo is { Exists: true } ? fileInfo.LastWriteTimeUtc : default;
        }
        var digest = entry.Digest;
        if (!digest.HasTranscript)
        {
            _current = new(id, "", "Esta sessão informa somente o estado de atividade, sem as etapas.", null);
            return;
        }
        if (digest.Steps.Count > 0)
        {
            _current = new(id, "", "Etapas registradas pela IA", DateTimeOffset.Now) { Steps = digest.Steps, StartedAt = digest.StartedAt };
            return;
        }

        // Sem plano registrado: a IA deduz o roteiro a partir da conversa.
        var source = new StringBuilder();
        source.AppendLine("Tarefa: " + session.DisplayTitle).AppendLine("Estado atual: " + session.State.ToPt())
            .AppendLine("Atividade atual: " + session.Caption).AppendLine("Pedido: " + digest.Request);
        foreach (var update in digest.Updates) source.AppendLine(update.Text);
        string evidence = source.ToString();
        if (evidence.Length > 24000) evidence = evidence[..4000] + "\n[trecho intermediário omitido]\n" + evidence[^20000..];
        string fingerprint = "plan-v1:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(evidence)));
        bool changed = fingerprint != entry.Fingerprint;
        bool allowed = _enabled();
        // Codex sem cota: a dedução falharia a cada 20-30 s abrindo processos à toa; fica parada até a cota voltar.
        bool paused = allowed && SkipInference?.Invoke(session) == true;
        string waiting = !allowed ? "A IA não registrou etapas. A dedução automática está desativada nas configurações."
            : entry.Inferred.Count > 0 ? "Roteiro deduzido pela IA a partir da conversa"
            : paused ? Localization.T("A IA não registrou etapas; a dedução fica parada enquanto a cota do Codex estiver esgotada.")
            : "A IA não registrou etapas; montando o roteiro…";
        _current = new(id, "", waiting, entry.UpdatedAt) { Steps = entry.Inferred, Inferred = entry.Inferred.Count > 0, StartedAt = digest.StartedAt };
        if (!allowed || paused || !changed || DateTimeOffset.UtcNow - entry.Attempt < TimeSpan.FromSeconds(Math.Max(20, _interval()))) return;
        entry.Attempt = DateTimeOffset.UtcNow;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(180));
        var steps = await InferAsync(session, InferredPlan.Prompt(evidence, digest.Partial, entry.Inferred), timeout.Token);
        if (steps.Count == 0)
        {
            if (entry.Inferred.Count == 0)
                _current = new(id, "", "A IA não registrou etapas e não foi possível montar o roteiro agora. Nova tentativa em breve.", entry.UpdatedAt) { StartedAt = digest.StartedAt };
            return;
        }
        entry.Inferred = steps;
        entry.Fingerprint = fingerprint;
        entry.UpdatedAt = DateTimeOffset.Now;
        try
        {
            string file = CacheFile(id);
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllText(file + ".tmp", JsonSerializer.Serialize(new Saved(steps.ToList(), fingerprint, entry.UpdatedAt)));
            File.Move(file + ".tmp", file, true);
        }
        catch (Exception ex) { App.Log("Salvar acompanhamento: " + ex.Message); }
        if (_session() is { } selected && selected.Kind + ":" + selected.Id == id)
            _current = new(id, "", "Roteiro deduzido pela IA a partir da conversa", entry.UpdatedAt) { Steps = steps, Inferred = true, StartedAt = digest.StartedAt };
    }

    public void Dispose() => _stop.Cancel();
}
