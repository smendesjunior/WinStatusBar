using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;

namespace WinStatusBar.Services;

/// <summary>
/// Créditos de IA do Copilot ("Saldo de crédito de IA" em account.microsoft.com/services), lidos da página oficial
/// num WebView2 oculto com o mesmo perfil do Copilot da barra (um login Microsoft só). Só os números são guardados.
/// </summary>
internal static class MicrosoftCreditsSession
{
    private const string UsageUrl = "https://account.microsoft.com/services?lang=pt-BR";
    private static readonly string Folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WinStatusBar", "CopilotWeb");
    private static readonly string CacheFile = Path.Combine(Folder, "credits.json");

    public sealed record Snapshot(int Remaining, int Total, int? Used, DateTimeOffset Updated, bool SignedIn, string? Plan = null);

    public static Snapshot? Current { get; private set; } = Read();
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
    private static WebView2? _web;
    private static bool _interactive, _busy;
    private static DispatcherQueueTimer? _timer;

    private static TimeSpan _interval = TimeSpan.FromMinutes(5);
    /// <summary>Intervalo da atualização das cotas (Configurações › IA › Cotas das IAs), no mínimo 5 min.</summary>
    public static void SetInterval(TimeSpan interval) { _interval = interval; if (_timer is not null) _timer.Interval = interval; }

    /// <summary>Atualiza a cada 5 min enquanto houver sessão (chamado uma vez pela aplicação).</summary>
    public static void Install(DispatcherQueue ui)
    {
        _ui = ui;
        _timer = ui.CreateTimer();
        _timer.Interval = TimeSpan.FromMinutes(5);
        _timer.Tick += (_, _) => { if (Current?.SignedIn == true) Refresh(); };
        _timer.Interval = _interval;
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
            var window = new Window { Title = "Conta Microsoft · créditos de IA", Content = web };
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
        catch (Exception ex) { App.Log("Créditos do Copilot: " + ex.Message); }
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
                if (Regex.IsMatch(text, @"\d+\s*/\s*\d+\s*(créditos|credits)|(Gerenciar|Manage)\s*:", RegexOptions.IgnoreCase)) break;
                if (web.CoreWebView2.Source.Contains("login.", StringComparison.OrdinalIgnoreCase) && i > 10) break;
            }
            // Os créditos ficam na página "Gerenciar" da assinatura, num bloco recolhido: abre a página e expande o bloco.
            string? plan = WinStatusBar.Core.Ai.MicrosoftCreditsText.Parse(text)?.Plan;
            if (!Regex.IsMatch(text, @"\d+\s*/\s*\d+\s*(créditos|credits)", RegexOptions.IgnoreCase))
            {
                // "Gerenciar: Microsoft 365 …" é um botão da página (não um link): clica nele; sem ele, abre os detalhes direto.
                string clicked = JsonSerializer.Deserialize<string>(await web.CoreWebView2.ExecuteScriptAsync(
                    "(() => { const e = [...document.querySelectorAll('a,button,[role=link],[role=button]')].find(x => /(gerenciar|manage)\\s*:\\s*(microsoft 365|copilot)/i.test((x.getAttribute('aria-label') || '') + ' ' + (x.innerText || ''))); if (e) { e.click(); return 'ok'; } return ''; })()")) ?? "";
                if (clicked != "ok") web.CoreWebView2.Navigate("https://account.microsoft.com/services/microsoft365/details?lang=pt-BR");
                {
                    for (int i = 0; i < 60; i++)
                    {
                        await Task.Delay(500);
                        await web.CoreWebView2.ExecuteScriptAsync(
                            "(() => { const e = [...document.querySelectorAll('button,[role=button],[aria-expanded]')].find(x => /(saldo de crédito de ia|ai credit)/i.test(x.innerText || x.getAttribute('aria-label') || '') && x.getAttribute('aria-expanded') !== 'true'); if (e) e.click(); return ''; })()");
                        text = JsonSerializer.Deserialize<string>(await web.CoreWebView2.ExecuteScriptAsync("document.body ? document.body.innerText : ''")) ?? "";
                        if (Regex.IsMatch(text, @"\d+\s*/\s*\d+\s*(créditos|credits)", RegexOptions.IgnoreCase)) break;
                    }
                }
            }
            var parsed = Parse(text);
            if (parsed is not null && parsed.Plan is null && plan is not null) parsed = parsed with { Plan = plan };
            if (parsed is null)
            {
                // Diagnóstico local: endereço e início do texto da página, para ajustar a leitura.
                try { File.WriteAllText(Path.Combine(Folder, "diagnostico-creditos.txt"), web.CoreWebView2.Source + "\n\n" + (text.Length > 3000 ? text[..3000] : text)); } catch { }
                App.Log("Créditos do Copilot: página sem os números esperados (" + new Uri(web.CoreWebView2.Source).Host + ").");
            }
            if (parsed is not null) Save(parsed);
            else if (web.CoreWebView2.Source.Contains("login.", StringComparison.OrdinalIgnoreCase))
                Save(new(0, 0, null, DateTimeOffset.Now, false));
        }
        catch (Exception ex) { App.Log("Créditos do Copilot: " + ex.Message); }
        finally { _busy = false; }
    });

    internal static Snapshot? Parse(string text)
        => WinStatusBar.Core.Ai.MicrosoftCreditsText.Parse(text) is { Total: not null } c ? new(c.Remaining ?? 0, c.Total.Value, c.Used, DateTimeOffset.Now, true, c.Plan) : null;
}
