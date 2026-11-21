using System.Collections;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace WinStatusBar.Core.Ai.Antigravity;

/// <summary>
/// Programa de terminal num console invisível do Windows (ConPTY): tudo o que ele desenha chega em <see cref="Output"/>
/// e as teclas vão por <see cref="Write"/>. Nenhuma janela aparece e nada toca o teclado ou o mouse do Windows.
/// Usado para ler telas que o programa só mostra no modo interativo (ex.: /usage do agy).
/// </summary>
public sealed class PseudoConsole : IDisposable
{
    private readonly IntPtr _console;
    private readonly IntPtr _process;
    private readonly FileStream _input;
    private readonly StringBuilder _output = new();
    private readonly object _sync = new();
    private bool _disposed;

    private PseudoConsole(IntPtr console, IntPtr process, FileStream input, FileStream output)
    {
        _console = console; _process = process; _input = input;
        var reader = new Thread(() =>
        {
            var buffer = new byte[8192];
            var decoder = Encoding.UTF8.GetDecoder();
            var chars = new char[8192];
            try
            {
                int n;
                while ((n = output.Read(buffer, 0, buffer.Length)) > 0)
                {
                    int c = decoder.GetChars(buffer, 0, n, chars, 0);
                    lock (_sync) _output.Append(chars, 0, c);
                }
            }
            catch { }
            finally { output.Dispose(); }
        }) { IsBackground = true, Name = "PseudoConsole" };
        reader.Start();
    }

    /// <summary>Tudo o que o programa escreveu até agora (com as sequências de controle do terminal).</summary>
    public string Output { get { lock (_sync) return _output.ToString(); } }

    public bool HasExited => WaitForSingleObject(_process, 0) == 0;

    public void Write(string text)
    {
        if (_disposed) return;
        var bytes = Encoding.UTF8.GetBytes(text);
        try { _input.Write(bytes, 0, bytes.Length); _input.Flush(); } catch (IOException) { }
    }

    /// <summary>Inicia <paramref name="commandLine"/> em <paramref name="workDir"/> num console de <paramref name="columns"/> × <paramref name="rows"/>.</summary>
    public static PseudoConsole Start(string commandLine, string workDir, short columns = 160, short rows = 50,
        IReadOnlyDictionary<string, string>? environmentOverrides = null)
    {
        if (!CreatePipe(out var inRead, out var inWrite, IntPtr.Zero, 0)) throw new Win32Exception();
        if (!CreatePipe(out var outRead, out var outWrite, IntPtr.Zero, 0)) { inRead.Dispose(); inWrite.Dispose(); throw new Win32Exception(); }
        int hr = CreatePseudoConsole(new Coord { X = columns, Y = rows }, inRead, outWrite, 0, out var console);
        // O console ficou com as pontas dele; as cópias daqui não são mais necessárias.
        inRead.Dispose(); outWrite.Dispose();
        if (hr != 0) { inWrite.Dispose(); outRead.Dispose(); throw new Win32Exception(hr); }

        IntPtr attributes = IntPtr.Zero;
        IntPtr environment = IntPtr.Zero;
        try
        {
            IntPtr size = IntPtr.Zero;
            InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref size);
            attributes = Marshal.AllocHGlobal(size);
            if (!InitializeProcThreadAttributeList(attributes, 1, 0, ref size)) throw new Win32Exception();
            if (!UpdateProcThreadAttribute(attributes, 0, (IntPtr)PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE, console, (IntPtr)IntPtr.Size, IntPtr.Zero, IntPtr.Zero))
                throw new Win32Exception();
            var startup = new StartupInfoEx { lpAttributeList = attributes };
            startup.StartupInfo.cb = Marshal.SizeOf<StartupInfoEx>();
            if (environmentOverrides is not null)
            {
                var variables = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (DictionaryEntry entry in Environment.GetEnvironmentVariables())
                    variables[(string)entry.Key] = (string)entry.Value!;
                foreach (var entry in environmentOverrides)
                    variables[entry.Key] = entry.Value;
                string block = string.Join('\0', variables.Select(entry => $"{entry.Key}={entry.Value}")) + "\0";
                environment = Marshal.StringToHGlobalUni(block);
            }
            if (!CreateProcessW(null, new StringBuilder(commandLine), IntPtr.Zero, IntPtr.Zero, false,
                    EXTENDED_STARTUPINFO_PRESENT | CREATE_UNICODE_ENVIRONMENT, environment, workDir, ref startup, out var info))
                throw new Win32Exception();
            CloseHandle(info.hThread);
            return new PseudoConsole(console, info.hProcess,
                new FileStream(inWrite, FileAccess.Write), new FileStream(outRead, FileAccess.Read));
        }
        catch
        {
            ClosePseudoConsole(console);
            inWrite.Dispose(); outRead.Dispose();
            throw;
        }
        finally
        {
            if (environment != IntPtr.Zero) Marshal.FreeHGlobal(environment);
            if (attributes != IntPtr.Zero) { DeleteProcThreadAttributeList(attributes); Marshal.FreeHGlobal(attributes); }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        // Nunca fica um processo sem dono: encerra o programa e fecha o console (o leitor termina sozinho).
        try { if (!HasExited) TerminateProcess(_process, 1); } catch { }
        try { _input.Dispose(); } catch { }
        try { ClosePseudoConsole(_console); } catch { }
        CloseHandle(_process);
    }

    // ---------------------------------------------------------------- Win32
    private const uint EXTENDED_STARTUPINFO_PRESENT = 0x00080000;
    private const uint CREATE_UNICODE_ENVIRONMENT = 0x00000400;
    private const int PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE = 0x00020016;

    [StructLayout(LayoutKind.Sequential)] private struct Coord { public short X; public short Y; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfo
    {
        public int cb; public string? lpReserved; public string? lpDesktop; public string? lpTitle;
        public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public short wShowWindow, cbReserved2; public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
    }

    [StructLayout(LayoutKind.Sequential)] private struct StartupInfoEx { public StartupInfo StartupInfo; public IntPtr lpAttributeList; }
    [StructLayout(LayoutKind.Sequential)] private struct ProcessInformation { public IntPtr hProcess, hThread; public int dwProcessId, dwThreadId; }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CreatePipe(out SafeFileHandle read, out SafeFileHandle write, IntPtr attributes, int size);
    [DllImport("kernel32.dll")]
    private static extern int CreatePseudoConsole(Coord size, SafeFileHandle input, SafeFileHandle output, uint flags, out IntPtr console);
    [DllImport("kernel32.dll")]
    private static extern void ClosePseudoConsole(IntPtr console);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool InitializeProcThreadAttributeList(IntPtr list, int count, int flags, ref IntPtr size);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool UpdateProcThreadAttribute(IntPtr list, uint flags, IntPtr attribute, IntPtr value, IntPtr size, IntPtr previous, IntPtr returnSize);
    [DllImport("kernel32.dll")]
    private static extern void DeleteProcThreadAttributeList(IntPtr list);
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CreateProcessW(string? application, StringBuilder commandLine, IntPtr processAttributes, IntPtr threadAttributes,
        bool inheritHandles, uint flags, IntPtr environment, string? currentDirectory, ref StartupInfoEx startup, out ProcessInformation info);
    [DllImport("kernel32.dll")] private static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);
    [DllImport("kernel32.dll")] private static extern bool TerminateProcess(IntPtr process, uint exitCode);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
}
