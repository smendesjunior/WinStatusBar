using WinStatusBar.Core.Ai;
using WinStatusBar.Services;
using T = WinStatusBar.Core.Ai.AgentPrompt.Target;

namespace WinStatusBar.Views;

public sealed partial class BarWindow
{
    private string AutoChoiceNote(T target, string? model, string? effort, string reason)
        => "Escolhido automaticamente: " + DestinationName(AgentPrompt.Key(target)) + " · " + ModelLabel(target, model)
           + " · " + (effort is null ? "padrão" : AgentPrompt.EffortLabel(effort)) + " — " + reason;

    private async Task<AutoDestinationRouter.Choice> ChooseDestinationAsync(string prompt, CancellationToken ct)
    {
        T defaultTarget = DefaultTarget();
        var (defaultModel, defaultEffort) = Effective(defaultTarget, _catalogs.GetValueOrDefault(defaultTarget));
        var fallback = new AutoDestinationRouter.Choice(defaultTarget, defaultModel, defaultEffort, "destino padrão; seleção automática indisponível",
            _catalogs.TryGetValue(defaultTarget, out var cached) && AutoDestinationRouter.Eligible(defaultTarget, cached));
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(25));
        var catalogs = new Dictionary<T, AgentPrompt.Catalog>();
        try
        {
            var api = ApiConfig();
            AdditionalAiAccounts.PrepareGemini(_settings.Current);
            // Network discovery and CLI/config reads stay off the UI thread. Each discovery has an independent catalog.
            var results = await Task.WhenAll(Enum.GetValues<T>().Where(t => t is not (T.Local or T.Qira)).Select(async t =>
                (Target: t, Catalog: await Task.Run(() => AgentPrompt.CatalogAsync(t, t == T.Api ? api : null, timeout.Token), timeout.Token)))).WaitAsync(timeout.Token);
            foreach (var item in results) catalogs[item.Target] = item.Catalog;
            // Keep DefaultTarget+Effective as the normal fallback. If that destination does not exist
            // on this PC, use a concrete available one instead of sending to an absent CLI.
            if (!catalogs.TryGetValue(defaultTarget, out var defaultCatalog) || !AutoDestinationRouter.Eligible(defaultTarget, defaultCatalog))
            {
                var available = AutoDestinationRouter.Routers(catalogs).FirstOrDefault();
                if (available is not null) defaultTarget = available.Target;
            }
            var effective = Effective(defaultTarget, catalogs.GetValueOrDefault(defaultTarget));
            fallback = fallback with { Target = defaultTarget, Model = effective.Model, Effort = effective.Effort,
                CanRun = catalogs.TryGetValue(defaultTarget, out var concrete) && AutoDestinationRouter.Eligible(defaultTarget, concrete) };
            foreach (var auxiliary in AutoDestinationRouter.Routers(catalogs))
            {
                ct.ThrowIfCancellationRequested();
                T router = auxiliary.Target;
                using var attempt = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
                attempt.CancelAfter(TimeSpan.FromSeconds(15));
                AgentPrompt.Result result;
                try
                {
                    result = await Task.Run(() => AgentPrompt.RunAsync(router, AutoDestinationRouter.Prompt(prompt, catalogs),
                        auxiliary.Model, auxiliary.Effort, router == T.Api ? api : null,
                        AgentPrompt.Options.None with { Ephemeral = true }, _ => { }, attempt.Token), attempt.Token).WaitAsync(attempt.Token);
                }
                catch (OperationCanceledException) when (!timeout.IsCancellationRequested && !ct.IsCancellationRequested) { continue; }
                ct.ThrowIfCancellationRequested();
                if (result.Error is not null) continue;
                return AutoDestinationRouter.Resolve(result.Text, catalogs, fallback);
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
        catch (Exception ex) when (!ct.IsCancellationRequested) { App.Log("Roteador automático: " + ex.Message); }
        ct.ThrowIfCancellationRequested();
        return fallback;
    }
}
