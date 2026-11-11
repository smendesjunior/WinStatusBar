using System.Text.Json;

namespace WinStatusBar.Services;

/// <summary>Uma conversa da barra de comando, de qualquer IA (Claude Code, Codex, IA local ou API).</summary>
internal sealed class SavedConversation
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Title { get; set; } = string.Empty;
    /// <summary>Contexto resumido para o histórico (assuntos perguntados e o começo da última resposta).</summary>
    public string Summary { get; set; } = string.Empty;
    /// <summary>Resumo escrito pela IA local, quando houve (senão null).</summary>
    public string? AiSummary { get; set; }
    public string Target { get; set; } = string.Empty;   // "claude", "codex", "local", "api"
    public string? ApiEndpoint { get; set; }
    public string? Model { get; set; }
    public string? Effort { get; set; }
    public string? AutoReason { get; set; }
    public string? Session { get; set; }                 // sessão do CLI (Claude/Codex) para continuar
    public bool ImportedToApp { get; set; }
    public string? ProjectName { get; set; }
    public List<string> ProjectPaths { get; set; } = new();
    /// <summary>Pasta de trabalho da conversa (o Claude só retoma a sessão na mesma pasta).</summary>
    public string? WorkDir { get; set; }
    public DateTimeOffset Created { get; set; } = DateTimeOffset.Now;
    public DateTimeOffset Updated { get; set; } = DateTimeOffset.Now;
    public List<SavedMessage> Messages { get; set; } = new();
}

internal sealed class SavedMessage
{
    public string Role { get; set; } = "user";
    public string Content { get; set; } = string.Empty;
    /// <summary>
    /// Só nas perguntas: IA, modelo e nível usados nesse envio (a conversa pode trocar de IA no meio, como no Codex).
    /// Conversas antigas não têm; valem os dados da conversa.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public string? Agent { get; set; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public string? Model { get; set; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public string? Effort { get; set; }
}

