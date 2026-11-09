using System.Text.Json;

namespace WinStatusBar.Core.Ai;

/// <summary>
/// Seletor de modo de um site de IA (Gemini, Copilot) numa página do WebView2: lê os modos do menu (inclusive os de
/// submenus, como "GPT"), escolhe o modo pedido antes de enviar e informa o que estava escolhido, para devolver depois.
/// É o mesmo para a barra e para as IAs no Codex (o perfil do navegador é compartilhado). A página é tocada só pelo
/// protocolo do WebView2: cliques e teclas vão para a página escondida, nunca para o mouse ou o teclado do Windows.
/// </summary>
public sealed class WebModePicker
{
    /// <summary>Um modo do site: caminho no menu (ex.: ["GPT", "GPT-5.6 Sol Think Deeper"]), descrição e se está bloqueado.</summary>
    public sealed record Mode(IReadOnlyList<string> Path, string Description, bool Disabled)
    {
        public string Label => Path[^1];
        public string Id => string.Join(" › ", Path);
        public static IReadOnlyList<string> ParseId(string id) => id.Split(" › ", StringSplitOptions.RemoveEmptyEntries);
    }

    /// <summary>
    /// Botão do seletor: o nome acessível diz que é o seletor (sondagem de 08/11/2026: Gemini "Abrir seletor de modo. No
    /// momento: …"; Copilot "Auto, Seletor de Modelo"). Português e inglês, conforme o idioma da conta.
    /// </summary>
    public const string LabeledPicker = """
        (() => { const vis = e => e && e.offsetParent !== null;
          return [...document.querySelectorAll('button,[role="button"]')].filter(vis)
            .find(b => /(seletor de modo|seletor de modelo|mode picker|mode selector|model selector|model picker|selecionar modelo|select model)/i.test(b.getAttribute('aria-label') || '')) || null; })()
        """;

    /// <summary>Itens visíveis do menu aberto.</summary>
    public const string GenericItems = """
        [...document.querySelectorAll('[role="menuitemradio"],[role="menuitem"],[role="option"],mat-option,.mat-mdc-menu-item')].filter(e => e.offsetParent !== null)
        """;

    private sealed record Item(string Label, string Description, bool Selected, bool Disabled, bool Radio, bool Submenu)
    {
        public static Item From(JsonElement e)
        {
            string role = e.GetProperty("role").GetString() ?? "", popup = e.GetProperty("popup").GetString() ?? "";
            return new(e.GetProperty("label").GetString()?.Trim() ?? "", e.GetProperty("description").GetString() ?? "",
                e.GetProperty("selected").GetBoolean(), e.GetProperty("disabled").GetBoolean(),
                role is "menuitemradio" or "option", popup is "menu" or "true");
        }
    }

    private readonly string _label, _picker, _items;
    private readonly Func<string, Task<string>> _execute;
    private readonly Func<string, string, Task> _cdp;
    private readonly Func<IReadOnlyList<Mode>> _known;
    private readonly Action<string> _log;

    /// <param name="label">Nome do site nas mensagens ("Microsoft Copilot").</param>
    /// <param name="execute">CoreWebView2.ExecuteScriptAsync da página.</param>
    /// <param name="cdp">CoreWebView2.CallDevToolsProtocolMethodAsync da página (método, parâmetros em JSON).</param>
    /// <param name="known">Modos já conhecidos (para reconhecer o submenu renomeado com o modelo escolhido).</param>
    public WebModePicker(string label, Func<string, Task<string>> execute, Func<string, string, Task> cdp, Func<IReadOnlyList<Mode>> known,
        Action<string> log, string picker = LabeledPicker, string items = GenericItems)
    {
        _label = label; _execute = execute; _cdp = cdp; _known = known; _log = log; _picker = picker.Trim(); _items = items.Trim();
    }

    private async Task<JsonElement> EvalAsync(string script)
    {
        string raw = await _execute(script);
        // ExecuteScriptAsync devolve JSON; os scripts devolvem uma string JSON, então há dupla codificação.
        using var outer = JsonDocument.Parse(raw);
        string inner = outer.RootElement.ValueKind == JsonValueKind.String ? outer.RootElement.GetString() ?? "null" : raw;
        using var doc = JsonDocument.Parse(inner);
        return doc.RootElement.Clone();
    }

    private static readonly JsonSerializerOptions SkipNulls = new() { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull };
    private async Task CdpAsync(string method, object parameters)
    {
        string json = JsonSerializer.Serialize(parameters, SkipNulls);
        try { await _cdp(method, json); }
        catch (Exception ex) { throw new InvalidOperationException(method + ": " + ex.Message.Trim(), ex); }
    }

    private const string Visible = "const vis = e => e && e.offsetParent !== null;";

