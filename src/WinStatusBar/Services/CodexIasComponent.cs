using System.Diagnostics;
using System.Linq;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using WinStatusBar.Core.Ai.Codex;

namespace WinStatusBar.Services;

/// <summary>
/// O componente das IAs no Codex (Automation\WinStatusBar.CodexIAs.exe): encaminhador do Codex e verificação de
/// modelos. A barra o inicia ao abrir quando a ligação com o Codex está ligada (ele continua rodando com a barra fechada)
/// e o usa pela aba Configurações › Codex e modelos.
/// </summary>
internal static class CodexIasComponent
{
    public static string ExePath => Path.Combine(AppContext.BaseDirectory, "Automation", CodexIntegration.ExeName);
    public static bool Available => File.Exists(ExePath);
    public static bool Running => Process.GetProcessesByName(CodexIntegration.ProcessName).Length > 0;

    private static readonly System.Net.Http.HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(3) };

    /// <summary>O encaminhador responde na porta 11435 e é o da barra (e não outro programa).</summary>
    public static async Task<bool> GatewayOnlineAsync()
    {
        try
        {
            string json = await Http.GetStringAsync($"http://127.0.0.1:{CodexIntegration.GatewayPort}/health");
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            return doc.RootElement.TryGetProperty("app", out var app) && app.GetString() == CodexIntegration.ProcessName;
        }
        catch { return false; }
    }

    /// <summary>Muda a cada verificação de modelos: as listas guardadas pela barra são lidas de novo.</summary>
    public static int Generation { get; private set; }
    public static void NotifyModelsChanged() => Generation++;

    private static int _commands, _ensuring, _startsWithoutAnswer;
    private static DateTime _nextStartUtc;

    /// <summary>Um comando do componente (--ligar, --desligar, --verificar…) está rodando: ele mesmo para e inicia o encaminhador.</summary>
    public static bool CommandInFlight => Volatile.Read(ref _commands) > 0;

    /// <summary>
    /// Ligação com o Codex ligada e o encaminhador sem responder (depois de uma atualização, ou porque ele caiu): inicia.
    /// Pode ser chamado de tempos em tempos: não faz nada com a ligação desligada, com o encaminhador respondendo ou com um
    /// comando do componente em andamento.
    /// </summary>
    public static void EnsureRunning() => _ = EnsureRunningAsync();

    /// <summary>True quando iniciou o encaminhador agora.</summary>
    public static async Task<bool> EnsureRunningAsync()
    {
        if (Interlocked.Exchange(ref _ensuring, 1) == 1) return false;
        try
        {
            if (!Available || CommandInFlight || !CodexIntegration.GatewayWanted()) return false;
            // Pela resposta do encaminhador, não pelo nome do processo: --verificar e --entrar têm o mesmo nome, e um
            // encaminhador travado continua na lista de processos.
            if (await GatewayOnlineAsync().ConfigureAwait(false)) { _startsWithoutAnswer = 0; _nextStartUtc = default; return false; }
            if (CommandInFlight || !CodexIntegration.GatewayWanted() || DateTime.UtcNow < _nextStartUtc) return false;
            // Uma segunda cópia sai sozinha (o encaminhador garante uma só). Se ele não responde depois de 3 inícios
            // (porta ocupada por outro programa), as tentativas passam a ser a cada 10 minutos.
            if (++_startsWithoutAnswer >= 3) _nextStartUtc = DateTime.UtcNow.AddMinutes(10);
            Process.Start(new ProcessStartInfo(ExePath) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = Path.GetDirectoryName(ExePath)! })?.Dispose();
            App.Log("IAs no Codex: encaminhador iniciado.");
            return true;
        }
        catch (Exception ex) { App.Log("IAs no Codex: " + ex.Message); return false; }
        finally { Volatile.Write(ref _ensuring, 0); }
    }

    /// <summary>
    /// Primeira vez com esta versão num PC com o Codex (determinação de 08/11/2026): liga as IAs da barra no Codex sozinha,
    /// com cópia do config.toml, depois que a barra termina de abrir. Só quando a ligação nunca foi feita neste PC (sem
    /// integracao.json): quem desligar depois continua desligado. Se falhar (ex.: porta ocupada), tenta de novo na próxima
    /// abertura da barra. Numa instalação nova o instalador já grava a ligação como desligada (decisão de 09/11/2026:
    /// ligar sozinho só onde a barra já estava instalada), então isto vale só para atualizações.
    /// </summary>
    public static void AutoEnableOnce()
    {
        try
        {
            if (!Available || File.Exists(CodexIntegration.StateFile)) return;
            bool codex = WinStatusBar.Core.Ai.Codex.CodexAppServerClient.FindCodexExecutable() is not null
                         || File.Exists(Path.Combine(WinStatusBar.Core.Ai.Codex.CodexAppServerClient.CodexHome(), "config.toml"));
            if (!codex) return;
            _ = Task.Run(async () =>
            {
                await Task.Delay(TimeSpan.FromSeconds(30));
                if (File.Exists(CodexIntegration.StateFile)) return;
                App.Log("IAs no Codex: ligação automática (primeira vez com esta versão neste PC)");
                var result = await RunAsync(["--ligar"], CancellationToken.None);
                NotifyModelsChanged();
                App.Log("IAs no Codex: ligação automática: " + (result.Ok ? "ok · " : "falhou · ") + result.Mensagem);
            });
        }
        catch (Exception ex) { App.Log("IAs no Codex: ligação automática: " + ex.Message); }
    }

    /// <summary>Roda um comando do componente (--ligar, --desligar, --verificar…) e devolve o resultado gravado por ele.</summary>
    public static async Task<CodexIntegration.CommandResult> RunAsync(string[] args, CancellationToken ct)
    {
        if (!Available) return new(args.FirstOrDefault() ?? "", false, "Componente das IAs no Codex ausente nesta instalação da barra.", DateTime.Now);
        var started = DateTime.Now.AddSeconds(-1);
        var psi = new ProcessStartInfo(ExePath) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = Path.GetDirectoryName(ExePath)! };
        foreach (var a in args) psi.ArgumentList.Add(a);
        Interlocked.Increment(ref _commands); // enquanto o comando roda, EnsureRunning não inicia o encaminhador por conta própria
        try
        {
            using var process = Process.Start(psi) ?? throw new InvalidOperationException("O componente das IAs no Codex não iniciou.");
            try { await process.WaitForExitAsync(ct); }
            catch (OperationCanceledException) { try { process.Kill(entireProcessTree: true); } catch { } throw; }
            var result = CodexIntegration.LoadCommand();
            return result is not null && result.Em >= started ? result
                : new(args.FirstOrDefault() ?? "", process.ExitCode == 0, process.ExitCode == 0 ? "Concluído." : $"O componente terminou com o código {process.ExitCode}.", DateTime.Now);
        }
        finally { Interlocked.Decrement(ref _commands); }
    }
}
