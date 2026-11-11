using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using WinStatusBar.Core.Ai;
using Mode = WinStatusBar.Core.Ai.WebModePicker.Mode;

namespace CodexIAs.Providers;

/// <summary>
/// Copilot ou Gemini pela conta, numa página oficial escondida (WebView2) que usa a MESMA pasta de perfil da
/// WinStatusBar: os dois apps compartilham a sessão do navegador interno e, com ela, o login. A pergunta é escrita no
/// campo da página pelo protocolo do WebView2 (nunca por teclado do Windows) e a resposta é lida da página.
/// Os modos de cada site (ex.: "3.1 Pro", "Think Deeper", submenu "GPT") são lidos do próprio seletor da página e
/// viram opções do Codex. Depois de cada resposta, o modo que estava escolhido no site volta, para a escolha feita no
/// Codex não mudar o que a barra encontra no site (o perfil é o mesmo).
/// </summary>
internal sealed class WebChatProvider : IProvider
{
    private readonly WebSite _site;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private WebHostForm? _form;
    private WebView2? _web;
    private string? _activeKey;      // conversa do Codex aberta na página
    private int _activeTurns;        // passos do histórico (falas, ações, resultados) esperados no próximo pedido dessa conversa
    private IReadOnlyList<Mode>? _modes;
    private DateTime _lastUse = DateTime.UtcNow;
    private System.Windows.Forms.Timer? _idle;
    /// <summary>Espera escolhida na barra (Manter a IA aberta depois da resposta); 1 min por padrão.</summary>
    private static TimeSpan IdleClose => WinStatusBar.Core.Settings.AppSettings.AiRelease(BarSettings.Load().AiReleaseSeconds);

    public WebChatProvider(WebSite site) { _site = site; _modes = LoadModes(); }

    public string Name => _site.Name;
    public string Label => _site.Label;

    /// <summary>Pasta de perfil; CODEXIAS_PERFIL_TESTE troca por outra (só diagnóstico).</summary>
    private string ProfileFolder => Environment.GetEnvironmentVariable("CODEXIAS_PERFIL_TESTE") is { Length: > 0 } test ? test : _site.ProfileFolder;

    // ---------------------------------------------------------------- modos (lista do seletor do Codex)
    private IReadOnlyList<Mode>? LoadModes()
    {
        try
        {
            if (!File.Exists(Paths.ModesFile)) return null;
            using var doc = JsonDocument.Parse(File.ReadAllText(Paths.ModesFile));
            if (!doc.RootElement.TryGetProperty(_site.Name, out var arr) || arr.ValueKind != JsonValueKind.Array) return null;
            return arr.EnumerateArray()
                .Select(m => new Mode(m.GetProperty("path").EnumerateArray().Select(p => p.GetString() ?? "").Where(p => p.Length > 0).ToList(),
                    m.TryGetProperty("description", out var d) ? d.GetString() ?? "" : "", false))
                .Where(m => m.Path.Count > 0).ToList();
        }
        catch { return null; }
    }

