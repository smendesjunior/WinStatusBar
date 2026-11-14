using WinStatusBar.Core.Ai.Antigravity;

namespace CodexIAs.Providers;

/// <summary>
/// Google Antigravity pelo CLI oficial (agy), com a conta Google. A lista vem do próprio agy ("agy models": Gemini,
/// Claude e GPT-OSS liberados para a conta), então modelos novos entram e os que saem da conta saem na verificação.
/// Cada pedido roda o agy com o histórico do Codex como contexto, só leitura na pasta da conversa (as ações quem
/// executa é o Codex, como nas outras IAs da barra).
/// </summary>
public sealed class AntigravityProvider : IProvider
{
    public string Name => "antigravity";
    public string Label => "Google Antigravity";

    /// <summary>Tamanho do pacote de passagem (como o do Claude).</summary>
    internal const int TranscriptChars = 60000;

    private static readonly Lazy<KillOnCloseJob?> Job = new(KillOnCloseJob.Create);

    public async Task<Listing> ListAsync(CancellationToken ct)
    {
        if (AntigravityCli.FindExecutable() is null) return Listing.Off("Antigravity (agy) não instalado neste PC");
        var models = await AntigravityCli.ModelsAsync(ct, fresh: true);
        if (models is null) return Listing.Fail("o Antigravity (agy) não respondeu");
        if (models.Count == 0) return Listing.SignIn("Antigravity sem login (rode \"agy\" num terminal e entre com a conta Google)");
        return new(ToModels(models));
    }

    internal static IReadOnlyList<AiModel> ToModels(IReadOnlyList<AntigravityCli.Model> models)
        => models.Select(m => new AiModel(
                CodexCatalog.Prefix + "antigravity-" + Slug.From(m.Id), "antigravity", m.Id, AntigravityCli.DisplayName(m),
                "Google Antigravity pela conta, pela WinStatusBar. Responde em texto e pode ler a pasta da conversa.",
                [], null, -5))
            .DistinctBy(m => m.Slug).ToList();

    public async Task<Answer> RunAsync(AiModel model, string? effort, Conversation conversation, Action<string> onText, CancellationToken ct)
    {
        string workDir = conversation.Cwd is { } wd && Directory.Exists(wd) ? wd : AntigravityCli.WorkFolder();
        string prompt = ConversationReader.WithTranscript(conversation, TranscriptChars, workDir);
        var args = AntigravityCli.Arguments(model.Id, effort, null, AntigravityCli.ReadOnlyDeny);
        var run = await AntigravityCli.RunAsync(prompt, args, workDir, onText, ct, p => Job.Value?.Add(p));
        return new(run.Text, run.Error);
    }
}
