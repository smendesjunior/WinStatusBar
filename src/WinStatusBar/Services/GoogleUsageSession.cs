using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;

namespace WinStatusBar.Services;

/// <summary>
/// Cotas do plano Gemini (gemini.google.com/usage: "Uso atual" e "Limite semanal"), lidas da página oficial num
/// WebView2 oculto com perfil próprio, como o faturamento da OpenAI. A pessoa entra na conta Google uma vez, na
/// janela exibida; depois a leitura é periódica e invisível. Só números e horários de renovação são guardados.
/// </summary>
internal static class GoogleUsageSession
{
    private const string UsageUrl = "https://gemini.google.com/usage?hl=pt-BR";
    private static readonly string Folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WinStatusBar", "GoogleWeb");
    private static readonly string CacheFile = Path.Combine(Folder, "usage.json");

    public sealed record Snapshot(string? Plan, double? CurrentPercent, string? CurrentReset, double? WeeklyPercent, string? WeeklyReset, DateTimeOffset Updated, bool SignedIn, string? Name = null, string? Email = null);

    public static Snapshot? Current { get; private set; } = Read();

    // ---- Chave de API (Google AI Studio): limites por modelo (pedidos por minuto/dia, tokens por minuto), mesma conta Google.
    // A página mostra o MÁXIMO de uso por modelo no intervalo escolhido (padrão 28 dias): pede o menor intervalo pela
    // URL e confere qual a página aplicou; se não for de um dia, tenta o próximo endereço na leitura seguinte.
    private static readonly string[] ApiLimitUrls =
    [
        "https://aistudio.google.com/rate-limit?timeRange=last-1-day",
        "https://aistudio.google.com/rate-limit?timeRange=last-24-hours",
        "https://aistudio.google.com/rate-limit?timeRange=last-1-hour",
        "https://aistudio.google.com/rate-limit",
    ];
    private static readonly string ApiCacheFile = Path.Combine(Folder, "api-usage.json");
    public sealed record ApiSnapshot(string? Tier, List<WinStatusBar.Core.Ai.AiStudioModelLimit> Models, DateTimeOffset Updated, string? Period = null)
    {
        /// <summary>Os números "usados" valem para hoje (intervalo de um dia) ou são o pico do intervalo maior.</summary>
        public bool Daily => WinStatusBar.Core.Ai.AiStudioUsage.IsDaily(Period);
    }
    /// <summary>Último uso lido da chave de API do Gemini (null enquanto não houver leitura).</summary>
    public static ApiSnapshot? Api { get; private set; } = ReadApi();
    /// <summary>Última tentativa (com ou sem sucesso) e qual endereço da página de limites funcionou.</summary>
    public static DateTimeOffset ApiTried { get; private set; }
    public static string? ApiError { get; private set; }
    private static int _apiUrl;
    /// <summary>Página oficial de limites da chave (a que respondeu por último).</summary>
    public static string ApiLimitsPage => ApiLimitUrls[_apiUrl % ApiLimitUrls.Length];
    /// <summary>"Atualizar agora": a próxima leitura inclui o AI Studio mesmo dentro do intervalo de 1 minuto.</summary>
    public static void ForceApi() => ApiTried = default;
    /// <summary>Modelos a mostrar: os usados hoje (mais usados primeiro) e o modelo padrão da barra; no máximo 4.</summary>
    public static IReadOnlyList<WinStatusBar.Core.Ai.AiStudioModelLimit> ApiModelsToShow()
    {
        if (Api is null) return [];
        static string Key(string s) => System.Text.RegularExpressions.Regex.Replace(s.ToLowerInvariant(), @"[\s_]+", "-");
        string standard = Key(WinStatusBar.Core.Ai.Gemini.GeminiCli.DefaultModel);
        var used = Api.Models.Where(m => m.RpdUsed > 0 || m.RpmUsed > 0).OrderByDescending(m => m.DailyPercent ?? 0);
        var main = Api.Models.Where(m => Key(m.Model) == standard || Key(m.Model).EndsWith(standard, StringComparison.Ordinal));
        return used.Concat(main).Distinct().Take(4).ToList() is { Count: > 0 } list ? list : Api.Models.Where(m => m.RpdLimit is not null).Take(1).ToList();
    }
    /// <summary>A cota diária da API gratuita zera à meia-noite do horário do Pacífico: horário local disso.</summary>
    public static string ApiDailyReset()
    {
        try
        {
            var pacific = TimeZoneInfo.FindSystemTimeZoneById("Pacific Standard Time");
            var now = TimeZoneInfo.ConvertTime(DateTimeOffset.Now, pacific);
            var midnight = new DateTimeOffset(now.Date.AddDays(1), pacific.GetUtcOffset(now.Date.AddDays(1)));
            return "zera às " + midnight.ToLocalTime().ToString("HH:mm");
        }
        catch { return "zera à meia-noite (Pacífico)"; }
    }
    private static ApiSnapshot? ReadApi() { try { return JsonSerializer.Deserialize<ApiSnapshot>(File.ReadAllText(ApiCacheFile)); } catch { return null; } }
    public static event Action? Changed;
    private static Snapshot? Read() { try { return JsonSerializer.Deserialize<Snapshot>(File.ReadAllText(CacheFile)); } catch { return null; } }
    private static void Save(Snapshot s)
    {
        Current = s;
        try { Directory.CreateDirectory(Folder); File.WriteAllText(CacheFile, JsonSerializer.Serialize(s)); } catch { }
        Changed?.Invoke();
    }