    // Marcado = aria-checked/aria-selected ou a classe "selected" (a classe "active" é só o item em destaque do teclado).
    private string ItemsScript => "(() => { " + Visible + " const items = " + _items + """
        ; return JSON.stringify(items.map(e => { const lines = (e.innerText || '').split('\n').map(x => x.trim()).filter(x => x);
            return { label: lines[0] || (e.getAttribute('aria-label') || '').trim(), description: lines.slice(1).join(' '),
              selected: e.getAttribute('aria-checked') === 'true' || e.getAttribute('aria-selected') === 'true' || /\bselected\b/i.test(String(e.className)),
              disabled: e.disabled === true || e.getAttribute('aria-disabled') === 'true',
              role: e.getAttribute('role') || '', popup: e.getAttribute('aria-haspopup') || '' }; })); })()
        """;

    private async Task<List<Item>> ItemsAsync() => (await EvalAsync(ItemsScript)).EnumerateArray().Select(Item.From).ToList();

    private string ItemExpr(int index) => "(() => { " + Visible + " return (" + _items + ")[" + index + "] || null; })()";

    /// <summary>Há seletor de modo na página.</summary>
    public async Task<bool> HasPickerAsync()
        => (await EvalAsync("(() => JSON.stringify({ ok: !!(" + _picker + ") }))()")).GetProperty("ok").GetBoolean();

    /// <summary>
    /// Clique de mouse de verdade no centro do elemento, entregue só à página pelo protocolo do WebView2 (o mouse do
    /// Windows não se mexe). Menus que abrem num evento e fecham no seguinte só respondem bem a isso.
    /// </summary>
    private async Task<bool> ClickAsync(string elementExpr)
    {
        var r = await EvalAsync("(() => { const e = " + elementExpr + """
            ; if (!e) return JSON.stringify(null); const b = e.getBoundingClientRect();
              return JSON.stringify({ x: b.left + b.width / 2, y: b.top + b.height / 2, w: b.width }); })()
            """);
        if (r.ValueKind != JsonValueKind.Object || r.GetProperty("w").GetDouble() <= 0) return false;
        double x = Math.Round(r.GetProperty("x").GetDouble(), 1), y = Math.Round(r.GetProperty("y").GetDouble(), 1);
        await CdpAsync("Input.dispatchMouseEvent", new { type = "mouseMoved", x, y });
        await CdpAsync("Input.dispatchMouseEvent", new { type = "mousePressed", x, y, button = "left", clickCount = 1 });
        await CdpAsync("Input.dispatchMouseEvent", new { type = "mouseReleased", x, y, button = "left", clickCount = 1 });
        return true;
    }

    private async Task<bool> MenuOpenAsync() => (await EvalAsync(ItemsScript)).GetArrayLength() > 0;

    private async Task<bool> WaitMenuAsync(int tries, CancellationToken ct)
    {
        for (int i = 0; i < tries; i++)
        {
            await Task.Delay(200, ct);
            if (await MenuOpenAsync()) return true;
        }
        return false;
    }

    private async Task<bool> OpenAsync(CancellationToken ct)
    {
        if (await MenuOpenAsync()) return true; // já aberto (clicar de novo fecharia)
        if (!await ClickAsync(_picker)) return false;
        if (await WaitMenuAsync(10, ct)) return true;
        // Reserva: clique pelo próprio elemento.
        await EvalAsync("(() => { const b = " + _picker + "; if (b) b.click(); return JSON.stringify({ ok: !!b }); })()");
        return await WaitMenuAsync(8, ct);
    }

    /// <summary>Fecha o menu: Esc; se continuar aberto, o próprio botão do seletor (alterna).</summary>
    private async Task CloseAsync()
    {
        for (int attempt = 0; attempt < 3 && await MenuOpenAsync(); attempt++)
        {
            if (attempt == 0)
                foreach (var type in new[] { "rawKeyDown", "keyUp" })
                    await CdpAsync("Input.dispatchKeyEvent", new { type, key = "Escape", code = "Escape", windowsVirtualKeyCode = 27, modifiers = 0 });
            else await ClickAsync(_picker);
            await Task.Delay(350);
        }
    }