    private void SaveModes(IReadOnlyList<Mode> modes)
    {
        try
        {
            Directory.CreateDirectory(Paths.DataDir);
            var all = new Dictionary<string, object>();
            if (File.Exists(Paths.ModesFile))
                foreach (var p in JsonDocument.Parse(File.ReadAllText(Paths.ModesFile)).RootElement.EnumerateObject()) all[p.Name] = p.Value.Clone();
            all[_site.Name] = modes.Select(m => new { path = m.Path, description = m.Description }).ToList();
            // Gravação atômica: a barra lê este arquivo e não pode pegá-lo pela metade.
            WinStatusBar.Core.Ai.Codex.CodexIntegration.WriteAtomic(Paths.ModesFile, JsonSerializer.Serialize(all,new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
        }
        catch (Exception ex) { Log.Write($"{_site.Label}: não gravou os modos: {ex.Message}"); }
    }

    /// <summary>
    /// Só com a conta conectada na barra (o perfil é o mesmo): abre a página escondida e lê os modos do seletor do site.
    /// Sem o perfil da barra, nem abre a página. Os modos lidos ficam guardados (a barra mostra os mesmos).
    /// </summary>
    public async Task<Listing> ListAsync(CancellationToken ct)
    {
        if (Environment.GetEnvironmentVariable("CODEXIAS_PERFIL_TESTE") is not { Length: > 0 } && !Directory.Exists(_site.ProfileFolder))
            return Listing.SignIn($"sem login no {_site.Label} (entre na conta pela barra)");
        try
        {
            var (found, problem, signIn) = await Ui.Run(() => DiscoverModesAsync(ct));
            if (signIn) return Listing.SignIn($"sem login no {_site.Label} (entre na conta pela barra)");
            if (problem is not null) return Listing.Fail(problem);
            if (found.Count > 0) { _modes = found; SaveModes(found); }
            return new(ToModels(found));
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Log.Write($"{_site.Label}: leitura dos modos falhou: {ex.Message}");
            return Listing.Fail("a página não abriu: " + ex.Message);
        }
    }

    internal IReadOnlyList<AiModel> ToModels(IReadOnlyList<Mode>? modes)
    {
        string baseDesc = $"{_site.Label} pela conta, pela WinStatusBar (mesmo login da barra). Só texto.";
        if (modes is not { Count: > 0 })
            return [new AiModel(CodexCatalog.Prefix + _site.Name, _site.Name, "", _site.DisplayPrefix, baseDesc + " Usa o modo escolhido no site.", [], null, _site.Priority)];
        return modes.Where(m => !m.Disabled).Select(m => new AiModel(
            CodexCatalog.Prefix + _site.Name + "-" + Slug.From(string.Join(" ", m.Path)), _site.Name, m.Id, $"{_site.DisplayPrefix} — {m.Label}",
            (m.Description.Length > 0 ? m.Description.TrimEnd('.') + ". " : "") + baseDesc, [], null, _site.Priority)).ToList();
    }

    // ---------------------------------------------------------------- página oculta
    private async Task<WebView2> EnsureAsync()
    {
        _lastUse = DateTime.UtcNow;
        if (_web?.CoreWebView2 is not null && _form is { IsDisposed: false }) return _web;
        // Pouco depois do último uso, a página fecha (libera memória); o próximo pedido a abre de novo.
        _idle ??= new System.Windows.Forms.Timer { Interval = 5_000 };
        _idle.Tick -= OnIdleTick;
        _idle.Tick += OnIdleTick;
        _idle.Start();
        var form = new WebHostForm(_site.Label + " · Codex IAs", hidden: true);
        var web = form.Web;
        _form = form; _web = web;
        form.FormClosed += (_, _) => { if (ReferenceEquals(_form, form)) { _form = null; _web = null; } };
        form.Show();
        try
        {
            var env = await CoreWebView2Environment.CreateAsync(null, ProfileFolder, new CoreWebView2EnvironmentOptions(HiddenWebPage.BrowserArguments));
            await web.EnsureCoreWebView2Async(env);
        }
        catch (Exception ex)
        {
            // Sem a página, a janela escondida não serve: fecha para não acumular janelas a cada tentativa.
            _form = null; _web = null;
            try { form.Close(); form.Dispose(); } catch { }
            Log.Write($"{_site.Label}: página oculta não abriu: {ex.Message}");
            throw new InvalidOperationException(ex.HResult == unchecked((int)0x8007139F)
                ? "o WebView2 recusou a pasta de perfil da barra (o navegador dela está aberto com outra configuração). Feche e abra a WinStatusBar e tente de novo."
                : "a página não abriu: " + ex.Message, ex);
        }
        web.CoreWebView2.Settings.IsPasswordAutosaveEnabled = false;
        web.CoreWebView2.Settings.IsGeneralAutofillEnabled = false;
        web.CoreWebView2.NewWindowRequested += (_, e) => { e.Handled = true; if (Uri.TryCreate(e.Uri, UriKind.Absolute, out var u) && u.Scheme == "https") web.CoreWebView2.Navigate(u.AbsoluteUri); };
        Log.Write($"{_site.Label}: página oculta aberta com o perfil da barra");
        return web;
    }

    private void OnIdleTick(object? sender, EventArgs e)
    {
        if (_gate.CurrentCount == 0 || DateTime.UtcNow - _lastUse < IdleClose || _form is null) return;
        Log.Write($"{_site.Label}: página oculta fechada (sem uso há {IdleClose.TotalSeconds:0} s)");
        _idle?.Stop();
        var form = _form;
        _form = null; _web = null; _activeKey = null;
        try { form.Close(); form.Dispose(); } catch { }
    }

    private static async Task<JsonElement> EvalAsync(WebView2 web, string script)
    {
        string raw = await web.CoreWebView2.ExecuteScriptAsync(script);
        using var outer = JsonDocument.Parse(raw);
        string inner = outer.RootElement.ValueKind == JsonValueKind.String ? outer.RootElement.GetString() ?? "null" : raw;
        using var doc = JsonDocument.Parse(inner);
        return doc.RootElement.Clone();
    }

    private static readonly JsonSerializerOptions SkipNulls = new() { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull };
    private static async Task CdpAsync(WebView2 web, string method, object parameters)
    {
        string json = JsonSerializer.Serialize(parameters, SkipNulls);
        try { await web.CoreWebView2.CallDevToolsProtocolMethodAsync(method, json); }
        catch (Exception ex) { throw new InvalidOperationException(method + ": " + ex.Message.Trim(), ex); }
    }

    /// <summary>Tecla entregue só à página oculta (protocolo do WebView2), nunca ao Windows.</summary>
    private static async Task PageKeyAsync(WebView2 web, string key, string code, int vk, int modifiers)
    {
        foreach (var type in new[] { "rawKeyDown", "keyUp" })
            await CdpAsync(web, "Input.dispatchKeyEvent", new { type, key, code, windowsVirtualKeyCode = vk, modifiers });
    }

    private static string Js(string s) => JsonSerializer.Serialize(s);

    private static async Task NavigateAndWaitAsync(WebView2 web, string url, CancellationToken ct)
    {
        var done = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        void Handler(object? s, CoreWebView2NavigationCompletedEventArgs e) => done.TrySetResult(e.IsSuccess);
        web.CoreWebView2.NavigationCompleted += Handler;
        try
        {
            web.CoreWebView2.Navigate(url);
            await Task.WhenAny(done.Task, Task.Delay(30000, ct));
        }
        finally { web.CoreWebView2.NavigationCompleted -= Handler; }
    }

    private string ProbeScript => "(() => { const i = " + _site.FindInput.Trim() + "; const signIn = [...document.querySelectorAll('button,a')].some(b => new RegExp("
        + Js(_site.SignInPattern) + ", 'i').test((b.innerText||b.getAttribute('aria-label')||'').trim())); return JSON.stringify({ input: !!i, signIn }); })()";

    /// <summary>Espera o campo de conversa (até 25 s). Devolve null quando pronto; senão, o motivo.</summary>
    private async Task<string?> WaitReadyAsync(WebView2 web, CancellationToken ct) => (await WaitReadyCoreAsync(web, ct)).Problem;

    private async Task<(string? Problem, bool SignIn)> WaitReadyCoreAsync(WebView2 web, CancellationToken ct)
    {
        bool ready = false, signInSeen = false, retriedLogin = false;
        for (int i = 0; i < 100 && !ready; i++)
        {
            await Task.Delay(250, ct);
            var p = await EvalAsync(web, ProbeScript);
            ready = p.GetProperty("input").GetBoolean();
            signInSeen = !ready && p.GetProperty("signIn").GetBoolean();
            // Página de apresentação após 6 s: tenta uma vez o login silencioso, que reaproveita a sessão gravada no perfil.
            if (!ready && signInSeen && !retriedLogin && i >= 24 && _site.Login != _site.Home) { retriedLogin = true; await NavigateAndWaitAsync(web, _site.Login, ct); i = 0; }
        }
        if (ready) return (null, false);
        return signInSeen ? ($"{_site.Label} pede login. {_site.LoginHint}", true)
            : ($"A página do {_site.Label} não carregou o campo de conversa. Tente de novo em instantes.", false);
    }

    // ---------------------------------------------------------------- seletor de modo da página (o mesmo da barra)
    private WebModePicker Picker(WebView2 web) => new(_site.Label, script => web.CoreWebView2.ExecuteScriptAsync(script),
        (method, json) => web.CoreWebView2.CallDevToolsProtocolMethodAsync(method, json), () => _modes ?? [], Log.Write, _site.Picker, _site.ModeItems);

    /// <summary>Modos do seletor do site. Problem: a página não ficou pronta; SignIn: o site pediu login.</summary>
    private async Task<(IReadOnlyList<Mode> Modes, string? Problem, bool SignIn)> DiscoverModesAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var web = await EnsureAsync();
            if (!(web.CoreWebView2.Source ?? "").StartsWith(_site.HomePrefix, StringComparison.OrdinalIgnoreCase)) await NavigateAndWaitAsync(web, _site.Home, ct);
            var (problem, signIn) = await WaitReadyCoreAsync(web, ct);
            if (problem is not null) { Log.Write($"{_site.Label}: modos não lidos: {problem}"); return ([], problem, signIn); }
            var (modes, _) = await Picker(web).ReadMenuAsync(ct);
            if (modes.Count == 0) Log.Write($"{_site.Label}: seletor de modo não encontrado na página");
            else Log.Write($"{_site.Label}: modos do site: {string.Join(", ", modes.Select(m => m.Id + (m.Disabled ? " (indisponível)" : "")))}");
            return (modes, null, false);
        }
        finally { _gate.Release(); }
    }

