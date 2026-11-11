using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.WebSockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using WinStatusBar.Core.Ai;

namespace CodexIAs.Providers;

/// <summary>
/// Lenovo Qira no modo Híbrido pelo mesmo Hub que a WinStatusBar usa (QuantumAI.Hub com a ponte permanente na porta
/// 9339, política do WebView2). O núcleo do Qira só aceita a interface assinada pela Lenovo; a pergunta segue pela
/// mesma mensagem da interface oficial ("AIGCChat"). Convivência com a barra: vez compartilhada (QiraSharedTurn, da barra) e
/// ajudantes próprios na página (__cdxQ / __cdxQira), para as respostas de um app nunca chegarem ao outro.
/// </summary>
internal sealed class QiraProvider : IProvider
{
    private static readonly string AppDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Lenovo", "Lenovo Qira", "QuantumApp");
    private static readonly string HubExe = Path.Combine(AppDir, "QuantumAI.Hub.exe");
    private static readonly string Launcher = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Lenovo", "Lenovo Qira", "Lenovo Qira.Launcher.exe");
    private static readonly string ModelRuntime = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Lenovo", "Lenovo Qira", "modelService", "runtime");
    private const int BridgePort = 9339;
    private const string Binding = "__cdxQira";
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(3) };
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ConcurrentDictionary<string, (string Session, int Turns)> _sessions = new();

    public string Name => "qira";
    public string Label => "Lenovo Qira";

    /// <summary>Só com o Lenovo Qira instalado e a ponte local dele ativada (a mesma que a barra usa).</summary>
    public Task<Listing> ListAsync(CancellationToken ct)
        => Task.FromResult(!File.Exists(HubExe) ? Listing.Off("Lenovo Qira não instalado neste PC")
            : !BridgePolicyOn() ? Listing.Off($"ponte local do Qira (porta {BridgePort}) não ativada neste PC")
            : new Listing([new AiModel(CodexCatalog.Prefix + "qira", "qira", "hybrid", "Lenovo Qira — híbrido",
                "Lenovo Qira no modo Híbrido, pela WinStatusBar (o mesmo Qira da barra, um pedido por vez). Só texto.", [], null, -3)]));

    public async Task<Answer> RunAsync(AiModel model, string? effort, Conversation conversation, Action<string> onText, CancellationToken ct)
    {
        if (!File.Exists(HubExe)) return new("", "Lenovo Qira não está instalado neste PC.");
        await _gate.WaitAsync(ct);
        IDisposable? turn = null;
        Cdp? cdp = null;
        try
        {
            QiraLifecycle.MarkUse(); // a contagem para encerrar o Qira recomeça a cada pedido
            // A barra pode estar usando o Qira agora: espera a vez (as respostas dele levam até 4 min).
            turn = await QiraSharedTurn.AcquireAsync(TimeSpan.FromSeconds(1), ct);
            if (turn is null)
            {
                Log.Write("Qira: em uso pela WinStatusBar; aguardando a vez");
                onText("*O Qira está respondendo na WinStatusBar; aguardando a vez…*");
                turn = await QiraSharedTurn.AcquireAsync(TimeSpan.FromMinutes(5), ct);
                if (turn is null)
                {
                    if (ct.IsCancellationRequested) { Log.Write("Qira: espera cancelada (o Codex desistiu do pedido)"); return new("", "Interrompido"); }
                    Log.Write("Qira: a vez não veio em 5 minutos");
                    return new("", "O Qira ficou ocupado com a WinStatusBar por mais de 5 minutos. Tente de novo.");
                }
                Log.Write("Qira: vez liberada");
            }
            bool launched = false;
            string? mode = null;
            // Fechado (o normal entre um pedido e outro): abre agora, só para este pedido.
            if (!QiraLifecycle.AnyRunning()) onText("*Abrindo o Qira…*");
            for (int round = 0; ; round++)
            {
                var hub = await EnsureHubAsync(ct);
                launched = hub.Launched;
                cdp?.Dispose();
                cdp = await Cdp.ConnectAsync(hub.WsUrl, ct);
                if (!await cdp.EvalAsync<bool>(SetupJs, ct)) return new("", "A interface do Qira ainda não está pronta. Tente de novo em alguns segundos.");
                mode = await ReadModeAsync(cdp, TimeSpan.FromSeconds(launched ? 45 : round > 0 ? 60 : 8), ct);
                if (mode is not null || launched || round > 0) break;
                if (EnsureModelService())
                {
                    Log.Write("Qira: núcleo sem resposta e serviço de modelos ausente; serviço reiniciado");
                    await Task.Delay(5000, ct);
                    if ((mode = await ReadModeAsync(cdp, TimeSpan.FromSeconds(20), ct)) is not null) break;
                }
                if (DateTime.UtcNow - QiraSharedTurn.LastRestartUtc() < QiraSharedTurn.RestartInterval) break;
                QiraSharedTurn.MarkRestart();
                onText("*O Qira parou de responder; reiniciando o Qira (cerca de 1 min)…*");
                // O reinício vai até o fim mesmo se o pedido for interrompido: parar no meio deixaria o Qira pela metade.
                await RestartQiraAsync(CancellationToken.None);
                ct.ThrowIfCancellationRequested();
            }
            if (mode is null && launched)
            {
                Log.Write("Qira: núcleo sem resposta após abrir; interface encerrada pelo comando do próprio Qira");
                try { await cdp.EvalAsync<bool>("window.__cdxQ.post('/win/ExitApplication', {}).then(() => true, () => true)", CancellationToken.None); } catch { }
                return new("", "O Qira não respondeu ao abrir. Envie de novo em alguns segundos.");
            }
            const string wanted = QiraProtocol.HybridMode;
            if (mode is not null && !string.Equals(mode, wanted, StringComparison.OrdinalIgnoreCase))
            {
                await cdp.EvalAsync<bool>($"window.__cdxQ.linked('/Settings/SetRoutingMode', {{ Mode: {JsonSerializer.Serialize(wanted)} }}).then(() => true)", ct);
                mode = await cdp.EvalAsync<string?>("window.__cdxQ.linked('/Settings/GetRoutingMode').then(d => d && d.Data && d.Data.Mode || null)", ct);
                Log.Write($"Qira: modo alterado para {mode ?? "desconhecido"}");
            }
            if (QiraProtocol.ModeError(mode, wanted) is { } modeError) return new("", modeError);

            // Uma sessão do Qira por conversa do Codex; continua nela quando o pedido é a sequência da conversa.
            int userTurns = conversation.History.Count(h => h.Role == "user");
            string? session = conversation.Key is { Length: > 0 } key && _sessions.TryGetValue(key, out var known) && known.Turns == userTurns ? known.Session : null;
            bool newSession = session is null;
            session ??= Guid.NewGuid().ToString();
            string text = newSession ? QiraProtocol.WithHistory(conversation.Prompt, conversation.History) : conversation.Prompt;
            var data = new JsonObject { ["data"] = text, ["fileList"] = new JsonArray(), ["type"] = "", ["sessionId"] = session, ["live"] = new JsonObject() };

            for (int attempt = 0; ; attempt++)
            {
                var acc = await StreamAsync(cdp, data, onText, ct);
                if (acc.Handler is { } h) Log.Write($"Qira: resposta via {h} ({acc.Text.Length} caracteres)");
                if (acc.Text.Length > 0)
                {
                    if (conversation.Key is { Length: > 0 } k) _sessions[k] = (session, userTurns + 1);
                    return new(acc.Final(), null);
                }
                if (attempt == 0 && acc.Failure == "O Qira não conseguiu responder.") { await Task.Delay(3000, ct); continue; }
                if (attempt == 0 && acc.NoReply) { Log.Write("Qira: pergunta sem resposta em 25 s; reenviando"); continue; }
                if (acc.NoReply) return new("", "O Qira não respondeu. Tente de novo em instantes.");
                if (attempt == 0 && acc.Failure?.StartsWith("O Qira escolheu o modelo local dele", StringComparison.Ordinal) == true && EnsureModelService())
                { await Task.Delay(6000, ct); continue; }
                if (acc.OtherTypes.Count > 0) return new("", $"O Qira respondeu com um conteúdo que só o aplicativo dele exibe ({string.Join(", ", acc.OtherTypes)}).");
                return new("", acc.Failure is { Length: > 0 } f ? "Qira: " + f : "O Qira não respondeu.");
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (QiraException ex) { return new("", ex.Message); }
        catch (Exception ex) { Log.Write("Qira: " + ex); return new("", "Falha ao conversar com o Qira: " + ex.Message); }
        finally
        {
            cdp?.Dispose();
            QiraLifecycle.MarkUse();
            turn?.Dispose();
            _gate.Release();
            ScheduleStop();
        }
    }

    private static System.Threading.Timer? _stopTimer;

    /// <summary>
    /// Depois de cada uso: encerra o Qira aberto sob demanda quando a espera escolhida na barra (Manter a IA aberta depois
    /// da resposta) passar sem outro pedido. Ocupado, na barra ou aqui, confere de novo depois (ver QiraLifecycle).
    /// </summary>
    private static void ScheduleStop(TimeSpan? after = null)
    {
        static TimeSpan Configured() => WinStatusBar.Core.Settings.AppSettings.AiRelease(BarSettings.Load().AiReleaseSeconds);
        var grace = after ?? Configured();
        var timer = new System.Threading.Timer(async _ =>
        {
            try
            {
                if (await QiraLifecycle.StopIfIdleAsync(Configured(), Log.Write)) return;
                if (QiraLifecycle.Owned) ScheduleStop(TimeSpan.FromSeconds(30) > grace ? TimeSpan.FromSeconds(30) : grace);
            }
            catch (Exception ex) { Log.Write("Qira: encerrar depois do uso: " + ex.Message); }
        }, null, grace + TimeSpan.FromSeconds(1), Timeout.InfiniteTimeSpan);
        Interlocked.Exchange(ref _stopTimer, timer)?.Dispose();
    }

    private static async Task<string?> ReadModeAsync(Cdp cdp, TimeSpan wait, CancellationToken ct)
    {
        var until = DateTime.UtcNow + wait;
        do
        {
            var mode = await cdp.EvalAsync<string?>("window.__cdxQ.linked('/Settings/GetRoutingMode').then(d => d && d.Data && d.Data.Mode || null)", ct);
            if (mode is not null) return mode;
            await Task.Delay(1000, ct);
        } while (DateTime.UtcNow < until);
        return null;
    }

    /// <summary>
    /// Reinício completo do Qira, a única recuperação que funciona quando o núcleo trava (verificado pela barra em
    /// 08/11/2026): encerra todas as partes juntas e abre de novo pelo iniciador oficial. Só roda com a vez na mão.
    /// </summary>
    private static async Task RestartQiraAsync(CancellationToken ct)
    {
        if (!File.Exists(Launcher)) throw new QiraException("O Qira parou de responder e o iniciador dele não foi encontrado para reabri-lo.");
        Log.Write("Qira: núcleo sem resposta; reiniciando o Qira inteiro (como Sair e abrir de novo)");
        bool owned = QiraLifecycle.Owned; // aberto sob demanda: continua assim depois do reinício (e fecha depois do uso)
        // Lista comum (QiraSharedTurn) e só o que roda da pasta do Qira: outro programa com o mesmo nome de processo fica de fora.
        foreach (var name in QiraSharedTurn.ProcessNames)
            foreach (var p in Process.GetProcessesByName(name)) using (p) { try { if (QiraSharedTurn.IsQiraProcess(p)) p.Kill(); } catch { } }
        await Task.Delay(4000, ct);
        using (Process.Start(new ProcessStartInfo(Launcher, "Qira") { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(Launcher)! })) { }
        var until = DateTime.UtcNow.AddSeconds(60);
        bool watching = false;
        while (DateTime.UtcNow < until)
        {
            if (!watching)
            {
                var hubs = HubProcesses();
                try { if (hubs.Count > 0) { QiraWindowGuard.Watch(hubs[0].Id, TimeSpan.FromSeconds(30)); watching = true; } }
                finally { foreach (var h in hubs) h.Dispose(); }
            }
            if (watching && await PageUrlAsync(BridgePort, ct) is not null) break;
            await Task.Delay(300, ct);
        }
        if (owned) await QiraLifecycle.MarkOwnedAsync(CancellationToken.None);
        Log.Write("Qira: reaberto pelo iniciador oficial");
    }

    private static async Task<QiraProtocol.Accumulator> StreamAsync(Cdp cdp, JsonObject data, Action<string> onText, CancellationToken ct)
    {
        var acc = new QiraProtocol.Accumulator();
        bool gotAny = false;
        string mid = Guid.NewGuid().ToString();
        var queue = new BlockingQueue();
        cdp.OnBinding = payload => queue.Add(payload);
        await cdp.SendAsync("Runtime.addBinding", new JsonObject { ["name"] = Binding }, ct);
        var msg = new JsonObject { ["MessageMethod"] = "AIGCChat", ["Data"] = data.DeepClone() };
        await cdp.EvalAsync<bool>($"window.__cdxQ.stream({JsonSerializer.Serialize(mid)}, {msg.ToJsonString()}), true", ct);
        try
        {
            while (!acc.Done)
            {
                string? payload = await queue.TakeAsync(TimeSpan.FromSeconds(gotAny ? 240 : 25), ct);
                if (payload is null && !gotAny) { acc.NoReply = true; break; }
                if (payload is null) { if (acc.Failure is null && acc.Text.Length == 0) throw new QiraException("O Qira não respondeu em 4 minutos (ele ficou carregando as ferramentas dos conectores)."); break; }
                gotAny = true;
                using var doc = JsonDocument.Parse(payload);
                var root = doc.RootElement;
                if (root.TryGetProperty("ErrorCode", out var ec) && ec.ValueKind == JsonValueKind.Number && ec.GetInt32() != 0)
                    throw new QiraException("O Qira recusou o pedido" + (root.TryGetProperty("ErrorMessage", out var em) && em.ValueKind == JsonValueKind.String ? ": " + em.GetString() : "."));
                if (root.TryGetProperty("KeepAlive", out var ka) && ka.ValueKind == JsonValueKind.True) continue;
                var body = root.TryGetProperty("Data", out var d) ? d : default;
                string? chunk = body.ValueKind == JsonValueKind.Object && body.TryGetProperty("Chunk", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString() : null;
                string? toolBefore = acc.ToolStatus, statusBefore = acc.Status;
                var parsed = QiraProtocol.Parse(chunk);
                if (parsed?.Approval is { } approval)
                {
                    // Política do usuário (07/11/2026): acesso total aos conectores das IAs, sem pedir autorização.
                    string sessionId = data["sessionId"]?.GetValue<string>() ?? "";
                    var reply = new JsonObject { ["Direction"] = 0, ["MessageSource"] = "", ["MessageDestination"] = "", ["MessageMethod"] = "ToolPermissionAction",
                        ["MessageId"] = Guid.NewGuid().ToString(), ["Timestamp"] = DateTimeOffset.Now.ToUnixTimeMilliseconds(), ["Data"] = QiraProtocol.ApprovalReply(approval, sessionId) };
                    await cdp.EvalAsync<bool>($"window.chrome.webview.postMessage({reply.ToJsonString()}), true", ct);
                    Log.Write($"Qira: ferramenta aprovada automaticamente: {approval.ToolName} ({approval.ServerName})");
                    onText("*Usando " + (approval.ServerName ?? approval.ToolName) + "…*");
                    continue;
                }
                if (parsed is not null && acc.Add(parsed)) onText(acc.Text);
                else if (acc.ToolStatus is { Length: > 0 } tool && tool != toolBefore && acc.Text.Length == 0) onText("*" + tool + "*");
                else if (acc.Status is { Length: > 0 } st && st != statusBefore && acc.Text.Length == 0) onText("*" + QiraProtocol.StatusText(st) + "*");
                if (body.ValueKind == JsonValueKind.Object && body.TryGetProperty("IsDone", out var done) && done.ValueKind == JsonValueKind.True) break;
            }
        }
        finally
        {
            cdp.OnBinding = null;
            try { await cdp.EvalAsync<bool>($"window.__cdxQ.stop({JsonSerializer.Serialize(mid)}), true", CancellationToken.None); } catch { }
        }
        return acc;
    }

    // Ajudante próprio das IAs no Codex na página do Hub (a barra usa __wsbQ): idempotente, pela ponte oficial da página.
    private const string SetupJs = """
        (() => {
          if (window.__cdxQ) return true;
          const wv = window.chrome && window.chrome.webview; if (!wv) return false;
          const listeners = new Map();
          wv.addEventListener('message', e => { const d = e.data; const f = d && d.MessageId && listeners.get(d.MessageId); if (f) f(d); });
          const body = (mid, data) => JSON.stringify({ MessageId: mid, Timestamp: Date.now(), MessageSource: '', Data: data || {} });
          const post = async (path, data) => { const r = await fetch('https://quantumapp.locallenovo' + path, { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: body(crypto.randomUUID(), data) }); return await r.text(); };
          const linked = (path, data, ms) => new Promise(res => {
            const mid = crypto.randomUUID();
            const t = setTimeout(() => { listeners.delete(mid); res(null); }, ms || 8000);
            listeners.set(mid, d => { clearTimeout(t); listeners.delete(mid); res(d); });
            fetch('https://quantumapp.locallenovo' + path, { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: body(mid, data) }).catch(() => {});
          });
          const stream = (mid, msg) => {
            listeners.set(mid, d => {
              try { window.__cdxQira(JSON.stringify(d)); } catch {}
              const x = d.Data || d.data || {};
              if (x.IsDone || (d.ErrorCode && d.ErrorCode !== 0)) listeners.delete(mid);
            });
            wv.postMessage({ Direction: 0, MessageSource: '', MessageDestination: '', ...msg, MessageId: mid, Timestamp: Date.now() });
          };
          const stop = mid => listeners.delete(mid);
          window.__cdxQ = { post, linked, stream, stop };
          return true;
        })()
        """;

    // ------------------------------------------------------------------ Hub
    /// <summary>Ponte permanente ligada (política do WebView2 em HKLM para o QuantumAI.Hub.exe).</summary>
    private static bool BridgePolicyOn()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Policies\Microsoft\Edge\WebView2\AdditionalBrowserArguments");
            return (key?.GetValue("QuantumAI.Hub.exe") as string ?? "").Contains($"--remote-debugging-port={BridgePort}", StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    private static async Task<(string WsUrl, bool Launched)> EnsureHubAsync(CancellationToken ct)
    {
        var hubs = HubProcesses();
        try
        {
            if (hubs.Count > 0)
            {
                // Com a ponte permanente (Recriar PC › ponte do Qira), todo Hub responde na porta fixa, inclusive o da barra.
                // Logo depois de abrir (ou de um reinício), a página ainda está carregando: espera até 45 s.
                var pageDeadline = DateTime.UtcNow.AddSeconds(45);
                do
                {
                    if (await PageUrlAsync(BridgePort, ct) is { } url) { QiraWindowGuard.Watch(hubs[0].Id); return (url, false); }
                    await Task.Delay(500, ct);
                } while (DateTime.UtcNow < pageDeadline && HubRunning());
                if (BridgePolicyOn())
                    throw new QiraException("O Qira está aberto, mas não respondeu na ponte local (porta 9339) em 45 s. Ele pode estar abrindo ou travado; tente de novo em instantes.");
                throw new QiraException("O Qira está aberto sem a ponte local (porta 9339). Ative a ponte permanente do Qira (WinStatusBar › tools\\qira-ponte\\Ativar-ponte-Qira.cmd) e saia do Qira pela bandeja uma vez.");
            }
        }
        finally { foreach (var p in hubs) p.Dispose(); }

        // Qira todo fechado: primeiro o núcleo (sem ele a interface abre sozinha e a primeira resposta leva ~50 s).
        await QiraLifecycle.EnsureNucleusAsync(Log.Write, ct);
        var psi = new ProcessStartInfo(HubExe) { UseShellExecute = false, WorkingDirectory = AppDir };
        // Depuração só em 127.0.0.1 e sem --remote-allow-origins (páginas da web não conseguem se conectar).
        psi.Environment["WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS"] = $"--remote-debugging-port={BridgePort}";
        using var hub = Process.Start(psi) ?? throw new QiraException("Não foi possível abrir o Qira.");
        Log.Write($"Qira: interface aberta em segundo plano (pid {hub.Id})");
        QiraWindowGuard.Watch(hub.Id, TimeSpan.FromSeconds(20));
        string? found = null;
        var until = DateTime.UtcNow.AddSeconds(40);
        while (DateTime.UtcNow < until && (found = await PageUrlAsync(BridgePort, ct)) is null)
        {
            if (hub.HasExited) throw new QiraException("O Qira fechou ao abrir.");
            await Task.Delay(300, ct);
        }
        if (found is null) throw new QiraException("O Qira não abriu a tempo.");
        // Fecha a barrinha também pelo comando do próprio Hub, para o estado dele ficar coerente com a janela escondida.
        var closeUntil = DateTime.UtcNow.AddSeconds(15);
        while (DateTime.UtcNow < closeUntil)
        {
            try
            {
                using var cdp = await Cdp.ConnectAsync(found, ct);
                if (await cdp.EvalAsync<bool>(SetupJs, ct) && await cdp.EvalAsync<bool>("""
                    (async () => { const i = JSON.parse(JSON.parse(await window.__cdxQ.post('/win/info', {})).Data);
                      if (!i || !i.id) return false;
                      if (i.type === 'Bar') { try { await window.__cdxQ.post('/win/close', { id: i.id }); } catch {} }
                      return true; })()
                    """, ct)) break;
            }
            catch (OperationCanceledException) { throw; }
            catch { }
            await Task.Delay(500, ct);
        }
        return (found, true);
    }

    /// <summary>Serviço de modelos do Qira: se caiu, inicia como o Qira faz (nunca o encerra). True quando iniciou.</summary>
    private static bool EnsureModelService()
    {
        string exe = Path.Combine(ModelRuntime, "lenovo.pfm.pipe.exe");
        if (!File.Exists(exe)) return false;
        var running = Process.GetProcessesByName("lenovo.pfm.pipe");
        foreach (var p in running) p.Dispose();
        if (running.Length > 0) return false;
        var dirs = new List<string> { ModelRuntime, Path.Combine(ModelRuntime, "llm") };
        foreach (var vendor in new[] { "cuda_v12.8", "intel", "amd" })
            if (Directory.Exists(Path.Combine(ModelRuntime, "llm", vendor))) dirs.Add(Path.Combine(ModelRuntime, "llm", vendor));
        var psi = new ProcessStartInfo(exe, "--modelflag 0 --logflag 0") { WorkingDirectory = ModelRuntime, UseShellExecute = false, CreateNoWindow = true };
        psi.Environment["PATH"] = string.Join(';', dirs) + ";" + Environment.GetEnvironmentVariable("PATH");
        uint previous = SetErrorMode(0x8003);
        try { using var started = Process.Start(psi); Log.Write($"Qira: serviço de modelos iniciado (pid {started?.Id})"); return started is not null; }
        catch (Exception ex) { Log.Write("Qira: serviço de modelos não iniciou: " + ex.Message); return false; }
        finally { SetErrorMode(previous); }
    }
    [DllImport("kernel32.dll")] private static extern uint SetErrorMode(uint mode);

    private static bool HubRunning()
    {
        var hubs = HubProcesses();
        foreach (var p in hubs) p.Dispose();
        return hubs.Count > 0;
    }

    private static List<Process> HubProcesses()
    {
        var list = new List<Process>();
        foreach (var p in Process.GetProcessesByName("QuantumAI.Hub"))
        {
            try { if (string.Equals(p.MainModule?.FileName, HubExe, StringComparison.OrdinalIgnoreCase)) { list.Add(p); continue; } } catch { }
            p.Dispose();
        }
        return list;
    }

    /// <summary>URL de depuração da página do Hub (local.lenovo.com/client), ou null.</summary>
    private static async Task<string?> PageUrlAsync(int port, CancellationToken ct)
    {
        try
        {
            var list = JsonNode.Parse(await Http.GetStringAsync($"http://127.0.0.1:{port}/json/list", ct))?.AsArray();
            return list?.FirstOrDefault(n => n?["type"]?.GetValue<string>() == "page"
                    && (n["url"]?.GetValue<string>() ?? "").StartsWith("https://local.lenovo.com/client/", StringComparison.OrdinalIgnoreCase))
                ?["webSocketDebuggerUrl"]?.GetValue<string>();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch { return null; }
    }

    private sealed class QiraException(string message) : Exception(message);

    private sealed class BlockingQueue
    {
        private readonly ConcurrentQueue<string> _items = new();
        private readonly SemaphoreSlim _count = new(0);
        public void Add(string s) { _items.Enqueue(s); _count.Release(); }
        public async Task<string?> TakeAsync(TimeSpan idle, CancellationToken ct)
            => await _count.WaitAsync(idle, ct) && _items.TryDequeue(out var s) ? s : null;
    }

    /// <summary>Cliente mínimo do protocolo de depuração (CDP) por WebSocket.</summary>
    private sealed class Cdp : IDisposable
    {
        private readonly ClientWebSocket _ws = new();
        private readonly ConcurrentDictionary<int, TaskCompletionSource<JsonNode?>> _pending = new();
        private readonly CancellationTokenSource _stop = new();
        private readonly SemaphoreSlim _send = new(1, 1);
        private int _id;
        public Action<string>? OnBinding;

        public static async Task<Cdp> ConnectAsync(string url, CancellationToken ct)
        {
            var c = new Cdp();
            c._ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            await c._ws.ConnectAsync(new Uri(url), timeout.Token);
            _ = c.ReceiveLoopAsync();
            return c;
        }

        public async Task<JsonNode?> SendAsync(string method, JsonObject? parameters, CancellationToken ct)
        {
            int id = Interlocked.Increment(ref _id);
            var tcs = new TaskCompletionSource<JsonNode?>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pending[id] = tcs;
            var msg = new JsonObject { ["id"] = id, ["method"] = method, ["params"] = parameters ?? new JsonObject() };
            byte[] bytes = Encoding.UTF8.GetBytes(msg.ToJsonString());
            try
            {
                await _send.WaitAsync(ct);
                try { await _ws.SendAsync(bytes, WebSocketMessageType.Text, true, ct); } finally { _send.Release(); }
                using (ct.Register(() => tcs.TrySetCanceled(ct)))
                    return await tcs.Task.WaitAsync(TimeSpan.FromSeconds(60), ct);
            }
            finally { _pending.TryRemove(id, out _); }
        }

        public async Task<T?> EvalAsync<T>(string expression, CancellationToken ct)
        {
            var r = await SendAsync("Runtime.evaluate", new JsonObject { ["expression"] = expression, ["awaitPromise"] = true, ["returnByValue"] = true }, ct);
            if (r?["exceptionDetails"] is { } ex) throw new InvalidOperationException("Script do Qira: " + (ex["exception"]?["description"] ?? ex["text"])?.ToString());
            var value = r?["result"]?["value"];
            return value is null ? default : value.Deserialize<T>();
        }

        private async Task ReceiveLoopAsync()
        {
            var buffer = new byte[64 * 1024];
            var sb = new MemoryStream();
            try
            {
                while (_ws.State == WebSocketState.Open && !_stop.IsCancellationRequested)
                {
                    var res = await _ws.ReceiveAsync(buffer, _stop.Token);
                    if (res.MessageType == WebSocketMessageType.Close) break;
                    sb.Write(buffer, 0, res.Count);
                    if (!res.EndOfMessage) continue;
                    var node = JsonNode.Parse(Encoding.UTF8.GetString(sb.GetBuffer(), 0, (int)sb.Length));
                    sb.SetLength(0);
                    if (node?["id"] is { } idNode && _pending.TryRemove(idNode.GetValue<int>(), out var tcs))
                    {
                        if (node["error"] is { } err) tcs.TrySetException(new InvalidOperationException(err["message"]?.ToString() ?? "erro do protocolo"));
                        else tcs.TrySetResult(node["result"]);
                    }
                    else if (node?["method"]?.GetValue<string>() == "Runtime.bindingCalled" && node["params"]?["name"]?.GetValue<string>() == Binding
                             && node["params"]?["payload"]?.GetValue<string>() is { } payload)
                        OnBinding?.Invoke(payload);
                }
            }
            catch { }
            foreach (var p in _pending.Values) p.TrySetException(new InvalidOperationException("Conexão com o Qira encerrada."));
        }

        public void Dispose()
        {
            _stop.Cancel();
            try { _ws.Abort(); } catch { }
            _ws.Dispose();
        }
    }
}
