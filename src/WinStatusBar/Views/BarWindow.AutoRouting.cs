using System.Diagnostics;
using WinStatusBar.Core.Ai;
using WinStatusBar.Services;
using T = WinStatusBar.Core.Ai.AgentPrompt.Target;

namespace WinStatusBar.Views;

public sealed partial class BarWindow
{
    // Tempos da escolha automática. As listas de modelos têm prazo próprio, para não gastar o tempo das tentativas.
    private static readonly TimeSpan RouteCatalogWait = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan RouteAttemptWait = TimeSpan.FromSeconds(12);
    private static readonly TimeSpan RouteAttemptsWait = TimeSpan.FromSeconds(30);
    /// <summary>Por quanto tempo a lista de modelos de uma conta vale para a escolha, sem consultar de novo a cada conversa.</summary>
    private static readonly TimeSpan RouteCatalogTtl = TimeSpan.FromMinutes(5);
    private readonly Dictionary<T, DateTime> _routeCatalogAt = new();

    private string AutoChoiceNote(T target, string? model, string? effort, string reason)
        => "Escolhido automaticamente: " + DestinationName(AgentPrompt.Key(target)) + " · " + ModelLabel(target, model)
           + " · " + (effort is null ? "padrão" : AgentPrompt.EffortLabel(effort)) + " — " + reason;

    /// <summary>
    /// Lista de modelos para a escolha. Contas e APIs reaproveitam a lista guardada por alguns minutos; IA local, Qira e
    /// Copilot são lidos sempre, porque a leitura é local e a disponibilidade deles muda (servidor parado, app fechado).
    /// Null quando a lista não chegou no prazo: o destino fica fora desta escolha.
    /// </summary>
    private async Task<AgentPrompt.Catalog?> RouteCatalogAsync(T t, CancellationToken ct)
    {
        bool fresh = t is not (T.Local or T.Qira or T.Copilot)
                     && _routeCatalogAt.TryGetValue(t, out var at) && DateTime.UtcNow - at < RouteCatalogTtl
                     && _catalogs.TryGetValue(t, out var cached) && cached.Error is null;
        try
        {
            var catalog = await CatalogFor(t, reload: !fresh).WaitAsync(RouteCatalogWait, ct);
            if (!fresh) _routeCatalogAt[t] = DateTime.UtcNow;
            return catalog;
        }
        catch (TimeoutException) { return null; }
    }

    /// <summary>Situação de cada destino agora (cota usada, tipo de conta), para o auxiliar pesar custo e disponibilidade.</summary>
    private Dictionary<T, string> RouteNotes()
    {
        var notes = new Dictionary<T, string>();
        var now = DateTimeOffset.Now;
        void Plan(T t, QuotaSnapshot? quota)
        {
            if (quota is null) return;
            if (quota.IsBlocked(now)) { notes[t] = "cota esgotada agora; não escolha"; return; }
            var parts = new List<string>();
            if (quota.Primary is { } p && !p.IsExpired(now)) parts.Add($"cota de 5 h: {Math.Round(p.UsedPercent)}% usada");
            if (quota.Secondary is { } s && !s.IsExpired(now)) parts.Add($"cota de 7 dias: {Math.Round(s.UsedPercent)}% usada");
            if (parts.Count > 0) notes[t] = string.Join("; ", parts);
        }
        Plan(T.Codex, _hub.Codex.Quota);
        Plan(T.Claude, _hub.Claude.Quota);
        notes[T.Gemini] = _settings.Current.GeminiAuth == "api" ? "chave de API do Google (cobrança por uso)" : "conta Google (sem custo por pedido)";
        if (QiraBridge.CurrentQuota is { Unlimited: false } credits) notes[T.Qira] = $"créditos Qira: {Math.Round(credits.PercentRemaining)}% restantes";
        return notes;
    }

