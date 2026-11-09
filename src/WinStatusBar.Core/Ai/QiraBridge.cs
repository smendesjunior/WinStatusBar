using System.Text;
using System.Text.Json;

namespace WinStatusBar.Core.Ai;

/// <summary>
/// Lenovo Qira no chat da barra, sempre em modo Híbrido (o modelo local fica com a "IA local": phi4-mini no Ollama, que
/// escolhe GPU NVIDIA, CPU ou iGPU sozinho; o modo Local do Qira só roda na GPU NVIDIA e travava o núcleo). A execução é fornecida pela aplicação: a pergunta segue pela
/// mesma ponte que a interface oficial do Qira usa (mensagem "AIGCChat" do QuantumAI.Hub ao núcleo QTCore), então o
/// roteamento local/nuvem continua sendo o nativo da Lenovo. Aqui ficam só as regras independentes da interface.
/// </summary>
public static class QiraBridge
{
    public const string HybridMode = "HYBRID_MODE";
    public const string ModelId = "hybrid";

    /// <summary>Nome do modo informado pelo Qira, para mensagens ("LOCAL_MODE" → "local").</summary>
    public static string ModeLabel(string mode) => mode == HybridMode ? "híbrido" : mode.Replace("_MODE", "").ToLowerInvariant();

    /// <summary>Executor instalado pela aplicação; sem ele, o destino informa que está indisponível.</summary>
    public static Func<string, string?, AgentPrompt.Options, Action<string>, CancellationToken, Task<AgentPrompt.Result>>? Runner { get; set; }
    /// <summary>Null quando pronto; senão, a orientação para a pessoa.</summary>
    public static Func<string?>? Available { get; set; }
    /// <summary>Quem respondeu a última pergunta, como o próprio Qira informa (por exemplo, "Azure Agent").</summary>
    public static string? LastHandler { get; set; }

    public static AgentPrompt.Catalog Catalog()
    {
        string? error = Runner is null ? "Qira indisponível nesta instalação da barra." : Available?.Invoke();
        return new(error is null ? [new(ModelId, "Híbrido · local + nuvem", [], null)] : [], ModelId, null, error);
    }

    public static Task<AgentPrompt.Result> RunAsync(string prompt, string? model, AgentPrompt.Options options, Action<string> onText, CancellationToken ct)
        => Runner is { } run ? run(prompt, model, options, onText, ct)
            : Task.FromResult(new AgentPrompt.Result("", null, "Qira indisponível nesta instalação da barra."));

    /// <summary>Créditos da nuvem do Qira ("qira-credits"), como o próprio app informa em Settings/GetCloudQuota.</summary>
    public sealed record Quota(double PercentRemaining, int Used, int Limit, bool Unlimited, DateTimeOffset? Renews, DateTimeOffset Updated)
    {
        public double PercentUsed => Unlimited ? 0 : Math.Clamp(100 - PercentRemaining, 0, 100);
    }

    /// <summary>Última leitura conhecida (atualizada pela aplicação); null se nunca foi lida.</summary>
    public static Quota? CurrentQuota { get => _quota; set { _quota = value; QuotaChanged?.Invoke(); } }
    private static Quota? _quota;
    public static event Action? QuotaChanged;

