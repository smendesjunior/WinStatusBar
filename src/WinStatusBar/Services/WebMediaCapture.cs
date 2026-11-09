using System.Text.Json;
using Microsoft.UI.Xaml.Controls;

namespace WinStatusBar.Services;

/// <summary>
/// Imagens e vídeos gerados pelas IAs que a barra usa por páginas oficiais (Copilot, Gemini da conta Google).
/// O leitor de cada página lista a mídia da última resposta; aqui ela é baixada dentro da própria página
/// (com o login dela) e salva em %LOCALAPPDATA%\WinStatusBar\Media, para aparecer no chat da barra.
/// </summary>
internal static class WebMediaCapture
{
    public static readonly string Folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WinStatusBar", "Media");

    /// <summary>
    /// Trecho JS que recebe o contêiner da resposta (variável <c>box</c>) e define <c>media</c> e <c>mediaLoading</c>.
    /// Imagens pequenas (ícones, avatares) e as que estão dentro de botões ficam de fora.
    /// </summary>
    public const string CollectJs = """
        const media = []; let mediaLoading = false;
        const mediaRoots = [box].concat(typeof extraMediaRoots === 'undefined' ? [] : extraMediaRoots).filter(Boolean);
        for (const root of mediaRoots) for (const e of root.querySelectorAll('img,video')) {
          // Imagens geradas costumam ficar dentro de um botão (miniatura clicável); só as pequenas (ícones) são ignoradas.
          if (e.closest('button') && e.tagName === 'IMG' && e.complete && (e.naturalWidth < 256 || e.naturalHeight < 256)) continue;
          if (e.tagName === 'VIDEO') {
            const src = e.currentSrc || e.src || (e.querySelector('source') || {}).src || '';
            if (!src) { mediaLoading = true; continue; }
            media.push({ kind: 'video', src }); continue;
          }
          const src = e.currentSrc || e.src || '';
          if (!src || src.startsWith('data:image/svg')) continue;
          if (!e.complete) { const r = e.getBoundingClientRect(); if (r.width >= 200 || r.height >= 200) mediaLoading = true; continue; }
          if (e.naturalWidth < 256 || e.naturalHeight < 256) continue;
          if (media.some(m => m.src === src)) continue;
          media.push({ kind: 'image', src, w: e.naturalWidth, h: e.naturalHeight });
        }
        """;

    public sealed record Item(string Kind, string Src);

    public static IReadOnlyList<Item> Read(JsonElement message)
    {
        var list = new List<Item>();
        if (message.TryGetProperty("media", out var media) && media.ValueKind == JsonValueKind.Array)
            foreach (var m in media.EnumerateArray())
                if (m.TryGetProperty("src", out var s) && s.GetString() is { Length: > 0 } src && !list.Any(x => x.Src == src))
                    list.Add(new(m.TryGetProperty("kind", out var k) ? k.GetString() ?? "image" : "image", src));
        return list;
    }

