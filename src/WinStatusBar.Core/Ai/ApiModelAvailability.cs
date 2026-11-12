using System.Text.Json;
using System.Text.RegularExpressions;

namespace WinStatusBar.Core.Ai;

/// <summary>
/// Modelos de API que o próprio serviço recusa para a chave cadastrada, mesmo aparecendo na lista de modelos dele:
/// aposentados ("no longer available to new users"), inexistentes ou sem acesso no plano (cota zero). Eles ficam fora
/// dos seletores da barra, do Codex e do suplemento do Office. Caso real (12/11/2026): o Gemini 2.5 Pro continuava na
/// lista da API do Google e respondia HTTP 404 a qualquer pedido.
/// A verificação de modelos testa cada um com um pedido mínimo e regrava o registro (um modelo que volta a responder
/// sai dele). Um pedido normal recusado do mesmo jeito também marca o modelo na hora. Cota temporária esgotada (HTTP 429
/// sem "limit: 0") e falhas de rede não tiram o modelo.
/// O registro fica em %LOCALAPPDATA%\WinStatusBar\api-modelos-recusados.json, lido pela barra e pelo encaminhador.
/// </summary>
public static partial class ApiModelAvailability
{
    public enum Verdict { Available, Refused, NoQuota, Unknown }

    /// <summary>Um modelo recusado: o motivo para a pessoa e quando foi visto.</summary>
    public sealed record Entry(string Reason, DateTime At);

    /// <summary>Resultado do teste de um modelo.</summary>
    public sealed record Probe(Verdict Verdict, string? Detail);

    /// <summary>Arquivo do registro (trocável só nos testes).</summary>
    public static string FilePath { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WinStatusBar", "api-modelos-recusados.json");

    private static readonly object Gate = new();
    private static (string File, DateTime Stamp, Dictionary<string, Entry> Map)? _cache;
    private static readonly JsonSerializerOptions Pretty = new() { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    [GeneratedRegex(@"limit:\s*0\b", RegexOptions.IgnoreCase)]
    private static partial Regex LimitZero();

    [GeneratedRegex(@"model[^.]{0,160}(not found|does not exist|no longer available|not available|not supported|deprecated|decommissioned|retired|discontinued|shut ?down)|model_not_found|(no|not have) access to (the |this )?model|no longer available",
        RegexOptions.IgnoreCase)]
    private static partial Regex ModelGone();

    /// <summary>Serviço (host do endereço) e modelo, sem o prefixo "models/" do Google e sem diferenciar maiúsculas.</summary>
    public static string Key(string baseUrl, string model)
    {
        string host = Uri.TryCreate(baseUrl?.Trim(), UriKind.Absolute, out var u) ? u.Host : (baseUrl ?? string.Empty).Trim();
        string id = (model ?? string.Empty).Trim();
        if (id.StartsWith("models/", StringComparison.OrdinalIgnoreCase)) id = id["models/".Length..];
        return (host + "|" + id).ToLowerInvariant();
    }

    /// <summary>O que uma resposta HTTP diz sobre o modelo.</summary>
    public static Verdict Classify(int status, string? message)
    {
        string m = message ?? string.Empty;
        if (status is >= 200 and < 300) return Verdict.Available;
        if (status == 429) return LimitZero().IsMatch(m) ? Verdict.Refused : Verdict.NoQuota;
        if (status == 404) return Verdict.Refused;
        if (status is 400 or 403 && ModelGone().IsMatch(m)) return Verdict.Refused;
        return Verdict.Unknown;
    }

    /// <summary>Motivo curto para a pessoa, a partir da resposta do serviço.</summary>
    public static string Reason(int status, string? message)
    {
        string m = message ?? string.Empty;
        if (status == 429) return "o seu plano da API não dá acesso a este modelo (cota zero)";
        if (Regex.IsMatch(m, "no longer available|deprecated|decommissioned|retired|discontinued|shut ?down", RegexOptions.IgnoreCase))
            return "o serviço aposentou este modelo para a sua chave";
        if (status == 403 || Regex.IsMatch(m, "access", RegexOptions.IgnoreCase)) return "a sua chave não tem acesso a este modelo";
        return "o serviço não reconhece este modelo";
    }

    /// <summary>Lê "HTTP 404: mensagem" (o formato dos erros das APIs na barra) e devolve o código e a mensagem.</summary>
    public static (int Status, string? Message)? ParseHttpError(string? error)
    {
        if (error is null) return null;
        var match = Regex.Match(error, @"^HTTP (\d{3})(?::\s*(.*))?$", RegexOptions.Singleline);
        if (!match.Success) return null;
        return (int.Parse(match.Groups[1].Value), match.Groups[2].Success ? match.Groups[2].Value : null);
    }

    private static Dictionary<string, Entry> Load()
    {
        lock (Gate)
        {
            string file = FilePath;
            DateTime stamp;
            try { stamp = File.Exists(file) ? File.GetLastWriteTimeUtc(file) : DateTime.MinValue; } catch { stamp = DateTime.MinValue; }
            if (_cache is { } c && c.File == file && c.Stamp == stamp) return c.Map;
            Dictionary<string, Entry> map = [];
            try
            {
                if (stamp != DateTime.MinValue)
                    map = JsonSerializer.Deserialize<Dictionary<string, Entry>>(File.ReadAllText(file)) ?? [];
            }
            catch { map = []; }
            map = new Dictionary<string, Entry>(map, StringComparer.OrdinalIgnoreCase);
            _cache = (file, stamp, map);
            return map;
        }
    }

    private static void Save(Dictionary<string, Entry> map)
    {
        lock (Gate)
        {
            string file = FilePath;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                string tmp = file + "." + Environment.ProcessId + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(map, Pretty));
                File.Move(tmp, file, overwrite: true);
                _cache = (file, File.GetLastWriteTimeUtc(file), new Dictionary<string, Entry>(map, StringComparer.OrdinalIgnoreCase));
            }
            catch { }
        }
    }

