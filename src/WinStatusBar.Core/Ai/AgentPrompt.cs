using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using WinStatusBar.Core.Ai.Codex;

namespace WinStatusBar.Core.Ai;

/// <summary>
/// Barra de comando da IA: envia um prompt como conversa NOVA e devolve a resposta em partes.
/// • Claude Code e Codex: CLI oficial / app-server; autenticação permanece com o provedor.
///   O modo de edição habilita o executor comum de desktop; aprovações nativas permanecem explícitas.
/// • IA local: Ollama em 127.0.0.1:11434.
/// • API própria: compatível com OpenAI (/chat/completions) ou Anthropic (/v1/messages).
/// Os modelos e o esforço padrão são lidos do app principal de cada agente (nunca gravados nele).
/// </summary>
public static class AgentPrompt
{
    public enum Target { Claude, Codex, Local, Api, Gemini, Copilot, Qira }

    public sealed record ModelOption(string Id, string Label, IReadOnlyList<string> Efforts, string? DefaultEffort);
    public sealed record Catalog(IReadOnlyList<ModelOption> Models, string? AppModel, string? AppEffort, string? Error);
    public sealed record ApiConfig(string Kind, string BaseUrl, string? Key);
    public sealed record Result(string Text, string? SessionId, string? Error)
    {
        /// <summary>Ferramentas que o agente tentou usar e foram negadas por falta de permissão (ex.: conectores do Claude).</summary>
        public IReadOnlyList<string> Blocked { get; init; } = [];
        /// <summary>Conectores (servidores MCP) que o agente carregou nesta execução, com o nome que ele informa.</summary>
        public IReadOnlyList<string> Servers { get; init; } = [];
        /// <summary>Aviso para a pessoa sobre como o pedido foi adaptado (ex.: imagem enviada como texto a um modelo só de texto).</summary>
        public string? Notice { get; init; }
    }

    private static readonly HttpClient Http = new() { Timeout = Timeout.InfiniteTimeSpan };
    private const string OllamaUrl = "http://127.0.0.1:11434";
    /// <summary>keep_alive enviado ao Ollama em cada pedido (configurável pela aplicação; padrão do Ollama se nulo).</summary>
    public static Func<string?>? OllamaKeepAlive { get; set; }

    public static string Key(Target t) => t switch { Target.Claude => "claude", Target.Codex => "codex", Target.Local => "local", Target.Gemini => "gemini", Target.Copilot => "copilot", Target.Qira => "qira", _ => "api" };
    public static Target? Parse(string? key) => key switch { "claude" => Target.Claude, "codex" => Target.Codex, "local" => Target.Local, "api" => Target.Api, "gemini" => Target.Gemini, "copilot" => Target.Copilot, "qira" => Target.Qira, _ => null };