    /// <summary>Baixa e salva cada item; devolve o markdown a acrescentar à resposta (imagem inline ou link do vídeo).</summary>
    public static async Task<string> SaveAsync(WebView2 web, IReadOnlyList<Item> items, string service, CancellationToken ct)
    {
        if (items.Count == 0) return "";
        Directory.CreateDirectory(Folder);
        var parts = new List<string>();
        int n = 0;
        foreach (var item in items.Take(8))
        {
            ct.ThrowIfCancellationRequested();
            n++;
            string? path = null;
            try
            {
                string type, b64;
                if (item.Src.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                {
                    // Imagem embutida (ex.: Designer do Copilot): já está no endereço, sem download.
                    int comma = item.Src.IndexOf(',');
                    int semi = item.Src.IndexOf(';');
                    type = item.Src[5..(semi > 5 && semi < comma ? semi : comma)];
                    b64 = item.Src[(comma + 1)..];
                }
                else
                {
                    // Busca dentro da página (com o login dela). ExecuteScriptAsync não espera Promises; o protocolo sim.
                    string script = "(async () => { try { const r = await fetch(" + JsonSerializer.Serialize(item.Src) + ", { credentials: 'include' }); "
                        + "if (!r.ok) return JSON.stringify({ error: 'HTTP ' + r.status }); const b = await r.blob(); "
                        + "const data = await new Promise(res => { const f = new FileReader(); f.onload = () => res(String(f.result).split(',')[1] || ''); f.readAsDataURL(b); }); "
                        + "return JSON.stringify({ type: b.type, data }); } catch (e) { return JSON.stringify({ error: String(e) }); } })()";
                    var args = new System.Text.Json.Nodes.JsonObject { ["expression"] = script, ["awaitPromise"] = true, ["returnByValue"] = true };
                    string raw = await web.CoreWebView2.CallDevToolsProtocolMethodAsync("Runtime.evaluate", args.ToJsonString());
                    using var outer = JsonDocument.Parse(raw);
                    string inner = outer.RootElement.GetProperty("result").TryGetProperty("value", out var v) ? v.GetString() ?? "{}" : "{}";
                    using var doc = JsonDocument.Parse(inner);
                    var root = doc.RootElement;
                    if (root.TryGetProperty("error", out var err)) { App.Log($"{service}: mídia não baixada ({err.GetString()})"); type = ""; b64 = ""; }
                    else { type = root.TryGetProperty("type", out var t) ? t.GetString() ?? "" : ""; b64 = root.TryGetProperty("data", out var d) ? d.GetString() ?? "" : ""; }
                }
                if (b64.Length == 0 && item.Src.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                {
                    // Bloqueio entre domínios na página (ex.: imagens do Google): baixa fora dela com os cookies do mesmo perfil.
                    var bytes = await DownloadWithCookiesAsync(web, item.Src, ct);
                    if (bytes is { Length: > 0 }) { type = bytes.Value.Type; b64 = Convert.ToBase64String(bytes.Value.Data); }
                }
                if (b64.Length == 0 && item.Kind == "image")
                {
                    // Arquivo protegido (ex.: Gemini): fotografa a imagem na própria página, recortada nela e em escala
                    // próxima da resolução original. Assim ela aparece no chat como arquivo local.
                    b64 = await CaptureElementAsync(web, item.Src) ?? "";
                    if (b64.Length > 0) { type = "image/png"; App.Log($"{service}: imagem capturada da página"); }
                }
                if (b64.Length > 0)
                {
                    string ext = Extension(type, item.Kind);
                    path = Path.Combine(Folder, $"{service}-{DateTime.Now:yyyyMMdd-HHmmss}-{n}{ext}");
                    await File.WriteAllBytesAsync(path, Convert.FromBase64String(b64), ct);
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { App.Log($"{service}: mídia não baixada ({ex.Message})"); }

            string uri = path is not null ? new Uri(path).AbsoluteUri : item.Src.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? item.Src : "";
            if (uri.Length == 0) continue;
            string name = path is not null ? Path.GetFileName(path) : (item.Kind == "video" ? "vídeo" : "imagem");
            parts.Add(item.Kind == "video" ? $"Vídeo gerado: [{name}]({uri})" : $"![{name}]({uri})\n[Abrir imagem]({uri})");
        }
        if (parts.Count > 0) App.Log($"{service}: {parts.Count} mídia(s) gerada(s) salva(s) em {Folder}");
        return string.Join("\n\n", parts);
    }

    /// <summary>Captura (PNG, base64) da imagem com esse endereço, pelo protocolo do WebView2; null se não achar.</summary>
    private static async Task<string?> CaptureElementAsync(WebView2 web, string src)
    {
        try
        {
            string locate = "(() => { const img = [...document.querySelectorAll('img')].find(i => (i.currentSrc || i.src) === " + JsonSerializer.Serialize(src) + ");"
                + " if (!img) return JSON.stringify(null); img.scrollIntoView({ block: 'center' }); const r = img.getBoundingClientRect();"
                + " return JSON.stringify({ x: r.left + scrollX, y: r.top + scrollY, w: r.width, h: r.height, nw: img.naturalWidth, nh: img.naturalHeight }); })()";
            string raw = await web.CoreWebView2.ExecuteScriptAsync(locate);
            using var doc = JsonDocument.Parse(JsonSerializer.Deserialize<string>(raw) ?? "null");
            var r = doc.RootElement;
            if (r.ValueKind != JsonValueKind.Object) return null;
            double w = r.GetProperty("w").GetDouble(), h = r.GetProperty("h").GetDouble();
            if (w < 50 || h < 50) return null;
            double scale = Math.Clamp(r.GetProperty("nw").GetDouble() / w, 1, 4);
            await Task.Delay(300);
            var args = new System.Text.Json.Nodes.JsonObject
            {
                ["format"] = "png", ["captureBeyondViewport"] = true,
                ["clip"] = new System.Text.Json.Nodes.JsonObject { ["x"] = r.GetProperty("x").GetDouble(), ["y"] = r.GetProperty("y").GetDouble(), ["width"] = w, ["height"] = h, ["scale"] = scale },
            };
            string shot = await web.CoreWebView2.CallDevToolsProtocolMethodAsync("Page.captureScreenshot", args.ToJsonString());
            using var s = JsonDocument.Parse(shot);
            return s.RootElement.TryGetProperty("data", out var data) ? data.GetString() : null;
        }
        catch (Exception ex) { App.Log("mídia: captura da imagem falhou: " + ex.Message); return null; }
    }

    /// <summary>
    /// Diagnóstico local quando a resposta fala em imagem/vídeo e nada foi capturado: lista os elementos gráficos
    /// da página (tag, tamanho, início do endereço, ancestrais), sem texto das conversas.
    /// </summary>
    public static async Task DumpAsync(WebView2 web, string service)
    {
        try
        {
            string raw = await web.CoreWebView2.ExecuteScriptAsync("""
                (() => { const path = e => { const a = []; for (let p = e.parentElement, i = 0; p && i < 6; p = p.parentElement, i++) a.push(p.tagName + (p.id ? '#' + p.id : '') + (p.getAttribute('role') ? '[' + p.getAttribute('role') + ']' : '') + (p.getAttribute('aria-labelledby') ? '{' + p.getAttribute('aria-labelledby') + '}' : '')); return a.join(' < '); };
                  const out = [];
                  for (const e of document.querySelectorAll('img,video,canvas,picture,source,[style*="background-image"]')) {
                    const r = e.getBoundingClientRect();
                    out.push({ tag: e.tagName, w: Math.round(r.width), h: Math.round(r.height), nw: e.naturalWidth || e.videoWidth || 0, nh: e.naturalHeight || e.videoHeight || 0,
                      src: String(e.currentSrc || e.src || e.srcset || e.style.backgroundImage || '').slice(0, 90), alt: (e.alt || e.getAttribute('aria-label') || '').slice(0, 40),
                      complete: e.complete, inButton: !!e.closest('button'), path: path(e) });
                  }
                  return JSON.stringify({ url: location.href, items: out.slice(-60) }); })()
                """);
            Directory.CreateDirectory(Folder);
            File.WriteAllText(Path.Combine(Folder, "diagnostico-" + service + ".json"), JsonSerializer.Deserialize<string>(raw) ?? raw);
            App.Log(service + ": diagnóstico de mídia salvo em " + Folder);
        }
        catch (Exception ex) { App.Log(service + ": diagnóstico de mídia falhou: " + ex.Message); }
    }

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(60) };

    private static async Task<(byte[] Data, string Type, int Length)?> DownloadWithCookiesAsync(WebView2 web, string url, CancellationToken ct)
    {
        try
        {
            var cookies = await web.CoreWebView2.CookieManager.GetCookiesAsync(url);
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            if (cookies.Count > 0) request.Headers.TryAddWithoutValidation("Cookie", string.Join("; ", cookies.Select(c => c.Name + "=" + c.Value)));
            request.Headers.TryAddWithoutValidation("User-Agent", web.CoreWebView2.Settings.UserAgent);
            request.Headers.Referrer = new Uri(web.CoreWebView2.Source);
            using var response = await Http.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode) { App.Log("mídia: download direto HTTP " + (int)response.StatusCode); return null; }
            var data = await response.Content.ReadAsByteArrayAsync(ct);
            return (data, response.Content.Headers.ContentType?.MediaType ?? "", data.Length);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { App.Log("mídia: download direto falhou: " + ex.Message); return null; }
    }

    private static string Extension(string mime, string kind) => mime.ToLowerInvariant() switch
    {
        "image/png" => ".png", "image/jpeg" or "image/jpg" => ".jpg", "image/webp" => ".webp", "image/gif" => ".gif",
        "video/mp4" => ".mp4", "video/webm" => ".webm", "video/quicktime" => ".mov",
        _ => kind == "video" ? ".mp4" : ".png",
    };
}
