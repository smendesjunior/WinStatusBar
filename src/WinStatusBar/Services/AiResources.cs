using WinStatusBar.Core.Ai.Local;

namespace WinStatusBar.Services;

/// <summary>
/// Recursos que cada IA do chat mantém abertos fora da barra (11/11/2026): a página oculta do Gemini e do Copilot
/// (WebView2, centenas de MB), o Lenovo Qira inteiro (1,8 a 2,6 GB) e o modelo da IA local no Ollama.
/// Regra: escolher uma IA não abre nada; os recursos abrem no envio da pergunta, fecham pouco depois da resposta
/// (espera das configurações) e, quando a pessoa troca de IA, os da anterior fecham na hora.
/// </summary>
internal static class AiResources
{
    private static readonly HttpClient Ollama = new() { Timeout = TimeSpan.FromSeconds(3) };

    /// <summary>A pessoa escolheu outra IA no chat: fecha o que as demais deixaram aberto. Quem estiver respondendo fica.</summary>
    public static void ReleaseOthers(string current)
    {
        if (current == "auto") return; // a IA só é conhecida no envio
        if (current != "gemini") Run("Gemini", async () => await GeminiWebSession.ReleaseAsync());
        if (current != "copilot") Run("Copilot", async () => await CopilotWebSession.ReleaseAsync());
        if (current != "qira") Run("Qira", async () => await QiraHubSession.ReleaseAsync());
        if (current != "local") Run("IA local", ReleaseLocalAsync);
    }

    /// <summary>A barra vai fechar: nada do que ela abriu fica para trás.</summary>
    public static void ReleaseAllForExit()
    {
        try { QiraHubSession.ReleaseAsync().Wait(TimeSpan.FromSeconds(4)); } catch { }
    }

    /// <summary>Modelos do Ollama carregados: libera agora, menos quando a pessoa escolheu "Sempre carregado".</summary>
    private static async Task ReleaseLocalAsync()
    {
        if (App.Current.Settings.Current.LocalModelKeepAlive == "-1") return;
        if (await OllamaModels.LoadedAsync(Ollama, CancellationToken.None) is not { Count: > 0 } loaded) return;
        foreach (var m in loaded) await OllamaModels.UnloadAsync(Ollama, m.Name, CancellationToken.None);
        App.Log($"IA local: {loaded.Count} modelo(s) liberado(s) na troca de IA");
    }

    private static async void Run(string what, Func<Task> release)
    {
        try { await release(); }
        catch (Exception ex) { App.Log($"Liberar recursos ({what}): {ex.Message}"); }
    }
}
