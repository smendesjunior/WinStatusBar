using System.Text.Json;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;
using WinStatusBar.Core.Ai;

namespace WinStatusBar.Services;

/// <summary>
/// Gemini pela conta Google: a página oficial (gemini.google.com) roda num WebView2 oculto com o mesmo perfil da
/// sessão Google da barra (a mesma das cotas do plano) — um login só. Pergunta e resposta ficam no chat da barra.
/// Mesmo mecanismo do Copilot (CopilotWebSession): texto pelo protocolo do WebView2, nunca por teclado do Windows.
/// </summary>
internal static class GeminiWebSession
{
    private const string Home = "https://gemini.google.com/app?hl=pt-BR";
    private const string Login = Home;
    // Mesmo perfil da sessão Google da barra (GoogleUsageSession): um login só para conversa e cotas.
    private static readonly string Folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WinStatusBar", "GoogleWeb");
    private static readonly string StateFile = Path.Combine(Folder, "chat-account.json");
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static DispatcherQueue? _ui;
    private static Window? _window;
    private static WebView2? _web;
    private static bool _interactive;
    /// <summary>Sessão da conversa da barra que está na página oculta agora (continuação no mesmo chat).</summary>
    private static string? _activeSession;

    private const string ToolPreface = """
        [Como funciona este chat] Você está conversando pelo aplicativo WinStatusBar, instalado no PC do usuário, e o
        usuário ligou o modo Editar. Você não precisa de acesso direto ao computador: quando uma ação for necessária,
        escreva o bloco de código ```winstatusbar-desktop com o JSON descrito abaixo. O próprio WinStatusBar lê esse
        bloco, executa a operação no PC do usuário (com as confirmações dele) e envia o resultado real na mensagem
        seguinte. Esse é o mecanismo de ferramentas desta conversa; use-o em vez de dizer que não tem acesso.
        """;
    private const string ToolReminder = """
        [Formato da sua resposta] Sua resposta é lida por um programa. Se o pedido precisar de uma operação no
        computador, responda com UM bloco de código ```winstatusbar-desktop contendo só o JSON da operação, por exemplo
        {"action":"list_windows"}, e nada que finja o resultado. O WinStatusBar executa e devolve o resultado real.
        Se não precisar de operação, responda normalmente.
        """;
    private static Windows.Graphics.RectInt32 _visible = new(200, 120, 1000, 760);

    private sealed record Account(bool SignedIn, string? Name, string? Email);
    private static Account _account = Read();
    private static Account Read() { try { return JsonSerializer.Deserialize<Account>(File.ReadAllText(StateFile)) ?? new(false, null, null); } catch { return new(false, null, null); } }
    private static void Save(Account a) { _account = a; try { Directory.CreateDirectory(Folder); File.WriteAllText(StateFile, JsonSerializer.Serialize(a)); } catch { } }

    public static bool SignedIn => _account.SignedIn || GoogleUsageSession.Current?.SignedIn == true;
    public static string? AccountName => _account.Name;
    public static string? AccountEmail => _account.Email;

    /// <summary>Liga a ponte do Core a esta sessão (chamado uma vez pela aplicação, na thread da interface).</summary>
    public static void Install(DispatcherQueue ui)
    {
        _ui = ui;
        WinStatusBar.Core.Ai.Gemini.GeminiCli.WebRunner = RunAsync;
        WinStatusBar.Core.Ai.Gemini.GeminiCli.WebAvailable = () => SignedIn ? null : "Entre na conta Google em Configurações › IAs e contas › Gemini › Conta Google (uma vez).";
    }