    // ---------------------------------------------------------------- conversa
    public Task<Answer> RunAsync(AiModel model, string? effort, Conversation conversation, Action<string> onText, CancellationToken ct)
        => Ui.Run(() => RunOnUiAsync(model, conversation, onText, ct));

    private async Task<Answer> RunOnUiAsync(AiModel model, Conversation conversation, Action<string> onText, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        string text = "";
        IReadOnlyList<string>? restore = null;
        WebView2? page = null;
        try
        {
            var web = page = await EnsureAsync();
            // Passos da conversa: falas, ações e resultados (cada ação pedida pela IA volta com o resultado na mesma página).
            int userTurns = conversation.History.Count;
            // Continua no mesmo chat só quando é a sequência da conversa aberta nesta página; senão, chat novo com o
            // histórico do Codex como contexto (reabrir o endereço de um chat começa outro).
            bool samePage = conversation.Key is { Length: > 0 } key && key == _activeKey && userTurns == _activeTurns
                && (web.CoreWebView2.Source ?? "").StartsWith(_site.HomePrefix, StringComparison.OrdinalIgnoreCase);
            Log.Write($"{_site.Label}: {(samePage ? "continua no mesmo chat" : "chat novo")}");
            if (!samePage) { await NavigateAndWaitAsync(web, _site.Home, ct); _activeKey = null; }
            // Chat novo: pacote de passagem (regras do projeto, conversa, ações e resultados) e, quando o Codex pode executar
            // ações pela IA, as instruções de como pedir; na mesma página, só o pedido novo (ou o resultado da ação).
            string request = samePage ? conversation.Prompt
                : ConversationReader.WithTranscript(conversation, _site == WebSite.Copilot ? 8000 : 11000);
            if (request.Length > 16000) return new("", $"Texto longo demais para o {_site.Label} pela conta. Comece uma conversa nova no Codex ou escolha outra IA.");

            if (await WaitReadyAsync(web, ct) is { } problem) return new("", problem);
            var (modeError, previous) = await Picker(web).SelectAsync(Mode.ParseId(model.Id), ct);
            restore = previous;
            if (modeError is not null) return new("", modeError);

            int before = (await EvalAsync(web, _site.Messages)).GetProperty("count").GetInt32();
            // Texto: foco no editor só dentro da página e Input.insertText pelo protocolo do WebView2.
            var focus = await EvalAsync(web, "(() => { const i = " + _site.FindInput.Trim() + """
                ; if (!i) return JSON.stringify({ ok: false, empty: false });
                  i.focus();
                  const v = i.tagName === 'TEXTAREA' || i.tagName === 'INPUT' ? i.value : i.innerText;
                  return JSON.stringify({ ok: true, empty: v.replace(/[​-‍﻿\s]/g, '') === '' }); })()
                """);
            if (!focus.GetProperty("ok").GetBoolean()) return new("", $"Campo de conversa do {_site.Label} não encontrado. Nada foi enviado.");
            if (!focus.GetProperty("empty").GetBoolean())
            {
                await PageKeyAsync(web, "a", "KeyA", 65, 2);
                await PageKeyAsync(web, "Backspace", "Backspace", 8, 0);
                await Task.Delay(100, ct);
            }
            // Linha a linha: cada quebra é um Shift+Enter entregue só à página (Enter envia).
            var lines = request.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
            for (int n = 0; n < lines.Length; n++)
            {
                if (n > 0)
                {
                    await PageKeyAsync(web, "Enter", "Enter", 13, 8);
                    if (n == 1 && lines[0].Trim().Length > 0)
                    {
                        var still = await EvalAsync(web, "(() => { const i = " + _site.FindInput.Trim() + """
                            ; const v = i ? (i.tagName === 'TEXTAREA' || i.tagName === 'INPUT' ? i.value : i.innerText) : '';
                              return JSON.stringify({ ok: v.replace(/[​-‍﻿\s]/g, '') !== '' }); })()
                            """);
                        if (!still.GetProperty("ok").GetBoolean())
                            return new("", $"O {_site.Label} enviou só a primeira linha da mensagem. Confira e tente de novo.");
                    }
                }
                if (lines[n].Length > 0) await CdpAsync(web, "Input.insertText", new { text = lines[n] });
            }
            bool same = false;
            for (int i = 0; i < 15 && !same; i++)
            {
                await Task.Delay(150, ct);
                var check = await EvalAsync(web, "(() => { const i = " + _site.FindInput.Trim() + "; const t = " + Js(request) + """
                    ; const norm = x => (x || '').replace(/[​-‍﻿\s]/g, '');
                      const v = i ? (i.tagName === 'TEXTAREA' || i.tagName === 'INPUT' ? i.value : i.innerText) : '';
                      return JSON.stringify({ same: norm(v) === norm(t) }); })()
                    """);
                same = check.GetProperty("same").GetBoolean();
            }
            if (!same) return new("", $"Não foi possível preencher o campo do {_site.Label}. Nada foi enviado.");
            bool clicked = false;
            for (int i = 0; i < 20 && !clicked; i++)
            {
                await Task.Delay(i == 0 ? 150 : 400, ct);
                var sent = await EvalAsync(web, "(() => { const btn = " + _site.SendButton + "; if (btn) btn.click(); return JSON.stringify({ ok: !!btn }); })()");
                clicked = sent.GetProperty("ok").GetBoolean();
            }
            if (!clicked) await PageKeyAsync(web, "Enter", "Enter", 13, 0);

            var state = new ResponseState();
            var clock = System.Diagnostics.Stopwatch.StartNew();
            bool slowSaved = false;
            while (clock.Elapsed < TimeSpan.FromSeconds(420))
            {
                await Task.Delay(400, ct);
                var m = await EvalAsync(web, _site.Messages);
                // Resposta demorando: guarda uma foto e o estado da página (diagnóstico local, sobrescrito a cada vez).
                if (!slowSaved && clock.Elapsed > TimeSpan.FromSeconds(40)) { slowSaved = true; await SaveSlowDiagnosticAsync(web, m, before); }
                bool generating = m.GetProperty("generating").GetBoolean();
                bool fresh = m.GetProperty("count").GetInt32() > before;
                string latest = fresh ? NormalizeReply(m.GetProperty("last").GetString() ?? "") : "";
                // Aviso provisório da página enquanto gera (ex.: Copilot "Alinhando tudo..."): vai como linha de estado,
                // que o servidor não transmite como texto da resposta.
                if (latest != text) { text = latest; if (text.Length > 0) onText(generating && LooksLikeStatus(text) ? "*" + text.Trim() + "*" : text); }
                if (state.Observe(text, generating, clock.Elapsed))
                {
                    _activeKey = conversation.Key;
                    _activeTurns = userTurns + 2; // o pedido de agora e a resposta (ou a ação) da IA
                    return new(text, null);
                }
                if (clock.Elapsed > TimeSpan.FromSeconds(150) && !generating && text.Length == 0) break;
            }
            _activeKey = null;
            return new(text, $"Tempo limite do {_site.Label}. A resposta não foi confirmada como concluída.");
        }
        catch (OperationCanceledException)
        {
            try { if (_web?.CoreWebView2 is { } core) await core.ExecuteScriptAsync(_site.Stop); } catch { }
            _activeKey = null;
            return new(text, "Interrompido");
        }
        catch (Exception ex) { Log.Write($"{_site.Label}: {ex.Message}"); _activeKey = null; return new(text, $"Falha no {_site.Label}: {ex.Message}"); }
        finally
        {
            // Devolve o modo que estava no site (o perfil é o mesmo da barra).
            if (restore is not null && page?.CoreWebView2 is not null)
            {
                try
                {
                    var (err, _) = await Picker(page).SelectAsync(restore, CancellationToken.None);
                    if (err is not null) Log.Write($"{_site.Label}: não devolveu o modo anterior ({string.Join(" › ", restore)}): {err}");
                }
                catch (Exception ex) { Log.Write($"{_site.Label}: não devolveu o modo anterior: {ex.Message}"); }
            }
            _lastUse = DateTime.UtcNow;
            _gate.Release();
        }
    }

