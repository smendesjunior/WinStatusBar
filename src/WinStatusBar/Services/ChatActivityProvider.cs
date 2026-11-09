using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using WinStatusBar.Core.Ai;

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
            bool open = false;
            try
            {
                if (_enabled())
                {
                    var windows = Windows();
                    open = windows.Count > 0;
                    var sw = Stopwatch.StartNew();
                    var found = open ? Probe(windows) : [];
                    LastProbeCost = sw.Elapsed;
                    Apply(found);
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
        var skip = new HashSet<string>(_skipTitles().Where(t => !string.IsNullOrWhiteSpace(t))!, StringComparer.OrdinalIgnoreCase);
        var now = DateTimeOffset.Now;
        bool changed = false;
        lock (_gate)
        {
            var live = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (title, caption) in found)
            {
                if (skip.Contains(title)) continue;
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
                    WinStatusBar.App.Log($"Chat {_app}: indicador ausente; conclusão não confirmada · {s.Title} após {(now - (s.StartedAt ?? now)).TotalSeconds:0} s");
                    s.State = AgentState.Unknown; s.Caption = "Indicador não observado · conclusão não confirmada"; s.LastActivity = now; s.StartedAt = null;
                    changed = true;
                }
                else if (now - s.LastActivity > KeepFinished) { _sessions.Remove(s.Id); changed = true; }
            }
        }
        if (changed) SessionsChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Janelas visíveis de nível superior do aplicativo (Chromium/Electron).</summary>
    private List<IntPtr> Windows()
    {
        if (_app == App.Qira) return QiraAppBridge.Windows();
        var pids = new HashSet<uint>();
        foreach (var p in Process.GetProcessesByName(_process)) { pids.Add((uint)p.Id); p.Dispose(); }
        var list = new List<IntPtr>();
        if (pids.Count == 0) return list;
        var cls = new StringBuilder(64);
        EnumWindows((h, _) =>
        {
            if (!IsWindowVisible(h)) return true;
            GetWindowThreadProcessId(h, out uint pid);
            if (!pids.Contains(pid)) return true;
            cls.Clear(); GetClassName(h, cls, cls.Capacity);
            if (cls.ToString() == "Chrome_WidgetWin_1" && GetWindowTextLength(h) > 0) list.Add(h);
            return true;
        }, IntPtr.Zero);
        return list;
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
}
