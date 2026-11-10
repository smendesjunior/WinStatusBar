using System.Text;

namespace CodexIAs;

/// <summary>Uma opção do seletor do Codex: provedor, identificador interno, nome e níveis de pensamento.</summary>
public sealed record AiModel(string Slug, string Provider, string Id, string DisplayName, string Description,
    IReadOnlyList<string> Efforts, string? DefaultEffort, int Priority);

/// <summary>Resposta de uma IA. Error preenchido quando falhou (vira texto da resposta no Codex).</summary>
public sealed record Answer(string Text, string? Error);

/// <summary>Pedido atual, histórico (falas da pessoa e respostas), pasta da conversa e chave da conversa no Codex.</summary>
public sealed record Conversation(string Prompt, IReadOnlyList<(string Role, string Content)> History, string? Cwd, string? Key);

/// <summary>
/// Leitura de uma IA na verificação: as opções dela, ou o motivo de ficar fora do Codex. <see cref="Failed"/> = falha
/// passageira (página fora do ar, programa sem resposta): as opções da verificação anterior continuam.
/// </summary>
public sealed record Listing(IReadOnlyList<AiModel> Models, string? Issue = null, bool Failed = false, bool NeedsLogin = false)
{
    /// <summary>IA não configurada neste PC (sem login, sem chave, não instalada): fica fora do Codex.</summary>
    public static Listing Off(string why) => new([], why);
    /// <summary>Site sem login (a barra mostra "Entrar" na linha dele).</summary>
    public static Listing SignIn(string why) => new([], why, false, true);
    /// <summary>Não deu para ler agora: mantém o que já estava.</summary>
    public static Listing Fail(string why) => new([], why, true);
}

public interface IProvider
{
    /// <summary>Nome curto usado no identificador do modelo (codexias-&lt;nome&gt;-…).</summary>
    string Name { get; }
    string Label { get; }
    /// <summary>Lê de novo da fonte (Claude Code, site, API…) as opções desta IA. Só na verificação manual.</summary>
    Task<Listing> ListAsync(CancellationToken ct);
    Task<Answer> RunAsync(AiModel model, string? effort, Conversation conversation, Action<string> onText, CancellationToken ct);
    /// <summary>Com linhas de andamento separadas do texto (só o Claude com comandos usa); os outros provedores só mandam texto.</summary>
    Task<Answer> RunAsync(AiModel model, string? effort, Conversation conversation, Action<string> onText, Action<string> onStatus, CancellationToken ct)
        => RunAsync(model, effort, conversation, onText, ct);
}

internal static class Paths
{
    /// <summary>Porta do encaminhador do Codex (a mesma do encaminhador em Python que ele substitui).</summary>
    public const int GatewayPort = WinStatusBar.Core.Ai.Codex.CodexIntegration.GatewayPort;
    /// <summary>Dados ficam com os da barra (o componente faz parte do pacote dela).</summary>
    public static string DataDir => WinStatusBar.Core.Ai.Codex.CodexIntegration.DataDir;
    /// <summary>Pasta do Codex IAs avulso (antes de ir para o pacote da barra): registro da OpenAI e modos são migrados.</summary>
    public static string OldDataDir => Path.Combine(LocalAppData, "Codex IAs");
    public static string ModesFile => WinStatusBar.Core.Ai.Codex.CodexIntegration.ModesFile;
    public static string ModelsFile => Path.Combine(DataDir, "modelos.json");
    public static string LogFile => Path.Combine(DataDir, "codex-ias.log");
    public static string LocalAppData => Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
    public static string CodexHome => Environment.GetEnvironmentVariable("CODEX_HOME") is { Length: > 0 } h ? h
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex");

    /// <summary>Uma vez: traz o registro de modelos da OpenAI e os modos dos sites da pasta antiga.</summary>
    public static void MigrateOldData()
    {
        try
        {
            Directory.CreateDirectory(DataDir);
            foreach (var name in new[] { "openai-modelos.json", "modos.json" })
            {
                string old = Path.Combine(OldDataDir, name), now = Path.Combine(DataDir, name);
                if (File.Exists(old) && !File.Exists(now)) File.Copy(old, now);
            }
        }
        catch { }
    }
}

internal static class Log
{
    private static readonly object Sync = new();
    /// <summary>Desligado nos testes (não gravam no log real).</summary>
    public static bool Enabled { get; set; } = true;

    public static void Write(string message)
    {
        if (!Enabled) return;
        try
        {
            lock (Sync)
            {
                Directory.CreateDirectory(Paths.DataDir);
                var file = new FileInfo(Paths.LogFile);
                if (file.Exists && file.Length > 1024 * 1024)
                {
                    string old = Paths.LogFile + ".1";
                    File.Delete(old);
                    File.Move(Paths.LogFile, old);
                }
                File.AppendAllText(Paths.LogFile, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {message}{Environment.NewLine}", Encoding.UTF8);
            }
        }
        catch { }
    }
}

internal static class Slug
{
    /// <summary>Texto livre → pedaço de identificador (minúsculas, sem acento, só letras, números e hífen).</summary>
    public static string From(string text)
    {
        string normalized = text.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder();
        foreach (char c in normalized)
        {
            if (System.Globalization.CharUnicodeInfo.GetUnicodeCategory(c) == System.Globalization.UnicodeCategory.NonSpacingMark) continue;
            char l = char.ToLowerInvariant(c);
            if (l is >= 'a' and <= 'z' or >= '0' and <= '9') sb.Append(l);
            else if (l == '.') sb.Append('-');
            else if (sb.Length > 0 && sb[^1] != '-') sb.Append('-');
        }
        return sb.ToString().Trim('-');
    }
}