    /// <summary>Lê o campo Data de GetCloudQuota: uma lista JSON (em texto) com um item por recurso.</summary>
    public static Quota? ParseQuota(string? data, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(data)) return null;
        try
        {
            using var doc = JsonDocument.Parse(data);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return null;
            foreach (var item in doc.RootElement.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) continue;
                string? feature = item.TryGetProperty("feature", out var f) && f.ValueKind == JsonValueKind.String ? f.GetString() : null;
                if (!string.Equals(feature, "qira-credits", StringComparison.OrdinalIgnoreCase)) continue;
                double Num(string name) => item.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : 0;
                bool unlimited = item.TryGetProperty("unlimited", out var u) && u.ValueKind == JsonValueKind.True;
                DateTimeOffset? renews = item.TryGetProperty("renewDate", out var r) && r.ValueKind == JsonValueKind.String
                    && DateTimeOffset.TryParse(r.GetString(), System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal, out var at) ? at : null;
                int used = (int)Num("consumption"), limit = (int)Num("limit");
                double remaining = item.TryGetProperty("percentageRemaining", out var pr) && pr.ValueKind == JsonValueKind.Number ? pr.GetDouble()
                    : limit > 0 ? 100.0 * (limit - used) / limit : 100;
                return new(Math.Clamp(remaining, 0, 100), used, limit, unlimited, renews, now);
            }
        }
        catch (JsonException) { }
        return null;
    }

    /// <summary>Texto curto dos créditos: "99% restantes · 1/100 usados · renova 01/12".</summary>
    public static string QuotaText(Quota q)
    {
        if (q.Unlimited) return "Créditos da nuvem ilimitados";
        var pt = System.Globalization.CultureInfo.GetCultureInfo("pt-BR");
        string renew = q.Renews is { } r ? " · renova " + r.ToLocalTime().ToString("dd/MM", pt) : "";
        return $"{Math.Round(q.PercentRemaining):0}% restantes · {q.Used}/{q.Limit} créditos usados{renew}";
    }

    private static readonly string[] BarHintMarkers =
        ["\n\n[Contexto da janela fornecido pelo usuário;", PromptLanguage.Marker, "\n\n[Formato da resposta, para o painel"];

    /// <summary>
    /// Só o texto da pessoa, sem as instruções que a barra acrescenta entre colchetes (idioma, formato, contexto).
    /// Quando o Qira responde pelo modelo local, o filtro de segurança dele (detecção de injeção de prompt) recusa essas
    /// instruções; a barra tenta de novo só com o texto da pessoa.
    /// </summary>
    public static string StripBarHints(string prompt)
    {
        int cut = BarHintMarkers.Select(m => prompt.IndexOf(m, StringComparison.Ordinal)).Where(i => i >= 0).DefaultIfEmpty(-1).Min();
        return cut < 0 ? prompt : prompt[..cut].TrimEnd();
    }

    /// <summary>Recusa quando o modo do Qira não pôde ser confirmado igual ao escolhido. Nada é enviado nesse caso.</summary>
    public static string? ModeError(string? mode, string wanted = HybridMode) => string.Equals(mode, wanted, StringComparison.OrdinalIgnoreCase) ? null
        : mode is null ? "O Qira não está respondendo (o núcleo dele travou). Nada foi enviado. A barra o reinicia sozinha no máximo a cada 10 min; se continuar, feche o Qira pela bandeja e abra de novo."
        : $"Não foi possível colocar o Qira no modo {ModeLabel(wanted)} (está em {ModeLabel(mode)}). Nada foi enviado.";

    /// <summary>
    /// Pergunta com o histórico da conversa da barra, no mesmo formato que a interface do Qira usa para dar contexto
    /// ("[Earlier in this conversation]" … "[End of earlier context]").
    /// </summary>
    public static string WithHistory(string prompt, IReadOnlyList<(string Role, string Content)> history, int maxChars = 12000)
    {
        if (history.Count == 0) return prompt;
        var lines = new List<string>();
        int used = 0;
        for (int i = history.Count - 1; i >= 0; i--)
        {
            var (role, content) = history[i];
            string line = (role == "assistant" ? "Assistant: " : "User: ") + content.Trim();
            if (used + line.Length > maxChars) break;
            lines.Insert(0, line);
            used += line.Length;
        }
        return lines.Count == 0 ? prompt
            : "[Earlier in this conversation]\n" + string.Join("\n\n", lines) + "\n[End of earlier context]\n\n" + prompt;
    }

    /// <summary>Um pedaço do fluxo de resposta do QTCore.</summary>
    public sealed record Chunk(string Status, string? Type, string? Response, IReadOnlyList<string> Links)
    {
        /// <summary>Pedido de aprovação de ferramenta de conector (status "user_approval_needed").</summary>
        public ToolApproval? Approval { get; init; }
    }

    /// <summary>Ferramenta que o Qira quer usar e aguarda aprovação; Sensitive = só "permitir uma vez" é oferecido pelo Qira.</summary>
    public sealed record ToolApproval(string ToolName, string? ServerName, string Method, string JobId, bool Sensitive);

    /// <summary>Aprova ferramentas automaticamente (acesso total a conectores); definido pela aplicação.</summary>
    public static Func<bool>? AutoApproveTools { get; set; }

    /// <summary>Resposta do Qira a um pedido de aprovação: accessType 2 = sempre permitir, 1 = permitir uma vez.</summary>
    public static System.Text.Json.Nodes.JsonObject ApprovalReply(ToolApproval a, string? sessionId) => new()
    {
        ["toolName"] = a.ToolName, ["accessType"] = a.Sensitive ? 1 : 2, ["jobId"] = a.JobId, ["method"] = a.Method, ["sessionId"] = sessionId ?? "",
    };

    /// <summary>
    /// Interpreta o campo "Chunk" das mensagens QTCoreStream:
    /// {"jobId":…,"status":"in_progress|complete|failed","data":{"text":"{\"type\":\"text\",\"response\":\"…\"}"}}.
    /// </summary>
    public static Chunk? Parse(string? chunkJson)
    {
        if (string.IsNullOrWhiteSpace(chunkJson)) return null;
        try
        {
            using var doc = JsonDocument.Parse(chunkJson);
            var root = doc.RootElement;
            string status = root.TryGetProperty("status", out var s) && s.ValueKind == JsonValueKind.String ? s.GetString()! : "";
            string jobId = root.TryGetProperty("jobId", out var jid) ? jid.ToString() : "";
            if (!root.TryGetProperty("data", out var data) || !data.TryGetProperty("text", out var textEl) || textEl.ValueKind != JsonValueKind.String)
                return new(status, null, null, []);
            string inner = textEl.GetString() ?? "";
            try
            {
                using var innerDoc = JsonDocument.Parse(inner);
                var o = innerDoc.RootElement;
                if (o.ValueKind == JsonValueKind.Array) // falhas vêm como ["codigo","mensagem"]
                    return new(status, null, string.Join(": ", o.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString())), []);
                if (o.ValueKind != JsonValueKind.Object) return new(status, null, inner, []);
                string? type = o.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null;
                if (status == "user_approval_needed" && o.TryGetProperty("response", out var ar) && ar.ValueKind == JsonValueKind.String)
                {
                    try
                    {
                        using var ad = JsonDocument.Parse(ar.GetString()!);
                        var a = ad.RootElement;
                        string? tool = a.TryGetProperty("toolName", out var tn) && tn.ValueKind == JsonValueKind.String ? tn.GetString() : null;
                        if (tool is { Length: > 0 })
                            return new(status, "approval", null, []) { Approval = new(tool,
                                a.TryGetProperty("serverName", out var sn) && sn.ValueKind == JsonValueKind.String ? sn.GetString() : null,
                                type ?? "tools/call", jobId, a.TryGetProperty("toolCategory", out var tc) && tc.ValueKind == JsonValueKind.Number && tc.GetInt32() == 1) };
                    }
                    catch (JsonException) { }
                }
                string? response = o.TryGetProperty("response", out var r) ? r.ValueKind == JsonValueKind.String ? r.GetString() : r.ToString() : null;
                var links = new List<string>();
                foreach (var name in new[] { "searchUrls", "sources" })
                    if (o.TryGetProperty(name, out var arr) && arr.ValueKind == JsonValueKind.Array)
                        foreach (var item in arr.EnumerateArray())
                        {
                            string? url = item.ValueKind == JsonValueKind.String ? item.GetString()
                                : item.ValueKind == JsonValueKind.Object && item.TryGetProperty("url", out var u) && u.ValueKind == JsonValueKind.String ? u.GetString() : null;
                            if (url is { Length: > 0 } && url.StartsWith("http", StringComparison.OrdinalIgnoreCase) && !links.Contains(url)) links.Add(url);
                        }
                return new(status, type, response, links);
            }
            catch (JsonException) { return new(status, "text", inner, []); }
        }
        catch (JsonException) { return null; }
    }

    /// <summary>Etapas que o Qira informa durante a resposta, em português (as demais ficam como vieram).</summary>
    public static string StatusText(string status) => status.Trim().TrimEnd('.', '…') switch
    {
        "Loading tools" => "Carregando as ferramentas dos conectores…",
        "Counting tokens" => "Preparando a pergunta…",
        "Checking quota" => "Conferindo os créditos…",
        "Processing message" or "Parsing messages" => "Processando a mensagem…",
        "Executing" => "Executando…",
        "Processing results" => "Processando os resultados…",
        "Run complete" => "Concluindo…",
        _ => status,
    };

    /// <summary>Acumula o fluxo: o texto do Qira chega sempre completo (cada pedaço repete o anterior e acrescenta).</summary>
    public sealed class Accumulator
    {
        private readonly List<string> _links = new();
        private readonly List<string> _media = new();
        private string _answer = "";
        /// <summary>Texto da resposta mais as imagens e vídeos gerados (em markdown, com o arquivo local).</summary>
        public string Text => _media.Count == 0 ? _answer : (_answer.Length > 0 ? _answer + "\n\n" : "") + string.Join("\n\n", _media);
        /// <summary>Etapa de ferramenta em andamento (ex.: "Gerando imagens…").</summary>
        public string? ToolStatus { get; private set; }
        public string? Handler { get; private set; }
        public string? Status { get; private set; }
        public string? Failure { get; private set; }
        public bool Done { get; private set; }
        /// <summary>Nenhum pedaço chegou (a mensagem se perdeu, típico logo após o Hub abrir): vale reenviar.</summary>
        public bool NoReply { get; set; }
        /// <summary>Tipos de conteúdo recebidos que só o app do Qira sabe exibir (cartões, imagens, ferramentas).</summary>
        public HashSet<string> OtherTypes { get; } = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Devolve true quando o texto visível mudou.</summary>
        public bool Add(Chunk c)
        {
            bool changed = false;
            if (c.Status is "complete" or "failed") Done = true;
            foreach (var l in c.Links) if (!_links.Contains(l)) _links.Add(l);
            switch (c.Type)
            {
                case "handler": Handler = c.Response; break;
                case "status": Status = c.Response; break;
                case "timings": break;
                case "approval": break; // tratado por quem lê o fluxo (aprovação automática ou recusa)
                case "text" or null when c.Response is { } text:
                    if (c.Status == "failed") { Failure = text.Length > 0 ? FriendlyFailure(text) : null; break; }
                    if (text.Length > 0 && text != _answer) { _answer = text; changed = true; }
                    break;
                case "tool": ToolStatus = c.Response; break;
                case "user_direct" when c.Response is { } direct && Media(direct) is { Count: > 0 } items:
                    foreach (var m in items) if (!_media.Contains(m)) { _media.Add(m); changed = true; }
                    break;
                default: if (c.Type is { Length: > 0 } other) OtherTypes.Add(other); break;
            }
            if (c.Status == "failed" && Failure is null) Failure = "O Qira não conseguiu responder.";
            return changed;
        }

        /// <summary>
        /// Resultado direto de ferramenta do Qira (geração de imagem/vídeo): {"code":200,"prompt":…,"mediaItems":[{"uri":"C:\\…png",
        /// "fileName":…,"mimetype":"image/png"}]}. Vira markdown: imagem inline (arquivo local) e link para abrir.
        /// </summary>
        public static List<string> Media(string json)
        {
            var list = new List<string>();
            try
            {
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.ValueKind != JsonValueKind.Object || !doc.RootElement.TryGetProperty("mediaItems", out var items) || items.ValueKind != JsonValueKind.Array) return list;
                foreach (var item in items.EnumerateArray())
                {
                    string? path = item.TryGetProperty("uri", out var u) && u.ValueKind == JsonValueKind.String ? u.GetString() : null;
                    if (string.IsNullOrWhiteSpace(path)) continue;
                    string uri = Uri.TryCreate(path, UriKind.Absolute, out var parsed) ? parsed.AbsoluteUri : path.Replace(" ", "%20");
                    string name = item.TryGetProperty("fileName", out var f) && f.ValueKind == JsonValueKind.String ? f.GetString()! : Path.GetFileName(path);
                    string mime = item.TryGetProperty("mimetype", out var mt) && mt.ValueKind == JsonValueKind.String ? mt.GetString()! : "";
                    list.Add(mime.StartsWith("video", StringComparison.OrdinalIgnoreCase)
                        ? $"Vídeo gerado: [{name}]({uri})"
                        : $"![{name}]({uri})\n[Abrir imagem]({uri})");
                }
            }
            catch (JsonException) { }
            return list;
        }

        /// <summary>Erros conhecidos do Qira em linguagem clara.</summary>
        public static string FriendlyFailure(string raw)
            => raw.Contains("local_llm_model_not_ready", StringComparison.OrdinalIgnoreCase)
                ? "O Qira escolheu o modelo local dele, que não está pronto neste PC (ele só roda com a GPU NVIDIA ligada). Tente de novo ou use a IA local (phi4-mini no Ollama)."
                : raw.Contains("failed_input_safety", StringComparison.OrdinalIgnoreCase)
                    ? "O filtro de segurança do Qira recusou esta mensagem. Reformule a pergunta."
                : raw;

        public string Final()
        {
            if (_links.Count == 0 || Text.Length == 0) return Text;
            var sb = new StringBuilder(Text).Append("\n\nFontes:\n");
            foreach (var l in _links.Take(8)) sb.Append("- ").Append(l).Append('\n');
            return sb.ToString().TrimEnd();
        }
    }
}
