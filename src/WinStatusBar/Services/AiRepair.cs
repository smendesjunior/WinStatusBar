using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using WinStatusBar.Core.Ai;
using WinStatusBar.Core.Ai.Codex;
using WinStatusBar.Core.Integration;

namespace WinStatusBar.Services;

/// <summary>
/// Corrigir problemas das IAs (menu "…" da barra e Configurações › Codex e modelos). Confere, em sequência, tudo de que as IAs da
/// barra e do Codex dependem e conserta sozinho o que dá:
/// - integrações da barra (atividade do Claude Code e do Codex) e a ligação do Claude Code com a barra;
/// - encaminhador do Codex (porta 11435): parado, travado, porta tomada pelo encaminhador antigo, config.toml alterado por
///   outro programa, IAs da barra fora do seletor, início com o Windows apagado;
/// - ponte antiga da barra (porta 11437), Ollama parado, Lenovo Qira (serviço de modelos, núcleo travado, ponte 9339);
/// - logins do Gemini e do Copilot, WebView2 e, no fim, a verificação dos modelos (listas da barra e do Codex).
/// O que depende da pessoa (login, administrador) volta como pendência, com o botão certo.
/// "Refazer todas as ligações" (<c>force</c>) refaz tudo mesmo sem erro detectado: salvaguarda para falhas que a
/// conferência não enxerga.
/// </summary>
internal static class AiRepair
{
    public enum Outcome { Ok, Fixed, NeedsYou, Failed, Skipped }

