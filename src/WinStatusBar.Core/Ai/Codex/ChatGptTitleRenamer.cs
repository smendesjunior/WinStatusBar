using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace WinStatusBar.Core.Ai.Codex;

/// <summary>
/// Renomeia sozinho os chats do ChatGPT e as conversas locais do Codex para "DD/MM/AAAA - Tema - Assunto". Usa o login do Codex (auth.json, o mesmo da
/// cota): lista as conversas recentes da conta, gera o título com o modelo mais leve da conta (endpoint do Codex, sem
/// guardar nada) e grava pelo mesmo pedido que o site faz ao renomear. Endpoints internos do ChatGPT: se mudarem, a
/// rodada falha em silêncio (só o registro) e nada é alterado.
/// </summary>
public sealed class ChatGptTitleRenamer : IDisposable
{
    public static readonly TimeSpan Interval = TimeSpan.FromHours(1);
    private static readonly TimeSpan RequestSpacing = TimeSpan.FromSeconds(5);
    private const int RoundLimit = 5;
    private const string Api = "https://chatgpt.com/backend-api";
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(60) };

    private readonly Func<bool> _enabled;
    private readonly Func<bool> _blocked;
    private readonly Action<string> _log;
    private readonly HashSet<string> _done = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _failures = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTimeOffset> _deferredReads = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private CancellationTokenSource? _cts;
    private DateTimeOffset _nextRequestUtc;
    private int _consecutiveRateLimits;

    private sealed class RenameRateLimitedException : Exception { }

    /// <param name="enabled">Opção ligada nas Configurações.</param>
    /// <param name="blocked">Cota do Codex esgotada: a rodada espera.</param>
    public ChatGptTitleRenamer(Func<bool> enabled, Func<bool> blocked, Action<string> log)
    { _enabled = enabled; _blocked = blocked; _log = log; }

    public int RenamedCount { get; private set; }
    public DateTimeOffset? LastRun { get; private set; }
    public DateTimeOffset RetryAtUtc { get; private set; }

    public void Start()
    {
        if (_cts is not null) return;
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        _ = Task.Run(async () =>
        {
            using var timer = new PeriodicTimer(Interval);
            while (await SafeTick(timer, ct))
            {
                if (_enabled() && !Presence.Away && !_blocked())
                    try { await RunOnceAsync(ct); } catch (OperationCanceledException) { return; } catch (Exception ex) { _log("Títulos do ChatGPT: " + ex.Message); }
            }
        }, ct);
    }

    private static async Task<bool> SafeTick(PeriodicTimer t, CancellationToken ct) { try { return await t.WaitForNextTickAsync(ct); } catch { return false; } }

    public void Stop() { try { _cts?.Cancel(); } catch { } _cts = null; }
    public void Dispose() => Stop();

    /// <summary>Processa até cinco conversas por rodada, compartilhadas entre ChatGPT e Codex.</summary>
    public async Task<int> RunOnceAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            if (DateTimeOffset.UtcNow < RetryAtUtc || ReadAuth() is not { } auth) return 0;
            LastRun = DateTimeOffset.Now;
            int initialCount = RenamedCount;
            int budget = RoundLimit;
            try
            {
                await RenameChatGptAsync(auth, () => budget, () => budget--, ct);
                await RenameCodexAsync(auth, () => budget, () => budget--, ct);
                _consecutiveRateLimits = 0;
            }
            catch (RenameRateLimitedException)
            {
                _log($"Títulos do ChatGPT: rodada interrompida por HTTP 429. Próxima tentativa a partir de {RetryAtUtc.ToLocalTime():g}.");
            }
            return RenamedCount - initialCount;
        }
        finally { _gate.Release(); }
    }

    private async Task RenameChatGptAsync(Auth auth, Func<int> left, Action spend, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var pending = new List<(string Id, string? Title, DateTimeOffset Created)>();
        for (int page = 0, offset = 0; page < 50; page++)
        {
            using var list = await SendAsync(auth, HttpMethod.Get, $"{Api}/conversations?offset={offset}&limit=100&order=updated", null, ct);
            if (list is null || !list.RootElement.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array) break;
            int count = 0;
            foreach (var item in items.EnumerateArray())
            {
                count++;
                string? id = Str(item, "id"), title = Str(item, "title");
                if (id is null || _done.Contains(id) || _failures.GetValueOrDefault(id) >= 3) continue;
                var created = Time(item, "create_time");
                if (IsFormatted(title)) { _done.Add(id); continue; }
                if (_deferredReads.GetValueOrDefault(id) > now) continue;
                _deferredReads.Remove(id);
                if (ShouldRename(title, created, now)) pending.Add((id, title, created!.Value));
            }
            offset += count;
            if (count < 100) break;
        }
        foreach (var (id, title, created) in pending)
        {
            if (left() <= 0) break;
            spend();
            using var conv = await SendAsync(auth, HttpMethod.Get, $"{Api}/conversation/{id}", null, ct);
            string excerpt = conv is null ? "" : Excerpt(conv.RootElement);
            if (excerpt.Length < 20)
            {
                // Deixa as outras conversas avançarem e volta à leitura em um dia.
                _deferredReads[id] = now.AddDays(1);
                continue;
            }
            string? subject = await GenerateAsync(auth, excerpt, ct);
            if (subject is null) { _failures[id] = _failures.GetValueOrDefault(id) + 1; continue; }
            string newTitle = Compose(created, subject);
            using var ok = await SendAsync(auth, HttpMethod.Patch, $"{Api}/conversation/{id}", new JsonObject { ["title"] = newTitle }.ToJsonString(), ct);
            if (ok is null) { _failures[id] = _failures.GetValueOrDefault(id) + 1; continue; }
            _done.Add(id); RenamedCount++;
            _log($"Títulos do ChatGPT: «{title}» → «{newTitle}»");
        }
    }

    /// <summary>
    /// Conversas locais do Codex (app e CLI, inclusive as de projetos e as fixadas): lista e renomeia pelo servidor
    /// oficial do Codex (thread/list e thread/name/set), nunca editando os arquivos. Subagentes, exec e conversas da
    /// barra ficam de fora.
    /// </summary>
    private async Task RenameCodexAsync(Auth auth, Func<int> left, Action spend, CancellationToken ct)
    {
        if (left() <= 0 || CodexAppServerClient.FindCodexExecutable() is null) return;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromMinutes(30));
            await using var rpc = await CodexComposerConnection.OpenAsync(timeout.Token);
            var now = DateTimeOffset.UtcNow;
            var pending = new List<(string Id, string? Title, DateTimeOffset Created, string Excerpt)>();
            string? cursor = null;
            for (int page = 0; page < 40; page++)
            {
                var request = new JsonObject { ["archived"] = false, ["sortKey"] = "updated_at", ["limit"] = 50 };
                if (cursor is not null) request["cursor"] = cursor;
                var result = await rpc.CallAsync("thread/list", request, timeout.Token);
                if (!result.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array) break;
                foreach (var item in data.EnumerateArray())
                {
                    var info = CodexThreadInfo.FromThreadList(item);
                    string key = "codex:" + info.Id;
                    if (info.Id.Length == 0 || _done.Contains(key) || _failures.GetValueOrDefault(key) >= 3) continue;
                    if (!CodexQuotaStops.IsUserThread(info) || IsFormatted(info.Title)) { _done.Add(key); continue; }
                    var created = Time(item, "createdAt");
                    if (!ShouldRename(info.Title, created, now)) continue;
                    string excerpt = Str(item, "preview") is { } p ? (p.Length > 3000 ? p[..3000] : p) : "";
                    // Conversa que começa com "oi": o nome dado pelo Codex basta para o título.
                    if (excerpt.Trim().Length + (info.Title?.Length ?? 0) >= 2) pending.Add((info.Id, info.Title, created!.Value, excerpt));
                }
                cursor = Str(result, "nextCursor");
                if (string.IsNullOrEmpty(cursor)) break;
            }
            foreach (var (id, title, created, excerpt) in pending)
            {
                if (left() <= 0) break;
                string key = "codex:" + id;
                spend();
                // O nome que o Codex deu entra junto: ajuda quando o início da conversa é só a lista de arquivos anexados.
                string? subject = await GenerateAsync(auth, (title is { Length: > 0 } ? "título atual: " + title + "\n" : "") + "user: " + excerpt, timeout.Token);
                if (subject is null) { _failures[key] = _failures.GetValueOrDefault(key) + 1; continue; }
                string newTitle = Compose(created, subject);
                await rpc.CallAsync("thread/name/set", new JsonObject { ["threadId"] = id, ["name"] = newTitle }, timeout.Token);
                _done.Add(key); RenamedCount++;
                _log($"Títulos do Codex: «{title}» → «{newTitle}»");
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) when (ex is not RenameRateLimitedException) { _log("Títulos do Codex: " + ex.Message); }
    }

    // ------------------------------------------------------------------ regras (testadas)

    private static readonly Regex Formatted = new(@"^\d{2}/\d{2}/\d{4} - .+ - .+", RegexOptions.Compiled);

    public static bool IsFormatted(string? title) => title is not null && Formatted.IsMatch(title.Trim());

    /// <summary>
    /// Fora do formato e com pelo menos 3 minutos (o ChatGPT dá o título dele logo depois da primeira resposta; antes
    /// disso, o nosso seria trocado). Qualquer idade: as antigas também entram.
    /// </summary>
    public static bool ShouldRename(string? title, DateTimeOffset? created, DateTimeOffset now) =>
        !IsFormatted(title) && created is { } c && now - c >= TimeSpan.FromMinutes(3);

    public static string Compose(DateTimeOffset createdUtc, string subject) =>
        createdUtc.ToLocalTime().ToString("dd/MM/yyyy", System.Globalization.CultureInfo.InvariantCulture) + " - " + subject;

    /// <summary>Limpa a resposta do modelo: uma linha, sem aspas nem data, até 80 caracteres; null se não vier "Tema - Assunto".</summary>
    public static string? CleanSubject(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        string s = raw.Trim().Split('\n')[0].Trim().Trim('"', '\'', '«', '»', '“', '”', '*', ' ', '.');
        s = Regex.Replace(s, @"^(t[íi]tulo:\s*)", "", RegexOptions.IgnoreCase);
        s = Regex.Replace(s, @"^\d{2}/\d{2}/\d{4}\s*-\s*", "");
        s = Regex.Replace(s, @"\s*[–—]\s*", " - ");
        s = Regex.Replace(s, @"\s+", " ").Trim();
        if (s.Length > 80) s = s[..80].TrimEnd(' ', '-');
        return s.Contains(" - ", StringComparison.Ordinal) && s.Length >= 5 ? s : null;
    }

    /// <summary>Início da conversa (até 4 mensagens de texto, 3000 caracteres) a partir do JSON de /conversation/{id}.</summary>
    public static string Excerpt(JsonElement conv)
    {
        if (!conv.TryGetProperty("mapping", out var mapping) || mapping.ValueKind != JsonValueKind.Object) return "";
        var msgs = new List<(double T, string Line)>();
        foreach (var node in mapping.EnumerateObject())
        {
            if (!node.Value.TryGetProperty("message", out var m) || m.ValueKind != JsonValueKind.Object) continue;
            string? role = m.TryGetProperty("author", out var a) ? Str(a, "role") : null;
            if (role is not ("user" or "assistant")) continue;
            if (!m.TryGetProperty("content", out var c) || !c.TryGetProperty("parts", out var parts) || parts.ValueKind != JsonValueKind.Array) continue;
            string text = string.Join(" ", parts.EnumerateArray().Where(p => p.ValueKind == JsonValueKind.String).Select(p => p.GetString()));
            if (string.IsNullOrWhiteSpace(text)) continue;
            double t = m.TryGetProperty("create_time", out var ct) && ct.ValueKind == JsonValueKind.Number ? ct.GetDouble() : 0;
            msgs.Add((t, role + ": " + text.Trim()));
        }
        string all = string.Join("\n", msgs.OrderBy(x => x.T).Take(4).Select(x => x.Line));
        return all.Length > 3000 ? all[..3000] : all;
    }

    /// <summary>Modelo mais leve da conta (models_cache.json do Codex): "luna"/"mini"; senão, o do config.toml.</summary>
    public static string? PickModel(string? modelsCacheJson, string? configToml)
    {
        try
        {
            if (modelsCacheJson is not null && JsonNode.Parse(modelsCacheJson)?["models"] is JsonArray arr)
            {
                var slugs = arr.Select(m => m?["slug"]?.GetValue<string>()).OfType<string>().ToList();
                if (slugs.FirstOrDefault(s => s.EndsWith("-luna", StringComparison.Ordinal)) is { } luna) return luna;
                if (slugs.FirstOrDefault(s => s.Contains("mini", StringComparison.Ordinal)) is { } mini) return mini;
            }
        }
        catch { }
        return configToml is null ? null
            : Regex.Match(configToml.Split("\n[", 2)[0], @"(?m)^\s*model\s*=\s*""([^""]+)""") is { Success: true } m ? m.Groups[1].Value : null;
    }

    // ------------------------------------------------------------------ rede

    private sealed record Auth(string Token, string? Account, string? Model);

    private Auth? ReadAuth()
    {
        try
        {
            string home = CodexAppServerClient.CodexHome();
            var root = JsonNode.Parse(File.ReadAllText(Path.Combine(home, "auth.json")));
            string? token = root?["tokens"]?["access_token"]?.GetValue<string>();
            if (string.IsNullOrEmpty(token)) return null;
            string? account = root?["tokens"]?["account_id"]?.GetValue<string>();
            string Read(string f) { try { return File.ReadAllText(Path.Combine(home, f)); } catch { return null!; } }
            return new Auth(token, account, PickModel(Read("models_cache.json"), Read("config.toml")));
        }
        catch { return null; }
    }

    internal static DateTimeOffset RateLimitRetryAt(DateTimeOffset now, RetryConditionHeaderValue? retryAfter, int consecutiveRateLimits)
    {
        var serverDeadline = retryAfter?.Date ?? now + (retryAfter?.Delta ?? TimeSpan.Zero);
        var fallbackDeadline = now + TimeSpan.FromHours(1 << Math.Clamp(consecutiveRateLimits - 1, 0, 2));
        return serverDeadline > fallbackDeadline ? serverDeadline : fallbackDeadline;
    }

    private async Task<HttpResponseMessage> SendPacedAsync(HttpRequestMessage request, HttpCompletionOption completion, CancellationToken ct)
    {
        var delay = _nextRequestUtc - DateTimeOffset.UtcNow;
        if (delay > TimeSpan.Zero) await Task.Delay(delay, ct);
        _nextRequestUtc = DateTimeOffset.UtcNow + RequestSpacing;
        var response = await Http.SendAsync(request, completion, ct);
        if (response.StatusCode != HttpStatusCode.TooManyRequests) return response;

        using (response)
            RetryAtUtc = RateLimitRetryAt(DateTimeOffset.UtcNow, response.Headers.RetryAfter, ++_consecutiveRateLimits);
        throw new RenameRateLimitedException();
    }

    private async Task<JsonDocument?> SendAsync(Auth auth, HttpMethod method, string url, string? json, CancellationToken ct)
    {
        using var req = Request(auth, method, url, json);
        using var resp = await SendPacedAsync(req, HttpCompletionOption.ResponseContentRead, ct);
        if (!resp.IsSuccessStatusCode)
        {
            _log($"Títulos do ChatGPT: {method} {new Uri(url).AbsolutePath} → HTTP {(int)resp.StatusCode}"
                + (resp.StatusCode == HttpStatusCode.Unauthorized ? " (login do Codex expirado; abra o Codex para renovar)" : ""));
            return null;
        }
        string text = await resp.Content.ReadAsStringAsync(ct);
        try { return JsonDocument.Parse(string.IsNullOrWhiteSpace(text) ? "{}" : text); } catch { return JsonDocument.Parse("{}"); }
    }

    private static HttpRequestMessage Request(Auth auth, HttpMethod method, string url, string? json)
    {
        var req = new HttpRequestMessage(method, url);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", auth.Token);
        if (auth.Account is { } acc) req.Headers.TryAddWithoutValidation("chatgpt-account-id", acc);
        req.Headers.TryAddWithoutValidation("User-Agent", "Mozilla/5.0 WinStatusBar");
        if (json is not null) req.Content = new StringContent(json, Encoding.UTF8, "application/json");
        return req;
    }

    private const string Instructions =
        "Gere um título curto para a conversa no formato 'Tema - Assunto' (em português, sem data, sem aspas, até 60 caracteres). Responda só o título.";

    private async Task<string?> GenerateAsync(Auth auth, string excerpt, CancellationToken ct)
    {
        if (auth.Model is null) { _log("Títulos do ChatGPT: nenhum modelo do Codex encontrado."); return null; }
        var body = new JsonObject
        {
            ["model"] = auth.Model, ["instructions"] = Instructions, ["stream"] = true, ["store"] = false,
            ["input"] = new JsonArray(new JsonObject
            {
                ["role"] = "user",
                ["content"] = new JsonArray(new JsonObject { ["type"] = "input_text", ["text"] = excerpt }),
            }),
        };
        using var req = Request(auth, HttpMethod.Post, $"{Api}/codex/responses", body.ToJsonString());
        using var resp = await SendPacedAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!resp.IsSuccessStatusCode) { _log($"Títulos do ChatGPT: título com {auth.Model} → HTTP {(int)resp.StatusCode}"); return null; }
        var sb = new StringBuilder();
        using var reader = new StreamReader(await resp.Content.ReadAsStreamAsync(ct));
        while (await reader.ReadLineAsync(ct) is { } line)
        {
            if (!line.StartsWith("data: ", StringComparison.Ordinal) || !line.Contains("output_text.delta", StringComparison.Ordinal)) continue;
            try { using var d = JsonDocument.Parse(line[6..]); if (Str(d.RootElement, "delta") is { } delta) sb.Append(delta); } catch { }
        }
        if (CleanSubject(sb.ToString()) is { } subject) return subject;
        // Resposta sem "Tema - Assunto" (às vezes vem só o assunto): vira "Conversa - <resposta>".
        string? fallback = CleanSubject("Conversa - " + sb.ToString().Trim().Split('\n')[0]);
        if (fallback is null) _log($"Títulos: resposta do modelo sem título utilizável («{(sb.Length > 60 ? sb.ToString()[..60] : sb.ToString())}»)");
        return fallback;
    }

    private static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static DateTimeOffset? Time(JsonElement e, string name)
    {
        if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(name, out var v)) return null;
        if (v.ValueKind == JsonValueKind.Number)
        {
            double n = v.GetDouble(); // segundos (ChatGPT, Codex) ou milissegundos
            return DateTimeOffset.FromUnixTimeMilliseconds((long)(n > 1e11 ? n : n * 1000));
        }
        return v.ValueKind == JsonValueKind.String && DateTimeOffset.TryParse(v.GetString(), System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AssumeUniversal, out var d) ? d : null;
    }
}
