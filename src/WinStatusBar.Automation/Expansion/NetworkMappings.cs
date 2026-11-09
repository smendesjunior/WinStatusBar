using Microsoft.Win32;
using System.ComponentModel;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using WinStatusBar.Core.Ai;

namespace WinStatusBar.ExecutorExpansion;

public sealed record NetworkMapping(string Drive, string Remote, bool Connected, bool Persistent);
public interface INetworkBackend
{
    IReadOnlyList<NetworkMapping> List();
    int Connect(string drive, string remote, bool persistent, bool promptCredentials, nint owner);
    int Restore(string drive, nint owner);
    int Disconnect(string drive, bool removeProfile);
}

/// <summary>Native credential UI belongs to Windows/provider; passwords never enter request/arguments/logs.</summary>
public sealed class NetworkMappings(INetworkBackend backend, nint owner = 0, Func<string, bool>? accessProbe = null)
{
    public static string Drive(string? drive)
    {
        if (drive is null || !Regex.IsMatch(drive, @"^[D-Zd-z]:$"))
            throw new ArgumentException("Informe uma unidade D: a Z:.");
        return drive.ToUpperInvariant();
    }
    public static string Remote(string? remote)
    {
        if (remote is null || !Regex.IsMatch(remote, @"^\\\\[^\\/:*?""<>|\s]+\\[^\\/:*?""<>|]+(?:\\[^/:*?""<>|]*)?$") ||
            remote.Split('\\').Any(p => p is "." or "..") || remote.Any(char.IsControl))
            throw new ArgumentException("Informe UNC absoluto \\\\servidor\\compartilhamento.");
        return remote.TrimEnd('\\');
    }
    public DesktopActionResult Execute(DesktopActionRequest r)
    {
        if (r.Text is not null || r.Arguments is not null || r.Value is not null)
            return Failure("Credenciais/dados livres não são aceitos. Use format:prompt para o diálogo nativo.");
        if (r.Op == "network_list") return Ok(System.Text.Json.JsonSerializer.Serialize(backend.List()));
        string drive = Drive(r.Name);
        var current = backend.List().SingleOrDefault(x => x.Drive.Equals(drive, StringComparison.OrdinalIgnoreCase));
        bool persistent = r.Scope == "persistent";
        if (r.Scope is not (null or "session" or "persistent") || r.Format is not (null or "prompt"))
            return Failure("scope deve ser session/persistent; format só aceita prompt.");
        int code;
        switch (r.Op)
        {
            case "network_map":
            {
                string remote = Remote(r.Path);
                if (current is not null && !Same(current.Remote, remote))
                    return Failure("Unidade já pertence a outro destino. Nenhuma conexão foi removida.");
                if (current is { Connected: true })
                    return current.Persistent == persistent ? CheckAccess(drive, "Mapeamento já está no estado pedido.")
                        : Failure("Destino já conectado, mas persistência difere. Não foi alterada silenciosamente.");
                if (current is not null && current.Persistent != persistent)
                    return Failure("Perfil existente tem persistência diferente. Revise o perfil antes de reconectar.");
                code = backend.Connect(drive, remote, persistent, r.Format == "prompt", owner);
                if (code != 0) return Error(code);
                var after = backend.List().SingleOrDefault(x => x.Drive.Equals(drive, StringComparison.OrdinalIgnoreCase));
                return after is { Connected: true } && Same(after.Remote, remote) && after.Persistent == persistent
                    ? CheckAccess(drive, "Mapeamento e persistência conferidos: " + drive)
                    : Uncertain("Windows aceitou a conexão, mas o estado final não foi confirmado.");
            }
            case "network_reconnect":
                if (current is null || !current.Persistent) return Failure("Não há perfil persistente nessa unidade.");
                if (r.Path is null || !Same(Remote(r.Path), current.Remote)) return Failure("Destino do perfil não confere com path.");
                if (current.Connected) return CheckAccess(drive, "Unidade já conectada.");
                code = r.Format == "prompt"
                    ? backend.Connect(drive, current.Remote, true, true, owner) : backend.Restore(drive, owner);
                if (code != 0) return Error(code);
                return backend.List().Any(x => x.Drive == drive && x.Connected && Same(x.Remote, current.Remote))
                    ? CheckAccess(drive, "Reconexão conferida.") : Uncertain("Reconexão não confirmada.");
            case "network_access":
                if (current is not { Connected: true } || r.Path is null || !Same(Remote(r.Path), current.Remote))
                    return Failure("Unidade não conectada ou destino não confere com path.");
                return CheckAccess(drive, "Destino do mapeamento confere.");
            case "network_disconnect":
                if (current is null) return Ok("Unidade já está sem conexão ou perfil.");
                if (r.Path is null || !Same(Remote(r.Path), current.Remote)) return Failure("Destino atual não confere com path.");
                // Never force-close files or connections. scope=persistent explicitly removes remembered profile.
                code = backend.Disconnect(drive, persistent);
                if (code != 0) return Error(code);
                var remaining = backend.List().SingleOrDefault(x => x.Drive == drive);
                return remaining?.Connected != true && (!persistent || remaining is null)
                    ? Ok(persistent ? "Conexão e perfil removidos; arquivos abertos não foram forçados." : "Conexão encerrada; perfil preservado.")
                    : Uncertain("Desconexão ou remoção do perfil não confirmada.");
            default: return Failure("Operação de mapeamento desconhecida.");
        }
    }
    private DesktopActionResult CheckAccess(string drive, string message)
    {
        try
        {
            bool accessible;
            if (accessProbe is not null) accessible = accessProbe(drive + "\\");
            else
            {
                // Enumeration distinguishes an empty accessible directory from a denied/offline root.
                using var entries = Directory.EnumerateFileSystemEntries(drive + "\\").GetEnumerator();
                entries.MoveNext(); accessible = true;
            }
            return accessible ? Ok(message + " Acesso de leitura à raiz conferido; escrita não foi testada.")
                : Uncertain(message + " Acesso à raiz não confirmado. O mapeamento não foi removido nem repetido.");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        { return Uncertain(message + " A raiz está indisponível ou negou leitura. O mapeamento foi preservado; autenticação SMB não pede UAC."); }
    }
    public static async Task<DesktopActionResult> DiagnoseAsync(string path, CancellationToken ct)
    {
        string remote = Remote(path), host = remote.Split('\\')[2];
        try
        {
            var addresses = await Dns.GetHostAddressesAsync(host, ct).WaitAsync(TimeSpan.FromSeconds(4), ct);
            using var client = new TcpClient();
            await client.ConnectAsync(host, 445, ct).AsTask().WaitAsync(TimeSpan.FromSeconds(4), ct);
            return Ok($"DNS respondeu ({addresses.Length} endereço(s)); TCP 445 acessível. Isso não confirma autenticação ou acesso ao compartilhamento.");
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception e) when (e is SocketException or TimeoutException)
        { return Failure("DNS/TCP 445 indisponível ou excedeu o prazo. Credenciais e acesso não foram testados."); }
    }
    private static bool Same(string a, string b) => a.TrimEnd('\\').Equals(b.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
    private static DesktopActionResult Ok(string text) => new(text, Method: "Win32/MPR");
    private static DesktopActionResult Failure(string text) => new(text, Success: false, Status: DesktopStatus.Blocked, Method: "Win32/MPR");
    private static DesktopActionResult Uncertain(string text) => new(text, Success: false, Status: DesktopStatus.Partial, Method: "Win32/MPR");
    private static DesktopActionResult Error(int code) => code switch
    {
        1223 => Failure("Diálogo nativo cancelado pelo usuário."),
        1219 => Failure("Já existe sessão com esse servidor usando outra credencial. Não encerrei outras sessões."),
        85 or 1202 => Failure("Letra/perfil já em uso. Não substituí o destino existente."),
        2401 or 2404 => Failure("Há arquivos/dispositivo em uso. Nenhum fechamento foi forçado."),
        5 or 86 or 1326 => Failure("Autenticação/acesso negado. Use format:prompt se deseja o diálogo nativo; UAC não corrige credenciais SMB."),
        _ => Failure($"Windows retornou código {code}: {new Win32Exception(code).Message}")
    };
}

public sealed class WindowsNetworkBackend : INetworkBackend
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NetResource { public int Scope, Type, DisplayType, Usage; public string? Local, Remote, Comment, Provider; }
    [DllImport("mpr.dll", CharSet = CharSet.Unicode)] private static extern int WNetAddConnection3W(nint hwnd, ref NetResource resource, nint password, nint user, int flags);
    [DllImport("mpr.dll", CharSet = CharSet.Unicode)] private static extern int WNetGetConnectionW(string local, StringBuilder remote, ref int length);
    [DllImport("mpr.dll", CharSet = CharSet.Unicode)] private static extern int WNetCancelConnection2W(string local, int flags, [MarshalAs(UnmanagedType.Bool)] bool force);
    [DllImport("mpr.dll", CharSet = CharSet.Unicode)] private static extern int WNetRestoreConnectionW(nint hwnd, string device, [MarshalAs(UnmanagedType.Bool)] bool useUi);
    public IReadOnlyList<NetworkMapping> List()
    {
        var rows = new Dictionary<string, NetworkMapping>(StringComparer.OrdinalIgnoreCase);
        using var profiles = Registry.CurrentUser.OpenSubKey("Network");
        foreach (string letter in profiles?.GetSubKeyNames() ?? [])
        {
            if (!Regex.IsMatch(letter, "^[A-Za-z]$")) continue;
            using var profile = profiles!.OpenSubKey(letter);
            if (profile?.GetValue("RemotePath") is string remote)
                rows[letter.ToUpperInvariant() + ":"] = new(letter.ToUpperInvariant() + ":", remote, false, true);
        }
        for (char c = 'D'; c <= 'Z'; c++)
        {
            string drive = c + ":"; int length = 32768; var buffer = new StringBuilder(length);
            int code = WNetGetConnectionW(drive, buffer, ref length);
            if (code == 0) rows[drive] = new(drive, buffer.ToString(), true, rows.ContainsKey(drive));
            else if (code is not (2250 or 1201)) throw new Win32Exception(code);
        }
        return rows.Values.OrderBy(x => x.Drive).ToArray();
    }
    public int Connect(string drive, string remote, bool persistent, bool promptCredentials, nint owner)
    {
        var resource = new NetResource { Type = 1, Local = drive, Remote = remote };
        // Null password/user: current Windows identity, or provider-managed protected credential UI.
        // CONNECT_PROMPT=0x10 requires CONNECT_INTERACTIVE=0x8; no CONNECT_CMD_SAVECRED.
        return WNetAddConnection3W(owner, ref resource, 0, 0, (persistent ? 1 : 0) | (promptCredentials ? 0x18 : 0));
    }
    public int Restore(string drive, nint owner) => WNetRestoreConnectionW(owner, drive, false);
    public int Disconnect(string drive, bool removeProfile) => WNetCancelConnection2W(drive, removeProfile ? 1 : 0, false);
}
