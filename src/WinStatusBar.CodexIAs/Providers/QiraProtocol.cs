using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CodexIAs.Providers;

/// <summary>Regras do fluxo de resposta do Qira (mesmas da WinStatusBar), independentes da conexão com o Hub.</summary>
internal static class QiraProtocol
{
    public const string HybridMode = "HYBRID_MODE";

    public static string ModeLabel(string mode) => mode == HybridMode ? "híbrido" : mode.Replace("_MODE", "").ToLowerInvariant();

    public static string? ModeError(string? mode, string wanted = HybridMode) => string.Equals(mode, wanted, StringComparison.OrdinalIgnoreCase) ? null
        : mode is null ? "O Qira não está respondendo (o núcleo dele travou). Nada foi enviado. A WinStatusBar o reinicia sozinha no máximo a cada 10 min; se continuar, feche o Qira pela bandeja e abra de novo."
        : $"Não foi possível colocar o Qira no modo {ModeLabel(wanted)} (está em {ModeLabel(mode)}). Nada foi enviado.";

    /// <summary>Pergunta com o histórico, no formato que a interface do Qira usa ("[Earlier in this conversation]").</summary>
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

    public sealed record Chunk(string Status, string? Type, string? Response, IReadOnlyList<string> Links)
    {
        public ToolApproval? Approval { get; init; }
    }

    public sealed record ToolApproval(string ToolName, string? ServerName, string Method, string JobId, bool Sensitive);

    /// <summary>Resposta a um pedido de aprovação: accessType 2 = sempre permitir, 1 = permitir uma vez.</summary>
    public static JsonObject ApprovalReply(ToolApproval a, string? sessionId) => new()
    {
        ["toolName"] = a.ToolName, ["accessType"] = a.Sensitive ? 1 : 2, ["jobId"] = a.JobId, ["method"] = a.Method, ["sessionId"] = sessionId ?? "",
    };

    /// <summary>Campo "Chunk" das mensagens QTCoreStream: {"jobId":…,"status":…,"data":{"text":"{\"type\":…,\"response\":…}"}}.</summary>
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
                if (o.ValueKind == JsonValueKind.Array)
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
        public string Text => _media.Count == 0 ? _answer : (_answer.Length > 0 ? _answer + "\n\n" : "") + string.Join("\n\n", _media);
        public string? ToolStatus { get; private set; }
        public string? Handler { get; private set; }
        public string? Status { get; private set; }
        public string? Failure { get; private set; }
        public bool Done { get; private set; }
        public bool NoReply { get; set; }
        public HashSet<string> OtherTypes { get; } = new(StringComparer.OrdinalIgnoreCase);

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
                case "approval": break;
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
                    list.Add(mime.StartsWith("video", StringComparison.OrdinalIgnoreCase) ? $"Vídeo gerado: [{name}]({uri})" : $"Imagem gerada: [{name}]({uri})");
                }
            }
            catch (JsonException) { }
            return list;
        }

        public static string FriendlyFailure(string raw)
            => raw.Contains("local_llm_model_not_ready", StringComparison.OrdinalIgnoreCase)
                ? "O Qira escolheu o modelo local dele, que não está pronto neste PC (ele só roda com a GPU NVIDIA ligada). Tente de novo."
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
