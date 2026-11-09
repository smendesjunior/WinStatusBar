using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using WinStatusBar.Core.Ai.Local;

namespace WinStatusBar.Services;

/// <summary>Processo de um aplicativo (linha expandida): rótulo é o título da janela ou o nome do executável.</summary>
internal sealed record AppProcess(uint Pid, string Label, long WorkingSet);

/// <summary>Aplicativo com janela aberta, agrupado como no Gerenciador de Tarefas (raiz + processos filhos).</summary>
internal sealed record AppGroup(uint RootPid, string Name, string? IconSource, IReadOnlyList<AppProcess> Processes, IReadOnlyList<IntPtr> Windows, bool Protected)
{
    public string Key => $"{RootPid}:{Name}";
}

/// <summary>
/// Lista os aplicativos com janelas visíveis (mesmos critérios da aba Processos › Aplicativos do Gerenciador de Tarefas):
/// janelas de nível superior visíveis, sem dono, não ocultas pelo DWM e que não sejam janelas de ferramenta.
/// Apps UWP hospedados pelo ApplicationFrameHost são atribuídos ao processo real do app.
/// </summary>
internal static class RunningApps
{
    private static readonly HashSet<string> ShellClasses = new(StringComparer.Ordinal) { "Progman", "WorkerW", "Shell_TrayWnd", "Shell_SecondaryTrayWnd" };
    /// <summary>Encerramento forçado nunca é oferecido: o shell e o host de apps UWP afetariam outras janelas.</summary>
    private static readonly HashSet<string> ProtectedImages = new(StringComparer.OrdinalIgnoreCase)
        { "explorer.exe", "ApplicationFrameHost.exe", "dwm.exe", "sihost.exe", "ShellExperienceHost.exe", "StartMenuExperienceHost.exe", "SearchHost.exe", "TextInputHost.exe" };
    private static readonly HashSet<string> HiddenImages = new(StringComparer.OrdinalIgnoreCase)
        { "TextInputHost.exe", "ShellExperienceHost.exe", "StartMenuExperienceHost.exe", "SearchHost.exe", "LockApp.exe" };
    private static readonly Dictionary<string, string> Descriptions = new(StringComparer.OrdinalIgnoreCase);

