using System.IO;
using System.Threading;
using System.Threading.Tasks;
using WinStatusBar.Core.Ai;

namespace WinStatusBar.Services;

/// <summary>
/// Liga a ponte de modelos do Codex (CodexModelBridge) às IAs da barra: usa o modelo escolhido para cada IA no painel
/// e mostra a atividade na barra como qualquer pedido feito por ela.
/// </summary>
internal static class CodexBridgeHost
{
    private static CodexModelBridge? _bridge;
    public static string InfoPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WinStatusBar", "codex-bridge.json");

    public static void Start()
    {
        try
        {
            _bridge = CodexModelBridge.Start(RunAsync, InfoPath);
            App.Log(_bridge is null ? "Ponte de modelos do Codex: nenhuma porta livre." : $"Ponte de modelos do Codex na porta {_bridge.Port}.");
        }
        catch (Exception ex) { App.Log("Ponte de modelos do Codex: " + ex.Message); }
    }

    public static void Stop() { _bridge?.Dispose(); _bridge = null; }

    private static async Task<AgentPrompt.Result> RunAsync(CodexModelBridge.Model model, string prompt, AgentPrompt.Options options, Action<string> onText, CancellationToken ct)
    {
        var app = App.Current;
        var settings = app.Settings.Current;
        string key = AgentPrompt.Key(model.Target);
        if (model.Target == AgentPrompt.Target.Gemini) AdditionalAiAccounts.PrepareGemini(settings);
        string? chosen = model.Target == AgentPrompt.Target.Qira ? QiraBridge.ModelId
            : settings.CommandModels.TryGetValue(key, out var m) && m is { Length: > 0 } ? m : null;
        // O Claude Code aparece na barra pelos próprios eventos; as demais IAs entram como atividade de chat.
        string conversation = "codex-bridge-" + Guid.NewGuid().ToString("N");
        string? run = model.Target == AgentPrompt.Target.Claude ? null
            : app.Hub.ApiActivity.Begin(conversation, key, model.Name, model.Target == AgentPrompt.Target.Qira ? "híbrido" : chosen);
        App.Log($"Codex pediu resposta a {model.Name}.");
        var started = DateTime.UtcNow;
        AgentPrompt.Result result;
        try { result = await AgentPrompt.RunAsync(model.Target, prompt, chosen, null, null, options, onText, ct); }
        catch (OperationCanceledException) { result = new("", null, "Interrompido"); }
        if (run is not null) app.Hub.ApiActivity.End(conversation, run, ct.IsCancellationRequested, result.Error);
        App.Log($"Codex recebeu {model.Name} em {(DateTime.UtcNow - started).TotalSeconds:0.0} s{(result.Error is null ? "" : " · " + result.Error)}.");
        return result;
    }
}
