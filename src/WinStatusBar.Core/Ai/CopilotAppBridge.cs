namespace WinStatusBar.Core.Ai;

/// <summary>
/// Microsoft Copilot no chat da barra. A execução é fornecida pela aplicação (Copilot embutido num WebView2 oculto,
/// página oficial, perfil próprio): pergunta e resposta ficam na barra. Não há API pública do Copilot pessoal.
/// A antiga automação do app Copilot por teclado/acessibilidade foi removida (07/11/2026): ela trazia a janela do
/// Copilot para frente e digitava nela.
/// </summary>
public static class CopilotAppBridge
{
    /// <summary>Executor instalado pela aplicação; sem ele, o destino informa que está indisponível.</summary>
    /// <summary>Executor instalado pela aplicação (pergunta, modo do site, opções); sem ele, o destino informa que está indisponível.</summary>
    public static Func<string, string?, AgentPrompt.Options, Action<string>, CancellationToken, Task<AgentPrompt.Result>>? Runner { get; set; }
    /// <summary>Null quando pronto; senão, a orientação para a pessoa (por exemplo, entrar na conta).</summary>
    public static Func<string?>? Available { get; set; }

    /// <summary>
    /// "Modo escolhido no site" e os modos do seletor do Copilot (ex.: "Think Deeper", submenu "GPT"), lidos na última
    /// verificação de Configurações › Codex e modelos (não há lista fixa no código).
    /// </summary>
    public static AgentPrompt.Catalog Catalog()
    {
        string? error = Runner is null ? "Copilot indisponível nesta instalação da barra." : Available?.Invoke();
        if (error is not null) return new([], "app", null, error);
        var models = new List<AgentPrompt.ModelOption> { new("app", "Modo escolhido no site (conta Microsoft)", [], null) };
        models.AddRange(Codex.CodexIntegration.SiteModes("copilot").Select(m => new AgentPrompt.ModelOption(m.Id, m.Label, [], null)));
        return new(models, "app", null, null);
    }

    public static Task<AgentPrompt.Result> RunAsync(string prompt, AgentPrompt.Options options, Action<string> onText, CancellationToken ct)
        => RunAsync(prompt, null, options, onText, ct);

    public static Task<AgentPrompt.Result> RunAsync(string prompt, string? model, AgentPrompt.Options options, Action<string> onText, CancellationToken ct)
        => Runner is { } run ? run(prompt, model, options, onText, ct)
            : Task.FromResult(new AgentPrompt.Result("", null, "Copilot indisponível nesta instalação da barra."));
}