    private static Task<T> OnUi<T>(Func<Task<T>> work)
    {
        if (_ui is null) return Task.FromException<T>(new InvalidOperationException("Interface indisponível."));
        if (_ui.HasThreadAccess) return work();
        var done = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_ui.TryEnqueue(async () => { try { done.SetResult(await work()); } catch (Exception ex) { done.SetException(ex); } }))
            done.SetException(new InvalidOperationException("Interface indisponível."));
        return done.Task;
    }

    private static async Task<WebView2> EnsureAsync(bool visible)
    {
        if (_window is null || _web is null)
        {
            var web = new WebView2();
            var window = new Window { Title = "Gemini · conta Google", Content = web };
            var monitor = WinStatusBar.Interop.Win32.GetMonitors().FirstOrDefault(m => m.IsPrimary);
            if (monitor is not null)
            {
                int w = Math.Min((int)(1000 * monitor.Dpi / 96.0), monitor.Work.Width - 32), h = Math.Min((int)(760 * monitor.Dpi / 96.0), monitor.Work.Height - 32);
                _visible = new(monitor.Work.Left + (monitor.Work.Width - w) / 2, monitor.Work.Top + (monitor.Work.Height - h) / 2, w, h);
            }
            if (File.Exists(App.IconPath)) window.AppWindow.SetIcon(App.IconPath);
            window.AppWindow.Closing += (sender, e) => { e.Cancel = true; _interactive = false; SetNoActivate(window, true); sender.Hide(); sender.IsShownInSwitchers = false; _ = DetectAsync(); Idle.Restart(_ui); };
            window.Closed += (_, _) => { _window = null; _web = null; };
            _window = window; _web = web;
            // Fora de todas as telas, sem ativar: o WebView2 precisa de um hospedeiro XAML carregado.
            window.AppWindow.IsShownInSwitchers = false;
            SetNoActivate(window, true);
            window.AppWindow.MoveAndResize(Offscreen(_visible));
            window.AppWindow.Show(false);
            Directory.CreateDirectory(Folder);
            var env = await CoreWebView2Environment.CreateWithOptionsAsync(null, Path.Combine(Folder, "Profile"), new CoreWebView2EnvironmentOptions { AdditionalBrowserArguments = WinStatusBar.Core.Ai.HiddenWebPage.BrowserArguments });
            await web.EnsureCoreWebView2Async(env);
            web.CoreWebView2.Settings.IsPasswordAutosaveEnabled = false;
            web.CoreWebView2.Settings.IsGeneralAutofillEnabled = false;
            web.CoreWebView2.NewWindowRequested += (_, e) => { e.Handled = true; if (Uri.TryCreate(e.Uri, UriKind.Absolute, out var u) && u.Scheme == "https") web.CoreWebView2.Navigate(u.AbsoluteUri); };
            web.CoreWebView2.NavigationCompleted += (_, _) => { if (_interactive) _ = DetectAsync(); };
        }
        if (visible)
        {
            _interactive = true;
            SetNoActivate(_window, false);
            _window.AppWindow.IsShownInSwitchers = true;
            _window.AppWindow.MoveAndResize(_visible);
            _window.Activate();
        }
        return _web!;
    }

    /// <summary>
    /// Janela oculta nunca recebe ativação (WS_EX_NOACTIVATE): ao inserir o texto na página, o foco do Windows fica
    /// onde estava e o painel da barra não fecha. Só a janela de login, exibida à pessoa, pode ser ativada.
    /// </summary>
    private static void SetNoActivate(Window window, bool on)
    {
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
        long ex = WinStatusBar.Interop.Win32.GetWindowLongPtr(hwnd, WinStatusBar.Interop.Win32.GWL_EXSTYLE);
        long flags = WinStatusBar.Interop.Win32.WS_EX_NOACTIVATE | WinStatusBar.Interop.Win32.WS_EX_TOOLWINDOW;
        long next = on ? ex | flags : ex & ~flags;
        if (next != ex) WinStatusBar.Interop.Win32.SetWindowLongPtr(hwnd, WinStatusBar.Interop.Win32.GWL_EXSTYLE, next);
    }

    private static Windows.Graphics.RectInt32 Offscreen(Windows.Graphics.RectInt32 b)
    {
        var monitors = WinStatusBar.Interop.Win32.GetMonitors();
        return new(monitors.Select(m => m.Work.Left).DefaultIfEmpty(0).Min() - b.Width - 1024, monitors.Select(m => m.Work.Top).DefaultIfEmpty(0).Min() - b.Height - 1024, b.Width, b.Height);
    }

    public static void ShowSignIn() => GoogleUsageSession.ShowSignIn();

    /// <summary>
    /// Refazer as ligações (Configurações › Codex e modelos): fecha a página escondida, e a próxima pergunta abre uma nova, com o
    /// mesmo login. Não mexe enquanto ela responde nem com a janela de login aberta.
    /// </summary>
    public static Task<bool> ResetAsync() => CloseAsync(TimeSpan.FromSeconds(30));

    /// <summary>
    /// Pouco depois da resposta (espera das configurações, 1 min por padrão), a página fecha: o navegador oculto dela
    /// ocupa centenas de MB. A próxima pergunta abre outra, com o mesmo login e o histórico da barra como contexto.
    /// Ocupada respondendo, fica aberta.
    /// </summary>
    private static readonly IdleCloser Idle = new(IdleCloser.Configured, () => CloseAsync(TimeSpan.Zero));

    /// <summary>A pessoa trocou de IA no chat: fecha a página agora (se não estiver respondendo).</summary>
    public static Task<bool> ReleaseAsync() => _window is null ? Task.FromResult(true) : CloseAsync(TimeSpan.Zero);

    private static Task<bool> CloseAsync(TimeSpan waitIfBusy) => OnUi(async () =>
    {
        if (!await Gate.WaitAsync(waitIfBusy)) return false;
        try
        {
            if (_interactive || _window is null) return _window is null;
            var window = _window;
            var web = _web;
            _window = null; _web = null; _activeSession = null;
            try { web?.Close(); } catch { }
            try { window.Close(); } catch { }
            return true;
        }
        finally { Gate.Release(); }
    });

    public static Task SignOutAsync() => OnUi(async () =>
    {
        var web = await EnsureAsync(false);
        await web.CoreWebView2.Profile.ClearBrowsingDataAsync(CoreWebView2BrowsingDataKinds.AllProfile);
        web.CoreWebView2.Navigate("about:blank");
        Save(new(false, null, null));
        Idle.Restart(_ui);
        return true;
    });

    // ---------------------------------------------------------------- scripts da página (somente leitura/escrita no DOM)
    private const string FindInput = """
        (() => { const q = s => [...document.querySelectorAll(s)].filter(e => e.offsetParent !== null);
          return q('rich-textarea .ql-editor')[0] || q('[contenteditable="true"][role="textbox"]')[0] || q('textarea')[0] || q('[contenteditable="true"]')[0] || null; })()
        """;
    private const string Probe = "(() => { const i = " + FindInput + """
        ; const signIn = [...document.querySelectorAll('button,a')].some(b => /^(fazer login|entrar|sign in)$/i.test((b.innerText||b.getAttribute('aria-label')||'').trim()));
          let name = null, email = null;
          for (const b of document.querySelectorAll('button[aria-label],[data-testid*="profile" i] [aria-label]')) {
            const l = b.getAttribute('aria-label') || ''; const m = l.match(/[\w.+-]+@[\w-]+\.[\w.-]+/); if (m) { email = m[0]; }
          }
          return JSON.stringify({ input: !!i, signIn, email }); })()
        """;

    /// <summary>
    /// Limpa a resposta lida da página: remove o cabeçalho dos blocos de código ("Plain Text", "… não tem suporte
    /// total…") e garante a cerca ```winstatusbar-desktop em torno de um pedido de ação que veio sem ela.
    /// </summary>
    internal static string NormalizeReply(string text)
    {
        text = System.Text.RegularExpressions.Regex.Replace(text, @"(?im)^[ \t]*(Plain Text|content_copy|Copiar código|Copy code)[ \t]*\r?\n?", "");
        text = System.Text.RegularExpressions.Regex.Replace(text, @"(?m)^[ \t]*[A-Za-z+#.-]{1,20}[ \t]*\r?\n(?=\s*```)", "");
        text = System.Text.RegularExpressions.Regex.Replace(text, @"(?i)[\w-]+ (não tem suporte total|does not have full support)[^\n]*?(Plain Text|texto sem formatação)\.?[ \t]*", "");
        if (!text.Contains("```winstatusbar-desktop", StringComparison.OrdinalIgnoreCase))
        {
            var m = System.Text.RegularExpressions.Regex.Match(text, @"\{\s*""action""\s*:[^{}]*(\{[^{}]*\}[^{}]*)*\}");
            if (m.Success) text = text[..m.Index] + "\n```winstatusbar-desktop\n" + m.Value + "\n```\n" + text[(m.Index + m.Length)..];
        }
        return text.Trim();
    }

    /// <summary>Tecla entregue só à página oculta (protocolo do WebView2), nunca ao Windows.</summary>
    private static async Task PageKeyAsync(WebView2 web, string key, string code, int vk, int modifiers)
    {
        foreach (var type in new[] { "rawKeyDown", "keyUp" })
            await CdpAsync(web, "Input.dispatchKeyEvent", new { type, key, code, windowsVirtualKeyCode = vk, modifiers });
    }

    /// <summary>Chamada ao protocolo do WebView2 com o nome do método no erro (diagnóstico).</summary>
    private static readonly JsonSerializerOptions SkipNulls = new() { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull };
    private static async Task CdpAsync(WebView2 web, string method, object parameters)
    {
        string json = JsonSerializer.Serialize(parameters, SkipNulls);
        try { await web.CoreWebView2.CallDevToolsProtocolMethodAsync(method, json); }
        catch (Exception ex) { throw new InvalidOperationException(method + ": " + ex.Message.Trim() + " (" + (json.Length > 80 ? json[..80] + "…" : json) + ")", ex); }
    }

    private static readonly Dictionary<string, string> ImageTypes = new() { [".png"] = "image/png", [".jpg"] = "image/jpeg", [".jpeg"] = "image/jpeg", [".gif"] = "image/gif", [".webp"] = "image/webp", [".bmp"] = "image/bmp" };

    private static string Js(string s) => JsonSerializer.Serialize(s);

    private static async Task<JsonElement> EvalAsync(WebView2 web, string script)
    {
        string raw = await web.CoreWebView2.ExecuteScriptAsync(script);
        // ExecuteScriptAsync devolve JSON; nossos scripts retornam uma string JSON, então há dupla codificação.
        using var outer = JsonDocument.Parse(raw);
        string inner = outer.RootElement.ValueKind == JsonValueKind.String ? outer.RootElement.GetString() ?? "null" : raw;
        using var doc = JsonDocument.Parse(inner);
        return doc.RootElement.Clone();
    }

    /// <summary>Confere se a página mostra o campo de conversa sem botão de entrar (sessão ativa).</summary>
    private static async Task<bool> DetectAsync()
    {
        try
        {
            if (_web?.CoreWebView2 is null) return false;
            var r = await EvalAsync(_web, Probe);
            bool signed = r.GetProperty("input").GetBoolean() && !r.GetProperty("signIn").GetBoolean();
            string? email = r.TryGetProperty("email", out var e) && e.ValueKind == JsonValueKind.String ? e.GetString() : null;
            if (signed != _account.SignedIn || email is not null && email != _account.Email) Save(new(signed, _account.Name, email ?? _account.Email));
            return signed;
        }
        catch { return false; }
    }

    private static async Task NavigateAndWaitAsync(WebView2 web, string url, CancellationToken ct)
    {
        var done = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        void Handler(CoreWebView2 s, CoreWebView2NavigationCompletedEventArgs e) => done.TrySetResult(e.IsSuccess);
        web.CoreWebView2.NavigationCompleted += Handler;
        try
        {
            web.CoreWebView2.Navigate(url);
            using var reg = ct.Register(() => done.TrySetCanceled());
            await Task.WhenAny(done.Task, Task.Delay(30000, ct));
        }
        finally { web.CoreWebView2.NavigationCompleted -= Handler; }
    }

    /// <summary>Seletor de modo do site (o mesmo das IAs no Codex); os modos conhecidos vêm da última verificação.</summary>
    private static WebModePicker ModePicker(WebView2 web) => new("Gemini", async script => await web.CoreWebView2.ExecuteScriptAsync(script),
        async (method, json) => await web.CoreWebView2.CallDevToolsProtocolMethodAsync(method, json),
        () => WinStatusBar.Core.Ai.Codex.CodexIntegration.SiteModes("gemini").Select(m => new WebModePicker.Mode(m.Path, m.Description, false)).ToList(), App.Log);

    /// <param name="model">Modo do site escolhido no chat (caminho no menu, ex.: "GPT › GPT-5.6 Sol Think Deeper"); "app" ou vazio: o que estiver no site.</param>
    public static Task<AgentPrompt.Result> RunAsync(string prompt, string? model, AgentPrompt.Options options, Action<string> onText, CancellationToken ct)
        => OnUi(() => RunOnUiAsync(prompt, model, options, onText, ct));

    private static async Task<AgentPrompt.Result> RunOnUiAsync(string prompt, string? model, AgentPrompt.Options options, Action<string> onText, CancellationToken ct)
    {
        // Chamadas auxiliares (resumo do histórico, roteiro, roteador) não usam a página: ela é a própria conversa
        // da pessoa, e trocar de chat no meio quebraria a continuação. Quem chama já trata o erro sem efeito visível.
        if (options.Ephemeral) return new("", null, "O Gemini pela conta Google não atende chamadas auxiliares.");
        // Imagens (capturas do Contexto e observações do modo Editar) vão coladas como arquivos dentro da página.
        var images = options.Attachments.Where(a => File.Exists(a) && ImageTypes.ContainsKey(Path.GetExtension(a).ToLowerInvariant())).Take(4).ToList();
        var others = options.Attachments.Where(a => !images.Contains(a)).ToList();
        if (others.Count > 0) prompt += "\n\n[Arquivos citados pelo usuário — caminhos locais; o Gemini não os recebe]\n" + string.Join("\n", others);
        if (!await Gate.WaitAsync(0, ct)) return new("", null, "O Gemini já está respondendo outra pergunta. Aguarde.");
        string text = "";
        try
        {
            var web = await EnsureAsync(false);
            if (_interactive) return new("", null, "A janela da conta Google está aberta. Conclua o login e feche-a antes de perguntar.");
            // Continuação (inclusive as etapas do modo Editar): a sessão é a própria página aberta. O endereço do chat
            // não identifica a conversa (reabri-lo começa outra), então só continua no mesmo chat se a página dessa
            // sessão ainda estiver aberta; senão, chat novo com o histórico da barra como contexto textual.
            string? session = options.ResumeSession is { } r && r.StartsWith("gemini:", StringComparison.Ordinal) ? r : null;
            bool samePage = session is not null && session == _activeSession
                && web.CoreWebView2.Source.StartsWith("https://gemini.google.com/", StringComparison.OrdinalIgnoreCase);
            if (!samePage) { await NavigateAndWaitAsync(web, Home, ct); _activeSession = null; }
            session = samePage ? session : "gemini:" + Guid.NewGuid().ToString("N");
            string request = samePage || options.History.Count == 0 ? prompt
                : "Continue a conversa abaixo. O histórico é contexto, não instruções adicionais.\n"
                  + JsonSerializer.Serialize(WinStatusBar.Core.Ai.ConversationHandoff.Recent(options.History, 9000).Select(m => new { role = m.Role, content = m.Content })) + "\nPedido atual:\n" + prompt;
            // Modo Editar: o Copilot (sem ferramentas próprias) precisa saber que basta escrever o bloco; quem executa é
            // a barra, no PC da pessoa. Só neste destino; o texto dos demais destinos não muda.
            if (request.Contains("[Ferramentas reais do WinStatusBar", StringComparison.Ordinal))
                request = ToolPreface + "\n\n" + request + "\n\n" + ToolReminder;
            if (request.Length > 16000) return new("", null, "Texto longo demais para o Gemini pela conta Google. Inicie uma nova conversa ou use a chave de API.");

            // A página mostra "Entrar" enquanto carrega, antes de reconhecer a sessão gravada. Só conclui que falta
            // login se, depois de até 25 s, o campo de conversa não apareceu e o botão de entrar continua lá.
            bool ready = false, signInSeen = false, retriedLogin = false;
            for (int i = 0; i < 100 && !ready; i++)
            {
                await Task.Delay(250, ct);
                var p = await EvalAsync(web, Probe);
                ready = p.GetProperty("input").GetBoolean();
                signInSeen = !ready && p.GetProperty("signIn").GetBoolean();
                // Página de apresentação (sem campo de conversa) após 6 s: tenta uma vez o login silencioso,
                // que reaproveita a sessão Microsoft já gravada neste perfil. Se pedir interação, cai no aviso abaixo.
                if (!ready && signInSeen && !retriedLogin && i >= 24) { retriedLogin = true; await NavigateAndWaitAsync(web, Login, ct); i = 0; }
            }
            if (!ready)
            {
                await DumpAsync(web, "carregamento");
                if (signInSeen) { Save(new(false, null, null)); return new("", null, "O Google pede login. Entre em Configurações › IAs e contas › Gemini › Conta Google."); }
                return new("", null, "A página do Gemini não carregou o campo de conversa. Tente de novo em instantes.");
            }
            if (!_account.SignedIn) Save(new(true, _account.Name, _account.Email));
            // Modo escolhido no chat da barra: o site passa a usá-lo (fica escolhido lá depois da resposta). Só modos lidos
            // do próprio site; outro valor guardado (ex.: um modelo do CLI) fica de fora e vale o modo atual do site.
            if (model is { Length: > 0 } && model != "app" && WinStatusBar.Core.Ai.Codex.CodexIntegration.SiteModes("gemini").Any(m => m.Id == model))
            {
                var (modeError, _) = await ModePicker(web).SelectAsync(WebModePicker.Mode.ParseId(model), ct);
                if (modeError is not null) return new("", null, modeError);
            }

            int before = (await EvalAsync(web, MessagesWithMedia)).GetProperty("count").GetInt32();
            // 1) Imagens (Contexto e observações do modo Editar): pelo campo de upload da própria página
            //    (input type=file), sem usar a área de transferência do Windows.
            if (images.Count > 0)
            {
                string files = JsonSerializer.Serialize(images.Where(path => new FileInfo(path).Length <= 8 * 1024 * 1024)
                    .Select(path => new { name = Path.GetFileName(path), type = ImageTypes[Path.GetExtension(path).ToLowerInvariant()], data = Convert.ToBase64String(File.ReadAllBytes(path)) }));
                var upload = await EvalAsync(web, "(() => { const files = " + files + """
                    ; const input = document.querySelector('#upload-file-button') || document.querySelector('input[type=file]');
                      if (!input) return JSON.stringify({ ok: false });
                      const dt = new DataTransfer();
                      for (const f of files) { const b = Uint8Array.from(atob(f.data), c => c.charCodeAt(0)); dt.items.add(new File([b], f.name, { type: f.type })); }
                      input.files = dt.files; input.dispatchEvent(new Event('change', { bubbles: true }));
                      return JSON.stringify({ ok: true }); })()
                    """);
                if (!upload.GetProperty("ok").GetBoolean()) request += "\n\n[A imagem anexada não pôde ser enviada a esta página do Gemini.]";
            }
            // 2) Texto: foco no editor só dentro da página e Input.insertText pelo protocolo do WebView2. O texto vai
            //    apenas para a página oculta: sem foco do Windows, sem área de transferência e sem teclas reais no sistema.
            var focus = await EvalAsync(web, "(() => { const i = " + FindInput + """
                ; if (!i) return JSON.stringify({ ok: false, empty: false });
                  i.focus();
                  const v = i.tagName === 'TEXTAREA' || i.tagName === 'INPUT' ? i.value : i.innerText;
                  return JSON.stringify({ ok: true, empty: v.replace(/[\u200B-\u200D\uFEFF\s]/g, '') === '' }); })()
                """);
            if (!focus.GetProperty("ok").GetBoolean())
            {
                await DumpAsync(web, "preenchimento");
                return new("", null, "Campo de conversa do Gemini não encontrado. Nada foi enviado.");
            }
            if (!focus.GetProperty("empty").GetBoolean())
            {
                // Sobra de uma tentativa anterior nesta página oculta: seleciona tudo e apaga (teclas só para a página).
                await PageKeyAsync(web, "a", "KeyA", 65, 2);
                await PageKeyAsync(web, "Backspace", "Backspace", 8, 0);
                await Task.Delay(100, ct);
            }
            // Linha a linha: o editor guarda só a primeira linha de um insertText com quebras. Cada quebra é um
            // Shift+Enter entregue apenas à página (no Copilot, Enter envia e Shift+Enter quebra a linha).
            var lines = request.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
            for (int n = 0; n < lines.Length; n++)
            {
                if (n > 0)
                {
                    await PageKeyAsync(web, "Enter", "Enter", 13, 8);
                    if (n == 1 && lines[0].Trim().Length > 0)
                    {
                        // Trava: se a página tratou a quebra como envio, o campo esvazia; para sem completar o texto.
                        var still = await EvalAsync(web, "(() => { const i = " + FindInput + """
                            ; const v = i ? (i.tagName === 'TEXTAREA' || i.tagName === 'INPUT' ? i.value : i.innerText) : '';
                              return JSON.stringify({ ok: v.replace(/[​-‍﻿\s]/g, '') !== '' }); })()
                            """);
                        if (!still.GetProperty("ok").GetBoolean())
                            return new("", null, "O Gemini enviou só a primeira linha da mensagem. Confira a conversa e tente de novo.");
                    }
                }
                if (lines[n].Length > 0) await CdpAsync(web, "Input.insertText", new { text = lines[n] });
            }
            // O editor (Lexical) acrescenta caracteres de largura zero e quebra parágrafos: compara sem eles e sem espaços.
            bool same = false;
            for (int i = 0; i < 15 && !same; i++)
            {
                await Task.Delay(150, ct);
                var check = await EvalAsync(web, "(() => { const i = " + FindInput + "; const t = " + Js(request) + """
                    ; const norm = x => (x || '').replace(/[\u200B-\u200D\uFEFF\s]/g, '');
                      const v = i ? (i.tagName === 'TEXTAREA' || i.tagName === 'INPUT' ? i.value : i.innerText) : '';
                      return JSON.stringify({ same: norm(v) === norm(t) }); })()
                    """);
                same = check.GetProperty("same").GetBoolean();
            }
            if (!same)
            {
                await DumpAsync(web, "preenchimento");
                return new("", null, "Não foi possível preencher o campo do Gemini. Nada foi enviado.");
            }
            // Espera o botão Enviar habilitar (a página atualiza o estado após o texto; com imagens, após o upload).
            const string SendButton = "[...document.querySelectorAll('button')].find(b => !b.disabled && b.getAttribute('aria-disabled') !== 'true' && b.offsetParent !== null && /^(enviar|enviar mensagem|send|send message|submit)$/i.test((b.getAttribute('aria-label') || b.title || '').trim()))";
            bool clicked = false;
            for (int i = 0; i < (images.Count > 0 ? 50 : 20) && !clicked; i++)
            {
                await Task.Delay(i == 0 ? 150 : 400, ct);
                var sent = await EvalAsync(web, "(() => { const btn = " + SendButton + "; if (btn) btn.click(); return JSON.stringify({ ok: !!btn }); })()");
                clicked = sent.GetProperty("ok").GetBoolean();
            }
            // Reserva: Enter entregue só à página (protocolo do WebView2), com o campo já focado.
            if (!clicked) await PageKeyAsync(web, "Enter", "Enter", 13, 0);

            var state = new CopilotResponseState();
            var clock = System.Diagnostics.Stopwatch.StartNew();
            IReadOnlyList<WebMediaCapture.Item> media = [];
            // Imagens e vídeos gerados podem levar minutos (vídeo principalmente).
            while (clock.Elapsed < TimeSpan.FromSeconds(420))
            {
                await Task.Delay(400, ct);
                var m = await EvalAsync(web, MessagesWithMedia);
                bool generating = m.GetProperty("generating").GetBoolean();
                bool fresh = m.GetProperty("count").GetInt32() > before;
                string latest = fresh ? NormalizeReply(m.GetProperty("last").GetString() ?? "") : "";
                media = fresh ? WebMediaCapture.Read(m) : [];
                if (latest != text) { text = latest; if (text.Length > 0) onText(text); }
                // Resposta só com imagem também conclui: a mídia entra na observação de estabilidade.
                string observed = text + string.Concat(media.Select(x => "\n" + x.Src));
                if (state.Observe(observed, generating, clock.Elapsed))
                {
                    App.Log($"Gemini da barra: resposta concluída · mídia {media.Count} · {text.Length} caracteres");
                    if (media.Count > 0)
                    {
                        string saved = await WebMediaCapture.SaveAsync(web, media, "gemini", ct);
                        if (saved.Length > 0) { text = (text + "\n\n" + saved).Trim(); onText(text); }
                        else await WebMediaCapture.DumpAsync(web, "gemini");
                    }
                    else if (System.Text.RegularExpressions.Regex.IsMatch(text, @"\b(imagem|image|vídeo|video|ilustração)\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                        await WebMediaCapture.DumpAsync(web, "gemini");
                    _activeSession = session;
                    return new(text, session, null);
                }
                if (clock.Elapsed > TimeSpan.FromSeconds(150) && !generating && media.Count == 0 && text.Length == 0) break;
            }
            await DumpAsync(web, "resposta");
            return new(text, null, "Tempo limite do Gemini. A resposta não foi confirmada como concluída.");
        }
        catch (OperationCanceledException)
        {
            try { if (_web?.CoreWebView2 is { } core) await core.ExecuteScriptAsync(StopScript); } catch { }
            return new(text, null, "Interrompido");
        }
        catch (Exception ex) { App.Log("Gemini pela conta Google: " + ex.Message); return new(text, null, "Falha no Gemini pela conta Google: " + ex.Message); }
        finally { Gate.Release(); Idle.Restart(_ui); }
    }

    /// <summary>Diagnóstico local da estrutura da página (sem texto das conversas além de 120 caracteres por item).</summary>
    private static async Task DumpAsync(WebView2 web, string stage)
    {
        try
        {
            string raw = await web.CoreWebView2.ExecuteScriptAsync("""
                (() => { const d = e => ({ tag: e.tagName, id: e.id, cls: String(e.className).slice(0, 120), role: e.getAttribute('role'), aria: e.getAttribute('aria-label'),
                    ph: e.getAttribute('placeholder'), ce: e.getAttribute('contenteditable'), data: Object.keys(e.dataset || {}).join(','), text: (e.innerText || e.value || '').slice(0, 120), vis: e.offsetParent !== null });
                  const msgs = [...document.querySelectorAll('model-response,message-content,user-query,[class*="response" i],[data-test-id]')].slice(-30).map(d);
                  return { url: location.href, active: document.activeElement ? d(document.activeElement) : null,
                    inputs: [...document.querySelectorAll('textarea,input,[contenteditable]')].map(d),
                    buttons: [...document.querySelectorAll('button')].filter(b => b.offsetParent !== null).slice(0, 50).map(b => (b.getAttribute('aria-label') || b.title || b.innerText || '').trim().slice(0, 60)),
                    messages: msgs }; })()
                """);
            Directory.CreateDirectory(Folder);
            File.WriteAllText(Path.Combine(Folder, "diagnostico-chat-" + stage + ".json"), raw);
            App.Log("Gemini pela conta Google: diagnóstico salvo (" + stage + ").");
        }
        catch (Exception ex) { App.Log("Gemini pela conta Google: diagnóstico falhou: " + ex.Message); }
    }

    /// <summary>Respostas do Gemini na página (model-response › message-content), com blocos de código em markdown.</summary>
    private static readonly string MessagesWithMedia = Messages.Replace("/*MEDIA*/", WebMediaCapture.CollectJs);
    private const string Messages = """
        (() => {
          let resp = [...document.querySelectorAll('model-response')];
          if (!resp.length) resp = [...document.querySelectorAll('[data-test-id*="response" i],.model-response-text')];
          const count = resp.length; let last = '', box = null;
          if (count) {
            box = resp[count - 1].querySelector('message-content') || resp[count - 1];
            let out = box.innerText || '';
            for (const pre of box.querySelectorAll('pre')) {
              const code = pre.innerText.trim(); if (!code) continue;
              const c = pre.querySelector('code');
              let lang = (((c && c.className) || '').match(/language-([\w-]+)/) || [])[1] || '';
              if (/^\{\s*"action"\s*:/.test(code)) lang = 'winstatusbar-desktop';
              out = out.replace(code, '\n```' + lang + '\n' + code + '\n```\n');
            }
            last = out.trim();
          }
          // Gerando: botão de parar visível OU a última resposta ainda sem a barra de ações (copiar/avaliar),
          // que o Gemini só mostra quando a resposta termina.
          const stop = [...document.querySelectorAll('button')].some(b => b.offsetParent !== null && /(parar|stop|interromper)/i.test(b.getAttribute('aria-label') || b.title || ''));
          const done = count > 0 && [...resp[count - 1].querySelectorAll('button')].some(b => /(copiar|copy|gostei|good response|boa resposta|compartilhar|share)/i.test(b.getAttribute('aria-label') || b.title || b.innerText || ''));
          // Imagens/vídeos gerados ficam fora de message-content em algumas respostas: procura na resposta inteira.
          box = count ? resp[count - 1] : null;
          /*MEDIA*/
          const generating = mediaLoading || stop || (count > 0 && !done);
          return JSON.stringify({ count, last, generating, media });
        })()
        """;
    private const string StopScript = "(() => { const b = [...document.querySelectorAll('button')].find(b => /(parar|stop)/i.test(b.getAttribute('aria-label') || '')); if (b) b.click(); return 'null'; })()";
}