    /// <summary>
    /// Diagnóstico de resposta lenta: foto da página escondida e o estado do que a conclusão olha (contagem, botões de
    /// parar e de ações, diálogos), em %LOCALAPPDATA%\WinStatusBar\codex-ias\lenta-&lt;site&gt;.png/.json. Fica só neste PC.
    /// </summary>
    private async Task SaveSlowDiagnosticAsync(WebView2 web, JsonElement messages, int before)
    {
        try
        {
            Directory.CreateDirectory(Paths.DataDir);
            string shot = await web.CoreWebView2.CallDevToolsProtocolMethodAsync("Page.captureScreenshot", "{\"format\":\"png\"}");
            using (var doc = JsonDocument.Parse(shot))
                await File.WriteAllBytesAsync(Path.Combine(Paths.DataDir, $"lenta-{_site.Name}.png"), Convert.FromBase64String(doc.RootElement.GetProperty("data").GetString() ?? ""));
            string page = await web.CoreWebView2.ExecuteScriptAsync("""
                (() => { const vis = e => e && e.offsetParent !== null;
                  const lab = b => (b.getAttribute('aria-label') || b.title || b.innerText || '').trim().slice(0, 60);
                  return { visibility: document.visibilityState, hasFocus: document.hasFocus(),
                    buttons: [...document.querySelectorAll('button')].filter(vis).map(lab).filter(x => x).slice(0, 60),
                    dialogs: [...document.querySelectorAll('[role="dialog"],[role="alertdialog"],mat-dialog-container,.cdk-overlay-pane')].filter(vis).map(d => (d.innerText || '').trim().slice(0, 200)),
                    responses: document.querySelectorAll('model-response').length,
                    lastResponse: (() => { const r = [...document.querySelectorAll('model-response')].pop(); return r ? { text: (r.innerText || '').trim().slice(0, 300), buttons: [...r.querySelectorAll('button')].map(lab).filter(x => x) } : null; })() }; })()
                """);
            File.WriteAllText(Path.Combine(Paths.DataDir, $"lenta-{_site.Name}.json"),
                $"{{\"antes\": {before}, \"mensagens\": {messages.GetRawText()}, \"pagina\": {page}}}");
            Log.Write($"{_site.Label}: resposta demorando (40 s); foto e estado da página guardados para diagnóstico");
        }
        catch (Exception ex) { Log.Write($"{_site.Label}: diagnóstico da resposta lenta falhou: {ex.Message}"); }
    }

