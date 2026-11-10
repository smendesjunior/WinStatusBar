using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using WinStatusBar.Core.Ai;
using WinStatusBar.Core.Ai.Codex;

namespace WinStatusBar.Services;

/// <summary>
/// Andamento das conversas de chat (ChatGPT e Claude), que rodam na nuvem e não deixam arquivos locais.
/// Lê a janela do aplicativo pela acessibilidade do Windows (UI Automation), somente leitura, com consultas
/// direcionadas e só enquanto o aplicativo está aberto:
///  • ChatGPT: o item da barra lateral da conversa em andamento traz um indicador "Processando" (StatusBar) — dá o título.
///  • Claude: a área da conversa anuncia "Claude está respondendo"; a tela de sessão do Code é ignorada (já vem do Claude Code).
/// </summary>
public sealed class ChatActivityProvider : IAgentActivityProvider, IDisposable
{
    public enum App { ChatGpt, Claude, Qira }

    private static readonly TimeSpan BusyInterval = TimeSpan.FromSeconds(2.5), IdleInterval = TimeSpan.FromSeconds(5);
    private DateTime _lastProbe = DateTime.MinValue, _lastUsed = DateTime.MinValue; // ChatProbeSchedule
    private static readonly TimeSpan KeepFinished = TimeSpan.FromMinutes(20);
    /// <summary>Nomes do indicador da barra lateral do ChatGPT que significam trabalho em andamento (pt/en).</summary>
    private static readonly Regex ChatGptBusy = new(@"process|pensa|think|pesquis|search|gera|generat|respond|trabalh|work|escrev|writ|analis|analy|execut|running|carregand|loading",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly string[] ClaudeResponding = ["Claude está respondendo", "Claude is responding", "Claude está pensando", "Claude is thinking"];
    private static readonly string[] ClaudeCodeView = ["Detalhes da sessão", "Session details"];

    private readonly App _app;
    private readonly string _process;
    private readonly Func<bool> _enabled;
    private readonly Func<IEnumerable<string?>> _skipTitles;
    private readonly object _gate = new();
    private readonly Dictionary<string, AgentSession> _sessions = new(StringComparer.OrdinalIgnoreCase);
    private CancellationTokenSource? _cts;
    private IUIAutomation? _uia;

    public AgentKind Kind { get; }
    public event EventHandler? SessionsChanged;
    /// <summary>Títulos das conversas do Codex (só o ChatGPT usa): uma conversa com registro é deixada para o registro.</summary>
    public Func<IEnumerable<string?>>? CodexThreadTitles { get; set; }
    /// <summary>Motivo curto do bloqueio do Codex, ou null quando livre (só o ChatGPT usa).</summary>
    public Func<string?>? CodexBlockedReason { get; set; }
    /// <summary>Duração da última consulta à janela (diagnóstico do custo).</summary>
    public TimeSpan LastProbeCost { get; private set; }

    /// <param name="skipTitles">Títulos já acompanhados por outro provedor (ex.: tarefas Work do Codex), para não duplicar.</param>
    public ChatActivityProvider(App app, Func<bool> enabled, Func<IEnumerable<string?>>? skipTitles = null)
    {
        _app = app;
        _enabled = enabled;
        _skipTitles = skipTitles ?? (() => []);
        Kind = app == App.Qira ? AgentKind.Qira : app == App.ChatGpt ? AgentKind.Codex : AgentKind.Claude;
        _process = app == App.Qira ? "QuantumAI.Hub" : app == App.ChatGpt ? "ChatGPT" : "claude";
    }

    public IReadOnlyList<AgentSession> Sessions
    {
        get { lock (_gate) return _sessions.Values.Select(s => s.Clone()).ToList(); }
    }

    public void Start()
    {
        if (_cts is not null) return;
        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        var thread = new Thread(() => Loop(token)) { IsBackground = true, Name = "ChatActivity-" + _app, Priority = ThreadPriority.BelowNormal };
        thread.SetApartmentState(ApartmentState.MTA);
        thread.Start();
    }

    public void Dispose() { _cts?.Cancel(); _cts = null; }

    private void Loop(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            // Tela apagada ou sessão bloqueada: não consulta as janelas (cada consulta também faz o app consultado
            // trabalhar: ~2% de um núcleo no ChatGPT, medido em 10/11/2026); volta assim que a tela acender.
            if (WinStatusBar.Core.Presence.Away && !WinStatusBar.Core.Presence.WaitVisible(token)) return;
            bool open = false;
            try
            {
                if (_enabled())
                {
                    var windows = Windows();
                    open = windows.Count > 0;
                    var now = DateTime.UtcNow;
                    if (open && windows.Contains(GetForegroundWindow())) _lastUsed = now;
                    if (!open || ChatProbeSchedule.ShouldProbe(now, _lastProbe, _lastUsed, HasRunning()))
                    {
                        _lastProbe = now;
                        var sw = Stopwatch.StartNew();
                        var found = open ? Probe(windows) : [];
                        LastProbeCost = sw.Elapsed;
                        Apply(found);
                    }
                }
                else Apply([]);
            }
            catch (Exception ex)
            {
                WinStatusBar.App.Log($"Chat {_app}: {ex.GetType().Name}: {ex.Message}");
                _uia = null; // recria na próxima volta
            }
            token.WaitHandle.WaitOne(open ? BusyInterval : IdleInterval);
        }
    }

