using System.Text;
using System.Text.Json;
using WinStatusBar.Core.Ai;

namespace WinStatusBar.ExecutorExpansion;

/// <summary>General-purpose fallback remains unrestricted by the recipe/application catalog.</summary>
public static class StructuredCommands
{
    private static readonly System.Text.RegularExpressions.Regex DeniedPattern = new(
        @"Access (to the path .* )?is denied|Acesso negado|O acesso ao caminho .* foi negado|UnauthorizedAccess|requires elevation|requer eleva|run as administrator|executar como administrador|PermissionDenied|0x80070005",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    private const string Wrapper = """
        $ErrorActionPreference='Stop'
        [Console]::InputEncoding=[Text.UTF8Encoding]::new($false)
        [Console]::OutputEncoding=[Text.UTF8Encoding]::new($false)
        $ProgressPreference='SilentlyContinue'
        try {
            $payload=[Console]::In.ReadToEnd() | ConvertFrom-Json
            $global:LASTEXITCODE=0
            $scriptArguments=@($payload.arguments)
            & ([scriptblock]::Create([string]$payload.script)) @scriptArguments
            if ($global:LASTEXITCODE -ne 0) { exit $global:LASTEXITCODE }
        } catch { [Console]::Error.WriteLine($_.ToString()); exit 1 }
        """;
    /// <summary>
    /// The whole result text goes into the next model request. The process may print up to 1,000,000 characters per
    /// stream; only the beginning goes back (the full output is still used below to detect "access denied").
    /// </summary>
    private const int MaxStdoutChars = 12000, MaxStderrChars = 4000;
    private static string Cut(string text, int max) => text.Length <= max ? text : text[..max] + "\n…(truncado; filtre no script)";

    public static async Task<DesktopActionResult> ExecuteAsync(DesktopActionRequest r, CancellationToken ct)
    {
        ProcessOutput result;
        if (r.Action == "powershell")
        {
            if (string.IsNullOrWhiteSpace(r.Text)) throw new ArgumentException("Informe text com script.");
            string data = JsonSerializer.Serialize(new { script = r.Text, arguments = r.Arguments ?? [] });
            string executable = Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe");
            result = await HiddenProcess.RunAsync(executable, ["-NoLogo", "-NoProfile", "-NonInteractive", "-EncodedCommand",
                Convert.ToBase64String(Encoding.Unicode.GetBytes(Wrapper))], data, ct, workingDirectory: r.Cwd);
        }
        else if (r.Action == "run")
        {
            if (string.IsNullOrWhiteSpace(r.Application)) throw new ArgumentException("Informe application.");
            result = await HiddenProcess.RunAsync(r.Application, r.Arguments ?? [], r.Text, ct, workingDirectory: r.Cwd);
        }
        else throw new ArgumentException("Ação não é comando.");
        string text = "Código de saída: " + result.ExitCode +
            ". Isso confirma o término do processo, não o objetivo da tarefa; confira o estado relevante." +
            (result.Truncated ? " Saída truncada no limite de tamanho." : "") +
            (result.Stdout.Length == 0 ? "" : "\nSaída:\n" + Cut(result.Stdout, MaxStdoutChars)) +
            (result.Stderr.Length == 0 ? "" : "\nErros:\n" + Cut(result.Stderr, MaxStderrChars));
        // Acesso negado por falta de administrador: bloqueada (não falha genérica) para a IA reenviar com elevated:true,
        // depois de conferir se houve efeito parcial. Acesso negado em rede/SMB não se resolve com UAC.
        bool denied = result.ExitCode != 0 && r.Elevated != true && DeniedPattern.IsMatch(result.Stderr + "\n" + result.Stdout)
            && !result.Stderr.Contains(@"\\", StringComparison.Ordinal);
        if (denied) text += "\n" + DesktopStatus.NeedsAdmin + " O Windows negou por falta de privilégio. Confira se houve efeito parcial e reenvie com \"elevated\":true (abre o UAC).";
        return new(text, Success: result.ExitCode == 0,
            Status: result.ExitCode == 0 ? DesktopStatus.Done : denied ? DesktopStatus.Blocked : DesktopStatus.Failed,
            Method: r.Action == "powershell" ? "PowerShell/structured-stdin/hidden" : "process/structured-arguments/hidden");
    }
}