    /// <summary>Texto curto de uma linha terminado em reticências: aviso de andamento da página, não resposta.</summary>
    internal static bool LooksLikeStatus(string text)
    {
        string t = text.Trim();
        return t.Length is > 0 and <= 60 && !t.Contains('\n') && (t.EndsWith("...", StringComparison.Ordinal) || t.EndsWith('…'));
    }

    /// <summary>Remove o cabeçalho que as páginas põem nos blocos de código ("Plain Text", "Copiar código").</summary>
    internal static string NormalizeReply(string text)
    {
        text = Regex.Replace(text, @"(?im)^[ \t]*(Plain Text|content_copy|Copiar código|Copy code)[ \t]*\r?\n?", "");
        text = Regex.Replace(text, @"(?m)^[ \t]*[A-Za-z+#.-]{1,20}[ \t]*\r?\n(?=\s*```)", "");
        text = Regex.Replace(text, @"(?i)[\w-]+ (não tem suporte total|does not have full support)[^\n]*?(Plain Text|texto sem formatação)\.?[ \t]*", "");
        return text.Trim();
    }

    // ---------------------------------------------------------------- login visível e sondagem (diagnóstico)
    /// <summary>Janela visível com o mesmo perfil, para a pessoa entrar na conta. Fecha quando a janela é fechada.</summary>
    public static async Task ShowLoginAsync(WebSite site)
    {
        var form = new WebHostForm($"{site.Label} · entrar na conta (Codex IAs)", hidden: false);
        form.FormClosed += (_, _) => Application.ExitThread();
        form.Show();
        var env = await CoreWebView2Environment.CreateAsync(null, site.ProfileFolder, new CoreWebView2EnvironmentOptions(HiddenWebPage.BrowserArguments));
        await form.Web.EnsureCoreWebView2Async(env);
        form.Web.CoreWebView2.Settings.IsPasswordAutosaveEnabled = false;
        form.Web.CoreWebView2.Navigate(site.Login);
    }

