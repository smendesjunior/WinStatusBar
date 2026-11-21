using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace WinStatusBar.Core.Ai.Antigravity;

/// <summary>
/// Cota do Google Antigravity (limite semanal restante de cada grupo de modelos), lida da tela "Models &amp; Quota" que o
/// próprio agy mostra com /usage. O agy abre num console invisível (ConPTY) na pasta de trabalho da barra, com o login
/// dele; nada é enviado a nenhum modelo, então a leitura não gasta cota. Também traz a conta e o plano do login.
/// </summary>
public static class AntigravityQuota
{
    /// <summary>Um grupo de modelos que divide o mesmo limite (ex.: "Gemini"; "Claude e GPT").</summary>
    public sealed record Group(string Name, string Models, string Limit, double RemainingPercent, string? Refresh);

    /// <summary>Leitura: conta, plano, grupos. SignedIn=false quando o agy pediu login; Error quando não deu para ler.</summary>
    public sealed record Snapshot(string? Account, string? Plan, List<Group> Groups, DateTimeOffset Updated, bool SignedIn = true, string? Error = null);

    private static readonly string CacheFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WinStatusBar", "antigravity-quota.json");

    /// <summary>Última leitura guardada (sobrevive ao reinício da barra).</summary>
    public static Snapshot? Load()
    {
        try { return File.Exists(CacheFile) ? JsonSerializer.Deserialize<Snapshot>(File.ReadAllText(CacheFile)) : null; } catch { return null; }
    }

    public static void Save(Snapshot snapshot)
    {
        try { Directory.CreateDirectory(Path.GetDirectoryName(CacheFile)!); File.WriteAllText(CacheFile, JsonSerializer.Serialize(snapshot)); } catch { }
    }

    // ---------------------------------------------------------------- texto da tela
    private static readonly Regex Csi = new(@"\x1b\[[0-9;?<>=]*[ -/]*[@-~]", RegexOptions.Compiled);
    private static readonly Regex Osc = new(@"\x1b\][^\x07\x1b]*(?:\x07|\x1b\\)", RegexOptions.Compiled);

    /// <summary>Tela do terminal → texto: posicionamento do cursor vira quebra de linha; cores e o resto somem.</summary>
    public static string Clean(string raw)
    {
        string s = Osc.Replace(raw, "");
        s = Csi.Replace(s, m => m.Value[^1] is 'H' or 'f' ? "\n" : m.Value[^1] == 'C' ? " " : "");
        s = Regex.Replace(s, @"\x1b[()][A-Z0-9]|\x1b[=>78]", "");
        return s.Replace("\r", "\n");
    }

    private static string Pretty(string header)
    {
        string name = Regex.Replace(header.Trim(), @"\s+MODELS?$", "", RegexOptions.IgnoreCase);
        name = CultureInfo.InvariantCulture.TextInfo.ToTitleCase(name.ToLowerInvariant());
        name = Regex.Replace(name, @"\bAnd\b", "e");
        return Regex.Replace(name, @"\bGpt\b", "GPT");
    }

    /// <summary>"167h 29m" → "6 d 23 h"; "3h 5m" → "3 h 5 min"; outros textos passam como vieram.</summary>
    public static string ShortRefresh(string refresh)
    {
        var m = Regex.Match(refresh, @"(?:(\d+)\s*d\s*)?(?:(\d+)\s*h\s*)?(?:(\d+)\s*m)?", RegexOptions.IgnoreCase);
        if (!m.Success || m.Length == 0) return refresh;
        int days = m.Groups[1].Success ? int.Parse(m.Groups[1].Value) : 0;
        int hours = m.Groups[2].Success ? int.Parse(m.Groups[2].Value) : 0;
        int minutes = m.Groups[3].Success ? int.Parse(m.Groups[3].Value) : 0;
        days += hours / 24; hours %= 24;
        return days > 0 ? $"{days} d {hours} h" : hours > 0 ? $"{hours} h {minutes} min" : $"{minutes} min";
    }