    private bool HasRunning()
    {
        lock (_gate) return _sessions.Values.Any(s => s.State == AgentState.Running);
    }

    /// <summary>Conversas em andamento agora: título → legenda.</summary>
    private List<(string Title, string Caption)> Probe(List<IntPtr> windows)
    {
        _uia ??= (IUIAutomation)new CUIAutomation();
        var result = new List<(string, string)>();
        foreach (var hwnd in windows)
        {
            var root = _uia.ElementFromHandle(hwnd);
            if (root is null) continue;
            if (_app == App.ChatGpt) ProbeChatGpt(root, result);
            else if (_app == App.Claude) ProbeClaude(root, result);
            else
            {
                // A visible stop-generation button is evidence; background Lenovo processes are not.
                var stop = root.FindFirst(TreeScopeDescendants, _uia!.CreatePropertyCondition(30011, "btn_stop_fetch"))
                    ?? root.FindFirst(TreeScopeDescendants, NameIn(["Stop generating", "Parar geração", "Parar de gerar"]));
                if (stop is not null && QiraActivity.IsGenerating(stop.GetCurrentPropertyValue(30011) as string, NameOf(stop),
                    stop.GetCurrentPropertyValue(30022) is false, stop.GetCurrentPropertyValue(30010) is true,
                    stop.GetCurrentPropertyValue(30003) is int type && type == 50000))
                    result.Add(("Conversa no Lenovo Qira", "Respondendo no aplicativo Lenovo · modo controlado pelo Qira"));
            }
        }
        return result;
    }

    private void ProbeChatGpt(IUIAutomationElement root, List<(string, string)> result)
    {
        var bars = root.FindAll(TreeScopeDescendants, _uia!.CreatePropertyCondition(ControlTypeProperty, StatusBarControl));
        if (bars is null) return;
        var walker = _uia.get_ControlViewWalker();
        for (int i = 0, n = bars.get_Length(); i < n; i++)
        {
            var bar = bars.GetElement(i);
            string status = NameOf(bar);
            if (status.Length == 0 || !ChatGptBusy.IsMatch(status)) continue;
            // O indicador fica dentro do botão da conversa na barra lateral: o nome do botão é o título.
            string title = NameOf(walker.GetParentElement(bar));
            if (title.Length == 0) title = Localization.T("Conversa no ChatGPT");
            result.Add((title, status));
        }
    }

    private void ProbeClaude(IUIAutomationElement root, List<(string, string)> result)
    {
        var responding = root.FindFirst(TreeScopeDescendants, NameIn(ClaudeResponding));
        if (responding is null) return;
        // Tela de sessão do Code: a atividade já vem das sessões do Claude Code.
        if (root.FindFirst(TreeScopeDescendants, NameIn(ClaudeCodeView)) is not null) return;
        result.Add((Localization.T("Conversa no Claude"), NameOf(responding)));
    }

    private IUIAutomationCondition NameIn(string[] names)
    {
        var conditions = names.Select(n => _uia!.CreatePropertyCondition(NameProperty, n)).ToArray();
        var c = conditions[0];
        for (int i = 1; i < conditions.Length; i++) c = _uia!.CreateOrCondition(c, conditions[i]);
        return c;
    }