    // Oferta de assinatura: diálogo visível que fala de upgrade/assinatura e NÃO é o próprio menu de modos (que pode ter
    // selos de "upgrade" nos modelos pagos).
    private const string UpsellDialog = """
        [...document.querySelectorAll('[role="dialog"],[role="alertdialog"],mat-dialog-container,.cdk-overlay-pane')].filter(e => e && e.offsetParent !== null)
          .filter(d => !d.querySelector('[role="menu"],[role="menuitemradio"],[role="menuitem"]') && !/^(menu|menuitemradio|menuitem)$/.test(d.getAttribute('role') || ''))
          .find(d => /(fazer upgrade|upgrade|assinar|assinatura|subscribe|\/mês|per month)/i.test(d.innerText || ''))
        """;
    private const string UpsellClose = "(() => { const d = " + UpsellDialog + """
        ; return d ? [...d.querySelectorAll('button,a')].find(b => /^(agora não|agora nao|not now|no thanks|não, obrigado|fechar|close|cancelar|cancel)$/i.test((b.innerText || b.getAttribute('aria-label') || '').trim())) || null : null; })()
        """;

    /// <summary>Fecha uma oferta de assinatura aberta na página ("Agora não"). True se havia uma.</summary>
    private async Task<bool> DismissUpsellAsync()
    {
        var found = await EvalAsync("(() => JSON.stringify({ upsell: !!(" + UpsellDialog + ") }))()");
        if (!found.GetProperty("upsell").GetBoolean()) return false;
        if (!await ClickAsync(UpsellClose))
            foreach (var type in new[] { "rawKeyDown", "keyUp" })
                await CdpAsync("Input.dispatchKeyEvent", new { type, key = "Escape", code = "Escape", windowsVirtualKeyCode = 27, modifiers = 0 });
        await Task.Delay(400);
        _log($"{_label}: oferta de assinatura do site fechada com \"Agora não\"");
        return true;
    }

    private static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    /// <summary>Modelos já conhecidos do submenu <paramref name="parent"/> (da última leitura do menu).</summary>
    private HashSet<string> KnownChildren(string parent)
        => _known().Where(m => m.Path.Count == 2 && Same(m.Path[0], parent)).Select(m => m.Path[1]).ToHashSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Posição do item que abre o submenu <paramref name="parent"/>. O site pode trocar o nome dele pelo modelo escolhido
    /// (Copilot: "GPT" vira "GPT-5.6 Sol Resposta rápida"): procura pelo nome, depois por um dos modelos dele e, por fim,
    /// se só há um submenu, é ele.
    /// </summary>
    private int ParentIndex(List<Item> items, string parent)
    {
        int i = items.FindIndex(x => x.Submenu && Same(x.Label, parent));
        if (i >= 0) return i;
        var known = KnownChildren(parent);
        i = items.FindIndex(x => x.Submenu && known.Contains(x.Label));
        if (i >= 0) return i;
        var subs = items.Select((x, k) => (x, k)).Where(t => t.x.Submenu).ToList();
        return subs.Count == 1 ? subs[0].k : -1;
    }

    /// <summary>Abre o submenu do item <paramref name="parent"/> e devolve os itens dele (os que vêm depois do menu principal).</summary>
    private async Task<List<Item>> OpenSubmenuAsync(List<Item> top, string parent, CancellationToken ct)
    {
        int index = ParentIndex(top, parent);
        if (index < 0) return [];
        await ClickAsync(ItemExpr(index));
        var labels = top.Select(t => t.Label).ToHashSet(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < 8; i++)
        {
            await Task.Delay(200, ct);
            var all = await ItemsAsync();
            // Os itens do submenu aparecem depois dos do menu principal (um deles pode ter o mesmo nome do item renomeado).
            var fresh = all.Count > top.Count ? all.Skip(top.Count).ToList() : all.Where(a => !labels.Contains(a.Label)).ToList();
            if (fresh.Count > 0) return fresh;
        }
        return [];
    }

    /// <summary>Lê o menu inteiro: os modos de primeiro nível e os de cada submenu (ex.: "GPT"). Itens que só abrem ofertas ficam de fora.</summary>
    public async Task<(List<Mode> Modes, IReadOnlyList<string>? Selected)> ReadMenuAsync(CancellationToken ct)
    {
        var modes = new List<Mode>();
        IReadOnlyList<string>? selected = null;
        if (!await OpenAsync(ct)) return (modes, null);
        var top = await ItemsAsync();
        foreach (var it in top.Where(i => i.Radio && i.Label.Length > 0))
        {
            modes.Add(new([it.Label], it.Description, it.Disabled));
            if (it.Selected) selected = [it.Label];
        }
        foreach (var parent in top.Where(i => i.Submenu && i.Label.Length > 0))
        {
            if (!await OpenAsync(ct)) break;
            // Submenu renomeado com o modelo escolhido (Copilot: "GPT" → "GPT-5.6 Sol …"): mantém o nome conhecido.
            string parentName = _known().Where(m => m.Path.Count == 2 && Same(m.Path[1], parent.Label)).Select(m => m.Path[0]).FirstOrDefault() ?? parent.Label;
            foreach (var child in (await OpenSubmenuAsync(top, parent.Label, ct)).Where(c => c.Radio && c.Label.Length > 0))
            {
                modes.Add(new([parentName, child.Label], child.Description.Length > 0 ? child.Description : parent.Description, child.Disabled));
                if (child.Selected || Same(child.Label, parent.Label)) selected = [parentName, child.Label];
            }
            await CloseAsync();
            await Task.Delay(400, ct);
        }
        await CloseAsync();
        return (modes.DistinctBy(m => m.Id).ToList(), selected);
    }