    private async Task<AutoDestinationRouter.Choice> ChooseDestinationAsync(string prompt, CancellationToken ct)
    {
        var clock = Stopwatch.StartNew();
        T defaultTarget = DefaultTarget();
        var (defaultModel, defaultEffort) = Effective(defaultTarget, _catalogs.GetValueOrDefault(defaultTarget));
        var fallback = new AutoDestinationRouter.Choice(defaultTarget, defaultModel, defaultEffort, "destino padrão; seleção automática indisponível",
            _catalogs.TryGetValue(defaultTarget, out var cached) && AutoDestinationRouter.Eligible(defaultTarget, cached));
        var catalogs = new Dictionary<T, AgentPrompt.Catalog>();
        string how = "destino padrão";
        try
        {
            var api = ApiConfig();
            // Contas, APIs e o Qira (se instalado e ligado) são lidos juntos; uma lista lenta não derruba as outras.
            bool qira = QiraAppBridge.IsInstalled && _settings.Current.QiraEnabled;
            var results = await Task.WhenAll(Enum.GetValues<T>().Where(t => t != T.Local && (t != T.Qira || qira))
                .Select(async t => (Target: t, Catalog: await RouteCatalogAsync(t, ct))));
            foreach (var item in results) if (item.Catalog is not null) catalogs[item.Target] = item.Catalog;
            // IA local: só é consultada quando não há conta nem API (último recurso), para não atrasar o caso comum.
            if (_settings.Current.LocalEnabled && !catalogs.Any(x => AutoDestinationRouter.Eligible(x.Key, x.Value))
                && await RouteCatalogAsync(T.Local, ct) is { } local) catalogs[T.Local] = local;

            // Keep DefaultTarget+Effective as the normal fallback. If that destination does not exist
            // on this PC, use a concrete available one instead of sending to an absent CLI.
            string? reason = null;
            if (!catalogs.TryGetValue(defaultTarget, out var defaultCatalog) || !AutoDestinationRouter.Eligible(defaultTarget, defaultCatalog))
            {
                if (AutoDestinationRouter.Routers(catalogs).FirstOrDefault() is { } available) defaultTarget = available.Target;
                else if (catalogs.FirstOrDefault(x => AutoDestinationRouter.Eligible(x.Key, x.Value)) is { Value: not null } other) defaultTarget = other.Key;
                else if (AutoDestinationRouter.LastResort(catalogs) is { } last) { defaultTarget = last; reason = AutoDestinationRouter.LastResortReason(last); how = "último recurso"; }
            }
            var effective = Effective(defaultTarget, catalogs.GetValueOrDefault(defaultTarget));
            fallback = fallback with { Target = defaultTarget, Model = effective.Model, Effort = effective.Effort, Reason = reason ?? fallback.Reason,
                CanRun = catalogs.TryGetValue(defaultTarget, out var concrete) && AutoDestinationRouter.Available(concrete) };

            using var attempts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            attempts.CancelAfter(RouteAttemptsWait);
            string request = AutoDestinationRouter.Prompt(prompt, catalogs, RouteNotes(), new(_attachments.Count, _allowEdits, _web));
            foreach (var auxiliary in AutoDestinationRouter.Routers(catalogs))
            {
                ct.ThrowIfCancellationRequested();
                if (attempts.IsCancellationRequested) break;
                T router = auxiliary.Target;
                using var attempt = CancellationTokenSource.CreateLinkedTokenSource(attempts.Token);
                attempt.CancelAfter(RouteAttemptWait);
                AgentPrompt.Result result;
                try
                {
                    result = await Task.Run(() => AgentPrompt.RunAsync(router, request,
                        auxiliary.Model, auxiliary.Effort, router == T.Api ? api : null,
                        AgentPrompt.Options.None with { Ephemeral = true }, _ => { }, attempt.Token), attempt.Token).WaitAsync(attempt.Token);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested) { continue; }
                ct.ThrowIfCancellationRequested();
                if (result.Error is not null) continue;
                // Resposta que não serve (destino inventado, JSON quebrado): tenta o próximo auxiliar antes do destino padrão.
                if (AutoDestinationRouter.Parse(result.Text, catalogs) is not { } choice) continue;
                App.Log($"Escolha por mim: {AgentPrompt.Key(choice.Target)} · auxiliar {AgentPrompt.Key(router)} · {clock.ElapsedMilliseconds} ms");
                return choice;
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
        catch (Exception ex) when (!ct.IsCancellationRequested) { App.Log("Roteador automático: " + ex.Message); }
        ct.ThrowIfCancellationRequested();
        App.Log($"Escolha por mim: {AgentPrompt.Key(fallback.Target)} · {how} · {clock.ElapsedMilliseconds} ms");
        return fallback;
    }
}