/// <summary>
/// Histórico universal das conversas da barra de comando:
/// um arquivo JSON por conversa (gravação atômica), por padrão no OneDrive para sincronizar entre PCs.
/// </summary>
internal static class ConversationStore
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };
    private static readonly Lock WriteGate = new();
    /// <summary>Pasta escolhida nas configurações (vazio = padrão).</summary>
    public static string? CustomFolder { get; set; }

    /// <summary>
    /// Padrão: OneDrive pessoal\WinStatusBar\Conversas (sincroniza e volta depois de formatar o PC);
    /// sem OneDrive pessoal (nunca usa o da empresa), %LOCALAPPDATA%\WinStatusBar\conversas.
    /// </summary>
    public static string DefaultFolder
    {
        get
        {
            // Sempre o OneDrive pessoal (nunca o da empresa).
            // Lido também do registro: o OneDrive pode ter sido configurado depois de a barra abrir.
            if (WinStatusBar.Core.Settings.OneDrivePaths.Personal is { } od)
                return Path.Combine(od, "WinStatusBar", "Conversas");
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WinStatusBar", "conversas");
        }
    }

    public static string Folder => string.IsNullOrWhiteSpace(CustomFolder) ? DefaultFolder : CustomFolder!;

    /// <summary>Subpasta de cada serviço no histórico (pedido de 07/11/2026): uma pasta por IA.</summary>
    public static string ServiceFolder(string target) => (target ?? "").ToLowerInvariant() switch
    {
        "claude" => "Claude Code",
        "codex" => "Codex",
        "gemini" => "Gemini",
        "copilot" => "Microsoft Copilot",
        "local" => "IA local",
        "api" => "API",
        "claude-chat" or "claudechat" => "Claude",
        "chatgpt" or "chatgpt-work" => "ChatGPT",
        "" => "Outros",
        var other => string.Concat(other.Split(Path.GetInvalidFileNameChars())),
    };

    private static string PathFor(string root, SavedConversation c) => Path.Combine(root, ServiceFolder(c.Target), c.Id + ".json");

    /// <summary>
    /// Arquivo existente da conversa (na pasta do serviço ou, se antigo, na raiz).
    /// <paramref name="expected"/> é onde a conversa deve estar: conferido antes da busca em todas as pastas, que
    /// percorria o histórico inteiro (no OneDrive) a cada salvamento. A busca continua valendo quando o arquivo não
    /// está lá (primeiro salvamento, ou conversa que trocou de IA e precisa ser movida).
    /// </summary>
    private static string? Find(string root, string id, string? expected = null)
    {
        try
        {
            string legacy = Path.Combine(root, id + ".json");
            if (File.Exists(legacy)) return legacy;
            if (expected is not null && File.Exists(expected)) return expected;
            return Directory.Exists(root) ? Directory.EnumerateFiles(root, id + ".json", SearchOption.AllDirectories).FirstOrDefault() : null;
        }
        catch { return null; }
    }

    public static void Save(SavedConversation c)
    {
        lock (WriteGate)
        {
        try
        {
            string root = Folder;
            string path = PathFor(root, c);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            // Background saves can arrive out of order (reply completion and summary).
            string? current = Find(root, c.Id, path);
            if (current is not null)
            {
                try
                {
                    var existing = JsonSerializer.Deserialize<SavedConversation>(File.ReadAllText(current), Json);
                    if (existing is not null && existing.Updated > c.Updated) return;
                }
                catch (JsonException) { } // a valid save repairs a damaged JSON file
            }
            string tmp = path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(c, Json));
            File.Move(tmp, path, overwrite: true);
            if (current is not null && !string.Equals(current, path, StringComparison.OrdinalIgnoreCase)) File.Delete(current);
        }
        catch (Exception ex) { App.Log("Histórico de conversas: não foi possível salvar: " + ex.Message); }
        }
        if (c.Target == "codex" && c.Session is { Length: > 0 } session && c.Title.Length > 0) CodexThreadName(session, c.Title);
    }

    private static readonly HashSet<string> NamedThreads = new();
    /// <summary>Nome da conversa na barra lateral do app Codex (método oficial thread/name/set), uma vez por título.</summary>
    private static void CodexThreadName(string threadId, string title)
    {
        string name = title.Length > 60 ? title[..59].TrimEnd() + "…" : title;
        lock (NamedThreads) if (!NamedThreads.Add(threadId + "|" + name)) return;
        _ = Task.Run(async () =>
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            if (!await WinStatusBar.Core.Ai.Codex.CodexComposer.SetThreadNameAsync(threadId, name, cts.Token))
            {
                lock (NamedThreads) NamedThreads.Remove(threadId + "|" + name);
                App.Log("Histórico do Codex: nome da conversa não aplicado (tentará de novo no próximo salvamento).");
            }
        });
    }

    /// <summary>
    /// Move as conversas antigas (na raiz) para a pasta do serviço de cada uma. Seguro para repetir; retorna quantas
    /// foram movidas. Arquivos ilegíveis ficam onde estão.
    /// </summary>
    public static int Categorize(string? root = null)
    {
        root ??= Folder;
        int moved = 0;
        lock (WriteGate)
        {
            if (!Directory.Exists(root)) return 0;
            foreach (string file in Directory.EnumerateFiles(root, "*.json", SearchOption.TopDirectoryOnly).ToList())
            {
                try
                {
                    var c = JsonSerializer.Deserialize<SavedConversation>(File.ReadAllText(file), Json);
                    if (c is null) continue;
                    string target = Path.Combine(root, ServiceFolder(c.Target), Path.GetFileName(file));
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    if (File.Exists(target)) { File.Delete(file); } else File.Move(file, target);
                    moved++;
                }
                catch { }
            }
        }
        return moved;
    }

    /// <summary>Conversas mais recentes primeiro (todas as pastas de serviço e as antigas da raiz).</summary>
    public static List<SavedConversation> List(int max = 200)
    {
        var list = new List<SavedConversation>();
        try
        {
            if (!Directory.Exists(Folder)) return list;
            foreach (var f in Directory.EnumerateFiles(Folder, "*.json", SearchOption.AllDirectories).OrderByDescending(File.GetLastWriteTimeUtc).Take(max))
            {
                try { if (JsonSerializer.Deserialize<SavedConversation>(File.ReadAllText(f), Json) is { } c) list.Add(c); }
                catch { }
            }
        }
        catch { }
        return list.OrderByDescending(c => c.Updated).ToList();
    }

    public static void Delete(string id)
    {
        lock (WriteGate)
        try { if (Find(Folder, id) is { } file) File.Delete(file); } catch { }
    }

    public static void Clear()
    {
        lock (WriteGate)
        try { if (Directory.Exists(Folder)) foreach (var f in Directory.EnumerateFiles(Folder, "*.json", SearchOption.AllDirectories)) File.Delete(f); } catch { }
    }
}