    /// <summary>
    /// Escolhe o modo na página. Devolve o erro (null quando deu certo) e o modo que estava escolhido antes, quando houve
    /// troca (para devolver depois da resposta).
    /// </summary>
    public async Task<(string? Error, IReadOnlyList<string>? Previous)> SelectAsync(IReadOnlyList<string> path, CancellationToken ct)
    {
        if (path.Count == 0) return (null, null);
        if (!await OpenAsync(ct)) return ($"Seletor de modo do {_label} não encontrado na página.", null);
        var top = await ItemsAsync();
        IReadOnlyList<string>? previous = top.FirstOrDefault(i => i.Radio && i.Selected) is { } sel ? [sel.Label] : null;
        // Submenu renomeado com o modelo escolhido: é o modo atual (para devolver depois, ou já é o pedido).
        if (previous is null)
            foreach (var parent in _known().Where(m => m.Path.Count == 2).Select(m => m.Path[0]).Distinct(StringComparer.OrdinalIgnoreCase))
                if (top.FirstOrDefault(i => i.Submenu && KnownChildren(parent).Contains(i.Label)) is { } renamed) { previous = [parent, renamed.Label]; break; }
        if (path.Count == 2 && top.Any(i => i.Submenu && Same(i.Label, path[1]))) { await CloseAsync(); return (null, null); }
        Item? target;
        if (path.Count == 1) target = top.FirstOrDefault(i => i.Radio && Same(i.Label, path[0]));
        else
        {
            var children = await OpenSubmenuAsync(top, path[0], ct);
            if (children.FirstOrDefault(c => c.Selected) is { } chosenChild) previous = [path[0], chosenChild.Label];
            target = children.FirstOrDefault(c => c.Radio && Same(c.Label, path[1]));
        }
        if (target is null)
        {
            await CloseAsync();
            return ($"O modo \"{string.Join(" › ", path)}\" não existe mais no {_label}. Na WinStatusBar, abra Configurações › Modelos e clique em Verificar e atualizar.", null);
        }
        if (target.Disabled) { await CloseAsync(); return ($"O modo \"{target.Label}\" está indisponível na sua conta do {_label}.", null); }
        if (target.Selected) { await CloseAsync(); return (null, null); }
        // Os modelos de um submenu vêm depois do menu principal: procura de trás para frente.
        var visible = await ItemsAsync();
        int index = path.Count == 2 ? visible.FindLastIndex(i => i.Radio && Same(i.Label, target.Label)) : visible.FindIndex(i => i.Radio && Same(i.Label, target.Label));
        await ClickAsync(ItemExpr(index));
        await Task.Delay(700, ct);
        // Modo pago: o site abre uma oferta de assinatura em vez de trocar. Fecha com "Agora não" (nunca assina).
        if (await DismissUpsellAsync())
        {
            await CloseAsync();
            return ($"O modo \"{target.Label}\" exige assinatura no {_label} (o site ofereceu o plano pago; nada foi assinado). Escolha outro modo.", null);
        }
        await CloseAsync();
        // Confere relendo o menu: o modo pedido precisa aparecer marcado.
        bool confirmed = false;
        string seen = "menu não reabriu";
        if (await OpenAsync(ct))
        {
            var again = await ItemsAsync();
            List<Item> children = [];
            if (path.Count == 1) confirmed = again.Any(i => i.Selected && Same(i.Label, path[0]));
            // Submenu: o item renomeado com o modelo escolhido já confirma; senão, o modelo aparece marcado dentro dele.
            else confirmed = again.Any(i => i.Submenu && Same(i.Label, path[1]))
                || (children = await OpenSubmenuAsync(again, path[0], ct)).Any(c => c.Selected && Same(c.Label, path[1]));
            seen = string.Join(", ", again.Concat(children).Select(i => i.Label + (i.Selected ? " [x]" : "") + (i.Submenu ? " >" : "")));
            await CloseAsync();
        }
        if (!confirmed) _log($"{_label}: conferência do modo \"{string.Join(" › ", path)}\" falhou; menu: {seen}");
        if (!confirmed) return ($"Não foi possível escolher o modo \"{target.Label}\" no {_label}. Nada foi enviado.", previous);
        _log($"{_label}: modo escolhido: {string.Join(" › ", path)}");
        return (null, previous);
    }
}
