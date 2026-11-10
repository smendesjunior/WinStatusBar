using System.Text.RegularExpressions;

namespace WinStatusBar.Core.Ai.Codex.ApiResume;

/// <summary>Estado da trava de escrita de uma conversa do Codex. Unknown é tratado como Held.</summary>
public enum ThreadLockState { Free, Held, Unknown }

/// <summary>
/// O app do Codex segura ~/.codex/thread-writer-locks/&lt;id&gt;.lock (trava de 1 byte) de toda conversa que carregou,
/// até fechar. Com a trava presa, o codex exec resume falha em 1 s sem custo ("already has an active writer").
/// A sondagem nunca cria o arquivo e solta a trava na hora.
/// </summary>
public static class CodexThreadLock
{
    private static readonly Regex ThreadId = new(@"^[0-9A-Za-z-]{8,64}$", RegexOptions.CultureInvariant);
    private const int SharingViolation = unchecked((int)0x80070020), LockViolation = unchecked((int)0x80070021);

    public static string PathFor(string codexHome, string threadId)
    {
        if (!ThreadId.IsMatch(threadId)) throw new ArgumentException("Id de conversa inválido.", nameof(threadId));
        return Path.Combine(codexHome, "thread-writer-locks", threadId + ".lock");
    }

    public static ThreadLockState Probe(string lockPath)
    {
        try
        {
            using var fs = new FileStream(lockPath, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete);
            fs.Lock(0, 1);
            try { return ThreadLockState.Free; }
            finally { fs.Unlock(0, 1); }
        }
        catch (FileNotFoundException) { return ThreadLockState.Free; }
        catch (DirectoryNotFoundException) { return ThreadLockState.Free; }
        catch (IOException e) when (e.HResult is SharingViolation or LockViolation) { return ThreadLockState.Held; }
        catch (Exception) { return ThreadLockState.Unknown; }
    }
}
