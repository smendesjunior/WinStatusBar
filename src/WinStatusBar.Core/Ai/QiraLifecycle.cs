using System.Diagnostics;
using System.Runtime.InteropServices;

namespace WinStatusBar.Core.Ai;

/// <summary>
/// Qira sob demanda (11/11/2026): o Qira inteiro (núcleo, serviço de modelos, conectores e interface) ocupa de 1,8 a
/// 2,6 GB parado. A barra e as IAs no Codex o abrem no primeiro pedido e o encerram pouco depois da resposta.
/// - Abrir: o núcleo sobe pelo ProcessHub oficial, sem janela (o mesmo comando que antes rodava no início do Windows).
/// - Encerrar: só o Qira aberto por nós (ProcessHub registrado com PID e hora de início), com a vez na mão, parado há
///   mais que a espera e sem janela do aplicativo Lenovo à vista. Todas as partes juntas, como no reinício completo:
///   encerrar uma parte só trava o resto (verificado em 08/11/2026).
/// O Qira aberto pela própria pessoa (aplicativo Lenovo) nunca é encerrado aqui.
/// </summary>
public static class QiraLifecycle
{
    private static string Folder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Qira compartilhado");
    private static string OwnedFile => Path.Combine(Folder, "aberto-sob-demanda.txt");
    private static string LastUseFile => Path.Combine(Folder, "ultimo-uso.txt");

    public static string ProcessHubExe => Path.Combine(QiraSharedTurn.InstallRoot, "QTServer", "ProcessHub.exe");

    /// <summary>Partes do Qira em execução agora (quem chama solta com Dispose).</summary>
    public static List<Process> Parts()
    {
        var list = new List<Process>();
        foreach (var name in QiraSharedTurn.ProcessNames)
            foreach (var p in Process.GetProcessesByName(name))
            {
                if (QiraSharedTurn.IsQiraProcess(p)) list.Add(p); else p.Dispose();
            }
        return list;
    }

    public static bool AnyRunning()
    {
        var parts = Parts();
        foreach (var p in parts) p.Dispose();
        return parts.Count > 0;
    }

    /// <summary>Marca um uso agora: a contagem para encerrar recomeça (vale para a barra e para as IAs no Codex).</summary>
    public static void MarkUse()
    {
        try { Directory.CreateDirectory(Folder); File.WriteAllText(LastUseFile, DateTime.UtcNow.ToString("o")); } catch { }
    }

    public static DateTime LastUseUtc()
    {
        try { return DateTime.TryParse(File.ReadAllText(LastUseFile).Trim(), null, System.Globalization.DateTimeStyles.RoundtripKind, out var at) ? at.ToUniversalTime() : DateTime.MinValue; }
        catch { return DateTime.MinValue; }
    }

    /// <summary>Texto gravado para reconhecer o ProcessHub aberto por nós: PID e hora de início (um PID reaproveitado não confunde).</summary>
    internal static string OwnerStamp(int pid, DateTime startUtc) => pid + "|" + startUtc.Ticks;

    internal static bool StampMatches(string? stamp, int pid, DateTime startUtc) => stamp is { Length: > 0 } && stamp.Trim() == OwnerStamp(pid, startUtc);

    private static Process? ProcessHub()
    {
        Process? found = null;
        foreach (var p in Process.GetProcessesByName("ProcessHub"))
        {
            if (found is null && QiraSharedTurn.IsQiraProcess(p)) found = p; else p.Dispose();
        }
        return found;
    }

    /// <summary>O Qira em execução foi aberto por nós (e não pela pessoa, pelo aplicativo Lenovo)?</summary>
    public static bool Owned
    {
        get
        {
            try
            {
                if (!File.Exists(OwnedFile)) return false;
                using var hub = ProcessHub();
                return hub is not null && StampMatches(File.ReadAllText(OwnedFile), hub.Id, hub.StartTime.ToUniversalTime());
            }
            catch { return false; }
        }
    }

    /// <summary>Registra o ProcessHub atual como aberto por nós. Espera até 8 s ele aparecer (acabou de ser iniciado).</summary>
    public static async Task<bool> MarkOwnedAsync(CancellationToken ct)
    {
        var until = DateTime.UtcNow.AddSeconds(8);
        while (true)
        {
            try
            {
                using var hub = ProcessHub();
                if (hub is not null)
                {
                    Directory.CreateDirectory(Folder);
                    File.WriteAllText(OwnedFile, OwnerStamp(hub.Id, hub.StartTime.ToUniversalTime()));
                    return true;
                }
            }
            catch { }
            if (DateTime.UtcNow >= until) return false;
            await Task.Delay(250, ct);
        }
    }