    /// <summary>O serviço recusou este modelo na última vez que foi testado ou usado.</summary>
    public static bool IsRefused(string baseUrl, string model) => Load().ContainsKey(Key(baseUrl, model));

    /// <summary>Motivo da recusa (null: não está recusado).</summary>
    public static string? RefusedReason(string baseUrl, string model) => Load().TryGetValue(Key(baseUrl, model), out var e) ? e.Reason : null;

    /// <summary>Grava o resultado de um teste ou pedido: recusado entra no registro, disponível sai; os demais não mudam nada.</summary>
    public static void Record(string baseUrl, string model, Verdict verdict, string? reason = null)
    {
        if (verdict is not (Verdict.Refused or Verdict.Available)) return;
        lock (Gate)
        {
            var map = new Dictionary<string, Entry>(Load(), StringComparer.OrdinalIgnoreCase);
            string key = Key(baseUrl, model);
            bool changed;
            if (verdict == Verdict.Refused)
            {
                string text = reason ?? "recusado pelo serviço";
                changed = !(map.TryGetValue(key, out var old) && old.Reason == text);
                if (changed) map[key] = new(text, DateTime.Now);
            }
            else changed = map.Remove(key);
            if (changed) Save(map);
        }
    }

    /// <summary>
    /// Grava o erro de um pedido normal: devolve o motivo quando ele mostra que o serviço recusa o modelo (o modelo
    /// sai dos seletores), ou null quando é outro erro.
    /// </summary>
    public static string? RecordRunError(string baseUrl, string model, string? error)
    {
        if (ParseHttpError(error) is not { } http) return null;
        if (Classify(http.Status, http.Message) != Verdict.Refused) return null;
        string reason = Reason(http.Status, http.Message);
        Record(baseUrl, model, Verdict.Refused, reason);
        return reason;
    }

    /// <summary>
    /// Testa cada modelo (até 4 por vez), grava o registro e devolve os que respondem ou ficaram sem conclusão,
    /// os recusados (com o motivo) e os sem cota agora.
    /// </summary>
    public static async Task<(List<T> Usable, List<(T Model, string Reason)> Refused, List<T> NoQuota)> CheckAsync<T>(
        string baseUrl, IReadOnlyList<T> models, Func<T, string> id, Func<T, CancellationToken, Task<Probe>> probe, CancellationToken ct)
    {
        using var turns = new SemaphoreSlim(4);
        var results = await Task.WhenAll(models.Select(async m =>
        {
            await turns.WaitAsync(ct);
            try { return (Model: m, Result: await probe(m, ct)); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) { return (Model: m, Result: new Probe(Verdict.Unknown, ex.Message)); }
            finally { turns.Release(); }
        }));
        List<T> usable = [], noQuota = [];
        List<(T, string)> refused = [];
        foreach (var (m, r) in results)
        {
            switch (r.Verdict)
            {
                case Verdict.Refused:
                    var http = ParseHttpError(r.Detail);
                    string reason = http is { } h ? Reason(h.Status, h.Message) : "recusado pelo serviço";
                    Record(baseUrl, id(m), Verdict.Refused, reason);
                    refused.Add((m, reason));
                    break;
                case Verdict.Available:
                    Record(baseUrl, id(m), Verdict.Available);
                    usable.Add(m);
                    break;
                case Verdict.NoQuota:
                    noQuota.Add(m); usable.Add(m);
                    break;
                default:
                    // Sem conclusão (rede, tempo esgotado, erro do servidor): fica como estava.
                    if (!IsRefused(baseUrl, id(m))) usable.Add(m);
                    else refused.Add((m, RefusedReason(baseUrl, id(m)) ?? "recusado pelo serviço"));
                    break;
            }
        }
        return (usable, refused, noQuota);
    }
}