    private static DispatcherQueue? _ui;
    private static Window? _window;
    private static int _live;
    /// <summary>Enquanto o menu de agentes ou os detalhes do Gemini estão abertos, relê a cada 2 s (pedido de 07/11/2026).</summary>
    public static void BeginLive() { if (++_live == 1 && _timer is not null) { _timer.Interval = TimeSpan.FromSeconds(2); Refresh(); } }
    public static void EndLive() { if (_live > 0 && --_live == 0 && _timer is not null) _timer.Interval = _interval; }
    private static TimeSpan _interval = TimeSpan.FromMinutes(5);
    /// <summary>Intervalo da atualização das cotas (Configurações › IA › Cotas das IAs), no mínimo 5 min.</summary>
    public static void SetInterval(TimeSpan interval) { _interval = interval; if (_live == 0 && _timer is not null) _timer.Interval = interval; }
    private static WebView2? _web;
    private static bool _interactive, _busy;
    private static DispatcherQueueTimer? _timer;

    /// <summary>Atualiza a cada 5 min enquanto houver sessão (chamado uma vez pela aplicação).</summary>
    public static void Install(DispatcherQueue ui)
    {
        _ui = ui;
        _timer = ui.CreateTimer();
        _timer.Interval = TimeSpan.FromMinutes(5);
        _timer.Tick += (_, _) => { if (Current?.SignedIn == true) Refresh(); };
        _timer.Start();
        // Primeira leitura ao iniciar: com sessão gravada (cookies no perfil) mesmo sem leitura anterior.
        bool profile = Directory.Exists(Path.Combine(Folder, "Profile"));
        if (Current?.SignedIn == true ? DateTimeOffset.Now - Current.Updated > TimeSpan.FromMinutes(5) : profile) Refresh();
    }

    /// <summary>Saída da barra: para o timer e não abre mais a janela oculta (recriada depois do Exit, ela mantinha o processo vivo).</summary>
    public static void Stop() { _timer?.Stop(); _ui = null; }

    private static async Task<WebView2> EnsureAsync(bool visible)
    {
        if (_window is null || _web is null)
        {
            var web = new WebView2();
            var window = new Window { Title = "Google · entrar para ler as cotas do Gemini", Content = web };
            var monitor = WinStatusBar.Interop.Win32.GetMonitors().FirstOrDefault(m => m.IsPrimary);
            var bounds = new Windows.Graphics.RectInt32(200, 120, 1000, 760);
            if (monitor is not null)
            {
                int w = Math.Min((int)(1000 * monitor.Dpi / 96.0), monitor.Work.Width - 32), h = Math.Min((int)(760 * monitor.Dpi / 96.0), monitor.Work.Height - 32);
                bounds = new(monitor.Work.Left + (monitor.Work.Width - w) / 2, monitor.Work.Top + (monitor.Work.Height - h) / 2, w, h);
            }
            if (File.Exists(App.IconPath)) window.AppWindow.SetIcon(App.IconPath);
            window.AppWindow.Closing += (sender, e) => { e.Cancel = true; _interactive = false; NoActivate(window, true); sender.Hide(); sender.IsShownInSwitchers = false; Refresh(); };
            window.Closed += (_, _) => { _window = null; _web = null; };
            _window = window; _web = web;
            window.AppWindow.IsShownInSwitchers = false;
            NoActivate(window, true);
            var monitors = WinStatusBar.Interop.Win32.GetMonitors();
            window.AppWindow.MoveAndResize(new(monitors.Select(m => m.Work.Left).DefaultIfEmpty(0).Min() - bounds.Width - 1024,
                monitors.Select(m => m.Work.Top).DefaultIfEmpty(0).Min() - bounds.Height - 1024, bounds.Width, bounds.Height));
            window.AppWindow.Show(false);
            Directory.CreateDirectory(Folder);
            var env = await CoreWebView2Environment.CreateWithOptionsAsync(null, Path.Combine(Folder, "Profile"), new CoreWebView2EnvironmentOptions { AdditionalBrowserArguments = WinStatusBar.Core.Ai.HiddenWebPage.BrowserArguments });
            await web.EnsureCoreWebView2Async(env);
            web.CoreWebView2.Settings.IsPasswordAutosaveEnabled = false;
            web.CoreWebView2.Settings.IsGeneralAutofillEnabled = false;
            web.CoreWebView2.NewWindowRequested += (_, e) => { e.Handled = true; if (Uri.TryCreate(e.Uri, UriKind.Absolute, out var u) && u.Scheme == "https") web.CoreWebView2.Navigate(u.AbsoluteUri); };
            web.Tag = bounds;
        }
        if (visible)
        {
            _interactive = true;
            NoActivate(_window, false);
            _window.AppWindow.IsShownInSwitchers = true;
            if (_web!.Tag is Windows.Graphics.RectInt32 b) _window.AppWindow.MoveAndResize(b);
            _window.Activate();
        }
        return _web!;
    }

