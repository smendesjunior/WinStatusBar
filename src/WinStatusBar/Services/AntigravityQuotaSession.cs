using System.Diagnostics;
using Microsoft.UI.Dispatching;
using WinStatusBar.Core.Ai.Antigravity;

namespace WinStatusBar.Services;

/// <summary>
/// Cota do Google Antigravity (limite semanal de cada grupo de modelos), lida a cada 30 min da tela /usage do agy num
/// console invisível (AntigravityQuota). O limite é semanal, então 30 min basta; não gasta cota. Também diz se o agy
/// está instalado e com login, para a página de IAs, e abre o login e a instalação oficiais num terminal visível.
/// </summary>
internal static class AntigravityQuotaSession
{
    public static AntigravityQuota.Snapshot? Current { get; private set; } = AntigravityQuota.Load();
    public static event Action? Changed;
    public static bool Installed => AntigravityCli.FindExecutable() is not null;
    public static bool Busy { get; private set; }

    private static DispatcherQueue? _ui;
    private static DispatcherQueueTimer? _timer;

    public static void Install(DispatcherQueue ui)
    {
        _ui = ui;
        _timer = ui.CreateTimer();
        _timer.Interval = TimeSpan.FromMinutes(1);
        // Com a tela apagada ou a sessão bloqueada não lê; ao voltar, lê se a última leitura venceu (30 min com login,
        // 3 min sem login). A leitura é sempre silenciosa (agy sem navegador, console invisível).
        _timer.Tick += (_, _) => { if (!Core.Presence.Away && AntigravityQuota.IsDue(Current, DateTimeOffset.Now)) _ = RefreshAsync(); };
        Core.Presence.Changed += away =>
        {
            if (!away) _ui?.TryEnqueue(() => { if (AntigravityQuota.IsDue(Current, DateTimeOffset.Now)) _ = RefreshAsync(); });
        };
        _timer.Start();
        if (Installed && AntigravityQuota.IsDue(Current, DateTimeOffset.Now)) _ = RefreshAsync();
    }

    public static void Stop() { _timer?.Stop(); _ui = null; }

    /// <summary>Lê agora (sem o agy instalado, não faz nada). Uma leitura por vez.</summary>
    private static int _loginMisses;

    public static async Task RefreshAsync()
    {
        if (Busy || !Installed) return;
        Busy = true;
        Changed?.Invoke();
        try
        {
            var read = await AntigravityQuota.ReadAsync(CancellationToken.None);
            if (read is null) return;
            if (read.Error is { } error && read.SignedIn)
            {
                // Falha passageira: mantém a leitura anterior (com a hora dela), só registra.
                App.Log("Cota do Antigravity: " + error);
                return;
            }
            if (!read.SignedIn && Current?.SignedIn == true && _loginMisses < 1)
            {
                // Logo depois de acordar (rede ainda voltando), o agy diz "sem login" porque não renovou o acesso; a leitura
                // seguinte vem normal. Mantém o login anterior e confere de novo em 2 min; só um segundo "sem login" seguido vale.
                _loginMisses++;
                App.Log("Cota do Antigravity: o agy disse \"sem login\" (provável rede voltando); confere de novo em 2 min");
                _ = Task.Delay(TimeSpan.FromMinutes(2)).ContinueWith(_ => { if (_ui is { } q) q.TryEnqueue(() => _ = RefreshAsync()); else _ = RefreshAsync(); });
                return;
            }
            _loginMisses = 0;
            Current = read;
            AntigravityQuota.Save(read);
            App.Log(read.SignedIn
                ? $"Cota do Antigravity: {string.Join(", ", read.Groups.Select(g => $"{g.Name} {g.RemainingPercent:0.#}% restantes"))} ({read.Plan ?? "plano não lido"})"
                : "Cota do Antigravity: o agy está sem login");
        }
        catch (Exception ex) { App.Log("Cota do Antigravity: leitura falhou: " + ex.Message); }
        finally
        {
            Busy = false;
            if (_ui is { } ui) ui.TryEnqueue(() => Changed?.Invoke()); else Changed?.Invoke();
        }
    }

    /// <summary>Leitura recente (até 6 h) com grupos.</summary>
    public static AntigravityQuota.Snapshot? Fresh
        => Current is { SignedIn: true, Groups.Count: > 0 } c && DateTimeOffset.Now - c.Updated < TimeSpan.FromHours(6) ? c : null;

    /// <summary>Login oficial: o agy num terminal visível (ele mostra o link do Google; a pessoa entra e fecha).</summary>
    public static void OpenSignIn()
    {
        if (AntigravityCli.FindExecutable() is not { } exe) return;
        Open(Path.Combine(Environment.SystemDirectory, "cmd.exe"), $"/k \"\"{exe}\"\"", AntigravityCli.WorkFolder());
    }

    /// <summary>Instalação oficial pelo winget (Google.AntigravityCLI), num terminal visível em que a pessoa acompanha.</summary>
    public static void OpenInstall()
        => Open(Path.Combine(Environment.SystemDirectory, "cmd.exe"),
            "/k winget install --id Google.AntigravityCLI -e --accept-source-agreements & echo. & echo Depois de instalar, feche esta janela e clique em Entrar.",
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));

    private static void Open(string file, string args, string dir)
    {
        try { Process.Start(new ProcessStartInfo(file, args) { UseShellExecute = true, WorkingDirectory = dir }); }
        catch (Exception ex) { App.Log("Antigravity: não abriu o terminal: " + ex.Message); }
    }
}
