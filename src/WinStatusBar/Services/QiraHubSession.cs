using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using WinStatusBar.Core.Ai;

namespace WinStatusBar.Services;

/// <summary>
/// Lenovo Qira dentro da barra, nos modos Híbrido e Local (escolhidos no seletor de modelo do chat).
/// O Qira é dividido em núcleo (LenovoQiraCore, que roteia entre o modelo local e a nuvem) e interface
/// (QuantumAI.Hub, WPF + WebView2). O núcleo só aceita a interface assinada pela Lenovo, então a barra não fala com
/// ele diretamente: ela abre o próprio QuantumAI.Hub em segundo plano, com a depuração do WebView2 ligada só em
/// 127.0.0.1, fecha a barrinha flutuante do Qira pelo comando do próprio Hub e envia a pergunta pela mesma mensagem
/// que a interface oficial usa ("AIGCChat"). O roteamento local/nuvem continua 100% nativo da Lenovo.
/// Antes de cada envio o modo é aplicado e conferido no Qira; se não confirmar, nada é enviado.
/// </summary>
internal static class QiraHubSession
{
    private static readonly string AppDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Lenovo", "Lenovo Qira", "QuantumApp");
    private static readonly string HubExe = Path.Combine(AppDir, "QuantumAI.Hub.exe");
    private static readonly string StateFile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WinStatusBar", "qira-hub.json");
    private const string Binding = "__wsbQira";
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(3) };
    private static readonly SemaphoreSlim Gate = new(1, 1);

    public static void Install()
    {
        QiraBridge.Runner = RunAsync;
        QiraBridge.AutoApproveTools = () => App.Current.Settings.Current.ConnectorsFullAccess;
        QiraBridge.Available = () => File.Exists(HubExe) ? null : "Lenovo Qira não está instalado neste PC.";
        try { if (File.Exists(QuotaFile)) QiraBridge.CurrentQuota = JsonSerializer.Deserialize<QiraBridge.Quota>(File.ReadAllText(QuotaFile)); } catch { }
    }

    private static readonly string SettingsFile = Path.Combine(Path.GetDirectoryName(StateFile)!, "qira-preferencias.json");

    /// <summary>Guarda as preferências da pessoa no Qira e as regrava quando ele volta ao padrão de fábrica (ver QiraSettingsGuard).</summary>
    private static async Task GuardSettingsAsync(Cdp cdp, CancellationToken ct)
    {
        try
        {
            string? body = await cdp.EvalAsync<string?>("window.__wsbQ.post('/Settings/GetConfig', {})", ct);
            if (QiraSettingsGuard.Groups(body) is not { } current) return;
            System.Text.Json.Nodes.JsonObject? remembered = null;
            try { if (File.Exists(SettingsFile)) remembered = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(SettingsFile)) as System.Text.Json.Nodes.JsonObject; } catch { }
            switch (QiraSettingsGuard.Decide(current, remembered))
            {
                case QiraSettingsGuard.Action.Remember:
                    File.WriteAllText(SettingsFile, current.ToJsonString());
                    break;
                case QiraSettingsGuard.Action.Restore:
                    string payload = JsonSerializer.Serialize(QiraSettingsGuard.SavePayload(remembered!));
                    await cdp.EvalAsync<string?>($"window.__wsbQ.post('/Settings/SaveConfig', {payload})", ct);
                    App.Log("Qira: preferências tinham voltado ao padrão; regravadas pela barra");
                    break;
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { App.Log("Qira: preferências: " + ex.Message); }
    }

    private static readonly string QuotaFile = Path.Combine(Path.GetDirectoryName(StateFile)!, "qira-quota.json");

    /// <summary>Créditos da nuvem, lidos do próprio Qira (Settings/GetCloudQuota). Só os números são guardados.</summary>
    private static async Task<bool> ReadQuotaAsync(Cdp cdp, CancellationToken ct)
    {
        try
        {
            string? data = await cdp.EvalAsync<string?>("window.__wsbQ.post('/Settings/GetCloudQuota', {}).then(t => { const d = JSON.parse(t).Data; return typeof d === 'string' ? d : JSON.stringify(d); })", ct);
            if (QiraBridge.ParseQuota(data, DateTimeOffset.Now) is not { } q) return false;
            QiraBridge.CurrentQuota = q;
            try { File.WriteAllText(QuotaFile, JsonSerializer.Serialize(q)); } catch { }
            return true;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { App.Log("Qira: créditos: " + ex.Message); return false; }
    }

    private static DateTime _quotaRetryAfter;

    /// <summary>
    /// Atualiza os créditos se a interface do Qira da barra já estiver aberta (nunca a abre só para isso). No máximo 15 s,
    /// para não segurar o chat; se o Qira não responder, só tenta de novo depois de 10 min.
    /// </summary>
    public static async Task RefreshQuotaAsync()
    {
        if (!File.Exists(HubExe) || DateTime.UtcNow < _quotaRetryAfter || !await Gate.WaitAsync(0)) return;
        bool ok = false;
        IDisposable? turn = null;
        try
        {
            // O Codex IAs pode estar usando o mesmo Qira agora: os créditos ficam para a próxima leitura.
            turn = await QiraSharedTurn.AcquireAsync(TimeSpan.Zero, CancellationToken.None);
            if (turn is null) { ok = true; return; }
            using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var state = ReadState();
            if (state is null) { ok = true; return; }
            var hubs = HubProcesses();
            bool ours = hubs.Any(p => p.Id == state.Pid);
            foreach (var p in hubs) p.Dispose();
            if (!ours || await PageUrlAsync(state.Port, limit.Token) is not { } url) { ok = true; return; }
            using var cdp = await Cdp.ConnectAsync(url, limit.Token);
            if (await cdp.EvalAsync<bool>(SetupJs, limit.Token) && await ReadQuotaAsync(cdp, limit.Token)) { ok = true; await GuardSettingsAsync(cdp, limit.Token); }
        }
        catch (OperationCanceledException) { App.Log("Qira: créditos: o Qira não respondeu em 15 s"); }
        catch (Exception ex) { App.Log("Qira: créditos: " + ex.Message); }
        finally
        {
            if (!ok) _quotaRetryAfter = DateTime.UtcNow.AddMinutes(10);
            turn?.Dispose();
            Gate.Release();
        }
    }

    /// <summary>Último reinício completo do Qira, por esta barra ou pelo Codex IAs (arquivo comum).</summary>
    private static DateTime LastRestart()
    {
        var shared = QiraSharedTurn.LastRestartUtc();
        return shared > _lastRestart ? shared : _lastRestart;
    }

    private static DateTime _healthyAt = DateTime.MinValue;

    /// <summary>
    /// Conferência antecipada (painel aberto com o Qira como destino): parado por um tempo, o núcleo do Qira costuma
    /// travar, e o envio só descobria isso depois de 8 s, somando ~25 s de reinício. Assim o reinício acontece enquanto a
    /// pessoa digita. Só uma por vez, e nunca durante um envio (mesma trava).
    /// </summary>
    public static void Prewarm()
    {
        if (!File.Exists(HubExe) || DateTime.UtcNow - _healthyAt < TimeSpan.FromMinutes(2)) return;
        _ = Task.Run(async () =>
        {
            if (!await Gate.WaitAsync(0)) return;
            IDisposable? turn = null;
            try
            {
                // Com o Qira em uso pelo Codex IAs, a conferência antecipada não é necessária agora.
                turn = await QiraSharedTurn.AcquireAsync(TimeSpan.Zero, CancellationToken.None);
                if (turn is null) return;
                using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(90));
                var hub = await EnsureHubAsync(limit.Token);
                using var cdp = await Cdp.ConnectAsync(hub.WsUrl, limit.Token);
                if (!await cdp.EvalAsync<bool>(SetupJs, limit.Token)) return;
                if (await ReadModeAsync(cdp, TimeSpan.FromSeconds(hub.Launched ? 45 : 6), limit.Token) is not null) { _healthyAt = DateTime.UtcNow; return; }
                if (DateTime.UtcNow - LastRestart() < QiraSharedTurn.RestartInterval) return;
                _lastRestart = DateTime.UtcNow;
                QiraSharedTurn.MarkRestart();
                App.Log("Qira: núcleo sem resposta ao abrir o painel; reiniciando antes do envio");
                // O reinício vai até o fim: parar no meio deixaria o Qira pela metade.
                await RestartQiraAsync(CancellationToken.None);
            }
            catch (Exception ex) { App.Log("Qira: conferência antecipada: " + ex.Message); }
            finally { turn?.Dispose(); Gate.Release(); }
        });
    }

    // ------------------------------------------------------------------ Corrigir problemas
    /// <summary>
    /// Corrigir problemas (menu "…" da barra e Configurações › Codex e modelos): serviço de modelos do Qira, ponte local 9339 e
    /// núcleo travado (reinício completo, como "Sair e abrir de novo"). Espera a vez: nunca mexe no Qira enquanto ele
    /// responde, na barra ou nas IAs do Codex.
    /// </summary>
    public static async Task<List<AiRepair.Step>> RepairAsync(CancellationToken ct)
    {
        const string area = "Lenovo Qira";
        var steps = new List<AiRepair.Step>();
        if (!File.Exists(HubExe)) { steps.Add(new(area, AiRepair.Outcome.Skipped, "Não instalado neste PC.")); return steps; }
        bool policy = BridgePolicyOn();
        if (!policy)
            steps.Add(new(area, AiRepair.Outcome.NeedsYou, "A ponte local do Qira (porta 9339) não está ativada: sem ela, a barra e o Codex só usam o Qira aberto por eles. Ativar pede administrador.", "qira-ponte"));
        if (!await Gate.WaitAsync(TimeSpan.FromSeconds(20), ct))
        { steps.Add(new(area, AiRepair.Outcome.Skipped, "O Qira está respondendo na barra agora; rode a correção de novo quando ele terminar.")); return steps; }
        IDisposable? turn = null;
        try
        {
            turn = await QiraSharedTurn.AcquireAsync(TimeSpan.FromSeconds(20), ct);
            if (turn is null) { steps.Add(new(area, AiRepair.Outcome.Skipped, "O Qira está respondendo no Codex agora; rode a correção de novo quando ele terminar.")); return steps; }
            if (EnsureModelService()) steps.Add(new(area, AiRepair.Outcome.Fixed, "O serviço de modelos do Qira estava parado e foi iniciado."));
            var hubs = HubProcesses();
            bool running = hubs.Count > 0;
            foreach (var h in hubs) h.Dispose();
            if (!running) { steps.Add(new(area, AiRepair.Outcome.Ok, "Qira fechado: ele abre sozinho, em segundo plano, no próximo pedido.")); return steps; }
            // Aberto: a página precisa responder na ponte e o núcleo, ao pedido do modo.
            string? url = null;
            var until = DateTime.UtcNow.AddSeconds(15);
            while ((url = await PageUrlAsync(BridgePort, ct)) is null && DateTime.UtcNow < until) await Task.Delay(500, ct);
            if (url is null && !policy) return steps; // sem a ponte não há como conferir; a pendência já está na lista
            bool healthy = false;
            if (url is not null)
            {
                try
                {
                    using var cdp = await Cdp.ConnectAsync(url, ct);
                    // Como no envio: uma segunda chance antes de reiniciar (o núcleo pode estar só lento), nunca derruba um Qira que responde.
                    healthy = await cdp.EvalAsync<bool>(SetupJs, ct)
                              && (await ReadModeAsync(cdp, TimeSpan.FromSeconds(12), ct) is not null || await ReadModeAsync(cdp, TimeSpan.FromSeconds(20), ct) is not null);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch { healthy = false; }
            }
            if (healthy) { _healthyAt = DateTime.UtcNow; steps.Add(new(area, AiRepair.Outcome.Ok, "Respondendo pela ponte local (porta 9339).")); return steps; }
            // Reiniciado há pouco (pela barra ou pelo Codex): pode estar terminando de abrir; não reinicia em seguida.
            if (DateTime.UtcNow - LastRestart() < TimeSpan.FromMinutes(3))
            {
                steps.Add(new(area, AiRepair.Outcome.Failed, "O Qira não respondeu, mas foi reiniciado há menos de 3 minutos e pode estar terminando de abrir. Rode a correção de novo em 1 minuto."));
                return steps;
            }
            _lastRestart = DateTime.UtcNow;
            QiraSharedTurn.MarkRestart();
            try
            {
                await RestartQiraAsync(CancellationToken.None); // vai até o fim: parar no meio deixaria o Qira pela metade
                steps.Add(new(area, AiRepair.Outcome.Fixed, "O Qira não respondia (núcleo travado) e foi reiniciado, como \"Sair e abrir de novo\"."));
            }
            catch (Exception ex) { steps.Add(new(area, AiRepair.Outcome.Failed, "O Qira não respondia e o reinício falhou: " + ex.Message)); }
            return steps;
        }
        finally { turn?.Dispose(); Gate.Release(); }
    }

    /// <summary>Ativa a ponte local do Qira (política do WebView2 para o QuantumAI.Hub, porta 9339). Pede administrador (UAC).</summary>
    public static async Task<string> EnableBridgeAsync()
    {
        if (BridgePolicyOn()) return "A ponte do Qira já estava ativada.";
        var psi = new ProcessStartInfo("reg.exe") { UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden };
        psi.ArgumentList.Add("add"); psi.ArgumentList.Add(@"HKLM\SOFTWARE\Policies\Microsoft\Edge\WebView2\AdditionalBrowserArguments");
        psi.ArgumentList.Add("/v"); psi.ArgumentList.Add("QuantumAI.Hub.exe");
        psi.ArgumentList.Add("/t"); psi.ArgumentList.Add("REG_SZ");
        psi.ArgumentList.Add("/d"); psi.ArgumentList.Add($"--remote-debugging-port={BridgePort}");
        psi.ArgumentList.Add("/f");
        try
        {
            using var process = Process.Start(psi);
            if (process is not null) await process.WaitForExitAsync();
        }
        catch (System.ComponentModel.Win32Exception) { return "Ativação cancelada (o pedido de administrador foi recusado)."; }
        if (!BridgePolicyOn()) return "A ponte do Qira não foi ativada.";
        App.Log("Qira: ponte local 9339 ativada pela correção de problemas");
        // Vale para o Hub que abrir depois: reinicia o Qira se estiver aberto.
        var hubs = HubProcesses();
        bool running = hubs.Count > 0;
        foreach (var h in hubs) h.Dispose();
        if (running)
        {
            await Gate.WaitAsync();
            IDisposable? turn = null;
            try
            {
                // Como nos demais reinícios: só com a vez na mão, nunca enquanto o Qira responde nas IAs do Codex.
                turn = await QiraSharedTurn.AcquireAsync(TimeSpan.FromSeconds(20), CancellationToken.None);
                if (turn is null) App.Log("Qira: ponte ativada sem reiniciar agora (o Qira está respondendo no Codex); vale quando ele abrir de novo");
                else { _lastRestart = DateTime.UtcNow; QiraSharedTurn.MarkRestart(); await RestartQiraAsync(CancellationToken.None); }
            }
            catch (Exception ex) { return "Ponte ativada, mas o Qira não reiniciou: " + ex.Message + ". Saia do Qira pela bandeja e abra de novo."; }
            finally { turn?.Dispose(); Gate.Release(); }
        }
        return "Ponte do Qira ativada (porta 9339, só neste PC).";
    }

    private sealed record State(int Pid, int Port);
    private static readonly HashSet<string> QiraFileTypes = [".jpg", ".jpeg", ".png", ".webp", ".pdf", ".ppt", ".pptx", ".doc", ".docx", ".js", ".py", ".txt", ".htm", ".html", ".css", ".md", ".csv", ".xml", ".rtf"];

    // ------------------------------------------------------------------ envio
    private static async Task<AgentPrompt.Result> RunAsync(string prompt, string? model, AgentPrompt.Options options, Action<string> onText, CancellationToken ct)
    {
        await Gate.WaitAsync(ct);
        Cdp cdp = null!;
        IDisposable? turn = null;
        try
        {
            // O Codex IAs pode estar usando o mesmo Qira: espera a vez (as respostas do Qira levam até 4 min).
            turn = await QiraSharedTurn.AcquireAsync(TimeSpan.FromSeconds(1), ct);
            if (turn is null)
            {
                onText("*O Qira está respondendo no Codex; aguardando a vez…*");
                turn = await QiraSharedTurn.AcquireAsync(TimeSpan.FromMinutes(5), ct);
                ct.ThrowIfCancellationRequested(); // parado pela pessoa durante a espera: "Interrompido", como nos demais casos
                if (turn is null) return Fail("O Qira ficou ocupado com o Codex por mais de 5 minutos. Tente de novo.");
            }
            // Modo: lido no próprio Qira a cada envio. Logo após abrir o Hub, o núcleo leva alguns segundos para responder.
            // Núcleo travado (acontece de tempos em tempos, com o Qira aberto): a barra reinicia o Qira inteiro, como "Sair e
            // abrir de novo", no máximo uma vez a cada 10 min, e tenta de novo.
            bool launched = false;
            string? mode = null;
            for (int round = 0; ; round++)
            {
                var hub = await EnsureHubAsync(ct);
                launched = hub.Launched;
                cdp?.Dispose();
                cdp = await Cdp.ConnectAsync(hub.WsUrl, ct);
                if (!await cdp.EvalAsync<bool>(SetupJs, ct)) return Fail("A interface do Qira ainda não está pronta. Tente de novo em alguns segundos.");
                mode = await ReadModeAsync(cdp, TimeSpan.FromSeconds(launched ? 45 : round > 0 ? 60 : 8), ct);
                if (mode is not null || launched || round > 0) break;
                // O núcleo também para quando o serviço de modelos some: com ele de volta, costuma se recuperar.
                if (EnsureModelService())
                {
                    App.Log("Qira: núcleo sem resposta e serviço de modelos ausente; serviço reiniciado pela barra");
                    await Task.Delay(5000, ct);
                    if ((mode = await ReadModeAsync(cdp, TimeSpan.FromSeconds(20), ct)) is not null) break;
                }
                if (DateTime.UtcNow - LastRestart() < QiraSharedTurn.RestartInterval) break;
                _lastRestart = DateTime.UtcNow;
                QiraSharedTurn.MarkRestart();
                onText("*O Qira parou de responder; reiniciando o Qira (cerca de 1 min)…*");
                // O reinício vai até o fim mesmo se o pedido for interrompido: parar no meio deixaria o Qira pela metade.
                await RestartQiraAsync(CancellationToken.None);
                ct.ThrowIfCancellationRequested();
            }
            if (mode is not null) _healthyAt = DateTime.UtcNow;
            if (mode is null && launched)
            {
                // Núcleo sem resposta logo após abrir: encerra o Hub pelo comando dele; o próximo envio o reabre limpo.
                App.Log("Qira: núcleo sem resposta após abrir; interface encerrada pelo comando do próprio Qira");
                try { await cdp.EvalAsync<bool>("window.__wsbQ.post('/win/ExitApplication', {}).then(() => true, () => true)", CancellationToken.None); } catch { }
                return Fail("O Qira não respondeu ao abrir. Envie de novo em alguns segundos.");
            }
            // Sempre Híbrido: aplicado pela própria API do Qira e conferido antes do envio.
            const string wanted = QiraBridge.HybridMode;
            if (mode is not null && !string.Equals(mode, wanted, StringComparison.OrdinalIgnoreCase))
            {
                await cdp.EvalAsync<bool>($"window.__wsbQ.linked('/Settings/SetRoutingMode', {{ Mode: {JsonSerializer.Serialize(wanted)} }}).then(() => true)", ct);
                mode = await cdp.EvalAsync<string?>("window.__wsbQ.linked('/Settings/GetRoutingMode').then(d => d && d.Data && d.Data.Mode || null)", ct);
                App.Log($"Qira: modo alterado para {mode ?? "desconhecido"} (pedido {wanted})");
            }
            if (QiraBridge.ModeError(mode, wanted) is { } modeError) { App.Log($"Qira: envio recusado (modo {mode ?? "desconhecido"})"); return Fail(modeError); }
            // Hub recém-aberto: o Qira perde as preferências da pessoa ao reabrir; a barra as regrava.
            if (launched) await GuardSettingsAsync(cdp, ct);
            // Sessão do Qira: a conversa continua na mesma sessão; sem ela, o histórico vai no formato da interface oficial.
            // Sessão própria de cada conversa da barra: o Qira guarda o histórico por sessionId; sem id, ele usaria uma sessão
            // padrão compartilhada (e misturaria conversas). NewSession só responde na janela de chat dele.
            string? session = options.ResumeSession;
            bool newSession = string.IsNullOrEmpty(session);
            if (newSession) session = Guid.NewGuid().ToString();
            string Text(string p) => newSession ? QiraBridge.WithHistory(p, options.History) : p;
            string text = Text(prompt);
            // O Qira aceita um arquivo por mensagem (imagem ou documento), como no app dele.
            var files = options.Attachments.Where(File.Exists).Where(p => QiraFileTypes.Contains(Path.GetExtension(p).ToLowerInvariant()))
                .Take(1).Select(p => new JsonObject { ["path"] = p, ["base64"] = "" }).ToArray();
            var data = new JsonObject
            {
                ["data"] = text, ["fileList"] = new JsonArray(files), ["type"] = "", ["sessionId"] = session ?? "", ["live"] = new JsonObject(),
            };

            for (int attempt = 0; ; attempt++)
            {
                var acc = await StreamAsync(cdp, data, onText, ct);
                if (acc.Handler is { } h) App.Log($"Qira: resposta via {h} ({acc.Text.Length} caracteres)");
                QiraBridge.LastHandler = acc.Handler;
                if (acc.Text.Length > 0)
                {
                    await ReadQuotaAsync(cdp, ct);
                    await GuardSettingsAsync(cdp, ct);
                    return new(acc.Final(), session, null);
                }
                // Logo após abrir o Hub, a primeira resposta pode falhar vazia: tenta uma vez mais.
                if (attempt == 0 && acc.Failure == "O Qira não conseguiu responder.") { await Task.Delay(3000, ct); continue; }
                if (attempt == 0 && acc.NoReply) { App.Log("Qira: pergunta sem resposta em 25 s; reenviando"); continue; }
                if (acc.NoReply) return Fail("O Qira não respondeu. Tente de novo em instantes.");
                // O Qira escolheu o modelo local dele e o serviço de modelos caiu: inicia de novo e tenta uma vez mais.
                // Só com GPU dedicada; sem ela o motor do Qira (Intel) não responde nesta iGPU.
                if (attempt == 0 && acc.Failure?.StartsWith("O Qira escolheu o modelo local dele", StringComparison.Ordinal) == true
                    && HasDedicatedGpu() && EnsureModelService()) { await Task.Delay(6000, ct); continue; }
                // O filtro de segurança do modelo local do Qira recusa as instruções da barra entre colchetes: tenta só com o texto.
                if (attempt == 0 && acc.Failure?.StartsWith("O filtro de segurança do Qira", StringComparison.Ordinal) == true
                    && QiraBridge.StripBarHints(prompt) is { } plain && plain != prompt)
                {
                    App.Log("Qira: filtro de segurança recusou; reenviando só o texto da pessoa");
                    data["data"] = Text(plain);
                    continue;
                }
                if (acc.OtherTypes.Count > 0) return Fail($"O Qira respondeu com um conteúdo que só o aplicativo dele exibe ({string.Join(", ", acc.OtherTypes)}).");
                return Fail(acc.Failure is { Length: > 0 } f ? "Qira: " + f : "O Qira não respondeu.");
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (QiraException ex) { return Fail(ex.Message); }
        catch (Exception ex) { App.Log("Qira: " + ex); return Fail("Falha ao conversar com o Qira: " + ex.Message); }
        finally
        {
            cdp?.Dispose();
            turn?.Dispose();
            Gate.Release();
        }
    }

    private static async Task<string?> ReadModeAsync(Cdp cdp, TimeSpan wait, CancellationToken ct)
    {
        var until = DateTime.UtcNow + wait;
        do
        {
            var mode = await cdp.EvalAsync<string?>("window.__wsbQ.linked('/Settings/GetRoutingMode').then(d => d && d.Data && d.Data.Mode || null)", ct);
            if (mode is not null) return mode;
            await Task.Delay(1000, ct);
        } while (DateTime.UtcNow < until);
        return null;
    }

    private static DateTime _lastRestart = DateTime.MinValue;
    private static readonly string Launcher = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Lenovo", "Lenovo Qira", "Lenovo Qira.Launcher.exe");

    /// <summary>
    /// Reinício completo do Qira, a única recuperação que funciona quando o núcleo trava (verificado em 08/11/2026):
    /// encerra todas as partes juntas e abre de novo pelo iniciador oficial. Nunca encerra só uma parte (isso trava o resto).
    /// A barrinha do Qira que abre junto fica escondida pelo QiraWindowGuard.
    /// </summary>
    private static async Task RestartQiraAsync(CancellationToken ct)
    {
        if (!File.Exists(Launcher)) throw new QiraException("O Qira parou de responder e o iniciador dele não foi encontrado para reabri-lo.");
        App.Log("Qira: núcleo sem resposta; reiniciando o Qira inteiro (como Sair e abrir de novo)");
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
        App.Log("Qira: reaberto pelo iniciador oficial");
    }

    private static AgentPrompt.Result Fail(string message) => new("", null, message);

    private static async Task<QiraBridge.Accumulator> StreamAsync(Cdp cdp, JsonObject data, Action<string> onText, CancellationToken ct)
    {
        var acc = new QiraBridge.Accumulator();
        bool gotAny = false;
        string mid = Guid.NewGuid().ToString();
        var queue = new BlockingQueue();
        cdp.OnBinding = payload => queue.Add(payload);
        await cdp.SendAsync("Runtime.addBinding", new JsonObject { ["name"] = Binding }, ct);
        var msg = new JsonObject { ["MessageMethod"] = "AIGCChat", ["Data"] = data.DeepClone() };
        await cdp.EvalAsync<bool>($"window.__wsbQ.stream({JsonSerializer.Serialize(mid)}, {msg.ToJsonString()}), true", ct);
        try
        {
            while (!acc.Done)
            {
                // O primeiro pedaço (quem vai responder) chega em segundos; sem ele em 25 s, a mensagem se perdeu.
                // Depois do primeiro pedaço, o Qira pode demorar carregando conectores lentos: espera até 4 min.
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
                var parsed = QiraBridge.Parse(chunk);
                if (parsed?.Approval is { } approval)
                {
                    if (QiraBridge.AutoApproveTools?.Invoke() != true)
                        throw new QiraException($"O Qira pediu autorização para usar \"{approval.ToolName}\" ({approval.ServerName}). Ative o acesso total a conectores nas configurações da barra ou permita a ferramenta no Qira.");
                    string sessionId = data["sessionId"]?.GetValue<string>() ?? "";
                    var reply = new JsonObject { ["Direction"] = 0, ["MessageSource"] = "", ["MessageDestination"] = "", ["MessageMethod"] = "ToolPermissionAction",
                        ["MessageId"] = Guid.NewGuid().ToString(), ["Timestamp"] = DateTimeOffset.Now.ToUnixTimeMilliseconds(), ["Data"] = QiraBridge.ApprovalReply(approval, sessionId) };
                    await cdp.EvalAsync<bool>($"window.chrome.webview.postMessage({reply.ToJsonString()}), true", ct);
                    App.Log($"Qira: ferramenta aprovada automaticamente: {approval.ToolName} ({approval.ServerName})");
                    onText("*Usando " + (approval.ServerName ?? approval.ToolName) + "…*");
                    continue;
                }
                if (parsed is not null && acc.Add(parsed)) onText(acc.Text);
                else if (acc.ToolStatus is { Length: > 0 } tool && tool != toolBefore && acc.Text.Length == 0) onText("*" + tool + "*");
                else if (acc.Status is { Length: > 0 } st && st != statusBefore && acc.Text.Length == 0) onText("*" + QiraBridge.StatusText(st) + "*");
                if (body.ValueKind == JsonValueKind.Object && body.TryGetProperty("IsDone", out var done) && done.ValueKind == JsonValueKind.True) break;
            }
        }
        finally
        {
            cdp.OnBinding = null;
            // Parar: a barra deixa de ouvir. O próprio Qira também não cancela a geração em andamento.
            try { await cdp.EvalAsync<bool>($"window.__wsbQ.stop({JsonSerializer.Serialize(mid)}), true", CancellationToken.None); } catch { }
        }
        return acc;
    }

    // Instalado na página do Hub; idempotente. Usa a ponte oficial (window.chrome.webview + quantumapp.locallenovo).
    private const string SetupJs = """
        (() => {
          if (window.__wsbQ) return true;
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
          const request = (msg, ms) => new Promise(res => {
            const mid = crypto.randomUUID();
            const t = setTimeout(() => { listeners.delete(mid); res(null); }, ms || 8000);
            listeners.set(mid, d => { clearTimeout(t); listeners.delete(mid); res(d); });
            wv.postMessage({ Direction: 0, MessageSource: '', MessageDestination: '', ...msg, MessageId: mid, Timestamp: Date.now() });
          });
          const stream = (mid, msg) => {
            listeners.set(mid, d => {
              try { window.__wsbQira(JSON.stringify(d)); } catch {}
              const x = d.Data || d.data || {};
              if (x.IsDone || (d.ErrorCode && d.ErrorCode !== 0)) listeners.delete(mid);
            });
            wv.postMessage({ Direction: 0, MessageSource: '', MessageDestination: '', ...msg, MessageId: mid, Timestamp: Date.now() });
          };
          const stop = mid => listeners.delete(mid);
          window.__wsbQ = { post, linked, request, stream, stop };
          return true;
        })()
        """;

    // ------------------------------------------------------------------ Hub em segundo plano
    private static async Task<(string WsUrl, bool Launched)> EnsureHubAsync(CancellationToken ct)
    {
        if (!File.Exists(HubExe)) throw new QiraException("Lenovo Qira não está instalado neste PC.");
        var state = ReadState();
        var hubs = HubProcesses();
        try
        {
            if (state is not null && hubs.Any(p => p.Id == state.Pid) && await PageUrlAsync(state.Port, ct) is { } ws) { QiraWindowGuard.Watch(state.Pid); return (ws, false); }
            // Hub aberto pelo app Lenovo (sem a ponte da barra). Nunca encerrar à força: o núcleo do Qira perde a conexão
            // com a interface (o chat para de responder) e as preferências podem voltar ao padrão.
            if (hubs.Count > 0)
            {
                // Com a política do WebView2 (tools\qira-ponte\Ativar-ponte-Qira.cmd), todo Hub do Qira abre a ponte na
                // porta fixa: a barra usa o Hub que o próprio Qira abriu, sem reiniciar nada.
                // Logo depois de abrir (ou de um reinício feito pelas IAs no Codex), a página ainda está carregando: espera até 45 s.
                var pageDeadline = DateTime.UtcNow.AddSeconds(45);
                do
                {
                    if (await PageUrlAsync(BridgePort, ct) is { } fixedUrl)
                    {
                        int pid = hubs[0].Id;
                        WriteState(new State(pid, BridgePort));
                        QiraWindowGuard.Watch(pid);
                        App.Log($"Qira: usando o Qira aberto pelo app Lenovo (pid {pid}) pela ponte {BridgePort}");
                        return (fixedUrl, false);
                    }
                    if (!BridgePolicyOn()) break; // sem a ponte permanente, esperar não adianta
                    await Task.Delay(500, ct);
                } while (DateTime.UtcNow < pageDeadline && HubProcesses() is { Count: > 0 } running && Release(running));
                if (BridgePolicyOn())
                    throw new QiraException("O Qira está aberto, mas não respondeu na ponte local (porta 9339) em 45 s. Ele pode estar abrindo ou travado; tente de novo em instantes.");
                throw new QiraException("O Qira foi aberto pelo aplicativo Lenovo e a barra não tem acesso a ele. Saia do Qira uma vez (ícone na bandeja › Sair) ou ative a ponte permanente (pasta tools/qira-ponte do projeto, Ativar-ponte-Qira.cmd).");
            }
        }
        finally { foreach (var p in hubs) p.Dispose(); }

        int port = PortFree(BridgePort) ? BridgePort : FreePort();
        var psi = new ProcessStartInfo(HubExe) { UseShellExecute = false, WorkingDirectory = AppDir };
        // Depuração só em 127.0.0.1 e sem --remote-allow-origins: páginas da web não conseguem se conectar.
        psi.Environment["WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS"] = $"--remote-debugging-port={port}";
        using var hub = Process.Start(psi) ?? throw new QiraException("Não foi possível abrir o Qira.");
        WriteState(new State(hub.Id, port));
        App.Log($"Qira: interface aberta em segundo plano (pid {hub.Id})");

        // Barrinha, bolha e janelas pequenas do Qira ficam escondidas no instante em que aparecem (sem lampejo).
        QiraWindowGuard.Watch(hub.Id, TimeSpan.FromSeconds(20));

        string? url = null;
        var until = DateTime.UtcNow.AddSeconds(40);
        while (DateTime.UtcNow < until && (url = await PageUrlAsync(port, ct)) is null)
        {
            if (hub.HasExited) throw new QiraException("O Qira fechou ao abrir.");
            await Task.Delay(300, ct);
        }
        if (url is null) throw new QiraException("O Qira não abriu a tempo.");

        // Fecha a barrinha também pelo comando do próprio Hub, para o estado dele ficar coerente com a janela escondida.
        string? lastError = null;
        var closeUntil = DateTime.UtcNow.AddSeconds(15);
        while (DateTime.UtcNow < closeUntil)
        {
            try
            {
                using var cdp = await Cdp.ConnectAsync(url, ct);
                // O Hub fecha a janela antes de responder ao /win/close: a requisição cai, e isso é o sucesso esperado.
                if (await cdp.EvalAsync<bool>(SetupJs, ct) && await cdp.EvalAsync<bool>("""
                    (async () => { const i = JSON.parse(JSON.parse(await window.__wsbQ.post('/win/info', {})).Data);
                      if (!i || !i.id) return false;
                      if (i.type === 'Bar') { try { await window.__wsbQ.post('/win/close', { id: i.id }); } catch {} }
                      return true; })()
                    """, ct)) { lastError = null; break; }
                lastError = "janela do Qira sem identificação";
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { lastError = ex.Message; }
            await Task.Delay(500, ct);
        }
        if (lastError is not null) App.Log("Qira: fechar a barrinha: " + lastError);
        return (url, true);
    }

    // ------------------------------------------------------------------ serviço de modelos do Qira
    /// <summary>GPU NVIDIA ou AMD dedicada presente (o modo de energia do Windows pode desligá-la e removê-la do sistema).</summary>
    private static bool HasDedicatedGpu()
    {
        try { return WinStatusBar.Core.Metrics.GpuMetricsProvider.EnumerateAdapters().Any(a => !a.IsIntegrated && a.VendorId is 0x10DE or 0x1002); }
        catch { return true; }
    }
    private static readonly string ModelRuntime = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Lenovo", "Lenovo Qira", "modelService", "runtime");

    /// <summary>
    /// Serviço de modelos do Qira (lenovo.pfm.pipe.exe, modelo local Phi-4). Se cair, o Qira não o reinicia. A barra só o
    /// inicia quando ele falta, como o Qira faz: argumentos oficiais, pastas dos motores (Intel, NVIDIA, AMD) no PATH e sem
    /// caixas de erro do Windows. Nunca o encerra: sem ele, o núcleo do Qira para de responder (verificado em 08/11/2026,
    /// nos modos Local e Híbrido). Devolve true quando iniciou o serviço.
    /// </summary>
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
        uint previous = SetErrorMode(0x8003); // sem caixas "DLL não encontrada" no serviço (herdado na criação)
        try { using var started = Process.Start(psi); App.Log($"Qira: serviço de modelos iniciado pela barra (pid {started?.Id})"); return started is not null; }
        catch (Exception ex) { App.Log("Qira: serviço de modelos não iniciou: " + ex.Message); return false; }
        finally { SetErrorMode(previous); }
    }
    [DllImport("kernel32.dll")] private static extern uint SetErrorMode(uint mode);

    /// <summary>Solta os processos consultados e devolve true (para usar dentro de uma condição).</summary>
    private static bool Release(List<Process> processes) { foreach (var p in processes) p.Dispose(); return true; }

    /// <summary>Ponte permanente do Qira ativada (política do WebView2 com a porta fixa para o QuantumAI.Hub).</summary>
    private static bool BridgePolicyOn()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Policies\Microsoft\Edge\WebView2\AdditionalBrowserArguments");
            return (key?.GetValue("QuantumAI.Hub.exe") as string ?? "").Contains($"--remote-debugging-port={BridgePort}", StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
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

    /// <summary>Porta fixa da ponte (a mesma da política do WebView2 para o QuantumAI.Hub.exe).</summary>
    private const int BridgePort = 9339;

    private static bool PortFree(int port)
    {
        try { var l = new TcpListener(IPAddress.Loopback, port); l.Start(); l.Stop(); return true; } catch { return false; }
    }

    private static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        try { return ((IPEndPoint)l.LocalEndpoint).Port; } finally { l.Stop(); }
    }

    private static State? ReadState() { try { return JsonSerializer.Deserialize<State>(File.ReadAllText(StateFile)); } catch { return null; } }
    private static void WriteState(State s) { try { Directory.CreateDirectory(Path.GetDirectoryName(StateFile)!); File.WriteAllText(StateFile, JsonSerializer.Serialize(s)); } catch { } }


    private sealed class QiraException(string message) : Exception(message);

    // ------------------------------------------------------------------ fila simples para os eventos da página
    private sealed class BlockingQueue
    {
        private readonly ConcurrentQueue<string> _items = new();
        private readonly SemaphoreSlim _count = new(0);
        public void Add(string s) { _items.Enqueue(s); _count.Release(); }
        public async Task<string?> TakeAsync(TimeSpan idle, CancellationToken ct)
            => await _count.WaitAsync(idle, ct) && _items.TryDequeue(out var s) ? s : null;
    }

    // ------------------------------------------------------------------ cliente mínimo do protocolo de depuração
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