    // ------------------------------------------------------------------ executáveis
    public static string? FindClaudeExecutable()
    {
        var candidates = new List<string>();
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        // CLI que acompanha o app Claude para desktop: %LOCALAPPDATA%\Claude-Data\<pacote>\claude-code\<versão>\<hash>\claude.exe
        try
        {
            string data = Path.Combine(local, "Claude-Data");
            if (Directory.Exists(data))
                candidates.AddRange(Directory.EnumerateDirectories(data)
                    .Select(d => Path.Combine(d, "claude-code")).Where(Directory.Exists)
                    .SelectMany(d => Directory.EnumerateFiles(d, "claude.exe", SearchOption.AllDirectories))
                    .OrderByDescending(File.GetLastWriteTimeUtc));
        }
        catch { }
        var dirs = new List<string> { Path.Combine(home, ".local", "bin"), Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "npm") };
        foreach (var scope in new[] { EnvironmentVariableTarget.Process, EnvironmentVariableTarget.User, EnvironmentVariableTarget.Machine })
            dirs.AddRange((Environment.GetEnvironmentVariable("PATH", scope) ?? string.Empty).Split(';', StringSplitOptions.RemoveEmptyEntries));
        foreach (var dir in dirs.Select(d => d.Trim()).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                foreach (var name in new[] { "claude.exe", "claude.cmd" })
                {
                    string p = Path.Combine(Environment.ExpandEnvironmentVariables(dir), name);
                    if (File.Exists(p)) candidates.Add(p);
                }
            }
            catch { }
        }
        return candidates.FirstOrDefault(File.Exists);
    }

    private static string? FindExecutable(Target target)
        => target == Target.Claude ? FindClaudeExecutable() : target == Target.Codex ? CodexAppServerClient.FindCodexExecutable() : null;

    // ------------------------------------------------------------------ projetos (lidos dos apps)
    public sealed record Project(string Name, IReadOnlyList<string> Paths);

    /// <summary>
    /// Projetos do app de cada agente: Codex — "local-projects" do app (nome + pastas, na ordem da barra lateral) e as pastas
    /// confiáveis do config.toml; Claude — pastas das conversas recentes do Claude Code no app Claude.
    /// </summary>
    public static IReadOnlyList<Project> Projects(Target target)
    {
        var list = new List<Project>();
        try
        {
            if (target == Target.Codex)
            {
                string home = CodexAppServerClient.CodexHome();
                string state = Path.Combine(home, ".codex-global-state.json");
                if (File.Exists(state) && JsonNode.Parse(File.ReadAllText(state)) is JsonObject root && root["local-projects"] is JsonObject projects)
                {
                    var order = (root["project-order"] as JsonArray)?.Select(x => x?.GetValue<string>()).OfType<string>().ToList() ?? [];
                    foreach (var (id, node) in projects.OrderBy(p => order.IndexOf(p.Key) is var i && i < 0 ? int.MaxValue : i))
                    {
                        var paths = (node?["rootPaths"] as JsonArray)?.Select(x => x?.GetValue<string>()).OfType<string>().Where(Directory.Exists).ToList() ?? [];
                        if (paths.Count > 0) list.Add(new Project(node?["name"]?.GetValue<string>() ?? Path.GetFileName(paths[0]), paths));
                    }
                }
                string cfg = Path.Combine(home, "config.toml");
                if (File.Exists(cfg))
                    foreach (Match m in Regex.Matches(File.ReadAllText(cfg), @"(?m)^\[projects\.(?:'([^']+)'|""((?:[^""\\]|\\.)+)"")\]"))
                    {
                        string path = m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value.Replace(@"\\", @"\");
                        if (Directory.Exists(path) && !list.Any(p => p.Paths.Contains(path, StringComparer.OrdinalIgnoreCase)))
                            list.Add(new Project(FolderName(path), [path]));
                    }
            }
            else if (target == Target.Claude)
            {
                string data = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Claude-Data");
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                if (Directory.Exists(data))
                    foreach (var f in Directory.EnumerateDirectories(data).Select(d => Path.Combine(d, "claude-code-sessions")).Where(Directory.Exists)
                                 .SelectMany(d => Directory.EnumerateFiles(d, "local_*.json", SearchOption.AllDirectories)).OrderByDescending(File.GetLastWriteTimeUtc))
                    {
                        string head = ReadHead(f, 16 * 1024);
                        var m = Regex.Match(head, "\"cwd\":\"((?:[^\"\\\\]|\\\\.)*)\"");
                        if (!m.Success) continue;
                        string path = Regex.Unescape(m.Groups[1].Value);
                        if (path.Contains("scratch-workspaces", StringComparison.OrdinalIgnoreCase) || !Directory.Exists(path) || !seen.Add(path)) continue;
                        list.Add(new Project(FolderName(path), [path]));
                        if (list.Count >= 15) break;
                    }
            }
        }
        catch { }
        return list;
    }

    private static string FolderName(string path) => Path.GetFileName(path.TrimEnd('\\', '/')) is { Length: > 0 } n ? n : path;

    // ------------------------------------------------------------------ catálogo de modelos (lido do app principal)
    // Reserva, só quando o Claude Code não informa a lista dele (ClaudeModelList).
    private static readonly string[] ClaudeEfforts = ["low", "medium", "high", "xhigh", "max"];
    private static readonly (string Id, string Label)[] ClaudeModels =
        [("claude-opus-5-5", "Opus 5.5"), ("claude-sonnet-5-5", "Sonnet 5.5"), ("claude-haiku-5-5", "Haiku 5.5"), ("claude-fable-5-1", "Fable 5.1")];

    public static async Task<Catalog> CatalogAsync(Target target, ApiConfig? api, CancellationToken ct)
    {
        try
        {
            return target switch
            {
                Target.Claude => await ClaudeCatalogAsync(ct),
                Target.Codex => CodexCatalog(),
                Target.Gemini => await Gemini.GeminiCli.CatalogAsync(ct),
                Target.Copilot => CopilotAppBridge.Catalog(),
                Target.Qira => QiraBridge.Catalog(),
                Target.Local => await LocalCatalogAsync(ct),
                _ => await ApiCatalogAsync(api, ct),
            };
        }
        catch (Exception ex) { return new([], null, null, ex.Message); }
    }

    /// <summary>
    /// Claude: os modelos que o próprio Claude Code informa (modelos novos aparecem sozinhos, cada um com os níveis dele)
    /// e o modelo e o esforço da conversa mais recente do app Claude (o que está escolhido lá).
    /// </summary>
    private static async Task<Catalog> ClaudeCatalogAsync(CancellationToken ct)
    {
        string? appModel = null, appEffort = null;
        var seen = new List<string>();
        try
        {
            string data = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Claude-Data");
            var files = Directory.Exists(data)
                ? Directory.EnumerateDirectories(data).Select(d => Path.Combine(d, "claude-code-sessions")).Where(Directory.Exists)
                    .SelectMany(d => Directory.EnumerateFiles(d, "local_*.json", SearchOption.AllDirectories))
                    .OrderByDescending(File.GetLastWriteTimeUtc).Take(30).ToList()
                : [];
            foreach (var f in files)
            {
                string head = ReadHead(f, 64 * 1024);
                string? m = Regex.Match(head, "\"model\":\"([^\"]+)\"") is { Success: true } mm ? mm.Groups[1].Value : null;
                string? e = Regex.Match(head, "\"effort\":\"([^\"]+)\"") is { Success: true } me ? me.Groups[1].Value : null;
                if (m is not null && !seen.Contains(m)) seen.Add(m);
                if (appModel is null && m is not null) { appModel = m; appEffort = e; }
            }
        }
        catch { }
        string? exe = FindClaudeExecutable();
        IReadOnlyList<ModelOption>? listed = null;
        try { listed = await Claude.ClaudeModelList.GetAsync(exe, ct); } catch { }
        var models = listed is { Count: > 0 } ? listed.ToList()
            : ClaudeModels.Select(m => new ModelOption(m.Id, m.Label, ClaudeEfforts, "medium")).ToList();
        foreach (var id in seen.Where(id => models.All(m => !m.Id.Equals(id, StringComparison.OrdinalIgnoreCase))))
            models.Add(new ModelOption(id, Pretty(id), ClaudeEfforts, "medium"));
        return new(models, appModel, appEffort, exe is null ? LocalizedText.T("Claude Code não encontrado neste PC") : null);
    }

    /// <summary>Codex: config.toml (model, model_reasoning_effort) e o catálogo de modelos que o app usa.</summary>
    private static Catalog CodexCatalog()
    {
        string cfgPath = Path.Combine(CodexAppServerClient.CodexHome(), "config.toml");
        string cfg = File.Exists(cfgPath) ? File.ReadAllText(cfgPath) : string.Empty;
        string top = cfg.Split("\n[", 2)[0]; // só as chaves de nível superior
        string? Toml(string key) => Regex.Match(top, $@"(?m)^\s*{key}\s*=\s*""([^""]*)""") is { Success: true } m ? m.Groups[1].Value : null;
        string? appModel = Toml("model"), appEffort = Toml("model_reasoning_effort");
        var models = new List<ModelOption>();
        string? catalogPath = Toml("model_catalog_json")?.Replace(@"\\", @"\");
        catalogPath ??= Path.Combine(CodexAppServerClient.CodexHome(), "models_cache.json");
        if (File.Exists(catalogPath))
        {
            var root = JsonNode.Parse(File.ReadAllText(catalogPath));
            if (root?["models"] is JsonArray arr)
                foreach (var m in arr)
                {
                    string? slug = m?["slug"]?.GetValue<string>();
                    if (slug is null || m?["visibility"]?.GetValue<string>() == "hide") continue;
                    // As IAs da própria barra no seletor do Codex (codexias-*, winstatusbar-*) já são destinos da barra.
                    if (slug.StartsWith("codexias-", StringComparison.Ordinal) || slug.StartsWith("winstatusbar-", StringComparison.Ordinal)) continue;
                    var efforts = (m?["supported_reasoning_levels"] as JsonArray)?.Select(x => x?["effort"]?.GetValue<string>()).OfType<string>().ToList() ?? [];
                    models.Add(new ModelOption(slug, m?["display_name"]?.GetValue<string>() ?? slug, efforts, m?["default_reasoning_level"]?.GetValue<string>()));
                }
        }
        if (appModel is not null && models.All(m => m.Id != appModel))
            models.Insert(0, new ModelOption(appModel, appModel, ["low", "medium", "high", "xhigh"], appEffort));
        return new(models, appModel, appEffort, CodexAppServerClient.FindCodexExecutable() is null ? LocalizedText.T("Codex não encontrado neste PC") : null);
    }

    /// <summary>
    /// Servidor local compatível com OpenAI encontrado (LM Studio, llama.cpp, KoboldCpp, LocalAI, Jan, llamafile),
    /// usado quando o Ollama não responde. Assim a "IA local" funciona com qualquer um deles, sem configurar nada.
    /// </summary>
    private static string? _localOpenAiBase;
    private static readonly int[] LocalOpenAiPorts = [1234, 8080, 5001, 1337, 8000];

    /// <summary>Ollama primeiro; senão, o primeiro servidor local compatível com OpenAI que responder em /v1/models.</summary>
    private static async Task<Catalog> LocalCatalogAsync(CancellationToken ct)
    {
        try
        {
            var ollama = await OllamaCatalogAsync(ct);
            if (ollama.Models.Count > 0) { _localOpenAiBase = null; return ollama; }
        }
        catch { }
        foreach (int port in LocalOpenAiPorts)
        {
            string baseUrl = $"http://127.0.0.1:{port}/v1";
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(TimeSpan.FromMilliseconds(1500));
                var data = JsonNode.Parse(await Http.GetStringAsync($"{baseUrl}/models", cts.Token))?["data"] as JsonArray;
                var models = data?.Select(m => m?["id"]?.GetValue<string>()).OfType<string>()
                    .Select(id => new ModelOption(id, id, [], null)).ToList() ?? [];
                if (models.Count == 0) continue;
                _localOpenAiBase = baseUrl;
                return new(models, models[0].Id, null, null);
            }
            catch { }
        }
        _localOpenAiBase = null;
        return new([], null, null, LocalizedText.T("Nenhum servidor local respondeu (Ollama, LM Studio, llama.cpp, Jan…)"));
    }

    /// <summary>Ollama: modelos instalados (/api/tags); o padrão é o que está carregado agora (/api/ps).</summary>
    private static async Task<Catalog> OllamaCatalogAsync(CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(4));
        var tags = JsonNode.Parse(await Http.GetStringAsync($"{OllamaUrl}/api/tags", cts.Token));
        var names = (tags?["models"] as JsonArray)?.Select(m => (Name: m?["name"]?.GetValue<string>(), Digest: m?["digest"]?.GetValue<string>()))
            .Where(m => m.Name is not null).ToList() ?? [];
        // Modelos que pensam (capacidade "thinking"): o seletor oferece "Sem raciocínio" (think=false, bem mais rápido
        // na CPU) e "Alto" (think=true, o padrão do modelo). Capacidades em cache por digest: /api/show só uma vez.
        var thinking = await Task.WhenAll(names.Select(m => ThinksAsync(m.Name!, m.Digest, cts.Token)));
        var models = names.Select((m, i) => thinking[i] ? new ModelOption(m.Name!, m.Name!, ["none", "high"], "high") : new ModelOption(m.Name!, m.Name!, [], null)).ToList();
        string? loaded = null;
        try
        {
            var ps = JsonNode.Parse(await Http.GetStringAsync($"{OllamaUrl}/api/ps", cts.Token));
            loaded = (ps?["models"] as JsonArray)?.Select(m => m?["name"]?.GetValue<string>()).OfType<string>().FirstOrDefault();
        }
        catch { }
        return new(models, loaded ?? models.FirstOrDefault()?.Id, null, models.Count == 0 ? LocalizedText.T("Nenhum modelo no Ollama") : null);
    }

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, bool> OllamaThinking = new();
    /// <summary>Capacidades informadas pelo Ollama (/api/show) por nome de modelo: "completion", "vision", "thinking", "tools"…</summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, string[]> OllamaCapabilities = new();
    private static async Task<bool> ThinksAsync(string model, string? digest, CancellationToken ct)
    {
        string key = model + "@" + digest;
        if (OllamaThinking.TryGetValue(key, out bool known)) return known;
        try { return OllamaThinking[key] = (await OllamaShowCapabilitiesAsync(model, ct)).Contains("thinking"); }
        catch (OperationCanceledException) { throw; }
        catch { return false; }
    }

    private static async Task<string[]> OllamaShowCapabilitiesAsync(string model, CancellationToken ct)
    {
        using var show = new StringContent(new JsonObject { ["model"] = model }.ToJsonString(), Encoding.UTF8, "application/json");
        using var resp = await Http.PostAsync($"{OllamaUrl}/api/show", show, ct);
        var caps = (JsonNode.Parse(await resp.Content.ReadAsStringAsync(ct))?["capabilities"] as JsonArray)?
            .Select(c => (c as JsonValue)?.ToString()).OfType<string>().ToArray() ?? [];
        return OllamaCapabilities[model] = caps;
    }

    /// <summary>
    /// O modelo do Ollama lê imagens? Só "vision" nas capacidades. Se o Ollama não informar (versão antiga, falha),
    /// considera que sim e deixa o servidor decidir; o erro, se houver, é explicado na conversa.
    /// </summary>
    private static async Task<bool> OllamaSeesImagesAsync(string model, CancellationToken ct)
    {
        if (OllamaCapabilities.TryGetValue(model, out var known) && known.Length > 0) return known.Contains("vision");
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(4));
            var caps = await OllamaShowCapabilitiesAsync(model, cts.Token);
            return caps.Length == 0 || caps.Contains("vision");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch { return true; }
    }

    private static async Task<Catalog> ApiCatalogAsync(ApiConfig? api, CancellationToken ct)
    {
        if (api is null || string.IsNullOrWhiteSpace(api.BaseUrl) || string.IsNullOrEmpty(api.Key))
            return new([], null, null, LocalizedText.T("Configure a API em Configurações › IAs e contas"));
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(8));
        using var req = new HttpRequestMessage(HttpMethod.Get, ApiUrl(api, "models"));
        Authorize(req, api);
        using var resp = await Http.SendAsync(req, cts.Token);
        string body = await resp.Content.ReadAsStringAsync(cts.Token);
        if (!resp.IsSuccessStatusCode)
            return new([], null, null, $"HTTP {(int)resp.StatusCode}" + (ProviderErrorMessage(body) is { } why ? $": {why}" : string.Empty));
        var models = (JsonNode.Parse(body)?["data"] as JsonArray)?
            .Select(m => (Id: m?["id"]?.GetValue<string>(), Name: m?["display_name"]?.GetValue<string>()))
            .Where(m => m.Id is not null).Select(m => new ModelOption(m.Id!, m.Name ?? m.Id!, [], null))
            .OrderBy(m => m.Label, StringComparer.OrdinalIgnoreCase).ToList() ?? [];
        if (Uri.TryCreate(api.BaseUrl, UriKind.Absolute, out var endpoint) && endpoint.Host.Equals("api.openai.com", StringComparison.OrdinalIgnoreCase))
        {
            // Níveis de modelos novos: lista do Codex instalado (se falhar, só os da tabela).
            try { await Codex.CodexModelLevels.EnsureAsync(ct); } catch { }
            models = ApiModelCatalog.Recent(models);
        }
        return new(models, models.FirstOrDefault()?.Id, null, null);
    }

    private static string ApiUrl(ApiConfig api, string path)
    {
        string b = api.BaseUrl.TrimEnd('/');
        if (api.Kind == "anthropic" && !b.EndsWith("/v1", StringComparison.OrdinalIgnoreCase)) b += "/v1";
        return $"{b}/{path}";
    }

    private static void Authorize(HttpRequestMessage req, ApiConfig api)
    {
        if (api.Kind == "anthropic") { req.Headers.Add("x-api-key", api.Key); req.Headers.Add("anthropic-version", "2023-06-01"); }
        else req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", api.Key);
    }

    // ------------------------------------------------------------------ execução
    /// <summary>
    /// Opções de um envio: anexos (arquivos/pastas), pesquisa na web, permissão para editar arquivos e a continuação
    /// da conversa (sessão do CLI para Claude/Codex; mensagens anteriores para IA local e API).
    /// </summary>
    public sealed record Options(IReadOnlyList<string> Attachments, bool Web, bool AllowEdits, string? ResumeSession,
                                 IReadOnlyList<(string Role, string Content)> History, bool Ephemeral = false,
                                 string? WorkDir = null, IReadOnlyList<string>? ExtraDirs = null,
                                 Func<DesktopActionRequest, CancellationToken, Task<DesktopActionResult>>? Desktop = null,
                                 Func<string, JsonElement, CancellationToken, Task<JsonNode>>? CodexRequest = null,
                                 bool FullAccess = false)
    {
        /// <summary>
        /// Acesso completo (lápis ligado + "Permitir acesso amplo ao PC e à rede"): além do executor comum da barra,
        /// as ferramentas nativas do Claude Code e do Codex rodam sem sandbox e sem pedido de aprovação por comando.
        /// A elevação continua passando pelo UAC do Windows.
        /// </summary>
        public bool Unrestricted => FullAccess && AllowEdits;
        /// <summary>
        /// Instruções de sistema (executor do computador). Codex, Claude, APIs e IA local recebem como sistema/desenvolvedor,
        /// fora da mensagem do usuário; os demais recebem no início da primeira mensagem da conversa.
        /// </summary>
        public string? SystemPrompt { get; init; }
        /// <summary>Regras de permissão liberadas pela pessoa (ex.: "mcp__claude_ai_Google_Drive"), passadas em --allowedTools.</summary>
        public IReadOnlyList<string> AllowedTools { get; init; } = [];
        public static readonly Options None = new([], false, false, null, []);
    }

    private static readonly string[] ImageExt = [".png", ".jpg", ".jpeg", ".gif", ".webp", ".bmp"];
    private static bool IsImage(string p) => ImageExt.Contains(Path.GetExtension(p).ToLowerInvariant());

    /// <summary>Executa o prompt (conversa nova ou continuação). <paramref name="onText"/> recebe o texto acumulado.</summary>
    public static async Task<Result> RunAsync(Target target, string prompt, string? model, string? effort, ApiConfig? api, Options options, Action<string> onText, CancellationToken ct)
    {
        if (options.Desktop is not { } desktop || !options.AllowEdits)
            return await RunOnceAsync(target, prompt, model, effort, api, options, onText, ct);
        // O executor faz várias rodadas; o aviso da primeira (ex.: imagem enviada como texto) chega à pessoa no fim.
        string? notice = null;
        var result = await DesktopAgentLoop.RunAsync(prompt, options, async (next, current, stream, token) =>
        {
            var round = await RunOnceAsync(target, next, model, effort, api, current, stream, token);
            notice ??= round.Notice;
            return round;
        }, desktop, onText, ct);
        return result.Notice is null && notice is not null ? result with { Notice = notice } : result;
    }

    private static async Task<Result> RunOnceAsync(Target target, string prompt, string? model, string? effort, ApiConfig? api, Options options, Action<string> onText, CancellationToken ct)
    {
        string? delivered = null;
        if (options.SystemPrompt is { Length: > 0 } fullSystem && target is Target.Gemini or Target.Copilot or Target.Qira)
        {
            // O retrato do PC muda a cada pedido: fica fora da comparação, senão as instruções seriam reenviadas sempre.
            int cut = fullSystem.IndexOf(DesktopAgentLoop.ContextMarker, StringComparison.Ordinal);
            string system = cut < 0 ? fullSystem : fullSystem[..cut].TrimEnd();
            string context = cut < 0 ? "" : fullSystem[cut..];
            if (ProviderInstructionMemo.ShouldSend(target.ToString(), options.ResumeSession, system))
            {
                prompt = fullSystem + "\n\n[Pedido do usuário]\n" + prompt;
                delivered = system;
            }
            else if (context.Length > 0) prompt = context + "\n\n[Pedido do usuário]\n" + prompt;
            options = options with { SystemPrompt = null };
        }
        var result = await RunOnceCoreAsync(target, prompt, model, effort, api, options, onText, ct);
        if (result.Error is null && delivered is not null)
            ProviderInstructionMemo.Acknowledge(target.ToString(), result.SessionId ?? options.ResumeSession, delivered);
        return result;
    }

    private static Task<Result> RunOnceCoreAsync(Target target, string prompt, string? model, string? effort, ApiConfig? api, Options options, Action<string> onText, CancellationToken ct)
        => target switch
        {
            Target.Codex => CodexComposer.RunAsync(prompt, model, effort, options, onText, ct),
            Target.Gemini => Gemini.GeminiCli.RunAsync(prompt, model, options, onText, ct),
            Target.Copilot => CopilotAppBridge.RunAsync(prompt, model, options, onText, ct),
            Target.Qira => QiraBridge.RunAsync(prompt, model, options, onText, ct),
            // Servidor local compatível com OpenAI (sem chave; o campo só preenche o cabeçalho exigido).
            Target.Local when _localOpenAiBase is { } local => RunApiAsync(new ApiConfig("openai", local, "local"), prompt, model, effort, options, onText, ct),
            Target.Local => RunOllamaAsync(prompt, model, effort, options, onText, ct),
            Target.Api => RunApiAsync(api, prompt, model, effort, options, onText, ct),
            _ => RunCliAsync(target, prompt, model, effort, options, onText, ct),
        };

    /// <summary>Lista os anexos no fim do prompt (os CLIs leem os arquivos com as próprias ferramentas).</summary>
    private static string WithAttachmentList(string prompt, IReadOnlyList<string> attachments)
        => attachments.Count == 0 ? prompt
            : prompt + "\n\n" + LocalizedText.T("Anexos:") + "\n" + string.Join("\n", attachments.Select(a => "- " + a));

    private static async Task<Result> RunCliAsync(Target target, string prompt, string? model, string? effort, Options o, Action<string> onText, CancellationToken ct)
    {
        string? exe = FindExecutable(target);
        if (exe is null)
            return new(string.Empty, null, target == Target.Claude ? LocalizedText.T("Claude Code não encontrado neste PC") : LocalizedText.T("Codex não encontrado neste PC"));

        // Pasta de trabalho: a primeira pasta anexada (ou a pasta do primeiro arquivo); senão, a pasta do usuário.
        var dirs = o.Attachments.Select(a => Directory.Exists(a) ? a : Path.GetDirectoryName(a)).OfType<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        // Projeto escolhido: pasta de trabalho fixa da conversa (o Claude só retoma a sessão na mesma pasta); demais pastas como --add-dir.
        string workDir = o.WorkDir is { } wd && Directory.Exists(wd) ? wd : dirs.FirstOrDefault() ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        dirs = dirs.Concat(o.ExtraDirs ?? []).Where(d => !d.Equals(workDir, StringComparison.OrdinalIgnoreCase) && Directory.Exists(d))
                   .Distinct(StringComparer.OrdinalIgnoreCase).Prepend(workDir).ToList();
        string? lastMessageFile = null;
        var args = new List<string>();
        bool viaCmd = exe.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase);
        // Pedido do Claude pela entrada padrão (só via cmd.exe ou quando é longo demais para a linha de comando).
        string? stdinPrompt = null;
        // Sessão nova no meio de uma conversa da barra (veio de outra IA): a conversa vai como contexto.
        if (o.ResumeSession is null) prompt = ConversationHandoff.WithTranscript(prompt, o.History);
        if (target == Target.Claude)
        {
            (var claudeArgs, stdinPrompt) = ClaudeArguments(WithAttachmentList(prompt, o.Attachments), model, effort, o, dirs.Skip(1), viaCmd);
            args.AddRange(claudeArgs);
        }
        else
        {
            lastMessageFile = Path.Combine(Path.GetTempPath(), $"wsb-codex-{Guid.NewGuid():N}.txt");
            args.AddRange(["exec", "--skip-git-repo-check", "--sandbox", o.Unrestricted ? "danger-full-access" : o.AllowEdits ? "workspace-write" : "read-only", "--json",
                           "--output-last-message", lastMessageFile, "-C", workDir]);
            foreach (var d in dirs.Skip(1)) args.AddRange(["--add-dir", d]);
            if (o.Ephemeral) args.Add("--ephemeral");
            if (o.ResumeSession is { } sid) args.AddRange(["resume", sid]);
            if (!string.IsNullOrEmpty(model)) args.AddRange(["-c", $"model=\"{model}\""]);
            if (!string.IsNullOrEmpty(effort)) args.AddRange(["-c", $"model_reasoning_effort=\"{effort}\""]);
            if (o.Web) args.AddRange(["-c", "web_search=\"live\""]);
            // "-i" aceita várias imagens seguidas: sem "=" e sem "--", o prompt era lido como mais uma imagem
            // e o Codex respondia "No prompt provided via stdin". "--" também protege prompts que começam com "-".
            foreach (var img in o.Attachments.Where(IsImage)) args.Add("--image=" + img);
            args.Add("--");
            args.Add(WithAttachmentList(prompt, o.Attachments));
        }

        var psi = new ProcessStartInfo(viaCmd ? Path.Combine(Environment.SystemDirectory, "cmd.exe") : exe)
        {
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = workDir,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
        };
        if (stdinPrompt is not null) psi.StandardInputEncoding = new UTF8Encoding(false); // sem BOM: iria no começo do pedido
        if (viaCmd) { psi.ArgumentList.Add("/d"); psi.ArgumentList.Add("/c"); psi.ArgumentList.Add(exe); }
        foreach (var a in args) psi.ArgumentList.Add(a);

        var text = new StringBuilder();
        var blocked = new List<string>();
        var loaded = new List<string>();
        string? sessionId = null, error = null, final = null;
        Process? proc = null;
        try
        {
            proc = Process.Start(psi);
            if (proc is null) return new(string.Empty, null, LocalizedText.T("Não foi possível iniciar o agente"));
            if (stdinPrompt is not null)
            {
                try { await proc.StandardInput.WriteAsync(stdinPrompt.AsMemory(), ct); proc.StandardInput.Close(); }
                catch (IOException) { } // o processo fechou antes de ler: o motivo vem pela saída de erro, logo abaixo
                catch { try { if (!proc.HasExited) proc.Kill(entireProcessTree: true); } catch { } throw; }
            }
            else proc.StandardInput.Close();
            var stderrTask = proc.StandardError.ReadToEndAsync(ct);
            using var reg = ct.Register(() => { try { if (!proc.HasExited) proc.Kill(entireProcessTree: true); } catch { } });

            while (await proc.StandardOutput.ReadLineAsync(ct) is { } line)
            {
                if (line.Length == 0 || line[0] != '{') continue;
                try
                {
                    using var doc = JsonDocument.Parse(line);
                    var root = doc.RootElement;
                    string type = Str(root, "type") ?? string.Empty;
                    if (target == Target.Claude)
                    {
                        sessionId ??= Str(root, "session_id");
                        if (type == "stream_event" && root.TryGetProperty("event", out var ev)
                            && Str(ev, "type") == "content_block_delta" && ev.TryGetProperty("delta", out var d) && Str(d, "type") == "text_delta")
                        {
                            text.Append(Str(d, "text"));
                            onText(text.ToString());
                        }
                        else if (type == "system" && Str(root, "subtype") == "init" && root.TryGetProperty("mcp_servers", out var servers) && servers.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var sv in servers.EnumerateArray())
                                if (Str(sv, "name") is { Length: > 0 } sn && Str(sv, "status") != "needs-auth" && !loaded.Contains(sn)) loaded.Add(sn);
                        }
                        else if (type == "assistant" && text.Length > 0 && !text.ToString().EndsWith("\n\n"))
                            text.Append("\n\n"); // entre turnos (uso de ferramentas), separa os blocos
                        else if (type == "result")
                        {
                            final = Str(root, "result");
                            if (root.TryGetProperty("permission_denials", out var denials) && denials.ValueKind == JsonValueKind.Array)
                                foreach (var dn in denials.EnumerateArray())
                                    if (Str(dn, "tool_name") is { Length: > 0 } tn && !blocked.Contains(tn)) blocked.Add(tn);
                            if (root.TryGetProperty("is_error", out var isErr) && isErr.ValueKind == JsonValueKind.True)
                                error = final ?? LocalizedText.T("O agente retornou um erro");
                        }
                    }
                    else
                    {
                        if (type == "thread.started") sessionId = Str(root, "thread_id");
                        else if (type is "item.completed" or "item.updated" && root.TryGetProperty("item", out var item)
                                 && Str(item, "type") is "agent_message" or "assistant_message")
                        {
                            final = Str(item, "text");
                            if (final is not null) onText(final);
                        }
                        else if (type is "error" or "turn.failed")
                            error = Str(root, "message") ?? (root.TryGetProperty("error", out var e) ? Str(e, "message") : null);
                    }
                }
                catch (JsonException) { }
            }
            await proc.WaitForExitAsync(ct);
            string stderr = await stderrTask;
            if (lastMessageFile is not null && File.Exists(lastMessageFile))
            {
                string last = await File.ReadAllTextAsync(lastMessageFile, ct);
                if (!string.IsNullOrWhiteSpace(last)) final = last.Trim();
            }
            string answer = !string.IsNullOrWhiteSpace(final) ? final!.Trim() : text.ToString().Trim();
            if (error is null && answer.Length == 0 && proc.ExitCode != 0)
                error = LastLine(stderr) ?? LocalizedText.T("O agente terminou sem resposta");
            return new(CliAnswer(target, error, answer, text.ToString()), sessionId, error) { Blocked = blocked, Servers = loaded };
        }
        catch (OperationCanceledException) { return new(text.ToString().Trim(), sessionId, LocalizedText.T("Interrompido")); }
        catch (Exception ex) { return new(text.ToString().Trim(), sessionId, ex.Message); }
        finally
        {
            proc?.Dispose();
            if (lastMessageFile is not null) try { File.Delete(lastMessageFile); } catch { }
        }
    }

    /// <summary>Acima disto o pedido do Claude vai pela entrada padrão (a linha de comando do Windows para em 32.767 caracteres).</summary>
    internal const int ClaudeArgumentLimit = 20000;

    /// <summary>
    /// Argumentos do "claude -p". O pedido segue como argumento, como sempre, menos em dois casos, em que vai pela
    /// entrada padrão (<c>Stdin</c>) e o "-p" fica sem valor: via cmd.exe (só existe o claude.cmd; quebras de linha
    /// cortariam o comando e aspas, &amp; e % seriam reinterpretados) ou quando os argumentos passam de
    /// <see cref="ClaudeArgumentLimit"/> caracteres.
    /// </summary>
    internal static (List<string> Args, string? Stdin) ClaudeArguments(string prompt, string? model, string? effort, Options o, IEnumerable<string> addDirs, bool viaCmd)
    {
        var args = new List<string>();
        args.AddRange(["-p", prompt, "--output-format", "stream-json", "--verbose", "--include-partial-messages"]);
        if (o.SystemPrompt is { Length: > 0 } system) args.AddRange(["--append-system-prompt", system]);
        if (o.ResumeSession is { } sid) args.AddRange(["--resume", sid]);
        if (!string.IsNullOrEmpty(model)) args.AddRange(["--model", model]);
        if (!string.IsNullOrEmpty(effort)) args.AddRange(["--effort", effort]);
        foreach (var d in addDirs) args.AddRange(["--add-dir", d]);
        var allowed = (o.Web ? new[] { "WebSearch", "WebFetch" } : []).Concat(o.AllowedTools).Distinct().ToList();
        if (allowed.Count > 0) { args.Add("--allowedTools"); args.AddRange(allowed); }
        if (o.Unrestricted) args.AddRange(["--permission-mode", "bypassPermissions"]);
        else if (o.AllowEdits) args.AddRange(["--permission-mode", "acceptEdits"]);
        // Não grava sessão nem carrega MCPs; sem hooks, a chamada auxiliar (resumo, roteiro do acompanhamento)
        // não aparece na barra como tarefa nem substitui a tarefa principal em destaque.
        if (o.Ephemeral) args.AddRange(["--no-session-persistence", "--strict-mcp-config", "--settings", "{\"disableAllHooks\":true}"]);
        // +3 por argumento: o espaço e as aspas que a linha de comando acrescenta.
        if (!viaCmd && args.Sum(a => a.Length + 3) <= ClaudeArgumentLimit) return (args, null);
        args.RemoveAt(1);
        return (args, prompt);
    }

    /// <summary>
    /// Resposta de uma execução por linha de comando. No Claude, um "result" com is_error traz o texto do provedor, que
    /// já é o erro: como resposta fica só o que chegou em fluxo (senão o erro era mostrado e guardado como a fala da IA).
    /// </summary>
    internal static string CliAnswer(Target target, string? error, string answer, string streamed)
        => error is null || target != Target.Claude ? answer : streamed.Trim();

    /// <summary>Para IA local e API: texto dos arquivos anexados no próprio prompt (até 200 KB cada) e a lista das pastas.</summary>
    private static string InlineAttachments(string prompt, IReadOnlyList<string> attachments)
    {
        if (attachments.Count == 0) return prompt;
        var sb = new StringBuilder(prompt);
        foreach (var a in attachments)
        {
            try
            {
                if (Directory.Exists(a))
                {
                    var names = Directory.EnumerateFileSystemEntries(a).Take(200).Select(Path.GetFileName);
                    sb.Append($"\n\n[{LocalizedText.T("Pasta")}: {a}]\n").Append(string.Join("\n", names));
                }
                else if (IsImage(a)) continue; // vai como imagem
                else if (new FileInfo(a).Length <= 200 * 1024)
                    sb.Append($"\n\n[{LocalizedText.T("Arquivo")}: {Path.GetFileName(a)}]\n").Append(File.ReadAllText(a));
                else sb.Append($"\n\n[{LocalizedText.T("Arquivo grande demais para anexar")}: {a}]");
            }
            catch { }
        }
        return sb.ToString();
    }

    private static JsonArray HistoryMessages(Options o)
    {
        var arr = new JsonArray();
        foreach (var (role, content) in o.History) arr.Add(new JsonObject { ["role"] = role, ["content"] = content });
        return arr;
    }

    private static async Task<Result> RunOllamaAsync(string prompt, string? model, string? effort, Options o, Action<string> onText, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(model)) return new(string.Empty, null, LocalizedText.T("Escolha um modelo local"));
        var messages = HistoryMessages(o);
        if (o.SystemPrompt is { Length: > 0 } system) messages.Insert(0, new JsonObject { ["role"] = "system", ["content"] = system });
        // Modelo só de texto (ex.: phi4-mini): o Ollama recusaria a imagem com HTTP 400. Vai o texto lido nela (OCR).
        bool vision = !o.Attachments.Any(IsImage) || await OllamaSeesImagesAsync(model, ct);
        var (user, notice) = await OllamaUserMessageAsync(prompt, model, o.Attachments, vision, ReadImageText, ct);
        messages.Add(user);
        var body = new JsonObject { ["model"] = model, ["stream"] = true, ["messages"] = messages };
        // Só nos modelos que pensam (o catálogo oferece os níveis); nos demais o Ollama recusaria "think".
        if (effort is "none" or "high") body["think"] = effort == "high";
        if (OllamaKeepAlive?.Invoke() is { Length: > 0 } keep)
            body["keep_alive"] = LocalKeepAlive.OllamaValue(keep) switch { double seconds => JsonValue.Create(seconds), var text => JsonValue.Create((string)text) };
        using var req = new HttpRequestMessage(HttpMethod.Post, $"{OllamaUrl}/api/chat") { Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json") };
        var result = await StreamAsync(req, onText, ct, line =>
        {
            var n = JsonNode.Parse(line);
            if (n?["error"] is JsonValue err && err.TryGetValue<string>(out var message)) throw new InvalidOperationException(message);
            return n?["message"]?["content"]?.GetValue<string>();
        });
        return notice is null ? result : result with { Notice = notice };
    }

    /// <summary>Leitor de texto das imagens para modelos sem visão (troca-se nos testes).</summary>
    internal static Func<string, CancellationToken, Task<string?>> ReadImageText { get; set; } = ImageText.ReadAsync;

    /// <summary>
    /// Mensagem do usuário para o /api/chat do Ollama. Com visão: imagens em base64. Sem visão: cada imagem vira o texto
    /// reconhecido nela (ou um aviso ao modelo, se nada foi lido), e <c>notice</c> explica à pessoa o que foi enviado.
    /// </summary>
    internal static async Task<(JsonObject User, string? Notice)> OllamaUserMessageAsync(string prompt, string model, IReadOnlyList<string> attachments,
        bool vision, Func<string, CancellationToken, Task<string?>> readText, CancellationToken ct)
    {
        var content = new StringBuilder(InlineAttachments(prompt, attachments));
        var user = new JsonObject { ["role"] = "user" };
        var imagePaths = attachments.Where(IsImage).ToList();
        if (vision)
        {
            var images = new JsonArray();
            foreach (var img in imagePaths) try { images.Add(Convert.ToBase64String(await File.ReadAllBytesAsync(img, ct))); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            if (images.Count > 0) user["images"] = images;
            user["content"] = content.ToString();
            return (user, null);
        }
        var read = new List<string>();
        var unread = new List<string>();
        foreach (var img in imagePaths)
        {
            string label = ImageLabel(img);
            string? text = await readText(img, ct);
            if (text is { Length: > 0 })
            {
                read.Add(label);
                content.Append("\n\n[").Append(LocalizedText.F("Texto reconhecido na imagem {0} (você não vê a imagem, só este texto lido por OCR)", new object?[] { label })).Append("]\n").Append(text);
            }
            else
            {
                unread.Add(label);
                content.Append("\n\n[").Append(LocalizedText.F("A pessoa anexou a imagem {0}, mas você não lê imagens e nenhum texto foi reconhecido nela", new object?[] { label })).Append(']');
            }
        }
        user["content"] = content.ToString();
        string? notice = null;
        if (read.Count > 0)
            notice = LocalizedText.F("O modelo {0} só lê texto: {1} foi enviada como o texto reconhecido nela, não como imagem.", new object?[] { model, string.Join(", ", read) });
        if (unread.Count > 0)
            notice = (notice is null ? "" : notice + " ") + LocalizedText.F("O modelo {0} só lê texto e não foi possível ler texto em {1}, então a imagem não foi enviada. Para a IA ver a tela, escolha um modelo que lê imagens.", new object?[] { model, string.Join(", ", unread) });
        return (user, notice);
    }

    /// <summary>"a captura da janela" para o contexto da tela; senão, o nome do arquivo.</summary>
    private static string ImageLabel(string path)
        => path.Replace('/', '\\').Contains(@"\cache\context\", StringComparison.OrdinalIgnoreCase)
            ? LocalizedText.T("a captura da janela") : "\"" + Path.GetFileName(path) + "\"";

    /// <summary>Mensagem legível do corpo de um erro HTTP (JSON de OpenAI, Anthropic ou Ollama, ou texto curto).</summary>
    internal static string? ProviderErrorMessage(string body)
    {
        string trimmed = body.Trim();
        if (trimmed.Length == 0) return null;
        if (trimmed.StartsWith('{'))
        {
            string inner = AiErrorExplainer.InnerMessage(trimmed);
            return inner == trimmed ? null : inner;
        }
        return trimmed.Length <= 300 && !trimmed.Contains('<') ? trimmed : null;
    }

    private static async Task<Result> RunApiAsync(ApiConfig? api, string prompt, string? model, string? effort, Options o, Action<string> onText, CancellationToken ct)
    {
        if (api is null || string.IsNullOrWhiteSpace(api.BaseUrl) || string.IsNullOrEmpty(api.Key))
            return new(string.Empty, null, LocalizedText.T("Configure a API em Configurações › IAs e contas"));
        if (string.IsNullOrEmpty(model)) return new(string.Empty, null, LocalizedText.T("Escolha um modelo da API"));
        // Pedido de imagem numa API com endpoint de imagens (OpenAI, xAI): gera e mostra no chat.
        if (api.Kind != "anthropic" && !o.Ephemeral && ApiImageGeneration.ImageModel(api.BaseUrl) is { } imageModel && ApiImageGeneration.IsImageRequest(prompt))
        {
            try
            {
                onText(LocalizedText.T("*Gerando imagem…*"));
                string? image = await ApiImageGeneration.GenerateAsync(Http, api.BaseUrl, api.Key!, prompt, ct);
                if (image is not null) { onText(image); return new(image, null, null); }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { return new(string.Empty, null, ex.Message); }
        }
        var messages = HistoryMessages(o);
        var content = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = InlineAttachments(prompt, o.Attachments) });
        foreach (var image in o.Attachments.Where(IsImage))
        {
            string ext = Path.GetExtension(image).ToLowerInvariant();
            string mime = ext is ".jpg" or ".jpeg" ? "image/jpeg" : ext == ".webp" ? "image/webp" : ext == ".gif" ? "image/gif" : "image/png";
            string encoded = Convert.ToBase64String(await File.ReadAllBytesAsync(image, ct));
            content.Add(api.Kind == "anthropic"
                ? new JsonObject { ["type"] = "image", ["source"] = new JsonObject { ["type"] = "base64", ["media_type"] = mime, ["data"] = encoded } }
                : new JsonObject { ["type"] = "image_url", ["image_url"] = new JsonObject { ["url"] = $"data:{mime};base64,{encoded}" } });
        }
        messages.Add(new JsonObject { ["role"] = "user", ["content"] = content.Count == 1 ? JsonValue.Create(InlineAttachments(prompt, o.Attachments)) : content });
        JsonObject body = api.Kind == "anthropic"
            ? new() { ["model"] = model, ["max_tokens"] = 8192, ["stream"] = true, ["messages"] = messages }
            : new() { ["model"] = model, ["stream"] = true, ["messages"] = messages };
        if (api.Kind != "anthropic" && effort is not null && ApiModelCatalog.Efforts(model).Contains(effort)) body["reasoning_effort"] = effort;
        if (o.SystemPrompt is { Length: > 0 } system)
        {
            if (api.Kind == "anthropic") body["system"] = system;
            else messages.Insert(0, new JsonObject { ["role"] = "system", ["content"] = system });
        }
        using var req = new HttpRequestMessage(HttpMethod.Post, ApiUrl(api, api.Kind == "anthropic" ? "messages" : "chat/completions"))
        { Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json") };
        Authorize(req, api);
        return await StreamAsync(req, onText, ct, line => ApiStreamPiece(api.Kind, line));
    }

    /// <summary>
    /// Trecho de texto de uma linha do fluxo (SSE) da API: Anthropic ("content_block_delta") ou compatível com OpenAI
    /// ("choices[0].delta.content", inclusive o servidor local). Linha sem texto devolve nulo: "choices" vazio (primeiro
    /// bloco do Azure, bloco final só com o uso) não interrompe mais a resposta. Um "error" vira exceção com a mensagem
    /// do provedor, seja objeto ({"message":…}) ou texto simples.
    /// </summary>
    internal static string? ApiStreamPiece(string kind, string line)
    {
        if (!line.StartsWith("data:")) return null;
        string data = line[5..].Trim();
        if (data == "[DONE]") return null;
        if (JsonNode.Parse(data) is not JsonObject n) return null;
        if (n["error"] is { } err)
            throw new InvalidOperationException(err switch
            {
                JsonObject o when o["message"] is JsonValue mv && mv.TryGetValue<string>(out var message) => message,
                JsonValue v when v.TryGetValue<string>(out var text) => text,
                _ => err.ToJsonString(),
            });
        static string? Text(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out var s) ? s : null;
        if (kind == "anthropic")
            return Text(n["type"]) == "content_block_delta" && n["delta"] is JsonObject delta ? Text(delta["text"]) : null;
        return n["choices"] is JsonArray { Count: > 0 } choices && choices[0] is JsonObject first && first["delta"] is JsonObject d
            ? Text(d["content"]) : null;
    }

    /// <summary>Lê uma resposta HTTP em fluxo, linha a linha, acumulando o texto extraído por <paramref name="piece"/>.</summary>
    private static async Task<Result> StreamAsync(HttpRequestMessage req, Action<string> onText, CancellationToken ct, Func<string, string?> piece)
    {
        var text = new StringBuilder();
        try
        {
            using var resp = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!resp.IsSuccessStatusCode)
            {
                // Ollama devolve {"error":"…"}; OpenAI/Anthropic, {"error":{"message":"…"}}. Antes só o segundo era lido
                // e o motivo do Ollama se perdia ("HTTP 400" sem explicação).
                string? msg = ProviderErrorMessage(await resp.Content.ReadAsStringAsync(ct));
                return new(string.Empty, null, $"HTTP {(int)resp.StatusCode}" + (msg is null ? string.Empty : $": {msg}"));
            }
            using var reader = new StreamReader(await resp.Content.ReadAsStreamAsync(ct), Encoding.UTF8);
            while (await reader.ReadLineAsync(ct) is { } line)
            {
                if (line.Length == 0) continue;
                string? p;
                try { p = piece(line); } catch (JsonException) { continue; }
                if (string.IsNullOrEmpty(p)) continue;
                text.Append(p);
                onText(text.ToString());
            }
            return new(text.ToString().Trim(), null, text.Length == 0 ? LocalizedText.T("Sem resposta") : null);
        }
        catch (OperationCanceledException) { return new(text.ToString().Trim(), null, LocalizedText.T("Interrompido")); }
        catch (Exception ex) { return new(text.ToString().Trim(), null, ex.Message); }
    }

    // ------------------------------------------------------------------ histórico e continuação
    /// <summary>Registra a conversa do Claude Code no histórico do app Claude (link oficial "claude://claude.ai/resume").</summary>
    public static string ClaudeHistoryLink(string sessionId) => $"claude://claude.ai/resume?session={Uri.EscapeDataString(sessionId)}";

    /// <summary>Abre a conversa no terminal para continuar (Claude: --resume; Codex: resume).</summary>
    public static bool ContinueInTerminal(Target target, string? sessionId)
    {
        string? exe = FindExecutable(target);
        if (exe is null) return false;
        var args = new List<string>();
        if (target == Target.Claude) { if (sessionId is not null) { args.Add("--resume"); args.Add(sessionId); } else args.Add("--continue"); }
        else { args.Add("resume"); args.Add(sessionId ?? "--last"); }
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string wt = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WindowsApps", "wt.exe");
        ProcessStartInfo psi;
        if (File.Exists(wt))
        {
            psi = new ProcessStartInfo(wt) { UseShellExecute = false, WorkingDirectory = home };
            psi.ArgumentList.Add("-d"); psi.ArgumentList.Add(home); psi.ArgumentList.Add(exe);
        }
        else
        {
            psi = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "cmd.exe")) { UseShellExecute = true, WorkingDirectory = home };
            psi.ArgumentList.Add("/k"); psi.ArgumentList.Add(exe);
        }
        foreach (var a in args) psi.ArgumentList.Add(a);
        try { Process.Start(psi)?.Dispose(); return true; } catch { return false; }
    }

    // ------------------------------------------------------------------ utilidades
    public static string Pretty(string id)
    {
        var m = Regex.Match(id, @"^claude-([a-z]+)-(\d+)-(\d+)");
        return m.Success ? $"{char.ToUpper(m.Groups[1].Value[0])}{m.Groups[1].Value[1..]} {m.Groups[2].Value}.{m.Groups[3].Value}" : id;
    }

    public static string EffortLabel(string effort) => effort switch
    {
        "none" => LocalizedText.T("Sem raciocínio"), "minimal" => LocalizedText.T("Mínimo"), "low" => LocalizedText.T("Baixo"),
        "medium" => LocalizedText.T("Médio"), "high" => LocalizedText.T("Alto"), "xhigh" => LocalizedText.T("Muito alto"),
        "max" => LocalizedText.T("Máximo"), "ultra" => "Ultra", _ => effort,
    };

    private static string ReadHead(string path, int max)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var buf = new byte[Math.Min(max, (int)Math.Min(int.MaxValue, fs.Length))];
        int n = fs.Read(buf, 0, buf.Length);
        return Encoding.UTF8.GetString(buf, 0, n);
    }

    private static string? Str(JsonElement e, string name)
        => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static string? LastLine(string s)
        => s.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault();
}