    public static List<AppGroup> Capture()
    {
        var procs = SystemSnapshot.ReadProcesses();
        uint self = (uint)Environment.ProcessId;
        var windowsByPid = new Dictionary<uint, List<(IntPtr Hwnd, string Title)>>();
        EnumWindows((hwnd, _) =>
        {
            if (!IsAppWindow(hwnd, out string title)) return true;
            GetWindowThreadProcessId(hwnd, out uint pid);
            if (ClassOf(hwnd) == "ApplicationFrameWindow")
            {
                uint host = pid;
                pid = HostedAppPid(hwnd, pid);
                if (pid == host) return true; // moldura vazia, sem app ativo dentro
            }
            if (pid == 0 || pid == self) return true;
            // Teclado virtual/painel de emojis: janela do sistema, não aparece como app no Gerenciador de Tarefas.
            if (procs.TryGetValue(pid, out var owner) && HiddenImages.Contains(owner.Name)) return true;
            if (!windowsByPid.TryGetValue(pid, out var list)) windowsByPid[pid] = list = new();
            list.Add((hwnd, title));
            return true;
        }, IntPtr.Zero);

        var children = new Dictionary<uint, List<uint>>();
        foreach (var p in procs.Values)
            if (p.ParentPid != p.Pid && procs.ContainsKey(p.ParentPid))
                (children.TryGetValue(p.ParentPid, out var l) ? l : children[p.ParentPid] = new()).Add(p.Pid);

        // Raiz do app: sobe enquanto o pai tiver o mesmo executável (ex.: janelas filhas do msedge.exe principal).
        uint RootOf(uint pid)
        {
            for (int guard = 0; guard < 64 && procs.TryGetValue(pid, out var p); guard++)
            {
                if (!procs.TryGetValue(p.ParentPid, out var parent) || parent.Pid == p.Pid
                    || !parent.Name.Equals(p.Name, StringComparison.OrdinalIgnoreCase)) break;
                pid = parent.Pid;
            }
            return pid;
        }
        var roots = new Dictionary<uint, List<(IntPtr, string)>>();
        foreach (var (pid, wins) in windowsByPid)
        {
            uint root = RootOf(pid);
            if (!roots.TryGetValue(root, out var list)) roots[root] = list = new();
            list.AddRange(wins);
        }

        var groups = new List<AppGroup>();
        foreach (var (root, wins) in roots)
        {
            if (!procs.TryGetValue(root, out var rootInfo)) continue;
            bool shell = ProtectedImages.Contains(rootInfo.Name);
            // Membros: raiz + descendentes, sem entrar na árvore de outro app com janela própria.
            // No shell (explorer) só conta o próprio explorer, não tudo o que foi aberto por ele.
            var members = new List<uint>();
            var stack = new Stack<uint>(); stack.Push(root);
            while (stack.Count > 0 && members.Count < 500)
            {
                uint pid = stack.Pop(); members.Add(pid);
                if (!children.TryGetValue(pid, out var kids)) continue;
                foreach (uint k in kids)
                {
                    if (k == self || roots.ContainsKey(k) || !procs.TryGetValue(k, out var kp)) continue;
                    if (shell && !kp.Name.Equals(rootInfo.Name, StringComparison.OrdinalIgnoreCase)) continue;
                    stack.Push(k);
                }
            }
            string? path = SystemSnapshot.ImagePath(root);
            string? aumid = WindowAumid(wins[0].Item1);
            // Apps da Store (WindowsApps) costumam ter descrição técnica no executável ("WhatsApp.Root"):
            // usa o nome exibido no menu Iniciar.
            bool packaged = path?.Contains(@"\WindowsApps\", StringComparison.OrdinalIgnoreCase) == true;
            string name = packaged && aumid is not null && AppsFolderName(aumid) is { } display ? display : Describe(path, rootInfo.Name);
            var titles = wins.GroupBy(w => w.Item1).ToDictionary(g => g.Key, g => g.First().Item2);
            var processes = members.Select(pid =>
            {
                string label = windowsByPid.TryGetValue(pid, out var pw) ? pw[0].Title : procs[pid].Name;
                return new AppProcess(pid, label, WorkingSet(pid));
            }).OrderByDescending(p => windowsByPid.ContainsKey(p.Pid)).ThenByDescending(p => p.WorkingSet).ToList();
            groups.Add(new AppGroup(root, name, aumid is not null ? @"shell:AppsFolder\" + aumid : path,
                processes, titles.Keys.ToList(), shell));
        }
        return groups.OrderBy(g => g.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    /// <summary>Fecha como o botão X: WM_CLOSE para cada janela do app. Retorna sem aguardar.</summary>
    public static void RequestClose(AppGroup app)
    {
        foreach (var hwnd in app.Windows) PostMessageW(hwnd, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
    }

    public static bool IsRunning(AppGroup app)
    {
        try { using var p = Process.GetProcessById((int)app.RootPid); return !p.HasExited; } catch { return false; }
    }

    /// <summary>Encerramento forçado só dos processos do grupo (nunca do shell).</summary>
    public static void ForceEnd(AppGroup app)
    {
        if (app.Protected) return;
        foreach (var member in app.Processes)
            try { using var p = Process.GetProcessById((int)member.Pid); p.Kill(); } catch { }
    }

    public static void SwitchTo(AppGroup app)
    {
        var hwnd = app.Windows.FirstOrDefault();
        if (hwnd == IntPtr.Zero) return;
        if (IsIconic(hwnd)) ShowWindow(hwnd, SW_RESTORE);
        SetForegroundWindow(hwnd);
    }

    private static string Describe(string? path, string image)
    {
        string fallback = Path.GetFileNameWithoutExtension(image);
        if (string.IsNullOrEmpty(path)) return fallback;
        lock (Descriptions)
        {
            if (Descriptions.TryGetValue(path, out var cached)) return cached;
            string result = fallback;
            try { var d = FileVersionInfo.GetVersionInfo(path).FileDescription?.Trim(); if (!string.IsNullOrEmpty(d)) result = d; } catch { }
            Descriptions[path] = result;
            return result;
        }
    }

    private static long WorkingSet(uint pid)
    {
        try { using var p = Process.GetProcessById((int)pid); return p.WorkingSet64; } catch { return 0; }
    }

    private static bool IsAppWindow(IntPtr hwnd, out string title)
    {
        title = string.Empty;
        if (!IsWindowVisible(hwnd) || GetWindow(hwnd, GW_OWNER) != IntPtr.Zero) return false;
        long ex = GetWindowLongPtrW(hwnd, GWL_EXSTYLE).ToInt64();
        if ((ex & WS_EX_TOOLWINDOW) != 0 && (ex & WS_EX_APPWINDOW) == 0) return false;
        // Oculta pelo próprio app (UWP suspenso/fechado) não conta; oculta pelo shell (outra área de trabalho virtual) conta,
        // como no Gerenciador de Tarefas.
        if (DwmGetWindowAttribute(hwnd, DWMWA_CLOAKED, out int cloaked, sizeof(int)) == 0 && cloaked != 0 && cloaked != DWM_CLOAKED_SHELL) return false;
        if (ShellClasses.Contains(ClassOf(hwnd))) return false;
        int len = GetWindowTextLengthW(hwnd);
        if (len == 0) return false;
        var sb = new StringBuilder(len + 1);
        GetWindowTextW(hwnd, sb, sb.Capacity);
        title = sb.ToString();
        return title.Length > 0;
    }

    private static string ClassOf(IntPtr hwnd)
    {
        var sb = new StringBuilder(256);
        return GetClassNameW(hwnd, sb, sb.Capacity) > 0 ? sb.ToString() : string.Empty;
    }

    private static uint HostedAppPid(IntPtr frame, uint hostPid)
    {
        uint found = hostPid;
        EnumChildWindows(frame, (child, _) =>
        {
            GetWindowThreadProcessId(child, out uint pid);
            if (pid != hostPid && pid != 0) { found = pid; return false; }
            return true;
        }, IntPtr.Zero);
        return found;
    }

    /// <summary>AppUserModelID da janela (ícone igual ao do menu Iniciar, inclusive apps da Store).</summary>
    private static string? WindowAumid(IntPtr hwnd)
    {
        try
        {
            var iid = typeof(IPropertyStore).GUID;
            if (SHGetPropertyStoreForWindow(hwnd, ref iid, out var store) != 0 || store is null) return null;
            try
            {
                var key = new PROPERTYKEY { fmtid = new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"), pid = 5 };
                if (store.GetValue(ref key, out var pv) != 0) return null;
                try { return pv.vt == 31 ? Marshal.PtrToStringUni(pv.p) : null; } finally { PropVariantClear(ref pv); }
            }
            finally { Marshal.ReleaseComObject(store); }
        }
        catch { return null; }
    }

    /// <summary>Nome exibido de um app pelo AppUserModelID (shell:AppsFolder), como no menu Iniciar.</summary>
    private static string? AppsFolderName(string aumid)
    {
        try
        {
            var iid = typeof(IShellItem).GUID;
            if (SHCreateItemFromParsingName(@"shell:AppsFolder\" + aumid, IntPtr.Zero, ref iid, out var item) != 0 || item is null) return null;
            try
            {
                if (item.GetDisplayName(0 /* SIGDN_NORMALDISPLAY */, out var p) != 0 || p == IntPtr.Zero) return null;
                try { var s = Marshal.PtrToStringUni(p); return string.IsNullOrWhiteSpace(s) ? null : s; }
                finally { Marshal.FreeCoTaskMem(p); }
            }
            finally { Marshal.ReleaseComObject(item); }
        }
        catch { return null; }
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHCreateItemFromParsingName(string path, IntPtr bc, ref Guid iid, [MarshalAs(UnmanagedType.Interface)] out IShellItem? item);

    [ComImport, Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItem
    {
        int BindToHandler(IntPtr bc, ref Guid bhid, ref Guid riid, out IntPtr ppv);
        int GetParent(out IntPtr parent);
        int GetDisplayName(uint sigdn, out IntPtr name);
        int GetAttributes(uint mask, out uint attribs);
        int Compare(IntPtr other, uint hint, out int order);
    }

    // ------------------------------------------------------------------ Win32
    private delegate bool EnumProc(IntPtr hwnd, IntPtr lParam);
    private const int GWL_EXSTYLE = -20, DWMWA_CLOAKED = 14, DWM_CLOAKED_SHELL = 2, SW_RESTORE = 9;
    private const uint GW_OWNER = 4, WM_CLOSE = 0x0010;
    private const long WS_EX_TOOLWINDOW = 0x80, WS_EX_APPWINDOW = 0x40000;
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumProc cb, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool EnumChildWindows(IntPtr parent, EnumProc cb, IntPtr lParam);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hwnd, int cmd);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr hwnd, uint cmd);
    [DllImport("user32.dll")] private static extern IntPtr GetWindowLongPtrW(IntPtr hwnd, int index);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowTextLengthW(IntPtr hwnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowTextW(IntPtr hwnd, StringBuilder text, int max);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassNameW(IntPtr hwnd, StringBuilder name, int max);
    [DllImport("user32.dll")] private static extern bool PostMessageW(IntPtr hwnd, uint msg, IntPtr w, IntPtr l);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attr, out int value, int size);
    [DllImport("shell32.dll")] private static extern int SHGetPropertyStoreForWindow(IntPtr hwnd, ref Guid iid, [MarshalAs(UnmanagedType.Interface)] out IPropertyStore? store);
    [DllImport("ole32.dll")] private static extern int PropVariantClear(ref PROPVARIANT pv);

    [StructLayout(LayoutKind.Sequential)] private struct PROPERTYKEY { public Guid fmtid; public uint pid; }
    [StructLayout(LayoutKind.Sequential)] private struct PROPVARIANT { public ushort vt; public ushort r1, r2, r3; public IntPtr p; public IntPtr p2; }
    [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        int GetCount(out uint count);
        int GetAt(uint index, out PROPERTYKEY key);
        int GetValue(ref PROPERTYKEY key, out PROPVARIANT value);
        int SetValue(ref PROPERTYKEY key, ref PROPVARIANT value);
        int Commit();
    }
}
