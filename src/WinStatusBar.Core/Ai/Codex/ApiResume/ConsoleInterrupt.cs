using System.Globalization;
using System.Runtime.InteropServices;

namespace WinStatusBar.Core.Ai.Codex.ApiResume;

public enum InterruptOutcome { Sent, NotSame, AttachFailed, GenerateFailed, HelperMissing, HelperFailed }

/// <summary>
/// Ctrl+C num codex.exe destacado, a sequência verificada no teste: AttachConsole(pid) → SetConsoleCtrlHandler(null,true) →
/// GenerateConsoleCtrlEvent(CTRL_C_EVENT,0) → 1 s → FreeConsole. O Codex grava turn_aborted e sai sem corromper a conversa.
/// Usado só pelo auxiliar sem console (WinStatusBar.Automation --parar-codex), nunca dentro da barra.
/// </summary>
public static class ConsoleInterrupt
{
    public const string Command = "--parar-codex";

    /// <summary>Argumentos do auxiliar: --parar-codex &lt;pid&gt; &lt;início em FILETIME&gt; [imagem].</summary>
    public static string[] HelperArgs(int pid, DateTimeOffset createdAt, string? image = null)
    {
        var args = new List<string> { Command, pid.ToString(CultureInfo.InvariantCulture), createdAt.ToFileTime().ToString(CultureInfo.InvariantCulture) };
        if (!string.IsNullOrEmpty(image)) args.Add(image);
        return [.. args];
    }

    public static bool TryParse(string[] args, out int pid, out DateTimeOffset createdAt, out string image)
    {
        pid = 0; createdAt = default; image = "codex.exe";
        if (args.Length is < 3 or > 4 || args[0] != Command) return false;
        if (!int.TryParse(args[1], NumberStyles.None, CultureInfo.InvariantCulture, out pid) || pid <= 0) return false;
        if (!long.TryParse(args[2], NumberStyles.None, CultureInfo.InvariantCulture, out long fileTime)) return false;
        try { createdAt = DateTimeOffset.FromFileTime(fileTime); }
        catch (ArgumentOutOfRangeException) { return false; }
        if (args.Length == 4)
        {
            if (string.IsNullOrWhiteSpace(args[3]) || args[3].IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return false;
            image = args[3];
        }
        return true;
    }

    /// <summary>Só no auxiliar: entra no console do processo, ignora o próprio Ctrl+C e manda o Ctrl+C a todos daquele console.</summary>
    public static InterruptOutcome Send(int pid)
    {
        FreeConsole();
        if (!AttachConsole((uint)pid)) return InterruptOutcome.AttachFailed;
        try
        {
            SetConsoleCtrlHandler(IntPtr.Zero, true);
            if (!GenerateConsoleCtrlEvent(CtrlCEvent, 0)) return InterruptOutcome.GenerateFailed;
            Thread.Sleep(1000); // o evento é entregue em outra thread; soltar o console antes perderia o Ctrl+C
            return InterruptOutcome.Sent;
        }
        finally { FreeConsole(); }
    }

    public static int ExitCode(InterruptOutcome o) => o switch
    {
        InterruptOutcome.Sent => 0,
        InterruptOutcome.NotSame => 3,
        InterruptOutcome.AttachFailed => 4,
        InterruptOutcome.GenerateFailed => 5,
        _ => 2,
    };

    public static InterruptOutcome FromExitCode(int code) => code switch
    {
        0 => InterruptOutcome.Sent,
        3 => InterruptOutcome.NotSame,
        4 => InterruptOutcome.AttachFailed,
        5 => InterruptOutcome.GenerateFailed,
        _ => InterruptOutcome.HelperFailed,
    };

    private const uint CtrlCEvent = 0;

    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool AttachConsole(uint pid);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool FreeConsole();
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool SetConsoleCtrlHandler(IntPtr handler, bool add);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GenerateConsoleCtrlEvent(uint ctrlEvent, uint processGroupId);
}
