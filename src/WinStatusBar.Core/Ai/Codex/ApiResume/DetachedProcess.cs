using System.Collections;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace WinStatusBar.Core.Ai.Codex.ApiResume;

/// <summary>Processo iniciado pela barra: pid e horário de criação (protege contra pid reaproveitado).</summary>
public sealed record DetachedStart(int Pid, DateTimeOffset CreatedAt);

/// <summary>
/// Inicia o codex.exe sem janela e destacado da barra: entrada vazia (NUL) e saídas em ARQUIVOS (com um canal ligado à barra,
/// o Codex quebraria na próxima escrita depois de a barra fechar ou atualizar). Só esses 3 identificadores são herdados.
/// O filho não é aberto como grupo novo, para o Ctrl+C funcionar. O ambiente do filho é o da barra menos o GEMINI_API_KEY e mais o
/// CODEX_API_KEY, montado num bloco que é zerado logo depois do início; a barra nunca põe a chave no próprio ambiente.
/// </summary>
public static class DetachedProcess
{
    public static IReadOnlyDictionary<string, string?> ResumeEnvironment(string apiKey) =>
        new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase) { ["GEMINI_API_KEY"] = null, ["CODEX_API_KEY"] = apiKey };

    /// <summary>Só o codex.exe é aceito: passar pelo codex.cmd estragaria as aspas das pastas do OneDrive e o Ctrl+C.</summary>
    public static string? RequireCodexExe(string? found) =>
        found is not null && Path.GetFileName(found).Equals("codex.exe", StringComparison.OrdinalIgnoreCase) ? found : null;

    public static DetachedStart Start(string exe, IReadOnlyList<string> args, string workingDir, string stdoutFile, string stderrFile,
        IReadOnlyDictionary<string, string?> envChanges)
    {
        char[] env = EnvironmentBlock(Environment.GetEnvironmentVariables(), envChanges);
        var envHandle = GCHandle.Alloc(env, GCHandleType.Pinned);
        IntPtr nul = IntPtr.Zero, outFile = IntPtr.Zero, errFile = IntPtr.Zero, attributes = IntPtr.Zero, handleList = IntPtr.Zero;
        var pi = default(ProcessInformation);
        try
        {
            nul = OpenInheritable("NUL", GenericRead, OpenExisting);
            outFile = OpenInheritable(stdoutFile, GenericWrite, CreateAlways);
            errFile = OpenInheritable(stderrFile, GenericWrite, CreateAlways);

            // Lista explícita de handles herdados: só a entrada vazia e os dois arquivos.
            handleList = Marshal.AllocHGlobal(IntPtr.Size * 3);
            Marshal.WriteIntPtr(handleList, 0, nul);
            Marshal.WriteIntPtr(handleList, IntPtr.Size, outFile);
            Marshal.WriteIntPtr(handleList, IntPtr.Size * 2, errFile);
            IntPtr size = IntPtr.Zero;
            InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref size);
            attributes = Marshal.AllocHGlobal(size);
            if (!InitializeProcThreadAttributeList(attributes, 1, 0, ref size)) throw new Win32Exception(Marshal.GetLastWin32Error());
            if (!UpdateProcThreadAttribute(attributes, 0, ProcThreadAttributeHandleList, handleList, (IntPtr)(IntPtr.Size * 3), IntPtr.Zero, IntPtr.Zero))
                throw new Win32Exception(Marshal.GetLastWin32Error());

            var si = new StartupInfoEx();
            si.StartupInfo.cb = Marshal.SizeOf<StartupInfoEx>();
            si.StartupInfo.dwFlags = StartfUseStdHandles;
            si.StartupInfo.hStdInput = nul;
            si.StartupInfo.hStdOutput = outFile;
            si.StartupInfo.hStdError = errFile;
            si.lpAttributeList = attributes;

            // O filho herda a marca "ignorar Ctrl+C" de quem o inicia (ex.: um terminal ou executor de testes que a ligou);
            // com ela, o Parar não chegaria. A barra não tem console, então liberar o Ctrl+C aqui não a expõe a nada.
            SetConsoleCtrlHandler(IntPtr.Zero, false);
            uint flags = CreateNoWindow | CreateUnicodeEnvironment | ExtendedStartupInfoPresent;
            string commandLine = WindowsCommandLine.Join(exe, args);
            bool ok = CreateProcessW(exe, new StringBuilder(commandLine), IntPtr.Zero, IntPtr.Zero, true, flags | CreateBreakawayFromJob,
                envHandle.AddrOfPinnedObject(), workingDir, ref si, out pi);
            int error = ok ? 0 : Marshal.GetLastWin32Error();
            if (!ok && error == 5) // o Windows negou sair do grupo de processos da barra: inicia sem isso
            {
                ok = CreateProcessW(exe, new StringBuilder(commandLine), IntPtr.Zero, IntPtr.Zero, true, flags,
                    envHandle.AddrOfPinnedObject(), workingDir, ref si, out pi);
                error = ok ? 0 : Marshal.GetLastWin32Error();
            }
            if (!ok) throw new Win32Exception(error); // a mensagem do Windows nunca leva o ambiente
            var created = GetProcessTimes(pi.hProcess, out long creation, out _, out _, out _)
                ? DateTimeOffset.FromFileTime(creation) : DateTimeOffset.Now;
            return new DetachedStart(pi.dwProcessId, created);
        }
        finally
        {
            Array.Clear(env);
            envHandle.Free();
            if (pi.hProcess != IntPtr.Zero) CloseHandle(pi.hProcess);
            if (pi.hThread != IntPtr.Zero) CloseHandle(pi.hThread);
            foreach (var h in new[] { nul, outFile, errFile })
                if (h != IntPtr.Zero && h != InvalidHandle) CloseHandle(h);
            if (attributes != IntPtr.Zero) { DeleteProcThreadAttributeList(attributes); Marshal.FreeHGlobal(attributes); }
            if (handleList != IntPtr.Zero) Marshal.FreeHGlobal(handleList);
        }
    }

    /// <summary>É o mesmo processo: vivo, criado no mesmo horário (±2 s) e com a mesma imagem.</summary>
    public static bool IsSame(int pid, DateTimeOffset createdAt, string image = "codex.exe")
    {
        if (pid <= 0) return false;
        IntPtr h = OpenProcess(ProcessQueryLimitedInformation, false, pid);
        if (h == IntPtr.Zero) return false;
        try
        {
            if (!GetExitCodeProcess(h, out uint code) || code != StillActive) return false;
            if (!GetProcessTimes(h, out long creation, out _, out _, out _)) return false;
            if ((DateTimeOffset.FromFileTime(creation) - createdAt).Duration() > TimeSpan.FromSeconds(2)) return false;
            var name = new StringBuilder(1024);
            int len = name.Capacity;
            if (!QueryFullProcessImageNameW(h, 0, name, ref len)) return false;
            return Path.GetFileName(name.ToString()).Equals(image, StringComparison.OrdinalIgnoreCase);
        }
        finally { CloseHandle(h); }
    }

    /// <summary>Espera o processo terminar; volta na hora se ele já não for o mesmo.</summary>
    public static async Task WaitForExitAsync(int pid, DateTimeOffset createdAt, string image, CancellationToken ct)
    {
        while (IsSame(pid, createdAt, image)) await Task.Delay(500, ct).ConfigureAwait(false);
    }

    /// <summary>Encerra o processo e os filhos, só depois de conferir que é o mesmo processo. Nunca é chamado sozinho pela barra.</summary>
    public static bool KillTree(int pid, DateTimeOffset createdAt, string image = "codex.exe")
    {
        if (!IsSame(pid, createdAt, image)) return false;
        try
        {
            using var p = Process.GetProcessById(pid);
            p.Kill(entireProcessTree: true);
            return true;
        }
        catch (Exception) { return false; }
    }

    /// <summary>Bloco de ambiente Unicode ("K=V\0…\0\0"), ordenado sem diferenciar maiúsculas; null nas mudanças remove a variável.</summary>
    internal static char[] EnvironmentBlock(IDictionary current, IReadOnlyDictionary<string, string?> changes)
    {
        var vars = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (DictionaryEntry e in current)
            if (e.Key is string k && k.Length > 0 && e.Value is string v) vars[k] = v;
        foreach (var (k, v) in changes)
        {
            if (v is null) vars.Remove(k);
            else vars[k] = v;
        }
        var sb = new StringBuilder();
        foreach (var (k, v) in vars) sb.Append(k).Append('=').Append(v).Append('\0');
        sb.Append('\0');
        var block = new char[sb.Length];
        sb.CopyTo(0, block, 0, sb.Length);
        sb.Clear();
        return block;
    }

    private static IntPtr OpenInheritable(string path, uint access, uint disposition)
    {
        var sa = new SecurityAttributes { nLength = Marshal.SizeOf<SecurityAttributes>(), bInheritHandle = true };
        IntPtr h = CreateFileW(path, access, FileShareRead | FileShareWrite | FileShareDelete, ref sa, disposition, FileAttributeNormal, IntPtr.Zero);
        if (h == InvalidHandle) throw new Win32Exception(Marshal.GetLastWin32Error(), $"Não foi possível abrir {Path.GetFileName(path)}.");
        return h;
    }

    // ---------------- Win32 ----------------
    private const uint GenericRead = 0x80000000, GenericWrite = 0x40000000;
    private const uint FileShareRead = 1, FileShareWrite = 2, FileShareDelete = 4;
    private const uint OpenExisting = 3, CreateAlways = 2, FileAttributeNormal = 0x80;
    private const uint CreateNoWindow = 0x08000000, CreateUnicodeEnvironment = 0x400, ExtendedStartupInfoPresent = 0x80000, CreateBreakawayFromJob = 0x01000000;
    private const int StartfUseStdHandles = 0x100;
    private const int ProcessQueryLimitedInformation = 0x1000;
    private const uint StillActive = 259;
    private static readonly IntPtr ProcThreadAttributeHandleList = (IntPtr)0x20002;
    private static readonly IntPtr InvalidHandle = new(-1);

    [StructLayout(LayoutKind.Sequential)]
    private struct SecurityAttributes { public int nLength; public IntPtr lpSecurityDescriptor; [MarshalAs(UnmanagedType.Bool)] public bool bInheritHandle; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfo
    {
        public int cb; public string? lpReserved; public string? lpDesktop; public string? lpTitle;
        public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public short wShowWindow, cbReserved2; public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInfoEx { public StartupInfo StartupInfo; public IntPtr lpAttributeList; }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation { public IntPtr hProcess, hThread; public int dwProcessId, dwThreadId; }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CreateProcessW(string? app, StringBuilder commandLine, IntPtr processAttributes, IntPtr threadAttributes,
        bool inheritHandles, uint flags, IntPtr environment, string? currentDirectory, ref StartupInfoEx startupInfo, out ProcessInformation info);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateFileW(string name, uint access, uint share, ref SecurityAttributes sa, uint disposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool InitializeProcThreadAttributeList(IntPtr list, int count, int flags, ref IntPtr size);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool UpdateProcThreadAttribute(IntPtr list, uint flags, IntPtr attribute, IntPtr value, IntPtr size, IntPtr previous, IntPtr returnSize);

    [DllImport("kernel32.dll")]
    private static extern void DeleteProcThreadAttributeList(IntPtr list);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(int access, bool inherit, int pid);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetProcessTimes(IntPtr process, out long creation, out long exit, out long kernel, out long user);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetExitCodeProcess(IntPtr process, out uint code);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool QueryFullProcessImageNameW(IntPtr process, int flags, StringBuilder name, ref int size);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetConsoleCtrlHandler(IntPtr handler, bool add);
}
