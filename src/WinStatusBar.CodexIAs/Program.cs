using System.Diagnostics;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json.Nodes;
using CodexIAs.Providers;
using WinStatusBar.Core.Ai.Codex;

namespace CodexIAs;

/// <summary>
/// IAs da barra no Codex (parte do pacote da WinStatusBar, na pasta Automation). Sem janela.
///   WinStatusBar.CodexIAs.exe                     encaminhador do Codex (porta 11435; só com a ligação ligada)
///   WinStatusBar.CodexIAs.exe --ligar             liga no Codex (config.toml com cópia), inicia e verifica os modelos
///   WinStatusBar.CodexIAs.exe --desligar          desfaz a ligação (config.toml de antes) e encerra
///   WinStatusBar.CodexIAs.exe --verificar [--remover-obsoletos]   relê as IAs e atualiza a barra e o Codex
///   WinStatusBar.CodexIAs.exe --entrar copilot    janela de login do Copilot (ou gemini), com o perfil da barra
///   WinStatusBar.CodexIAs.exe --sondar copilot    diagnóstico da página (sonda-copilot.json)
///   WinStatusBar.CodexIAs.exe --sair              encerra o encaminhador
/// O resultado de cada comando fica em %LOCALAPPDATA%\WinStatusBar\codex-ias\comando.json (a barra mostra).
/// </summary>
internal static class Program
{
    private const string ServiceMutex = @"Local\WinStatusBar.CodexIAs.Servico";
    private const string CheckMutex = @"Local\WinStatusBar.CodexIAs.Verificacao";
    private const string QuitEventName = CodexIntegration.QuitEventName;

