using WinStatusBar.Core.Ai;

namespace CodexIAs.Providers;

/// <summary>O que muda entre o Copilot e o Gemini: endereços, perfil de login e os seletores da página.</summary>
internal sealed record WebSite(
    string Name, string Label, string DisplayPrefix, int Priority,
    string Home, string Login, string HomePrefix, string ProfileFolder,
    string FindInput, string SignInPattern, string Messages, string SendButton, string Stop,
    string ModePicker, string ModeItems, string LoginHint)
{
    private static string Local => Paths.LocalAppData;

    // Sem a captura de imagens geradas da barra: pelo Codex, só texto.
    private const string NoMedia = "const mediaLoading = false, media = [];";

    // Seletor de modo: o mesmo da barra (WebModePicker, no Core).
    private const string LabeledPicker = WebModePicker.LabeledPicker;
    private const string GenericItems = WebModePicker.GenericItems;

    public static WebSite Copilot { get; } = new(
        "copilot", "Microsoft Copilot", "Microsoft Copilot", -4,
        "https://copilot.com/", "https://copilot.com/login?ru=%2f", "https://copilot.com/",
        Path.Combine(Local, "WinStatusBar", "CopilotWeb", "Profile"),
        FindInput: """
            (() => { const q = s => [...document.querySelectorAll(s)].filter(e => e.offsetParent !== null);
              return q('textarea#userInput')[0] || q('textarea')[0] || q('[contenteditable="true"][role="textbox"]')[0] || q('[contenteditable="true"]')[0] || null; })()
            """,
        SignInPattern: "^(entrar|sign in|iniciar sessão)$",
        Messages: """
            (() => {
              const titles = [...document.querySelectorAll('[id^="copilot-message-"][id$="-title"]')];
              let count = titles.length, last = '', box = null;
              if (count) {
                const t = titles[count - 1];
                box = document.querySelector('[aria-labelledby~="' + t.id + '"]') || t.closest('[role="article"],[data-testid*="message" i]') || t.parentElement;
                let out = box ? box.innerText : '';
                for (const pre of (box ? box.querySelectorAll('pre') : [])) {
                  const code = pre.innerText.trim(); if (!code) continue;
                  const c = pre.querySelector('code');
                  const lang = (((c && c.className) || '').match(/language-([\w-]+)/) || [])[1] || '';
                  out = out.replace(code, '\n```' + lang + '\n' + code + '\n```\n');
                }
                last = out.replace(t.innerText || '', '').replace(/^\s*(Copilot (said|disse)|O Copilot disse):?\s*/i, '').trim();
              } else {
                for (const s of ['[data-content="ai-message"]', '[data-testid*="ai-message" i]', '[class*="ai-message" i]', '[data-author="bot"]', '[data-message-author-role="assistant"]']) {
                  const n = [...document.querySelectorAll(s)]; if (n.length) { count = n.length; box = n[n.length - 1]; last = box.innerText.trim(); break; }
                }
              }
              __MEDIA__
              const generating = mediaLoading || [...document.querySelectorAll('button')].some(b => b.offsetParent !== null && /(parar|stop)/i.test(b.getAttribute('aria-label') || b.title || ''));
              return JSON.stringify({ count, last, generating });
            })()
            """.Replace("__MEDIA__", NoMedia),
        SendButton: "[...document.querySelectorAll('button')].find(b => !b.disabled && b.getAttribute('aria-disabled') !== 'true' && b.offsetParent !== null && /^(enviar|enviar mensagem|send|send message|submit)$/i.test((b.getAttribute('aria-label') || b.title || '').trim()))",
        Stop: "(() => { const b = [...document.querySelectorAll('button')].find(b => /(parar|stop)/i.test(b.getAttribute('aria-label') || '')); if (b) b.click(); return 'null'; })()",
        ModePicker: LabeledPicker, ModeItems: GenericItems,
        LoginHint: "Entre no Copilot pela barra (Configurações › IA › Microsoft Copilot) ou rode \"CodexIAs.exe --entrar copilot\". O login vale para os dois.");

    public static WebSite Gemini { get; } = new(
        "gemini", "Gemini", "Gemini", -5,
        "https://gemini.google.com/app?hl=pt-BR", "https://gemini.google.com/app?hl=pt-BR", "https://gemini.google.com/",
        Path.Combine(Local, "WinStatusBar", "GoogleWeb", "Profile"),
        FindInput: """
            (() => { const q = s => [...document.querySelectorAll(s)].filter(e => e.offsetParent !== null);
              return q('rich-textarea .ql-editor')[0] || q('[contenteditable="true"][role="textbox"]')[0] || q('textarea')[0] || q('[contenteditable="true"]')[0] || null; })()
            """,
        SignInPattern: "^(fazer login|entrar|sign in)$",
        Messages: """
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
                  const lang = (((c && c.className) || '').match(/language-([\w-]+)/) || [])[1] || '';
                  out = out.replace(code, '\n```' + lang + '\n' + code + '\n```\n');
                }
                last = out.trim();
              }
              const stop = [...document.querySelectorAll('button')].some(b => b.offsetParent !== null && /(parar|stop|interromper)/i.test(b.getAttribute('aria-label') || b.title || ''));
              const done = count > 0 && [...resp[count - 1].querySelectorAll('button')].some(b => /(copiar|copy|gostei|good response|boa resposta|compartilhar|share)/i.test(b.getAttribute('aria-label') || b.title || b.innerText || ''));
              box = count ? resp[count - 1] : null;
              __MEDIA__
              const generating = mediaLoading || stop || (count > 0 && !done);
              return JSON.stringify({ count, last, generating });
            })()
            """.Replace("__MEDIA__", NoMedia),
        SendButton: "[...document.querySelectorAll('button')].find(b => !b.disabled && b.getAttribute('aria-disabled') !== 'true' && b.offsetParent !== null && /^(enviar|enviar mensagem|send|send message|submit)$/i.test((b.getAttribute('aria-label') || b.title || '').trim()))",
        Stop: "(() => { const b = [...document.querySelectorAll('button')].find(b => /(parar|stop)/i.test(b.getAttribute('aria-label') || '')); if (b) b.click(); return 'null'; })()",
        ModePicker: LabeledPicker, ModeItems: GenericItems,
        LoginHint: "Entre na conta Google pela barra (Configurações › IA › Gemini › Conta Google) ou rode \"CodexIAs.exe --entrar gemini\". O login vale para os dois.");

    public string Picker => ModePicker.Replace("__INPUT__", FindInput.Trim());
}