    /// <summary>
    /// Com o Qira todo fechado, sobe o núcleo (ProcessHub -AutoStart: núcleo, serviço de modelos e conectores), sem janela,
    /// e o registra como aberto por nós. Devolve true quando iniciou. Com qualquer parte do Qira aberta, não faz nada.
    /// </summary>
    public static async Task<bool> EnsureNucleusAsync(Action<string> log, CancellationToken ct)
    {
        if (!File.Exists(ProcessHubExe) || AnyRunning()) return false;
        // O ProcessHub é um programa de console: pelo conhost sem interface ele não abre janela (verificado em 09/11/2026).
        string conhost = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "conhost.exe");
        var psi = new ProcessStartInfo(conhost) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = Path.GetDirectoryName(ProcessHubExe)! };
        psi.ArgumentList.Add("--headless");
        psi.ArgumentList.Add(ProcessHubExe);
        psi.ArgumentList.Add("-AutoStart");
        using (Process.Start(psi)) { }
        MarkUse();
        bool owned = await MarkOwnedAsync(ct);
        // O núcleo aparece em 1 a 2 s; a interface só é aberta depois dele.
        var until = DateTime.UtcNow.AddSeconds(15);
        while (DateTime.UtcNow < until)
        {
            var core = Process.GetProcessesByName("LenovoQiraCore");
            foreach (var p in core) p.Dispose();
            if (core.Length > 0) break;
            await Task.Delay(300, ct);
        }
        log("Qira: núcleo iniciado sob demanda" + (owned ? "" : " (ProcessHub não apareceu em 8 s)"));
        return true;
    }

    /// <summary>
    /// Encerra o Qira aberto por nós se ele está parado há mais que <paramref name="grace"/>. Não encerra quando: não fomos
    /// nós que abrimos, alguém está com a vez (barra ou Codex respondendo) ou a janela do aplicativo Lenovo está à vista.
    /// </summary>
    public static async Task<bool> StopIfIdleAsync(TimeSpan grace, Action<string> log)
    {
        if (!Owned || DateTime.UtcNow - LastUseUtc() < grace) return false;
        var turn = await QiraSharedTurn.AcquireAsync(TimeSpan.Zero, CancellationToken.None);
        if (turn is null) return false;
        var parts = new List<Process>();
        try
        {
            if (!Owned || DateTime.UtcNow - LastUseUtc() < grace) return false;
            parts = Parts();
            if (parts.Count == 0) return false;
            if (HasVisibleAppWindow(parts.Where(p => p.ProcessName.Equals("QuantumAI.Hub", StringComparison.OrdinalIgnoreCase)).Select(p => p.Id).ToHashSet()))
            {
                log("Qira: a janela do aplicativo está aberta; fica ligado enquanto a pessoa o usa");
                return false;
            }
            long bytes = 0;
            foreach (var p in parts) { try { bytes += p.WorkingSet64; } catch { } }
            foreach (var p in parts) { try { p.Kill(); } catch { } }
            try { File.Delete(OwnedFile); } catch { }
            log($"Qira: encerrado depois do uso ({parts.Count} processos, {bytes / 1048576.0:0} MB liberados)");
            return true;
        }
        finally
        {
            foreach (var p in parts) p.Dispose();
            turn.Dispose();
        }
    }

    /// <summary>Janela principal do aplicativo (grande ou minimizada): as pequenas (barrinha, bolha) a barra já esconde.</summary>
    internal static bool IsAppWindow(int width, int height, bool minimized) => minimized || (height >= 360 && width >= 420);

    private static bool HasVisibleAppWindow(HashSet<int> hubPids)
    {
        if (hubPids.Count == 0) return false;
        bool found = false;
        try
        {
            EnumWindows((h, _) =>
            {
                GetWindowThreadProcessId(h, out uint owner);
                if (!hubPids.Contains((int)owner) || !IsWindowVisible(h) || !GetWindowRect(h, out var r)) return true;
                if (IsAppWindow(r.Right - r.Left, r.Bottom - r.Top, IsIconic(h))) { found = true; return false; }
                return true;
            }, IntPtr.Zero);
        }
        catch { }
        return found;
    }

    private delegate bool EnumProc(IntPtr h, IntPtr p);
    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumProc proc, IntPtr p);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr h);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr h, out RECT r);
}