    private static void NoActivate(Window window, bool on)
    {
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
        long ex = WinStatusBar.Interop.Win32.GetWindowLongPtr(hwnd, WinStatusBar.Interop.Win32.GWL_EXSTYLE);
        long flags = WinStatusBar.Interop.Win32.WS_EX_NOACTIVATE | WinStatusBar.Interop.Win32.WS_EX_TOOLWINDOW;
        long next = on ? ex | flags : ex & ~flags;
        if (next != ex) WinStatusBar.Interop.Win32.SetWindowLongPtr(hwnd, WinStatusBar.Interop.Win32.GWL_EXSTYLE, next);
    }

    /// <summary>Abre a página de limites numa janela visível para a pessoa entrar na conta Google.</summary>
    public static void ShowSignIn() => _ui?.TryEnqueue(async () =>
    {
        try { var web = await EnsureAsync(true); web.CoreWebView2.Navigate(UsageUrl); }
        catch (Exception ex) { App.Log("Cotas do Gemini: " + ex.Message); }
    });

    public static void SignOut() => _ui?.TryEnqueue(async () =>
    {
        try
        {
            var web = await EnsureAsync(false);
            await web.CoreWebView2.Profile.ClearBrowsingDataAsync(CoreWebView2BrowsingDataKinds.AllProfile);
            web.CoreWebView2.Navigate("about:blank");
        }
        catch { }
        try { File.Delete(CacheFile); } catch { }
        Current = null; Changed?.Invoke();
    });

    /// <summary>Lê a página em segundo plano (sem ativar janelas); não faz nada com a janela de login aberta.</summary>
    public static void Refresh() => _ui?.TryEnqueue(async () =>
    {
        if (_busy || _interactive) return;
        _busy = true;
        try
        {
            var web = await EnsureAsync(false);
            web.CoreWebView2.Navigate(UsageUrl);
            string text = "";
            for (int i = 0; i < 60; i++)
            {
                await Task.Delay(500);
                string raw = await web.CoreWebView2.ExecuteScriptAsync("document.body ? document.body.innerText : ''");
                text = JsonSerializer.Deserialize<string>(raw) ?? "";
                if (Regex.IsMatch(text, @"(Quantidade usada|Amount used)\s*:?\s*\d", RegexOptions.IgnoreCase)) break;
                if (web.CoreWebView2.Source.Contains("accounts.google.com", StringComparison.OrdinalIgnoreCase) && i > 10) break;
            }
            var parsed = Parse(text);
            if (parsed is not null)
            {
                // Conta: o botão de perfil do Google traz "Conta do Google: Nome (email)" no rótulo acessível.
                string label = JsonSerializer.Deserialize<string>(await web.CoreWebView2.ExecuteScriptAsync(
                    "(() => { const e = [...document.querySelectorAll('a[aria-label],button[aria-label]')].find(x => /@/.test(x.getAttribute('aria-label'))); return e ? e.getAttribute('aria-label') : ''; })()")) ?? "";
                var email = Regex.Match(label, @"[\w.+-]+@[\w-]+\.[\w.-]+");
                var name = Regex.Match(label, @":\s*([^(\r\n]+?)\s*\(");
                parsed = parsed with { Email = email.Success ? email.Value : Current?.Email, Name = name.Success ? name.Groups[1].Value.Trim() : Current?.Name };
            }
            if (parsed is null)
            {
                // Diagnóstico local: endereço e início do texto da página, para ajustar a leitura.
                try { File.WriteAllText(Path.Combine(Folder, "diagnostico-cotas.txt"), web.CoreWebView2.Source + "\n\n" + (text.Length > 3000 ? text[..3000] : text)); } catch { }
                App.Log("Cotas do Gemini: página sem os números esperados (" + new Uri(web.CoreWebView2.Source).Host + ").");
            }
            if (parsed is not null) Save(parsed);
            else if (web.CoreWebView2.Source.Contains("accounts.google.com", StringComparison.OrdinalIgnoreCase))
                Save(new(null, null, null, null, null, DateTimeOffset.Now, false));
            // Limites da chave de API: no máximo 1 leitura por minuto (mesmo com o menu aberto, que relê o plano a cada 2 s).
            if (parsed is not null && ApiKeyStore.Get(AdditionalAiAccounts.Gemini.Resource) is not null && DateTimeOffset.Now - ApiTried > TimeSpan.FromSeconds(60))
                await ReadApiAsync(web);
        }
        catch (Exception ex) { App.Log("Cotas do Gemini: " + ex.Message); }
        finally { _busy = false; }
    });