    /// <param name="Action">Botão da linha: "login-gemini", "login-copilot", "qira-ponte", "codex-ligar" ou "verificar:&lt;id&gt;".</param>
    public sealed record Step(string Area, Outcome Outcome, string Detail, string? Action = null);

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(3) };
    private static readonly SemaphoreSlim Running = new(1, 1);
    public static bool Busy => Running.CurrentCount == 0;

    /// <summary>Roda a correção. Null quando outra já está em andamento.</summary>
    public static async Task<List<Step>?> RunAsync(bool force, Action<string> progress, CancellationToken ct)
    {
        if (!await Running.WaitAsync(0, ct)) return null;
        try
        {
            App.Log(force ? "Refazer todas as ligações: início" : "Corrigir problemas das IAs: início");
            var steps = new List<Step>();
            progress("Reiniciando as integrações da barra…");
            steps.Add(Integrations());
            progress("Conferindo o Claude Code…");
            steps.Add(await Task.Run(() => ClaudeHooks(force), ct));
            progress("Conferindo a ponte antiga da barra (porta 11437)…");
            steps.Add(await OldBridgeAsync(force, ct));
            progress("Conferindo o Ollama…");
            steps.Add(await OllamaAsync(ct));
            progress("Conferindo o Lenovo Qira…");
            // Qira: confere de verdade (página e núcleo) e reinicia só se não responder; um Qira saudável não é derrubado.
            steps.AddRange(await QiraHubSession.RepairAsync(ct));
            steps.AddRange(await WebSitesAsync(force));
            steps.Add(await Task.Run(WebView2Runtime, ct));
            progress("Conferindo a ligação com o Codex (porta 11435)…");
            var (codex, verified) = await CodexAsync(force, progress, ct);
            steps.AddRange(codex);
            if (!verified)
            {
                progress("Verificando os modelos de novo (Gemini e Copilot são lidos dos próprios sites)…");
                var result = await CodexIasComponent.RunAsync(["--verificar"], ct);
                CodexIasComponent.NotifyModelsChanged();
                if (!result.Ok) steps.Add(new("Listas de modelos", Outcome.Failed, "A verificação dos modelos falhou: " + result.Mensagem));
            }
            else CodexIasComponent.NotifyModelsChanged();
            steps.AddRange(FromReport(steps).ToList()); // lista pronta antes de acrescentar (FromReport lê as etapas)
            App.Log($"Correção das IAs: {steps.Count(s => s.Outcome == Outcome.Fixed)} corrigidos, {steps.Count(s => s.Outcome == Outcome.NeedsYou)} pendências, {steps.Count(s => s.Outcome == Outcome.Failed)} falhas");
            return steps;
        }
        finally { Running.Release(); }
    }

    // ------------------------------------------------------------------ integrações da barra
    private static Step Integrations()
    {
        try
        {
            App.Current.Hub.RestartIntegrations();
            return new("Integrações da barra", Outcome.Fixed, "Reiniciadas (atividade do Claude Code e cotas e tarefas do Codex na barra).");
        }
        catch (Exception ex) { return new("Integrações da barra", Outcome.Failed, "Não reiniciaram: " + ex.Message); }
    }

    private static Step ClaudeHooks(bool force)
    {
        const string area = "Claude Code";
        if (AgentPrompt.FindClaudeExecutable() is null) return new(area, Outcome.Skipped, "Não instalado neste PC.");
        string collector = Path.Combine(AppContext.BaseDirectory, "WinStatusBar.Collector.exe");
        try
        {
            bool installed = ClaudeIntegrationInstaller.IsInstalled();
            if (installed && !force) return new(area, Outcome.Ok, "Ligação do Claude Code com a barra ativa.");
            if (!File.Exists(collector)) return new(area, installed ? Outcome.Ok : Outcome.Failed, installed ? "Ligação com a barra ativa." : "O coletor da barra não está nesta instalação; reinstale a barra.");
            ClaudeIntegrationInstaller.Install(collector);
            return new(area, Outcome.Fixed, installed ? "Ligação do Claude Code com a barra refeita." : "A ligação do Claude Code com a barra tinha sumido e foi refeita (com cópia do settings.json).");
        }
        catch (Exception ex) { return new(area, Outcome.Failed, "Ligação com a barra não refeita: " + ex.Message); }
    }

    // ------------------------------------------------------------------ ponte antiga (11437)
    private static async Task<bool> PortOpenAsync(int port, CancellationToken ct)
    {
        try
        {
            using var tcp = new TcpClient();
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
            limit.CancelAfter(TimeSpan.FromSeconds(2));
            await tcp.ConnectAsync("127.0.0.1", port, limit.Token);
            return true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch { return false; }
    }

    private static async Task<Step> OldBridgeAsync(bool force, CancellationToken ct)
    {
        const string area = "Ponte antiga da barra (11437)";
        int? port = null;
        try { using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(CodexBridgeHost.InfoPath)); port = doc.RootElement.GetProperty("port").GetInt32(); } catch { }
        if (!force && port is { } p && await PortOpenAsync(p, ct)) return new(area, Outcome.Ok, $"Respondendo na porta {p} (conversas antigas do Codex).");
        CodexBridgeHost.Stop();
        CodexBridgeHost.Start();
        port = null;
        try { using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(CodexBridgeHost.InfoPath)); port = doc.RootElement.GetProperty("port").GetInt32(); } catch { }
        return port is { } q && await PortOpenAsync(q, ct)
            ? new(area, Outcome.Fixed, force ? $"Reiniciada (porta {q})." : $"Não respondia e foi reiniciada (porta {q}).")
            : new(area, Outcome.Failed, "Não reiniciou: nenhuma porta livre. Feche e abra a barra.");
    }

    // ------------------------------------------------------------------ Ollama
    private static string? OllamaExe()
    {
        string local = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Ollama", "ollama.exe");
        if (File.Exists(local)) return local;
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries))
            if (File.Exists(Path.Combine(dir.Trim(), "ollama.exe"))) return Path.Combine(dir.Trim(), "ollama.exe");
        return null;
    }

    private static async Task<bool> OllamaUpAsync(CancellationToken ct)
    {
        try { using var r = await Http.GetAsync("http://127.0.0.1:11434/api/version", ct); return r.IsSuccessStatusCode; }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch { return false; }
    }

    private static async Task<Step> OllamaAsync(CancellationToken ct)
    {
        const string area = "Ollama (IA local)";
        string? exe = OllamaExe();
        if (await OllamaUpAsync(ct)) return new(area, Outcome.Ok, "Respondendo (porta 11434).");
        if (exe is null) return new(area, Outcome.Skipped, "Não instalado neste PC.");
        try
        {
            // O app da bandeja do Ollama sobe o servidor; sem ele, o próprio servidor.
            string app = Path.Combine(Path.GetDirectoryName(exe)!, "ollama app.exe");
            if (File.Exists(app)) Process.Start(new ProcessStartInfo(app) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(app)! })?.Dispose();
            else Process.Start(new ProcessStartInfo(exe, "serve") { UseShellExecute = false, CreateNoWindow = true })?.Dispose();
        }
        catch (Exception ex) { return new(area, Outcome.Failed, "Estava parado e não abriu: " + ex.Message); }
        for (int i = 0; i < 40; i++)
        {
            await Task.Delay(750, ct);
            if (await OllamaUpAsync(ct)) return new(area, Outcome.Fixed, "Estava parado e foi aberto.");
        }
        return new(area, Outcome.Failed, "Estava parado e não respondeu em 30 s. Abra o Ollama pelo menu Iniciar.");
    }

    // ------------------------------------------------------------------ Gemini e Copilot (páginas da barra)
    private static string Profile(string folder) => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WinStatusBar", folder, "Profile");

    private static async Task<List<Step>> WebSitesAsync(bool force)
    {
        var steps = new List<Step>();
        foreach (var (area, folder, signedIn, reset, action) in new (string, string, bool, Func<Task<bool>>, string)[]
                 {
                     ("Gemini (conta Google)", "GoogleWeb", GeminiWebSession.SignedIn, GeminiWebSession.ResetAsync, "login-gemini"),
                     ("Microsoft Copilot", "CopilotWeb", CopilotWebSession.SignedIn, CopilotWebSession.ResetAsync, "login-copilot"),
                 })
        {
            if (!Directory.Exists(Profile(folder))) { steps.Add(new(area, Outcome.Skipped, "Não usado neste PC (sem login na barra).")); continue; }
            if (!signedIn) { steps.Add(new(area, Outcome.NeedsYou, "Sem login na barra: entre na conta para usar na barra e no Codex.", action)); continue; }
            if (!force) { steps.Add(new(area, Outcome.Ok, "Com login.")); continue; }
            bool done = false;
            try { done = await reset(); } catch { }
            steps.Add(done ? new(area, Outcome.Fixed, "Página escondida reaberta (a próxima pergunta abre uma nova, com o mesmo login).")
                : new(area, Outcome.Skipped, "Respondendo agora ou com a janela de login aberta; a página não foi reaberta."));
        }
        return steps;
    }

    private static Step WebView2Runtime()
    {
        const string client = @"Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}";
        foreach (var (hive, path) in new[] { (Microsoft.Win32.Registry.LocalMachine, @"SOFTWARE\WOW6432Node\" + client), (Microsoft.Win32.Registry.LocalMachine, @"SOFTWARE\" + client), (Microsoft.Win32.Registry.CurrentUser, @"Software\" + client) })
        {
            try
            {
                using var key = hive.OpenSubKey(path);
                if (key?.GetValue("pv") is string pv && pv.Length > 0 && pv != "0.0.0.0") return new("WebView2 (páginas do Gemini, Copilot e Qira)", Outcome.Ok, "Instalado (versão " + pv + ").");
            }
            catch { }
        }
        return new("WebView2 (páginas do Gemini, Copilot e Qira)", Outcome.NeedsYou, "O Microsoft Edge WebView2 Runtime não foi encontrado. Instale a versão mais nova da barra (o instalador o instala sozinho).");
    }

    // ------------------------------------------------------------------ ligação com o Codex
    private static string? TomlValue(string top, string key)
    {
        var m = Regex.Match(top, $@"(?m)^\s*{key}\s*=\s*(?:""((?:[^""\\]|\\.)*)""|'([^']*)')");
        if (!m.Success) return null;
        return m.Groups[1].Success ? m.Groups[1].Value.Replace(@"\\", @"\") : m.Groups[2].Value;
    }

    [System.Runtime.InteropServices.DllImport("iphlpapi.dll")]
    private static extern uint GetExtendedTcpTable(IntPtr table, ref int size, bool order, int family, int tableClass, uint reserved);

    /// <summary>
    /// Programa que escuta na porta (tabela de portas do Windows, por processo; não depende do idioma como o netstat):
    /// pid e nome; null se ninguém escuta.
    /// </summary>
    private static (int Pid, string Name)? PortOwner(int port)
    {
        const int AfInet = 2, TcpTableOwnerPidListener = 3, RowSize = 24;
        int size = 0;
        GetExtendedTcpTable(IntPtr.Zero, ref size, false, AfInet, TcpTableOwnerPidListener, 0);
        if (size <= 0) return null;
        IntPtr buffer = System.Runtime.InteropServices.Marshal.AllocHGlobal(size);
        try
        {
            if (GetExtendedTcpTable(buffer, ref size, false, AfInet, TcpTableOwnerPidListener, 0) != 0) return null;
            int count = System.Runtime.InteropServices.Marshal.ReadInt32(buffer);
            for (int i = 0; i < count; i++)
            {
                IntPtr row = buffer + 4 + i * RowSize;
                int raw = System.Runtime.InteropServices.Marshal.ReadInt32(row, 8); // porta na ordem da rede, nos 2 bytes de baixo
                int local = ((raw & 0xFF) << 8) | ((raw >> 8) & 0xFF);
                if (local != port) continue;
                int pid = System.Runtime.InteropServices.Marshal.ReadInt32(row, 20);
                if (pid <= 4) continue;
                try { using var proc = Process.GetProcessById(pid); return (pid, proc.ProcessName); } catch { return (pid, "?"); }
            }
        }
        finally { System.Runtime.InteropServices.Marshal.FreeHGlobal(buffer); }
        return null;
    }

    private static bool RunEntryPresent()
    {
        try { using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run"); return key?.GetValue("WinStatusBar.CodexIAs") is string; }
        catch { return false; }
    }

    /// <summary>Início automático do encaminhador antigo em Python (pasta Inicializar): guardado em "desativado".</summary>
    private static bool DisableOldGatewayStartup()
    {
        bool any = false;
        try
        {
            string startup = Environment.GetFolderPath(Environment.SpecialFolder.Startup);
            string keep = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OpenAI", "CodexApiGateway", "desativado");
            foreach (var file in Directory.EnumerateFiles(startup, "*.vbs").Concat(Directory.EnumerateFiles(startup, "*.cmd")))
            {
                string text = "";
                try { text = File.ReadAllText(file); } catch { }
                if (!text.Contains("CodexApiGateway", StringComparison.OrdinalIgnoreCase)) continue;
                Directory.CreateDirectory(keep);
                File.Move(file, Path.Combine(keep, Path.GetFileName(file)), overwrite: true);
                any = true;
            }
        }
        catch { }
        return any;
    }

    /// <summary>Devolve as etapas da ligação com o Codex e se a verificação dos modelos já rodou (o --ligar verifica).</summary>
    private static async Task<(List<Step> Steps, bool Verified)> CodexAsync(bool force, Action<string> progress, CancellationToken ct)
    {
        const string area = "Ligação com o Codex";
        if (!CodexIasComponent.Available) return ([new(area, Outcome.Skipped, "Componente das IAs no Codex ausente nesta instalação da barra.")], false);
        var state = CodexIntegration.LoadState();
        string cfgFile = Path.Combine(CodexAppServerClient.CodexHome(), "config.toml");
        string top = File.Exists(cfgFile) ? File.ReadAllText(cfgFile).Split("\n[", 2)[0] : "";
        string? baseUrl = TomlValue(top, "openai_base_url"), catalog = TomlValue(top, "model_catalog_json");
        bool pointsToGateway = baseUrl is not null && Regex.IsMatch(baseUrl, @"^https?://(127\.0\.0\.1|localhost):11435(/|$)", RegexOptions.IgnoreCase);
        if (!state.Ligado && !pointsToGateway)
            return ([new(area, Outcome.Skipped, "As IAs da barra estão desligadas no Codex (ligue em Configurações › Codex e modelos, se quiser).")], false);

        var problems = new List<string>();
        if (!state.Ligado) problems.Add("o Codex apontava para o encaminhador com a ligação desligada (ex.: configuração restaurada)");
        bool online = await CodexIasComponent.GatewayOnlineAsync();
        if (!online || force)
        {
            // Respondendo, a porta é do próprio encaminhador; parado, confere quem a ocupa.
            var owner = online ? null : await Task.Run(() => PortOwner(CodexIntegration.GatewayPort), ct);
            if (owner is { } o && o.Name is "python" or "pythonw" or "py")
            {
                try { using var old = Process.GetProcessById(o.Pid); old.Kill(entireProcessTree: true); } catch { }
                problems.Add("o encaminhador antigo, em Python, ocupava a porta 11435 e foi encerrado");
                if (DisableOldGatewayStartup()) problems.Add("o início automático dele foi guardado em \"desativado\"");
            }
            else if (owner is { } other && other.Name != CodexIntegration.ProcessName)
                return ([new(area, Outcome.NeedsYou, $"A porta 11435 está em uso por outro programa ({other.Name}, pid {other.Pid}). Feche esse programa e rode a correção de novo.")], false);
            if (!online) problems.Add("o encaminhador não respondia");
            // Encaminhador da barra parado de vez (travado ou para refazer): encerra; o --ligar o inicia de novo.
            foreach (var p in Process.GetProcessesByName(CodexIntegration.ProcessName))
                using (p) { try { if (p.Id != Environment.ProcessId) p.Kill(); } catch { } }
            if (force && online) problems.Add("encaminhador reiniciado");
            await Task.Delay(800, ct);
        }
        if (!pointsToGateway) problems.Add("o config.toml do Codex não apontava mais para o encaminhador (outro programa o alterou, como um novo \"ollama launch\")");
        if (catalog is null || !File.Exists(catalog)) problems.Add("o catálogo de modelos do Codex não foi encontrado");
        else if (!File.ReadAllText(catalog).Contains("\"codexias-", StringComparison.Ordinal)) problems.Add("as IAs da barra tinham sumido do seletor do Codex");
        if (!RunEntryPresent()) problems.Add("o início com o Windows tinha sumido");
        if (problems.Count == 0 && !force) return ([new(area, Outcome.Ok, "Encaminhador respondendo (porta 11435); config.toml, seletor do Codex e início com o Windows em ordem.")], false);

        progress(force ? "Refazendo a ligação com o Codex (config.toml com cópia, encaminhador e modelos)…" : "Corrigindo a ligação com o Codex…");
        var result = await CodexIasComponent.RunAsync(["--ligar"], ct);
        bool ok = result.Ok && await CodexIasComponent.GatewayOnlineAsync();
        string what = problems.Count > 0 ? string.Join("; ", problems) : "ligação refeita";
        return ([ok ? new(area, Outcome.Fixed, char.ToUpper(what[0]) + what[1..] + ". Ligação refeita: config.toml, encaminhador e modelos.")
                    : new(area, Outcome.Failed, char.ToUpper(what[0]) + what[1..] + ". A ligação não foi refeita: " + result.Mensagem, "codex-ligar")], true);
    }

    // ------------------------------------------------------------------ relatório da verificação
    /// <summary>IAs que falharam ou pedem algo na verificação dos modelos (sem repetir o que já está na lista).</summary>
    private static IEnumerable<Step> FromReport(List<Step> done)
    {
        var report = CodexIntegration.LoadReport();
        if (report is null) yield break;
        foreach (var s in report.Servicos)
        {
            if (s.Falhou)
                yield return new(s.Nome, Outcome.Failed, $"Não foi possível ler agora: {s.Motivo}.", s.Id is { Length: > 0 } id ? "verificar:" + id : null);
            else if (s.Id == "claude" && s.Total == 0 && s.Motivo is { } why && why.Contains("conta", StringComparison.OrdinalIgnoreCase))
                yield return new(s.Nome, Outcome.NeedsYou, "Claude Code sem conta conectada: abra um terminal, rode \"claude\" e entre na conta (/login).");
            else if (s.Login is "gemini" or "copilot" && done.All(d => d.Action != "login-" + s.Login))
                yield return new(s.Nome, Outcome.NeedsYou, char.ToUpper(s.Motivo![0]) + s.Motivo[1..] + ".", "login-" + s.Login);
        }
        int ok = report.Servicos.Count(s => s.Total > 0 && !s.Falhou);
        yield return new("Listas de modelos", Outcome.Ok, $"Modelos verificados: {ok} IAs com opções nas listas da barra{(report.Codex.Ligado ? " e no seletor do Codex" : "")}.");
    }

    /// <summary>Ativa a ponte do Qira (administrador) e confere o Qira de novo.</summary>
    public static Task<string> EnableQiraBridgeAsync() => QiraHubSession.EnableBridgeAsync();
}