    private static string NameOf(IUIAutomationElement? e)
    {
        if (e is null) return string.Empty;
        try { return (e.GetCurrentPropertyValue(NameProperty) as string)?.Trim() ?? string.Empty; }
        catch { return string.Empty; }
    }

    private void Apply(List<(string Title, string Caption)> found)
    {
        var skip = _skipTitles().Where(t => !string.IsNullOrWhiteSpace(t)).ToList();
        var now = DateTimeOffset.Now;
        bool changed = false;
        lock (_gate)
        {
            var live = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (title, caption) in found)
            {
                if (ChatProbeSchedule.MatchesThread(title, skip)) continue;
                string id = $"{(_app == App.Qira ? "qira" : _app == App.ChatGpt ? "chatgpt" : "claude-chat")}:{title}";
                if (!live.Add(id)) continue;
                if (!_sessions.TryGetValue(id, out var s))
                {
                    s = new AgentSession { Kind = Kind, Id = id, ObservationSource = "Acessibilidade da janela (inferência)", AgentName = _app == App.Qira ? "Lenovo Qira" : _app == App.ChatGpt ? "ChatGPT" : "Claude", Title = title, StartedAt = now };
                    _sessions[id] = s;
                    changed = true;
                }
                if (s.State != AgentState.Running)
                    WinStatusBar.App.Log($"Chat {_app}: em andamento · {title} · {caption} (consulta {LastProbeCost.TotalMilliseconds:0} ms)");
                if (s.State != AgentState.Running || s.Caption != caption) { s.State = AgentState.Running; s.Caption = caption; s.StartedAt ??= now; changed = true; }
                s.LastActivity = now;
            }
            foreach (var s in _sessions.Values.Where(s => !live.Contains(s.Id)).ToList())
            {
                if (s.State == AgentState.Running)
                {
                    // Só o ChatGPT consulta o Codex: com registro, o registro manda; sem registro e com o Codex sem cota, foi a cota.
                    string? blocked = null;
                    var action = _app == App.ChatGpt
                        ? ChatProbeSchedule.OnIndicatorGone(ChatProbeSchedule.MatchesThread(s.Title, CodexThreadTitles?.Invoke() ?? []),
                            blocked = CodexBlockedReason?.Invoke())
                        : IndicatorGoneAction.Unconfirmed;
                    double seconds = (now - (s.StartedAt ?? now)).TotalSeconds;
                    switch (action)
                    {
                        case IndicatorGoneAction.Remove:
                            WinStatusBar.App.Log($"Chat {_app}: indicador ausente · {s.Title} · a conversa está no registro do Codex, que define o estado");
                            _sessions.Remove(s.Id);
                            break;
                        case IndicatorGoneAction.BlockedByQuota:
                            WinStatusBar.App.Log($"Chat {_app}: indicador ausente · {s.Title} · Codex sem cota ({blocked}) após {seconds:0} s");
                            s.State = AgentState.Interrupted; s.Caption = CodexQuotaText.ChatRefused(blocked!); s.LastActivity = now; s.StartedAt = null;
                            break;
                        default:
                            WinStatusBar.App.Log($"Chat {_app}: indicador ausente; conclusão não confirmada · {s.Title} após {seconds:0} s");
                            s.State = AgentState.Unknown; s.Caption = "Indicador não observado · conclusão não confirmada"; s.LastActivity = now; s.StartedAt = null;
                            break;
                    }
                    changed = true;
                }
                else if (now - s.LastActivity > KeepFinished) { _sessions.Remove(s.Id); changed = true; }
            }
        }
        if (changed) SessionsChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Janelas visíveis de nível superior do aplicativo (Chromium/Electron). Parte das janelas, não dos processos:
    /// listar todos os processos do PC (GetProcessesByName) a cada 2,5 s custava mais do que achar as janelas, e o nome
    /// do processo de cada janela Chromium fica guardado enquanto ela existir.
    /// </summary>
    private List<IntPtr> Windows()
    {
        if (_app == App.Qira) return QiraAppBridge.Windows();
        var list = new List<IntPtr>();
        var seen = new HashSet<uint>();
        var cls = new StringBuilder(64);
        EnumWindows((h, _) =>
        {
            if (!IsWindowVisible(h)) return true;
            cls.Clear(); GetClassName(h, cls, cls.Capacity);
            if (cls.ToString() != "Chrome_WidgetWin_1" || GetWindowTextLength(h) == 0) return true;
            GetWindowThreadProcessId(h, out uint pid);
            seen.Add(pid);
            if (!_processNames.TryGetValue(pid, out var name)) _processNames[pid] = name = ProcessName(pid);
            if (string.Equals(name, _process, StringComparison.OrdinalIgnoreCase)) list.Add(h);
            return true;
        }, IntPtr.Zero);
        foreach (var gone in _processNames.Keys.Where(p => !seen.Contains(p)).ToList()) _processNames.Remove(gone);
        return list;
    }

