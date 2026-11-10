using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;

namespace WinStatusBar.Core.Ai.Codex.ApiResume;

public enum ModelCheckResult { Ok, ModelNotFound, KeyRejected, OtherError }

/// <summary>Resposta da conferência do modelo: resultado, organização cobrada (cabeçalho openai-organization) e detalhe sem a chave.</summary>
public sealed record ModelCheck(ModelCheckResult Result, string? Organization, string? Detail, DateTimeOffset CheckedAt);

/// <summary>Confere de graça se a chave enxerga o modelo (GET /v1/models/{id}), com o proxy do sistema e cache de 10 min.</summary>
public sealed class OpenAiModelCheck(HttpMessageHandler? handler = null, Func<DateTimeOffset>? now = null)
{
    public const string BaseUrl = "https://api.openai.com/v1";
    public static readonly TimeSpan CacheFor = TimeSpan.FromMinutes(10);

    private readonly HttpClient _http = new(handler ?? new HttpClientHandler { UseProxy = true }, disposeHandler: handler is null)
    {
        Timeout = TimeSpan.FromSeconds(15),
    };
    private readonly Func<DateTimeOffset> _now = now ?? (() => DateTimeOffset.Now);
    private readonly Dictionary<string, ModelCheck> _cache = new(StringComparer.Ordinal);
    private readonly object _gate = new();

    /// <summary>Confere o modelo. <paramref name="fresh"/> ignora e renova o cache (usado logo antes de cada início).</summary>
    public async Task<ModelCheck> CheckAsync(string model, string apiKey, bool fresh, CancellationToken ct)
    {
        string cacheKey = model + "|" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(apiKey)))[..16];
        if (!fresh)
            lock (_gate)
                if (_cache.TryGetValue(cacheKey, out var cached) && _now() - cached.CheckedAt < CacheFor) return cached;

        var result = await QueryAsync(model, apiKey, ct).ConfigureAwait(false);
        if (result.Result != ModelCheckResult.OtherError)
            lock (_gate) _cache[cacheKey] = result;
        return result;
    }

    private async Task<ModelCheck> QueryAsync(string model, string apiKey, CancellationToken ct)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"{BaseUrl}/models/{Uri.EscapeDataString(model)}");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
            using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
            string body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            string? org = response.Headers.TryGetValues("openai-organization", out var values) ? values.FirstOrDefault() : null;
            int code = (int)response.StatusCode;
            var verdict = response.StatusCode switch
            {
                HttpStatusCode.OK => ModelCheckResult.Ok,
                HttpStatusCode.NotFound => ModelCheckResult.ModelNotFound,
                HttpStatusCode.Forbidden when body.Contains("model", StringComparison.OrdinalIgnoreCase) => ModelCheckResult.ModelNotFound,
                HttpStatusCode.Unauthorized => ModelCheckResult.KeyRejected,
                _ => ModelCheckResult.OtherError,
            };
            string? detail = verdict == ModelCheckResult.Ok ? null
                : $"HTTP {code}: {Redact(body.Length > 300 ? body[..300] : body, apiKey)}";
            return new ModelCheck(verdict, org, detail, _now());
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception e)
        {
            return new ModelCheck(ModelCheckResult.OtherError, null, Redact($"{e.GetType().Name}: {e.Message}", apiKey), _now());
        }
    }

    private static string Redact(string text, string apiKey) =>
        apiKey.Length > 0 ? text.Replace(apiKey, "•••", StringComparison.Ordinal) : text;
}