    public static string Version { get; } = (typeof(Program).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0").Split('+')[0];

    private static readonly HttpClient Local = CreateLocal();
    private static HttpClient CreateLocal()
    {
        var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{Paths.GatewayPort}"), Timeout = TimeSpan.FromMinutes(10) };
        http.DefaultRequestHeaders.Add(CodexIntegration.CommandHeader, CodexIntegration.CommandValue);
        return http;
    }

    [STAThread]
    private static int Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        // Roda escondido: um erro inesperado vai para o registro, nunca para uma janela de erro na frente da pessoa.
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => Log.Write("Erro inesperado (interface): " + e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Log.Write("Erro inesperado: " + e.ExceptionObject);
        TaskScheduler.UnobservedTaskException += (_, e) => { Log.Write("Erro inesperado (tarefa): " + e.Exception.GetBaseException().Message); e.SetObserved(); };
        string command = args.FirstOrDefault()?.ToLowerInvariant() ?? "";
        WebSite? Site(int i) => args.Length > i ? args[i].ToLowerInvariant() switch { "copilot" => WebSite.Copilot, "gemini" => WebSite.Gemini, _ => null } : null;
        Paths.MigrateOldData();

        switch (command)
        {
            case "--sair": return QuitService() ? 0 : 1;
            case "--ligar": return RunCommand("ligar", EnableAsync);
            case "--desligar": return RunCommand("desligar", DisableAsync);
            case "--verificar":
                bool remove = args.Any(a => a.Equals("--remover-obsoletos", StringComparison.OrdinalIgnoreCase));
                int ia = Array.FindIndex(args, a => a.Equals("--ia", StringComparison.OrdinalIgnoreCase));
                string? only = ia >= 0 && ia + 1 < args.Length ? args[ia + 1] : null;
                return RunCommand("verificar", () => CheckAsync(remove, only));
        }
        if (command == "--entrar" && Site(1) is { } login)
        {
            using var ui = new Control(); ui.CreateControl(); Ui.Init();
            _ = WebChatProvider.ShowLoginAsync(login);
            Application.Run();
            return 0;
        }
        if (command == "--sondar" && Site(1) is { } probeSite)
        {
            using var ui = new Control(); ui.CreateControl(); Ui.Init();
            var provider = new WebChatProvider(probeSite);
            _ = Task.Run(async () =>
            {
                try { await Ui.Run(async () => { await provider.ProbeAsync(CancellationToken.None); return true; }); }
                catch (Exception ex) { Log.Write("Sondagem: " + ex); }
                finally { Application.Exit(); }
            });
            Application.Run();
            return 0;
        }
        return RunService();
    }

    // ------------------------------------------------------------------ encaminhador (serviço em segundo plano)
    private static int RunService()
    {
        using var instance = new Mutex(true, ServiceMutex, out bool first);
        if (!first) return 0;
        if (!CodexIntegration.GatewayWanted()) { Log.Write("Encaminhador não iniciado: a ligação com o Codex está desligada e nenhum programa do PC (Office) usa as IAs da barra"); return 0; }

        using var host = new Control(); host.CreateControl(); Ui.Init();
        var registry = new ModelRegistry();
        async Task<(int, JsonNode)> Command(string name, JsonNode? body, CancellationToken ct)
        {
            switch (name)
            {
                case "verificar":
                    bool remove = body?["removerObsoletos"] is JsonValue v && v.TryGetValue<bool>(out bool r) && r;
                    string? only = body?["ia"] is JsonValue o && o.TryGetValue<string>(out var id) && id.Length > 0 ? id : null;
                    var report = await registry.CheckAsync(remove, ct, only);
                    return (200, JsonNode.Parse(CodexIntegration.Serialize(report))!);
                case "modelos":
                    // Lista guardada das IAs da barra para outros programas do PC (suplemento do Office). Sem lista ainda,
                    // dispara a primeira verificação em segundo plano.
                    var models = registry.Models;
                    if (models.Count == 0)
                        _ = Task.Run(async () => { try { await registry.CheckAsync(false, CancellationToken.None); } catch (Exception ex) { Log.Write("Verificação pedida pela lista: " + ex.Message); } });
                    return (200, new JsonObject { ["modelos"] = JsonNode.Parse(System.Text.Json.JsonSerializer.Serialize(models.OrderBy(m => m.Provider).ThenBy(m => m.DisplayName).ToList())) });
                case "sair":
                    try { host.BeginInvoke(new Action(Application.Exit)); } catch { }
                    return (200, new JsonObject { ["ok"] = true });
                default:
                    return (404, new JsonObject { ["error"] = new JsonObject { ["message"] = "Comando desconhecido: " + name } });
            }
        }
        using var gateway = Gateway.Start(registry, Command);
        if (gateway is null) return 1;
        Log.Write($"Encaminhador do Codex iniciado (pid {Environment.ProcessId}, versão {Version}, porta {Paths.GatewayPort}, {registry.Models.Count} opções da barra)");
        // Primeira vez (sem lista guardada): uma verificação para o seletor do Codex não ficar sem as IAs da barra.
        if (!File.Exists(Paths.ModelsFile))
            _ = Task.Run(async () => { try { await registry.CheckAsync(false, CancellationToken.None); } catch (Exception ex) { Log.Write("Verificação inicial: " + ex.Message); } });

        using var quitEvent = new EventWaitHandle(false, EventResetMode.AutoReset, QuitEventName);
        var watcher = new Thread(() => { quitEvent.WaitOne(); try { host.BeginInvoke(new Action(Application.Exit)); } catch { } }) { IsBackground = true };
        watcher.Start();
        Application.Run();
        Log.Write("Encaminhador do Codex encerrado");
        return 0;
    }

    /// <summary>O encaminhador em execução é este componente (e não outro programa na porta 11435).</summary>
    private static async Task<bool> ServiceRunningAsync()
    {
        try
        {
            using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            var health = await Local.GetFromJsonAsync<JsonObject>("/codexias/saude", limit.Token);
            return health?["app"]?.GetValue<string>() == "WinStatusBar.CodexIAs";
        }
        catch { return false; }
    }

    /// <summary>Algum programa responde na porta 11435.</summary>
    private static async Task<bool> PortBusyAsync()
    {
        try
        {
            using var tcp = new System.Net.Sockets.TcpClient();
            using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await tcp.ConnectAsync("127.0.0.1", Paths.GatewayPort, limit.Token);
            return true;
        }
        catch { return false; }
    }

    private static bool QuitService()
    {
        try { using var quit = EventWaitHandle.OpenExisting(QuitEventName); quit.Set(); return true; } catch { return false; }
    }

    private static void StartService()
    {
        if (Environment.ProcessPath is not { } exe) return;
        Process.Start(new ProcessStartInfo(exe) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(exe)! })?.Dispose();
    }

    // ------------------------------------------------------------------ comandos (aba Modelos da barra)
    private static int RunCommand(string name, Func<Task<(bool Ok, string Message)>> work)
    {
        (bool Ok, string Message) result;
        try { result = Task.Run(work).GetAwaiter().GetResult(); }
        catch (Exception ex) { result = (false, ex.Message); Log.Write($"Comando {name}: {ex}"); }
        try { CodexIntegration.SaveCommand(new(name, result.Ok, result.Message, DateTime.Now)); } catch { }
        Log.Write($"Comando {name}: {(result.Ok ? "ok" : "falhou")} · {result.Message}");
        return result.Ok ? 0 : 1;
    }