    /// <summary>Lê a tela (já limpa ou não): conta e plano do cabeçalho e os grupos do último painel "Models &amp; Quota".</summary>
    public static Snapshot? Parse(string screen)
    {
        string text = screen.Contains('\x1b') ? Clean(screen) : screen.Replace("\r", "\n");
        string? account = null, plan = null;
        // Cabeçalho: "conta (plano)". O agy desenha o plano depois, noutra posição da tela, então cada um é lido à parte.
        if (Regex.Match(text, @"[\w.+-]+@[\w-]+(?:\.[\w-]+)+") is { Success: true } mail) account = mail.Value;
        if (Regex.Match(text, @"\(((?:Antigravity|Google AI)[^()\n]{1,50}|[^()\n]{0,40}\bQuota\b[^()\n]{0,20})\)") is { Success: true } p) plan = p.Groups[1].Value.Trim();
        int panel = text.LastIndexOf("Models & Quota", StringComparison.OrdinalIgnoreCase);
        if (panel < 0) return null;
        string body = text[panel..];
        if (Regex.Match(body, @"Account:\s*([\w.+-]+@[\w-]+(?:\.[\w-]+)+)") is { Success: true } acc) account = acc.Groups[1].Value;

        var groups = new List<Group>();
        string? name = null, models = null, limit = null; double? remaining = null;
        void Flush(string? refresh)
        {
            if (name is not null && remaining is { } r) groups.Add(new(name, models ?? "", limit ?? "Limite", r, refresh));
            remaining = null; limit = null;
        }
        foreach (var rawLine in body.Split('\n'))
        {
            string line = rawLine.Trim().Trim('│', '┃', '└', ' ').Trim();
            if (line.Length == 0) continue;
            if (Regex.IsMatch(line, @"^[A-Z][A-Z0-9 &/+.-]*MODELS?$")) { if (remaining is not null) Flush(null); name = Pretty(line); models = null; continue; }
            if (Regex.Match(line, @"^Models within this group:\s*(.+)$", RegexOptions.IgnoreCase) is { Success: true } mm) { models = mm.Groups[1].Value.Trim(); continue; }
            if (Regex.Match(line, @"^(.*?Limit)\s+Remaining", RegexOptions.IgnoreCase) is { Success: true } lm) { limit = lm.Groups[1].Value.Trim(); continue; }
            if (Regex.Match(line, @"(\d{1,3}(?:[.,]\d+)?)\s*%\s*$") is { Success: true } pm && name is not null)
            {
                if (remaining is not null) Flush(null);
                remaining = double.Parse(pm.Groups[1].Value.Replace(',', '.'), CultureInfo.InvariantCulture);
                continue;
            }
            if (Regex.Match(line, @"^Refreshes in\s+(.+)$", RegexOptions.IgnoreCase) is { Success: true } rm) { Flush(rm.Groups[1].Value.Trim()); continue; }
        }
        if (remaining is not null) Flush(null);
        return groups.Count == 0 ? null : new(account, plan, groups, DateTimeOffset.Now);
    }

    /// <summary>A tela pede login (sem conta conectada no agy).</summary>
    public static bool AsksForLogin(string cleanText)
        // Textos do agy 1.5.9: "You are currently not signed in.", "You are not logged into…", "Log in with your…",
        // "Please sign in to view available models."
        => Regex.IsMatch(cleanText, @"sign in with google|log ?in with (google|your)|sign in to (continue|antigravity|view)|currently not signed in|not (signed|logged) in(to)?\b|please sign in|choose (a|an) (login|sign-in|authentication) method", RegexOptions.IgnoreCase);

    // ---------------------------------------------------------------- leitura
    private static readonly SemaphoreSlim Gate = new(1, 1);

