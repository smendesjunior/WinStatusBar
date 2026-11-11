namespace WinStatusBar.Core.Ai;

/// <summary>
/// Combinado com o Codex IAs (Aplicativos IA\Codex IAs), que usa o mesmo Hub do Qira pelo seletor do Codex (o mesmo
/// código existe lá; nomes e caminho precisam ficar iguais nos dois):
/// - uma pergunta por vez entre os dois apps (trava do Windows com nome, numa thread própria; se o dono cair, o
///   Windows a libera como "abandonada" e o próximo assume);
/// - a hora do último reinício completo do Qira num arquivo comum, para os dois não reiniciarem em sequência.
/// Sem o Codex IAs instalado, ninguém mais pega a trava e nada muda.
/// </summary>
public static class QiraSharedTurn
{
    public const string LockName = @"Local\AplicativosIA.Qira.Vez";
    public static readonly TimeSpan RestartInterval = TimeSpan.FromMinutes(10);
    private static string RestartFile => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Qira compartilhado", "ultimo-reinicio.txt");

    /// <summary>
    /// Partes do Lenovo Qira (nomes de processo), numa lista só para a barra, as IAs no Codex e o executor. O reinício
    /// completo encerra todas juntas; nenhuma é encerrada sozinha (isso trava o resto).
    /// </summary>
    public static readonly IReadOnlyList<string> ProcessNames =
    [
        "QuantumAI.Hub", "nextmove-bridge", "ProcessHub", "LenovoQiraCore", "mcpMgmtService", "Lenovo Qira.Launcher", "Ludp.Listener",
        "Notification.Listener", "lenovo.pfm.pipe", "LocalAgent", "CreatorIMG.MCPServer", "CatchMeUpMcpServer", "CzCloudMcpServer", "file-search-service",
    ];

    /// <summary>Pasta de instalação do Qira; todas as partes rodam de dentro dela.</summary>
    public static string InstallRoot => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Lenovo", "Lenovo Qira");

    /// <summary>O executável roda da pasta do Qira? Nomes como "LocalAgent" ou "ProcessHub" existem em outros programas.</summary>
    public static bool IsQiraImage(string? imagePath) => IsQiraImage(imagePath, InstallRoot);

    public static bool IsQiraImage(string? imagePath, string root)
        => !string.IsNullOrWhiteSpace(imagePath)
           && imagePath.StartsWith(Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    /// <summary>Processo que é mesmo do Qira (pelo caminho do executável). Caminho ilegível: não é tratado como Qira.</summary>
    public static bool IsQiraProcess(System.Diagnostics.Process process)
    {
        try { return IsQiraImage(Local.SystemSnapshot.ImagePath((uint)process.Id)); }
        catch { return false; }
    }

    /// <summary>Pega a vez. Devolve null se não conseguiu no prazo (ou se foi cancelado). Liberar com Dispose.</summary>
    public static Task<IDisposable?> AcquireAsync(TimeSpan timeout, CancellationToken ct) => AcquireAsync(LockName, timeout, ct);

    /// <summary>Mesma trava com outro nome (testes).</summary>
    public static async Task<IDisposable?> AcquireAsync(string name, TimeSpan timeout, CancellationToken ct)
    {
        var acquired = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new ManualResetEventSlim(false);
        var cancelled = new ManualResetEvent(false);
        var thread = new Thread(() =>
        {
            using var mutex = new Mutex(false, name);
            bool owned = false;
            try
            {
                try { owned = WaitHandle.WaitAny([mutex, cancelled], timeout) == 0; }
                catch (AbandonedMutexException) { owned = true; } // o dono anterior caiu: a vez passa para cá
                acquired.TrySetResult(owned);
                if (owned) release.Wait();
            }
            catch (Exception ex) { acquired.TrySetException(ex); }
            finally { if (owned) try { mutex.ReleaseMutex(); } catch { } }
        }) { IsBackground = true, Name = "Vez do Qira" };
        thread.Start();
        using (ct.Register(() => { try { cancelled.Set(); } catch { } }))
        {
            bool owned = await acquired.Task;
            if (!owned) { thread.Join(1000); cancelled.Dispose(); release.Dispose(); return null; }
        }
        return new Holder(release, thread, cancelled);
    }

    private sealed class Holder(ManualResetEventSlim release, Thread thread, ManualResetEvent cancelled) : IDisposable
    {
        private int _done;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _done, 1) == 1) return;
            release.Set();
            thread.Join(2000);
            release.Dispose();
            cancelled.Dispose();
        }
    }

    /// <summary>Hora (UTC) do último reinício completo do Qira feito por qualquer um dos dois apps.</summary>
    public static DateTime LastRestartUtc()
    {
        try { return DateTime.TryParse(File.ReadAllText(RestartFile).Trim(), null, System.Globalization.DateTimeStyles.RoundtripKind, out var at) ? at.ToUniversalTime() : DateTime.MinValue; }
        catch { return DateTime.MinValue; }
    }

    public static void MarkRestart()
    {
        try { Directory.CreateDirectory(Path.GetDirectoryName(RestartFile)!); File.WriteAllText(RestartFile, DateTime.UtcNow.ToString("o")); } catch { }
    }
}
