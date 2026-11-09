using WinStatusBar.Core.Ai;

namespace CodexIAs.Providers;

/// <summary>
/// Modelos locais (Ollama, LM Studio, llama.cpp, Jan…) no Codex, pela mesma chamada que a barra faz. Fica de fora quando
/// o Codex já recebe os modelos locais pelo próprio Ollama ("ollama launch codex"), para não aparecerem repetidos.
/// </summary>
internal sealed class LocalProvider : IProvider
{
    private bool _catalogRead;

    public string Name => "local";
    public string Label => "IA local";

    private static bool OllamaLaunch => File.Exists(Path.Combine(Paths.CodexHome, "ollama-launch-codex-routing.json"));

    public async Task<Listing> ListAsync(CancellationToken ct)
    {
        if (OllamaLaunch) return Listing.Off("os modelos locais já chegam ao Codex pelo Ollama (ollama launch)");
        var catalog = await AgentPrompt.CatalogAsync(AgentPrompt.Target.Local, null, ct);
        _catalogRead = true;
        if (catalog.Models.Count == 0) return Listing.Off(catalog.Error ?? "nenhum servidor local respondeu");
        return new(catalog.Models.Select(m => new AiModel(CodexCatalog.Prefix + "local-" + Slug.From(m.Id), Name, m.Id, "Local — " + m.Label,
                "Modelo local neste PC, pela WinStatusBar (sem internet e sem custo). Só texto.", m.Efforts, m.DefaultEffort, -1))
            .DistinctBy(m => m.Slug).ToList());
    }

    public async Task<Answer> RunAsync(AiModel model, string? effort, Conversation conversation, Action<string> onText, CancellationToken ct)
    {
        // A leitura da lista também escolhe o servidor local (Ollama ou outro compatível com a OpenAI).
        if (!_catalogRead) { await AgentPrompt.CatalogAsync(AgentPrompt.Target.Local, null, ct); _catalogRead = true; }
        var result = await AgentPrompt.RunAsync(AgentPrompt.Target.Local, ConversationReader.WithTranscript(conversation.Prompt, conversation.History),
            model.Id, effort, null, AgentPrompt.Options.None, onText, ct);
        return new(result.Text, result.Error);
    }
}