    private static async Task<(bool, string)> EnableAsync()
    {
        if (!await ServiceRunningAsync() && await PortBusyAsync())
            return (false, $"A porta {Paths.GatewayPort} está em uso por outro programa (talvez o encaminhador antigo do Codex). Feche-o e tente de novo.");
        // Como estava antes: se o encaminhador não subir, uma ligação nova é desfeita (o Codex não fica numa porta morta).
        bool hadState = File.Exists(CodexIntegration.StateFile);
        var before = CodexIntegration.LoadState();
        bool wasOn = before.Ligado;
        if (await CodexConfig.EnableAsync(CancellationToken.None) is { } error) return (false, error);
        if (!await ServiceRunningAsync())
        {
            bool up = false;
            try
            {
                StartService();
                var until = DateTime.UtcNow.AddSeconds(20);
                while (!(up = await ServiceRunningAsync()) && DateTime.UtcNow <= until) await Task.Delay(500);
            }
            catch (Exception ex) { Log.Write("Ligar: o encaminhador não iniciou: " + ex.Message); }
            if (!up)
            {
                bool undone = false;
                try { undone = await CodexConfig.UndoEnableAsync(wasOn, hadState, before.Copia, CancellationToken.None); }
                catch (Exception ex) { Log.Write("Ligar: a ligação não foi desfeita: " + ex.Message); }
                return (false, (undone ? "O encaminhador não iniciou, então a ligação foi desfeita: o Codex voltou à configuração de antes."
                    : "A ligação foi feita, mas o encaminhador não iniciou.") + " Veja o registro em " + Paths.LogFile);
            }
        }
        var (ok, message) = await CheckAsync(false);
        return (ok, ok ? "Ligado no Codex. " + message : "Ligado no Codex, mas a verificação dos modelos falhou: " + message);
    }

    private static async Task<(bool, string)> DisableAsync()
    {
        // Com o suplemento do Office usando as IAs da barra, o encaminhador continua rodando para ele.
        bool office = File.Exists(CodexIntegration.OfficeClientFile);
        if (!office && await ServiceRunningAsync())
        {
            try { using var empty = new StringContent(""); await Local.PostAsync("/codexias/sair", empty); } catch { }
            for (int i = 0; i < 40 && await ServiceRunningAsync(); i++) await Task.Delay(250);
        }
        await CodexConfig.DisableAsync(CancellationToken.None);
        return (true, "Desligado: o Codex voltou à configuração de antes." + (office ? " O encaminhador continua ligado para o suplemento do Office." : ""));
    }

    private static async Task<(bool, string)> CheckAsync(bool removeObsolete, string? only = null)
    {
        CodexIntegration.Report? report = null;
        if (await ServiceRunningAsync())
        {
            // Corpo com tamanho conhecido (Content-Length): o encaminhador não aceita envio em partes ("chunked").
            using var body = new StringContent(System.Text.Json.JsonSerializer.Serialize(new { removerObsoletos = removeObsolete, ia = only }), System.Text.Encoding.UTF8, "application/json");
            using var response = await Local.PostAsync("/codexias/verificar", body);
            report = CodexIntegration.ParseReport(await response.Content.ReadAsStringAsync());
            if (!response.IsSuccessStatusCode || report is null) return (false, $"O encaminhador respondeu {(int)response.StatusCode}.");
        }
        else
        {
            // Semáforo com nome (e não Mutex): a espera e a liberação acontecem em threads diferentes (await).
            using var turn = new Semaphore(1, 1, CheckMutex);
            if (!turn.WaitOne(TimeSpan.FromMinutes(10))) return (false, "Outra verificação está em andamento.");
            try
            {
                // Sem o encaminhador: a verificação roda aqui, com a thread da interface que as páginas escondidas precisam.
                var done = new TaskCompletionSource<CodexIntegration.Report>(TaskCreationOptions.RunContinuationsAsynchronously);
                var thread = new Thread(() =>
                {
                    using var host = new Control(); host.CreateControl(); Ui.Init();
                    _ = Task.Run(async () =>
                    {
                        try { done.SetResult(await new ModelRegistry().CheckAsync(removeObsolete, CancellationToken.None, only)); }
                        catch (Exception ex) { done.SetException(ex); }
                        finally { try { host.BeginInvoke(new Action(Application.ExitThread)); } catch { } }
                    });
                    Application.Run();
                });
                thread.SetApartmentState(ApartmentState.STA);
                thread.IsBackground = true;
                thread.Start();
                report = await done.Task;
            }
            finally { turn.Release(); }
        }
        // As linhas da OpenAI já contam os modelos novos e os que saíram dela.
        int novos = report.Servicos.Sum(s => s.Novos.Count), sairam = report.Servicos.Sum(s => s.Sairam.Count);
        int noCodex = report.Servicos.Count(s => s.Total > 0 && !s.Falhou), falhas = report.Servicos.Count(s => s.Falhou);
        return (true, $"Verificado: {noCodex} IAs com opções; {novos} novidades, {sairam} saíram" + (falhas > 0 ? $"; {falhas} com falha (tente de novo nelas)." : "."));
    }
}
