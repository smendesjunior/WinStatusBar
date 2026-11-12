using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace WinStatusBar.Core.Ai.Gemini;

/// <summary>
/// Google Gemini pelo Gemini CLI oficial (@google/gemini-cli), com a conta Google da pessoa (login feito uma vez no
/// terminal). Roda node.exe com o script do pacote, sem cmd.exe: o prompt vai por argumento, sem reinterpretação
/// de caracteres especiais. Saída em stream-json; a sessão é retomada com --resume.
/// </summary>
public static class GeminiCli
{
    public sealed record Install(string Node, string Script);

    public static Install? Find()
    {
        try
        {
            string npm = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "npm");
            string package = Path.Combine(npm, "node_modules", "@google", "gemini-cli");
            string manifest = Path.Combine(package, "package.json");
            if (!File.Exists(manifest)) return null;
            var bin = JsonNode.Parse(File.ReadAllText(manifest))?["bin"];
            string? relative = bin is JsonObject o ? o["gemini"]?.GetValue<string>() : bin?.GetValue<string>();
            if (relative is null) return null;
            string script = Path.GetFullPath(Path.Combine(package, relative));
            if (!File.Exists(script)) return null;
            string? node = FindNode(npm);
            return node is null ? null : new(node, script);
        }
        catch { return null; }
    }

    private static string? FindNode(string npm)
    {
        var dirs = new List<string> { npm, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "nodejs") };
        foreach (var scope in new[] { EnvironmentVariableTarget.Process, EnvironmentVariableTarget.User, EnvironmentVariableTarget.Machine })
            dirs.AddRange((Environment.GetEnvironmentVariable("PATH", scope) ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries));
        foreach (var dir in dirs.Select(d => Environment.ExpandEnvironmentVariables(d.Trim())).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try { string p = Path.Combine(dir, "node.exe"); if (File.Exists(p)) return p; } catch { }
        }
        return null;
    }

    /// <summary>
    /// Método de autenticação usado pela barra: "login" (conta Google do CLI) ou "api" (chave do Google AI Studio).
    /// Aplicado só aos processos iniciados pela barra (GEMINI_CLI_AUTH_OVERRIDE); o settings.json do CLI não é alterado.
    /// </summary>
    public static string AuthMode { get; set; } = "login";

    /// <summary>Padrão provisório (pedido de 07/11/2026): Flash-Lite mais recente, o mais barato e rápido da linha.</summary>
    public const string DefaultModel = "gemini-3.5-flash-lite";
    public static bool UsesApiKey => AuthMode == "api";
    /// <summary>Projeto do Google Cloud para o login (GOOGLE_CLOUD_PROJECT); o nível gratuito é recusado pelo Google.</summary>
    public static string? CloudProject { get; set; }

    /// <summary>
    /// Conversa pela conta Google (gemini.google.com numa página oculta da barra), instalada pela aplicação. No modo
    /// "login" substitui o login do CLI, que o Google recusa no nível gratuito. Sem ela, o modo login usa o CLI.
    /// </summary>
    public static Func<string, string?, AgentPrompt.Options, Action<string>, CancellationToken, Task<AgentPrompt.Result>>? WebRunner { get; set; }
    public static Func<string?>? WebAvailable { get; set; }
    private static bool UsesWeb => !UsesApiKey && WebRunner is not null;

    private static string Home => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".gemini");

    /// <summary>Credenciais OAuth gravadas pelo próprio CLI ("Sign in with Google").</summary>
    public static bool HasLogin() => File.Exists(Path.Combine(Home, "oauth_creds.json"));
    public static bool HasApiKey() => !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("GEMINI_API_KEY"));

    /// <summary>O método escolhido tem credencial local (não confirma validade online).</summary>
    public static bool SignedIn() => UsesApiKey ? HasApiKey() : HasLogin();

    /// <summary>
    /// O Google recusa o login gratuito "Gemini Code Assist for individuals" neste cliente (UNSUPPORTED_CLIENT /
    /// IneligibleTierError). Traduz para uma orientação objetiva em vez do rastreamento de pilha.
    /// </summary>
    public static string? FriendlyError(string? error)
    {
        if (error is null) return null;
        if (error.Contains("UNSUPPORTED_CLIENT", StringComparison.OrdinalIgnoreCase) || error.Contains("IneligibleTier", StringComparison.OrdinalIgnoreCase)
            || error.Contains("no longer supported for Gemini Code Assist", StringComparison.OrdinalIgnoreCase))
            return LocalizedText.T("O Google recusou o login desta conta no Gemini CLI: o nível gratuito \"Gemini Code Assist para pessoas físicas\" não é mais aceito neste cliente. Use Gemini · chave de API (chave gratuita no Google AI Studio). Com Gemini Code Assist Standard/Enterprise, informe o projeto do Google Cloud em Configurações › IAs e contas › Gemini.");
        if (error.Contains("API key not valid", StringComparison.OrdinalIgnoreCase) || error.Contains("API_KEY_INVALID", StringComparison.OrdinalIgnoreCase))
            return LocalizedText.T("A chave do Gemini foi recusada pelo Google. Confira ou troque a chave em Configurações › IAs e contas › Gemini.");
        if (error.Contains("Please set an Auth method", StringComparison.OrdinalIgnoreCase) || error.Contains("Login required", StringComparison.OrdinalIgnoreCase))
            return LocalizedText.T("Gemini sem autenticação. Entre com a conta Google ou salve uma chave em Configurações › IAs e contas › Gemini.");
        return error;
    }

    /// <summary>Lista de reserva quando a lista da conta não pôde ser consultada (sem chave ou sem rede).</summary>
    private static readonly string[] Fallback = ["gemini-3.5-flash", "gemini-3.1-flash-lite", "gemini-2.5-flash", "gemini-2.5-pro"];
    private static IReadOnlyList<string>? _accountModels;
    private static DateTime _accountModelsAt;
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(8) };

    /// <summary>
    /// Modelos de texto disponíveis para a conta, consultados na API do Google com a chave (listar não gera texto
    /// nem tem custo). Cache de 1 h. O login do CLI usa os mesmos nomes de modelo.
    /// </summary>
    public static async Task<AgentPrompt.Catalog> CatalogAsync(CancellationToken ct)
    {
        if (UsesWeb)
        {
            string? webError = WebAvailable?.Invoke();
            if (webError is not null) return new([], "app", null, webError);
            // Modos do seletor do site (ex.: "Rápido", "Pro"), lidos na última verificação de Configurações › Codex e modelos.
            var modes = new List<AgentPrompt.ModelOption> { new("app", "Modo escolhido no Gemini (conta Google)", [], null) };
            modes.AddRange(Codex.CodexIntegration.SiteModes("gemini").Select(m => new AgentPrompt.ModelOption(m.Id, m.Label, [], null)));
            return new(modes, "app", null, null);
        }
        string? key = Environment.GetEnvironmentVariable("GEMINI_API_KEY");
        if (key is { Length: > 0 } && (_accountModels is null || DateTime.UtcNow - _accountModelsAt > TimeSpan.FromHours(1)))
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, "https://generativelanguage.googleapis.com/v1beta/models?pageSize=200");
                request.Headers.Add("x-goog-api-key", key);
                using var response = await Http.SendAsync(request, ct);
                if (response.IsSuccessStatusCode)
                {
                    using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
                    _accountModels = ChatModels(doc.RootElement);
                    _accountModelsAt = DateTime.UtcNow;
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException) { }
        }
        return Catalog();
    }

    /// <summary>Filtra a resposta de /v1beta/models: só modelos Gemini de conversa (sem imagem, voz, embeddings…), mais novos primeiro.</summary>
    public static IReadOnlyList<string> ChatModels(JsonElement root)
    {
        var skip = new[] { "image", "tts", "audio", "embedding", "live", "native", "transcribe", "robotics", "computer-use", "banana", "omni", "customtools", "aqa" };
        var list = new List<string>();
        if (root.TryGetProperty("models", out var models) && models.ValueKind == JsonValueKind.Array)
            foreach (var m in models.EnumerateArray())
            {
                string id = (Str(m, "name") ?? "").Replace("models/", "");
                bool chat = m.TryGetProperty("supportedGenerationMethods", out var g) && g.ValueKind == JsonValueKind.Array && g.EnumerateArray().Any(x => x.GetString() == "generateContent");
                if (chat && id.StartsWith("gemini-", StringComparison.Ordinal) && !skip.Any(id.Contains)) list.Add(id);
            }
        return list.OrderByDescending(Version).ThenBy(id => id.Contains("preview") ? 1 : 0).ThenBy(id => id).ToList();
        static double Version(string id) { var m = System.Text.RegularExpressions.Regex.Match(id, @"gemini-(\d+(?:\.\d+)?)"); return m.Success ? double.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture) : 0; }
    }

    /// <summary>Endereço da API do Google no registro de recusas (a chave do registro usa só o host).</summary>
    internal const string GoogleApiBase = "https://generativelanguage.googleapis.com";

    /// <summary>O Google recusou este modelo no último teste ou uso (fica fora do seletor).</summary>
    public static bool IsRefused(string model) => ApiModelAvailability.IsRefused(GoogleApiBase, model);

    /// <summary>
    /// Erro do CLI que mostra que o Google não atende o modelo (404 / NOT_FOUND, ou 429 com "limit: 0"): grava no
    /// registro de recusas e devolve o motivo; outros erros devolvem null e não mudam nada.
    /// </summary>
    public static string? RecordRefusal(string model, string? error)
    {
        if (string.IsNullOrEmpty(error)) return null;
        int status = System.Text.RegularExpressions.Regex.IsMatch(error, @"\b404\b|NOT_FOUND", System.Text.RegularExpressions.RegexOptions.IgnoreCase) ? 404
            : System.Text.RegularExpressions.Regex.IsMatch(error, @"\b429\b|RESOURCE_EXHAUSTED", System.Text.RegularExpressions.RegexOptions.IgnoreCase) ? 429
            : System.Text.RegularExpressions.Regex.IsMatch(error, @"\b403\b|PERMISSION_DENIED", System.Text.RegularExpressions.RegexOptions.IgnoreCase) ? 403
            : System.Text.RegularExpressions.Regex.IsMatch(error, @"\b400\b|INVALID_ARGUMENT", System.Text.RegularExpressions.RegexOptions.IgnoreCase) ? 400 : 0;
        return status == 0 ? null : ApiModelAvailability.RecordRunError(GoogleApiBase, model, $"HTTP {status}: {error}");
    }

    public static AgentPrompt.Catalog Catalog()
    {
        string? model = null;
        try
        {
            string settings = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".gemini", "settings.json");
            if (File.Exists(settings) && JsonNode.Parse(File.ReadAllText(settings)) is JsonObject root)
                model = root["model"] is JsonObject m ? m["name"]?.GetValue<string>() : root["model"]?.GetValue<string>();
        }
        catch { }
        // Modelos que o Google recusa para a chave (aposentados, cota zero no plano, inexistentes) ficam fora, como no
        // Codex e no Office: o registro é o da verificação de modelos (Configurações › Codex e modelos). Antes, este
        // seletor lia a lista do Google direto e mostrava o Gemini 2.5, o 4 Argon etc. mesmo recusados.
        if (model is not null && IsRefused(model)) model = null;
        var models = new List<AgentPrompt.ModelOption>();
        if (model is not null) models.Add(new(model, AgentPrompt.Pretty(model), [], null));
        // Sem modelo no settings.json, o roteamento automático do CLI escolheu um modelo que levou 252 s para "6 x 7";
        // o padrão provisório é o Flash-Lite atual (3,5 s no mesmo teste). A pessoa pode trocar no seletor do chat.
        foreach (var id in new[] { DefaultModel }.Concat(_accountModels ?? Fallback))
            if (!IsRefused(id) && !models.Any(m => m.Id == id)) models.Add(new(id, AgentPrompt.Pretty(id), [], null));
        model ??= models.FirstOrDefault()?.Id ?? DefaultModel;
        string? error = Find() is null ? LocalizedText.T("Gemini CLI não encontrado neste PC")
            : !SignedIn() ? (UsesApiKey ? LocalizedText.T("Salve uma chave do Google AI Studio em Configurações › IAs e contas › Gemini › Chave de API")
                : LocalizedText.T("Entre com a conta Google em Configurações › IAs e contas › Gemini › Entrar")) : null;
        return new(models, model, null, error);
    }

    /// <summary>Argumentos do CLI (testável): modelo, retomada, aprovação conforme o acesso e saída em stream-json.</summary>
    public static List<string> Arguments(string prompt, string? model, AgentPrompt.Options o)
    {
        var args = new List<string> { "--output-format", "stream-json" };
        if (!string.IsNullOrEmpty(model)) args.AddRange(["--model", model]);
        if (o.ResumeSession is { } session) args.AddRange(["--resume", session]);
        // Sessão nova no meio de uma conversa da barra (veio de outra IA): a conversa vai como contexto.
        else prompt = ConversationHandoff.WithTranscript(prompt, o.History);
        // Acesso completo: sem pedido de aprovação; lápis ligado: edições automáticas; senão, só leitura/resposta.
        args.AddRange(["--approval-mode", o.Unrestricted ? "yolo" : o.AllowEdits ? "auto_edit" : "default"]);
        foreach (var dir in (o.ExtraDirs ?? []).Where(Directory.Exists)) args.AddRange(["--include-directories", dir]);
        string text = o.Attachments.Count == 0 ? prompt
            : prompt + "\n\n" + LocalizedText.T("Anexos:") + "\n" + string.Join("\n", o.Attachments.Select(a => "@" + a));
        args.AddRange(["--prompt", text]);
        return args;
    }

    public static async Task<AgentPrompt.Result> RunAsync(string prompt, string? model, AgentPrompt.Options o, Action<string> onText, CancellationToken ct)
    {
        if (UsesWeb) return await WebRunner!(prompt, model, o, onText, ct);
        if (Find() is not { } install) return new("", null, LocalizedText.T("Gemini CLI não encontrado neste PC"));
        string workDir = o.WorkDir is { } wd && Directory.Exists(wd) ? wd : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var psi = new ProcessStartInfo(install.Node)
        {
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = workDir,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
        };
        psi.ArgumentList.Add(install.Script);
        // Nunca deixa o CLI no roteamento automático (lento): sem escolha, usa o padrão provisório.
        foreach (var a in Arguments(prompt, string.IsNullOrEmpty(model) ? DefaultModel : model, o)) psi.ArgumentList.Add(a);
        if (o.Ephemeral) psi.Environment["GEMINI_CLI_NO_RELAUNCH"] = "true";
        // Método escolhido na barra; o login do CLI e a chave coexistem sem que um apague o outro.
        psi.Environment["GEMINI_CLI_AUTH_OVERRIDE"] = UsesApiKey ? "gemini-api-key" : "oauth-personal";
        if (!UsesApiKey) psi.Environment.Remove("GEMINI_API_KEY");
        if (!UsesApiKey && !string.IsNullOrWhiteSpace(CloudProject)) psi.Environment["GOOGLE_CLOUD_PROJECT"] = CloudProject;
        var text = new StringBuilder();
        string? session = o.ResumeSession, error = null, final = null;
        Process? proc = null;
        try
        {
            proc = Process.Start(psi);
            if (proc is null) return new("", null, LocalizedText.T("Não foi possível iniciar o Gemini CLI"));
            proc.StandardInput.Close();
            var stderr = proc.StandardError.ReadToEndAsync(ct);
            using var reg = ct.Register(() => { try { if (!proc.HasExited) proc.Kill(entireProcessTree: true); } catch { } });
            while (await proc.StandardOutput.ReadLineAsync(ct) is { } line)
            {
                var ev = ParseEvent(line);
                if (ev.Session is not null) session = ev.Session;
                if (ev.Delta is { } delta) { text.Append(delta); onText(text.ToString()); }
                if (ev.Final is not null) final = ev.Final;
                if (ev.Error is not null) error = ev.Error;
            }
            await proc.WaitForExitAsync(ct);
            string err = (await stderr).Trim();
            if (text.Length == 0 && final is { Length: > 0 }) { text.Append(final); onText(final); }
            if (proc.ExitCode != 0 && error is null)
                error = err.Length > 0 ? Short(FriendlyError(err)!) : LocalizedText.F("O Gemini CLI terminou com erro ({0})", new object?[] { proc.ExitCode });
            // Pedido pela chave recusado porque o Google não atende o modelo: sai do seletor na hora, como nas APIs.
            if (text.Length == 0 && UsesApiKey && RecordRefusal(string.IsNullOrEmpty(model) ? DefaultModel : model, error ?? err) is { } why)
                error = LocalizedText.F("O Google recusou este modelo ({0}). Ele saiu da lista; escolha outro.", new object?[] { why });
            return new(text.ToString(), session, FriendlyError(error));
        }
        catch (OperationCanceledException) { return new(text.ToString(), session, "Interrompido"); }
        catch (Exception ex) { return new(text.ToString(), session, ex.Message); }
        finally { proc?.Dispose(); }
    }

    public sealed record Event(string? Session, string? Delta, string? Final, string? Error);

    /// <summary>Uma linha do stream-json: sessão, trecho da resposta do assistente, resultado final ou erro.</summary>
    public static Event ParseEvent(string line)
    {
        if (line.Length == 0 || line[0] != '{') return new(null, null, null, null);
        try
        {
            using var doc = JsonDocument.Parse(line);
            var r = doc.RootElement;
            string? type = Str(r, "type");
            string? session = Str(r, "session_id") ?? Str(r, "sessionId");
            string? delta = null, final = null, error = null;
            if (type == "message" && Str(r, "role") == "assistant") delta = Str(r, "content") ?? Str(r, "text");
            else if (type == "result")
            {
                final = Str(r, "response") ?? Str(r, "result");
                if (Str(r, "status") is "error" or "failed" || r.TryGetProperty("error", out var e) && e.ValueKind is JsonValueKind.Object or JsonValueKind.String)
                    error = r.TryGetProperty("error", out var ee) ? (ee.ValueKind == JsonValueKind.Object ? Str(ee, "message") ?? ee.ToString() : ee.GetString()) : "O Gemini CLI retornou um erro";
            }
            else if (type == "error") error = Str(r, "message") ?? line;
            return new(session, delta, final, error);
        }
        catch (JsonException) { return new(null, null, null, null); }
    }

    private static string? Str(JsonElement e, string n) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    private static string Short(string s) => s.Length <= 400 ? s : s[..400] + "…";
}