    /// <summary>Lê a página oficial de limites do AI Studio (tabela por modelo) no mesmo WebView oculto.</summary>
    private static async Task ReadApiAsync(WebView2 web)
    {
        ApiTried = DateTimeOffset.Now;
        string url = ApiLimitUrls[_apiUrl % ApiLimitUrls.Length];
        web.CoreWebView2.Navigate(url);
        string text = "";
        for (int i = 0; i < 50; i++)
        {
            await Task.Delay(500);
            text = JsonSerializer.Deserialize<string>(await web.CoreWebView2.ExecuteScriptAsync("document.body ? document.body.innerText : ''")) ?? "";
            if (i > 6 && WinStatusBar.Core.Ai.AiStudioUsageText.Parse(text) is { Models.Count: > 0 }) { await Task.Delay(800); text = JsonSerializer.Deserialize<string>(await web.CoreWebView2.ExecuteScriptAsync("document.body.innerText")) ?? text; break; }
            if (web.CoreWebView2.Source.Contains("accounts.google.com", StringComparison.OrdinalIgnoreCase) && i > 10) break;
        }
        // Diagnóstico local (endereço e início do texto), para ajustar a leitura se o Google mudar a página.
        try { File.WriteAllText(Path.Combine(Folder, "diagnostico-aistudio.txt"), web.CoreWebView2.Source + "\n\n" + (text.Length > 6000 ? text[..6000] : text)); } catch { }
        var parsed = WinStatusBar.Core.Ai.AiStudioUsageText.Parse(text);
        if (parsed is null)
        {
            _apiUrl++; // tenta o outro endereço da página de limites na próxima leitura
            ApiError = web.CoreWebView2.Source.Contains("accounts.google.com", StringComparison.OrdinalIgnoreCase)
                ? "O AI Studio pediu login (entre na conta Google em Configurações › IA › Gemini)." : "Página de limites do AI Studio sem a tabela esperada.";
            App.Log("Cotas da API do Gemini: " + ApiError);
            Changed?.Invoke();
            return;
        }
        ApiError = null;
        Api = new(parsed.Tier, parsed.Models.ToList(), DateTimeOffset.Now, parsed.Period);
        // Intervalo não diário: na próxima leitura tenta outro endereço (até achar um que a página aceite como 1 dia).
        if (!Api.Daily && _apiUrl % ApiLimitUrls.Length < ApiLimitUrls.Length - 1) { _apiUrl++; ApiTried = default; }
        App.Log($"Cotas da API do Gemini: {parsed.Models.Count} modelo(s), intervalo {parsed.Period ?? "?"} ({new Uri(web.CoreWebView2.Source).PathAndQuery}).");
        try { File.WriteAllText(ApiCacheFile, JsonSerializer.Serialize(Api)); } catch { }
        Changed?.Invoke();
    }

    internal static Snapshot? Parse(string text)
        => WinStatusBar.Core.Ai.GoogleUsageText.Parse(text) is { } u
            ? new(u.Plan, u.CurrentPercent, u.CurrentReset, u.WeeklyPercent, u.WeeklyReset, DateTimeOffset.Now, true) : null;
}