    /// <summary>Nome do processo de cada janela Chromium vista na última volta (PID → nome, sem extensão).</summary>
    private readonly Dictionary<uint, string> _processNames = new();

    private static string ProcessName(uint pid)
    {
        IntPtr h = OpenProcess(0x1000 /* PROCESS_QUERY_LIMITED_INFORMATION */, false, pid);
        if (h == IntPtr.Zero) return string.Empty;
        try
        {
            var path = new StringBuilder(1024);
            int size = path.Capacity;
            return QueryFullProcessImageNameW(h, 0, path, ref size) ? System.IO.Path.GetFileNameWithoutExtension(path.ToString()) : string.Empty;
        }
        finally { CloseHandle(h); }
    }

    // ------------------------------------------------------------------ UI Automation (UIAutomationCore.dll, já no Windows)
    // Somente os métodos usados; os anteriores ocupam seus lugares na tabela da interface (ordem do UIAutomationClient.h).
    private const int TreeScopeDescendants = 4, ControlTypeProperty = 30003, NameProperty = 30005, StatusBarControl = 50017;

    [ComImport, Guid("ff48dba4-60ef-4201-aa87-54103eef594e")] private class CUIAutomation { }

    [ComImport, Guid("30cbe57d-d9d0-452a-ab13-7ac5ac4825ee"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IUIAutomation
    {
        void CompareElements(); void CompareRuntimeIds(); void GetRootElement();
        IUIAutomationElement? ElementFromHandle(IntPtr hwnd);
        void ElementFromPoint(); void GetFocusedElement(); void GetRootElementBuildCache(); void ElementFromHandleBuildCache();
        void ElementFromPointBuildCache(); void GetFocusedElementBuildCache(); void CreateTreeWalker();
        IUIAutomationTreeWalker get_ControlViewWalker();
        void get_ContentViewWalker(); void get_RawViewWalker();
        void get_RawViewCondition(); void get_ControlViewCondition(); void get_ContentViewCondition();
        void CreateCacheRequest(); void CreateTrueCondition(); void CreateFalseCondition();
        IUIAutomationCondition CreatePropertyCondition(int propertyId, [MarshalAs(UnmanagedType.Struct)] object value);
        void CreatePropertyConditionEx(); void CreateAndCondition(); void CreateAndConditionFromArray(); void CreateAndConditionFromNativeArray();
        IUIAutomationCondition CreateOrCondition(IUIAutomationCondition c1, IUIAutomationCondition c2);
    }

    [ComImport, Guid("352ffba8-0973-437c-a61f-f64cafd81df9"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IUIAutomationCondition { }

    [ComImport, Guid("4042c624-389c-4afc-a630-9df854a541fc"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IUIAutomationTreeWalker
    {
        IUIAutomationElement? GetParentElement(IUIAutomationElement element);
    }

    [ComImport, Guid("14314595-b4bc-4055-95f2-58f2e42c9855"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IUIAutomationElementArray
    {
        int get_Length();
        IUIAutomationElement GetElement(int index);
    }

    [ComImport, Guid("d22108aa-8ac5-49a5-837b-37bbb3d7591e"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IUIAutomationElement
    {
        void SetFocus(); void GetRuntimeId();
        IUIAutomationElement? FindFirst(int scope, IUIAutomationCondition condition);
        IUIAutomationElementArray? FindAll(int scope, IUIAutomationCondition condition);
        void FindFirstBuildCache(); void FindAllBuildCache(); void BuildUpdatedCache();
        [return: MarshalAs(UnmanagedType.Struct)] object? GetCurrentPropertyValue(int propertyId);
    }

    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    [DllImport("user32.dll")] private static extern int GetWindowTextLength(IntPtr hwnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr hwnd, StringBuilder name, int max);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("kernel32.dll")] private static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern bool QueryFullProcessImageNameW(IntPtr process, uint flags, StringBuilder name, ref int size);
}
