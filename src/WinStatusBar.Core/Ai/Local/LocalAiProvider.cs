using System.Text.Json;

namespace WinStatusBar.Core.Ai.Local;

/// <summary>Servidor de inferência local reconhecido pelo nome do processo.</summary>
public sealed record LocalServerKind(string Display, string[] ProcessNames, int? DefaultPort, bool OllamaApi);

/// <summary>
/// Agente "Local": detecta inferência de modelos no próprio computador, independente de quem a pediu
/// (Codex, suplemento do Office, navegador, scripts...).
///  • Servidores: Ollama, LM Studio, llama.cpp, Foundry Local, KoboldCpp, LocalAI, Jan, llamafile (+ nomes extras configuráveis).
///  • Atividade: uso de GPU (contadores GPU Engine por PID) e CPU desses processos, com histerese.
///  • Modelo: /api/ps do Ollama (somente localhost).
///  • Cliente: tabela TCP (GetExtendedTcpTable) → processo conectado à porta do servidor; se esse processo for um
///    intermediário (servidor local que repassa pedidos), segue mais um salto; depois sobe a árvore de processos
///    até um aplicativo conhecido (ex.: WebView2 → WINWORD = Word).
/// </summary>
public sealed class LocalAiProvider : IAgentActivityProvider, IDisposable
{
    public static readonly LocalServerKind[] KnownServers =
    [
        new("Ollama", ["ollama", "ollama_llama_server"], 11434, true),
        new("LM Studio", ["lms", "lm studio", "llmster"], 1234, false),
        new("llama.cpp", ["llama-server", "llama-cli"], 8080, false),
        new(global::WinStatusBar.Core.LocalizedText.T("Foundry Local"), ["inference.service.agent", "foundry"], null, false),
        new("KoboldCpp", ["koboldcpp"], 5001, false),
        new("LocalAI", ["local-ai", "localai"], 8080, false),
        new("Jan", ["jan", "cortex", "cortex-server"], 1337, false),
        new("llamafile", ["llamafile"], 8080, false),
    ];