    /// <summary>
    /// Sondagem (diagnóstico dos seletores): página, botões visíveis, o seletor de modo e os modos lidos do menu, com o
    /// que está marcado. Grava em %LOCALAPPDATA%\Codex IAs\sonda-&lt;site&gt;.json. Não envia mensagens nem escolhe modos.
    /// </summary>
    public async Task ProbeAsync(CancellationToken ct)
    {
        var web = await EnsureAsync();
        await NavigateAndWaitAsync(web, _site.Home, ct);
        string? problem = await WaitReadyAsync(web, ct);
        string dump = await web.CoreWebView2.ExecuteScriptAsync("""
            (() => { const vis = e => e && e.offsetParent !== null;
              const d = e => ({ tag: e.tagName, aria: e.getAttribute('aria-label'), popup: e.getAttribute('aria-haspopup'),
                testid: e.getAttribute('data-test-id') || e.getAttribute('data-testid'), text: (e.innerText || '').trim().slice(0, 80) });
              return { url: location.href, title: document.title,
                buttons: [...document.querySelectorAll('button,[role="button"],[aria-haspopup]')].filter(vis).slice(0, 120).map(d) }; })()
            """);
        var picker = await EvalAsync(web, "(() => { const b = " + _site.Picker.Trim() + "; return JSON.stringify(b ? { text: (b.innerText||'').trim(), aria: b.getAttribute('aria-label') } : null); })()");
        List<Mode> modes = [];
        IReadOnlyList<string>? selected = null;
        if (picker.ValueKind == JsonValueKind.Object) (modes, selected) = await Picker(web).ReadMenuAsync(ct);
        string json = JsonSerializer.Serialize(new
        {
            site = _site.Name, problema = problem ?? "", pagina = JsonDocument.Parse(dump).RootElement, seletor = picker,
            modos = modes.Select(m => new { m.Path, m.Description, m.Disabled }), marcado = selected,
        }, new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
        Directory.CreateDirectory(Paths.DataDir);
        File.WriteAllText(Path.Combine(Paths.DataDir, $"sonda-{_site.Name}.json"), json);
    }
}

/// <summary>A resposta só termina depois de parar de gerar e ficar igual em leituras seguidas (pelo menos 3 s).</summary>
internal sealed class ResponseState
{
    private string _text = "";
    private TimeSpan _stableSince;
    private int _samples;
    private bool _wasGenerating;

    public bool Observe(string text, bool generating, TimeSpan now)
    {
        if (generating || _wasGenerating || string.IsNullOrWhiteSpace(text) || text != _text)
        { _text = text; _stableSince = now; _samples = 0; }
        else _samples++;
        _wasGenerating = generating;
        return !generating && !string.IsNullOrWhiteSpace(text) && _samples >= 2 && now - _stableSince >= TimeSpan.FromSeconds(3);
    }
}