    /// <summary>
    /// Abre o agy escondido, pede /usage e lê o painel (até ~45 s). Só a pasta de trabalho da barra recebe o "confiar
    /// nesta pasta" do primeiro uso. Null: o agy não está instalado.
    /// </summary>
    public static async Task<Snapshot?> ReadAsync(CancellationToken ct)
    {
        if (AntigravityCli.FindExecutable() is not { } exe) return null;
        await Gate.WaitAsync(ct);
        try { return await Task.Run(() => ReadCore(exe, ct), ct); }
        finally { Gate.Release(); }
    }

    private static Snapshot ReadCore(string exe, CancellationToken ct)
    {
        string work = AntigravityCli.WorkFolder();
        var clock = Stopwatch.StartNew();
        // O modo sem navegador mantém o terminal interativo necessário para /usage.
        using var console = PseudoConsole.Start($"\"{exe}\"", work, environmentOverrides: new Dictionary<string, string>
        {
            ["AGY_CLI_INTERACTIVE_HEADLESS"] = "1"
        });
        bool trusted = false, asked = false;
        try
        {
            // 1) Espera o campo de pedido ("? for shortcuts").
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                Thread.Sleep(300);
                string text = Clean(console.Output);
                if (!trusted && text.Contains("Do you trust the contents of this project", StringComparison.OrdinalIgnoreCase)
                    && (text.Contains(work.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase) || text.Contains(work, StringComparison.OrdinalIgnoreCase)))
                { console.Write("\r"); trusted = true; continue; }
                if (AsksForLogin(text)) return SignedOut();
                if (text.Contains("for shortcuts", StringComparison.OrdinalIgnoreCase)) break;
                if (console.HasExited) return Fail("o agy fechou antes de abrir", text);
                if (clock.Elapsed > TimeSpan.FromSeconds(30)) return Fail("o agy não abriu em 30 s", text);
            }
            // 2) /usage e espera o painel completo (todas as linhas "Refreshes in" e a tela parada).
            int panelFrom = console.Output.Length;
            Thread.Sleep(500);
            console.Write("/usage");
            Thread.Sleep(800);
            console.Write("\r");
            asked = true;
            string last = "";
            int stable = 0;
            while (clock.Elapsed < TimeSpan.FromSeconds(45))
            {
                ct.ThrowIfCancellationRequested();
                Thread.Sleep(500);
                string now = console.Output;
                if (now.Length > panelFrom && AsksForLogin(Clean(now[panelFrom..]))) return SignedOut();
                if (now.Length > panelFrom && Clean(now[panelFrom..]).Contains("Refreshes in", StringComparison.OrdinalIgnoreCase))
                {
                    stable = now == last ? stable + 1 : 0;
                    if (stable >= 3) break;
                }
                last = now;
            }
            var snapshot = Parse(console.Output);
            return snapshot ?? Fail("o painel de cota do agy não apareceu", Clean(console.Output));
        }
        finally
        {
            // Fecha o painel e sai; o Dispose encerra o processo se ainda estiver aberto.
            if (asked)
            {
                console.Write("\x1b");
                Thread.Sleep(300);
                console.Write("/exit\r");
                Thread.Sleep(500);
            }
        }

        static Snapshot SignedOut() => new(null, null, [], DateTimeOffset.Now, SignedIn: false, Error: "o Antigravity está sem login");

        static Snapshot Fail(string why, string text)
        {
            string tail = string.Join(" | ", text.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).TakeLast(6));
            return new(null, null, [], DateTimeOffset.Now, Error: why + (tail.Length > 0 ? $" (tela: {(tail.Length > 300 ? tail[^300..] : tail)})" : ""));
        }
    }

    /// <summary>Texto curto para o menu: "Antigravity: Gemini 92 % · Claude e GPT 96 % restantes".</summary>
    public static string MenuText(Snapshot s, bool remaining)
        => "Antigravity: " + string.Join(" · ", s.Groups.Select(g => $"{g.Name} {Math.Round(remaining ? g.RemainingPercent : 100 - g.RemainingPercent):0} %"))
           + (remaining ? " restantes" : " usados");
}