    private static readonly Dictionary<string, string> AppNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["winword"] = "Word", ["excel"] = "Excel", ["powerpnt"] = "PowerPoint", ["outlook"] = "Outlook", ["olk"] = "Outlook",
        ["onenote"] = "OneNote", ["msaccess"] = "Access", ["mspub"] = "Publisher", ["teams"] = "Teams", ["ms-teams"] = "Teams",
        ["codex"] = global::WinStatusBar.Core.LocalizedText.T("Codex"), ["chatgpt"] = "ChatGPT", ["claude"] = global::WinStatusBar.Core.LocalizedText.T("Claude"), ["code"] = "VS Code", ["cursor"] = "Cursor",
        ["msedge"] = "Edge", ["chrome"] = "Chrome", ["firefox"] = "Firefox", ["brave"] = "Brave", ["opera"] = "Opera",
        ["obsidian"] = "Obsidian", ["acad"] = "AutoCAD", ["devenv"] = "Visual Studio",
    };

    private static readonly Dictionary<string, string> GenericNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["python"] = "Python", ["pythonw"] = "Python", ["node"] = "Node.js", ["powershell"] = "PowerShell", ["pwsh"] = "PowerShell",
        ["cmd"] = global::WinStatusBar.Core.LocalizedText.T("Prompt de Comando"), ["curl"] = "curl", ["dotnet"] = ".NET", ["java"] = "Java", ["msedgewebview2"] = "WebView2",
    };

    private readonly Dictionary<uint, double> _lastCpu = new();
    private readonly Lock _gate = new();
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(2) };
    private DateTime _lastPoll = DateTime.UtcNow;
    private DateTime _lastBusy = DateTime.MinValue;
    private DateTime _lastModelQuery = DateTime.MinValue;
    private AgentSession? _session;
    private string? _model;
    private int _loadedModels;
    private List<string> _clients = new();

    public AgentKind Kind => AgentKind.Local;
    public event EventHandler? SessionsChanged;
    public IReadOnlyList<string> ExtraProcessNames { get; set; } = [];
    public string? ServerDisplay { get; private set; }
    public string? ServerImagePath { get; private set; }
    public double? LastGpu { get; private set; }
    public double? LastCpuCores { get; private set; }
    public IReadOnlyList<string> LastClients => _clients;

    /// <summary>Limiares de atividade (GPU % do adaptador ou núcleos de CPU) e tempo de histerese.</summary>
    public double GpuThreshold { get; set; } = 8;
    public double CpuCoresThreshold { get; set; } = 0.6;
    public TimeSpan Hysteresis { get; set; } = TimeSpan.FromSeconds(6);

    public IReadOnlyList<AgentSession> Sessions
    {
        get { lock (_gate) return _session is null ? [] : [_session.Clone()]; }
    }

    /// <summary>Chamado pelo agendador central (fora da UI) a cada ~2 s com a amostra mais recente dos motores de GPU.</summary>
    public void Poll(IReadOnlyList<KeyValuePair<string, double>>? gpuEngines)
    {
        var now = DateTime.UtcNow;
        double dt = Math.Max(0.2, (now - _lastPoll).TotalSeconds);
        _lastPoll = now;

        var snapshot = SystemSnapshot.Capture(includeTcp: false);
        var (kind, serverPids) = FindServer(snapshot.Processes.Values, ExtraProcessNames);
        if (kind is null)
        {
            bool had;
            lock (_gate) { had = _session is not null; _session = null; }
            _lastCpu.Clear(); ServerDisplay = null;
            if (had) SessionsChanged?.Invoke(this, EventArgs.Empty);
            return;
        }
        ServerDisplay = kind.Display;
        // Ícone: executável principal do servidor (não o runner filho)
        ServerImagePath ??= serverPids
            .OrderByDescending(p => snapshot.Processes.TryGetValue(p, out var pi) && kind.ProcessNames.Contains(pi.Name, StringComparer.OrdinalIgnoreCase))
            .Select(SystemSnapshot.ImagePath).FirstOrDefault(p => p is not null);

        // CPU (núcleos) dos processos do servidor desde a última amostra
        double cores = 0;
        foreach (var pid in serverPids)
        {
            var secs = SystemSnapshot.CpuSeconds(pid);
            if (secs is null) continue;
            if (_lastCpu.TryGetValue(pid, out var prev) && secs >= prev) cores += (secs.Value - prev) / dt;
            _lastCpu[pid] = secs.Value;
        }
        foreach (var gone in _lastCpu.Keys.Where(p => !serverPids.Contains(p)).ToList()) _lastCpu.Remove(gone);
        double? gpu = gpuEngines is null ? null : GpuForPids(gpuEngines, serverPids);
        LastGpu = gpu; LastCpuCores = cores;

        bool busyNow = IsBusy(gpu, cores, GpuThreshold, CpuCoresThreshold);
        bool wasRunning = now - _lastBusy <= Hysteresis;
        if (busyNow && !wasRunning) _clients = new(); // nova geração: não herda o cliente da anterior
        if (busyNow) _lastBusy = now;
        bool running = now - _lastBusy <= Hysteresis;

        // Modelo carregado (Ollama) — consulta leve, só no localhost, no máximo a cada 5 s ou quando começa a gerar
        if (kind.OllamaApi && (busyNow && (_model is null || now - _lastModelQuery > TimeSpan.FromSeconds(5)) || now - _lastModelQuery > TimeSpan.FromSeconds(30)))
        {
            _lastModelQuery = now;
            var port = ListeningPorts(SystemSnapshot.ReadTcp(), serverPids).DefaultIfEmpty(kind.DefaultPort ?? 11434).First();
            (_model, _loadedModels) = QueryOllamaModels(port);
        }

        // Cliente: somente enquanto há geração (tabela TCP é lida apenas nesse momento)
        if (busyNow)
        {
            var tcp = SystemSnapshot.ReadTcp();
            var full = new SystemSnapshot { Processes = snapshot.Processes, Connections = tcp };
            var clients = ResolveClients(full, serverPids, (uint)Environment.ProcessId);
            if (clients.Count > 0) _clients = clients;
        }

        bool changed;
        lock (_gate)
        {
            var s = _session ??= new AgentSession { Kind = AgentKind.Local, Id = "local:" + kind.Display, StartedAt = DateTimeOffset.Now };
            var prevState = s.State;
            var prevCaption = s.Caption;
            s.Title = _model is not null ? $"{kind.Display} · {_model}" : kind.Display;
            s.Model = _model;
            if (running)
            {
                if (prevState != AgentState.Running) { s.StartedAt = DateTimeOffset.Now; }
                s.State = AgentState.Running;
                s.LastActivity = DateTimeOffset.Now;
                string via = _clients.Count == 0 ? global::WinStatusBar.Core.LocalizedText.T("Gerando resposta") : global::WinStatusBar.Core.LocalizedText.T("Gerando para ") + string.Join(", ", _clients);
                string load = gpu is >= 1 ? global::WinStatusBar.Core.LocalizedText.F(" · GPU {0}%", new object?[] {Math.Round(gpu.Value)}) : cores >= 0.1 ? global::WinStatusBar.Core.LocalizedText.F(" · CPU {0:0.0} núcleos", new object?[] {cores}) : string.Empty;
                s.Caption = via + load;
            }
            else
            {
                if (prevState == AgentState.Running) { s.State = AgentState.Unknown; s.LastActivity = DateTimeOffset.Now; s.Caption = _clients.Count > 0 ? global::WinStatusBar.Core.LocalizedText.F("Geração concluída ({0})", new object?[] {string.Join(", ", _clients)}) : global::WinStatusBar.Core.LocalizedText.T("Geração concluída"); }
                else if (s.State == AgentState.Completed && DateTimeOffset.Now - s.LastActivity > TimeSpan.FromMinutes(15)) s.State = AgentState.Idle;
                if (s.State == AgentState.Unknown) s.Caption = "Atividade não observada · conclusão não confirmada";
                if (s.State == AgentState.Idle) s.Caption = _loadedModels > 0 ? global::WinStatusBar.Core.LocalizedText.F("{0} modelo(s) carregado(s) na memória", new object?[] {_loadedModels}) : global::WinStatusBar.Core.LocalizedText.T("Servidor local ativo, sem modelo carregado");
            }
            changed = prevState != s.State || prevCaption != s.Caption;
        }
        if (changed) SessionsChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Algum servidor de IA local instalado neste PC (mesmo que parado)? Usado para listar "IA local" no menu.</summary>
    public static bool IsAnyInstalled()
    {
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string[] paths =
        [
            Path.Combine(local, "Programs", "Ollama", "ollama.exe"), Path.Combine(home, ".ollama"),
            Path.Combine(local, "Programs", "LM Studio"), Path.Combine(local, "Programs", "lm-studio"), Path.Combine(home, ".lmstudio"),
            Path.Combine(local, "Programs", "jan"), Path.Combine(home, "jan"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Ollama"),
        ];
        return paths.Any(p => File.Exists(p) || Directory.Exists(p));
    }

    internal static bool IsBusy(double? gpu, double cores, double gpuThreshold, double coresThreshold)
        => gpu >= gpuThreshold || cores >= coresThreshold;

    internal static (LocalServerKind? kind, HashSet<uint> pids) FindServer(IEnumerable<ProcInfo> processes, IReadOnlyList<string> extra)
    {
        var list = processes.ToList();
        foreach (var k in KnownServers)
        {
            var pids = list.Where(p => k.ProcessNames.Contains(p.Name, StringComparer.OrdinalIgnoreCase)).Select(p => p.Pid).ToHashSet();
            if (pids.Count > 0) return (k, WithDescendants(list, pids));
        }
        foreach (var name in extra.Where(n => !string.IsNullOrWhiteSpace(n)))
        {
            string n = name.Trim().EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name.Trim()[..^4] : name.Trim();
            var pids = list.Where(p => p.Name.Equals(n, StringComparison.OrdinalIgnoreCase)).Select(p => p.Pid).ToHashSet();
            if (pids.Count > 0) return (new LocalServerKind(n, [n], null, false), WithDescendants(list, pids));
        }
        return (null, new HashSet<uint>());
    }

    /// <summary>
    /// Inclui os processos filhos: servidores como o Ollama delegam a inferência a "runners"
    /// (ex.: lib\ollama\llama-server.exe), que é onde a CPU/GPU é efetivamente consumida.
    /// </summary>
    internal static HashSet<uint> WithDescendants(List<ProcInfo> all, HashSet<uint> roots)
    {
        var set = new HashSet<uint>(roots);
        bool added = true;
        for (int guard = 0; added && guard < 8; guard++)
        {
            added = false;
            foreach (var p in all)
                if (!set.Contains(p.Pid) && set.Contains(p.ParentPid) && p.Pid != p.ParentPid) { set.Add(p.Pid); added = true; }
        }
        return set;
    }

    /// <summary>Utilização de GPU atribuída aos PIDs (soma por motor, maior motor) — mesma regra do Gerenciador de Tarefas.</summary>
    internal static double? GpuForPids(IEnumerable<KeyValuePair<string, double>> engines, HashSet<uint> pids)
    {
        var perEngine = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        bool any = false;
        foreach (var kv in engines)
        {
            // pid_1234_luid_0x..._0x..._phys_0_eng_3_engtype_3D
            if (!kv.Key.StartsWith("pid_", StringComparison.OrdinalIgnoreCase)) continue;
            int us = kv.Key.IndexOf('_', 4);
            if (us < 0 || !uint.TryParse(kv.Key.AsSpan(4, us - 4), out var pid) || !pids.Contains(pid)) continue;
            int e = kv.Key.IndexOf("_engtype_", StringComparison.OrdinalIgnoreCase);
            string engine = e > us ? kv.Key[(us + 1)..e] : kv.Key[(us + 1)..];
            perEngine[engine] = perEngine.GetValueOrDefault(engine) + kv.Value;
            any = true;
        }
        return any ? Math.Clamp(perEngine.Values.DefaultIfEmpty(0).Max(), 0, 100) : 0;
    }

    internal static IEnumerable<int> ListeningPorts(IEnumerable<TcpConn> tcp, HashSet<uint> pids)
        => tcp.Where(c => c.Listening && pids.Contains(c.Pid)).Select(c => c.LocalPort).Distinct();

    private const int EphemeralPortStart = 49152;

    /// <summary>
    /// Processos conectados a portas de serviço de <paramref name="servers"/> por loopback.
    /// Casa os dois lados de cada conexão (servidor: local=porta do serviço; cliente: local=efêmera, remota=porta do serviço),
    /// sem depender do socket LISTEN (que nem sempre aparece na tabela, ex.: Ollama).
    /// </summary>
    internal static List<uint> InboundPeers(SystemSnapshot snap, HashSet<uint> servers, HashSet<uint> exclude)
    {
        var listening = ListeningPorts(snap.Connections, servers).ToHashSet();
        var byEndpoint = new Dictionary<(int local, int remote), List<uint>>();
        foreach (var c in snap.Connections)
        {
            if (!c.Established || !c.Loopback) continue;
            if (!byEndpoint.TryGetValue((c.LocalPort, c.RemotePort), out var l)) byEndpoint[(c.LocalPort, c.RemotePort)] = l = new();
            l.Add(c.Pid);
        }
        var peers = new List<uint>();
        foreach (var c in snap.Connections)
        {
            if (!c.Established || !c.Loopback || !servers.Contains(c.Pid)) continue;
            bool servicePort = listening.Contains(c.LocalPort) || (c.LocalPort < EphemeralPortStart && c.RemotePort >= c.LocalPort);
            if (!servicePort) continue;
            if (!byEndpoint.TryGetValue((c.RemotePort, c.LocalPort), out var other)) continue;
            foreach (var pid in other)
                if (pid != 0 && !servers.Contains(pid) && !exclude.Contains(pid) && !peers.Contains(pid)) peers.Add(pid);
        }
        return peers;
    }

    /// <summary>Aplicativos de origem dos pedidos ao servidor local.</summary>
    public static List<string> ResolveClients(SystemSnapshot snap, HashSet<uint> serverPids, uint? selfPid = null)
    {
        var exclude = new HashSet<uint>(serverPids);
        if (selfPid is not null) exclude.Add(selfPid.Value); // consultas do próprio WinStatusBar ao /api/ps
        var result = new List<string>();
        foreach (var pid in InboundPeers(snap, serverPids, exclude))
            foreach (var app in Resolve(snap, pid, exclude, depth: 0))
                if (!result.Contains(app)) result.Add(app);
        return result;
    }

    private static IEnumerable<string> Resolve(SystemSnapshot snap, uint pid, HashSet<uint> exclude, int depth)
    {
        if (!snap.Processes.TryGetValue(pid, out var proc)) return [];
        if (AppNames.TryGetValue(proc.Name, out var known)) return [known];

        // Intermediário: o cliente também atende conexões locais (ex.: servidor.py do suplemento do Office)? Segue um salto.
        if (depth < 2)
        {
            var ex2 = new HashSet<uint>(exclude) { pid };
            var upstream = InboundPeers(snap, [pid], ex2);
            var apps = upstream.SelectMany(u => Resolve(snap, u, ex2, depth + 1)).Distinct().ToList();
            if (apps.Count > 0) return apps;
        }

        // Árvore de processos: WebView2 → WINWORD, codex.exe → Codex, etc.
        uint current = proc.ParentPid;
        for (int i = 0; i < 6 && snap.Processes.TryGetValue(current, out var parent); i++)
        {
            if (AppNames.TryGetValue(parent.Name, out var app)) return [app];
            if (parent.ParentPid == current) break;
            current = parent.ParentPid;
        }
        return [GenericNames.TryGetValue(proc.Name, out var g) ? g : proc.Name];
    }

    private (string? model, int count) QueryOllamaModels(int port)
    {
        try
        {
            string json = _http.GetStringAsync($"http://127.0.0.1:{port}/api/ps").GetAwaiter().GetResult();
            return ParseOllamaPs(json);
        }
        catch { return (_model, _loadedModels); }
    }

    /// <summary>/api/ps → modelo usado mais recentemente (maior expires_at) e quantidade carregada.</summary>
    public static (string? model, int count) ParseOllamaPs(string json)
    {
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("models", out var models) || models.ValueKind != JsonValueKind.Array) return (null, 0);
        string? best = null; DateTimeOffset bestExp = DateTimeOffset.MinValue; int n = 0;
        foreach (var m in models.EnumerateArray())
        {
            n++;
            string? name = m.TryGetProperty("name", out var nm) ? nm.GetString() : null;
            DateTimeOffset exp = m.TryGetProperty("expires_at", out var ex) && ex.ValueKind == JsonValueKind.String && DateTimeOffset.TryParse(ex.GetString(), out var d) ? d : DateTimeOffset.MinValue;
            if (best is null || exp > bestExp) { best = name; bestExp = exp; }
        }
        return (best, n);
    }

    public void Dispose() => _http.Dispose();
}
