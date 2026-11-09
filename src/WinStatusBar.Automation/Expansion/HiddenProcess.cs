using System.Diagnostics;
using System.Text;

namespace WinStatusBar.ExecutorExpansion;

public sealed record ProcessOutput(int ExitCode, string Stdout, string Stderr, bool Truncated);

/// <summary>Only invoke for explicitly selected tools; never kill Office/Revit/user apps with this runner.</summary>
public static class HiddenProcess
{
    public static async Task<ProcessOutput> RunAsync(string executable, IEnumerable<string> arguments,
        string? stdin, CancellationToken ct, int maxChars = 1_000_000, string? workingDirectory = null)
    {
        ct.ThrowIfCancellationRequested();
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
            StandardInputEncoding = new UTF8Encoding(false)
        };
        if (workingDirectory is not null)
        {
            if (!Path.IsPathFullyQualified(workingDirectory) || !Directory.Exists(workingDirectory))
                throw new ArgumentException("cwd deve ser uma pasta absoluta existente.");
            start.WorkingDirectory = workingDirectory;
        }
        foreach (string arg in arguments) start.ArgumentList.Add(arg);
        using var process = new Process { StartInfo = start };
        if (!process.Start()) throw new IOException("Não foi possível iniciar a ferramenta.");
        using var cancel = ct.Register(() => { try { if (!process.HasExited) process.Kill(true); } catch (InvalidOperationException) { } });
        var output = DrainAsync(process.StandardOutput, maxChars);
        var error = DrainAsync(process.StandardError, maxChars);
        try
        {
            if (stdin is not null) await process.StandardInput.WriteAsync(stdin.AsMemory(), ct);
            process.StandardInput.Close();
            await process.WaitForExitAsync(ct);
            var stdout = await output; var stderr = await error;
            return new(process.ExitCode, stdout.Text, stderr.Text, stdout.Truncated || stderr.Truncated);
        }
        finally
        {
            if (!process.HasExited) { try { process.Kill(true); } catch (InvalidOperationException) { } }
        }
    }
    public static Task<ProcessOutput> PowerShellAsync(string trustedScript, CancellationToken ct)
    {
        string exe = Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe");
        string preamble = "$ErrorActionPreference='Stop';[Console]::OutputEncoding=[Text.UTF8Encoding]::new($false);";
        return RunAsync(exe, ["-NoLogo", "-NoProfile", "-NonInteractive", "-EncodedCommand",
            Convert.ToBase64String(Encoding.Unicode.GetBytes(preamble + trustedScript))], null, ct);
    }
    private static async Task<(string Text, bool Truncated)> DrainAsync(StreamReader reader, int limit)
    {
        char[] buffer = new char[4096]; var text = new StringBuilder(); bool truncated = false; int n;
        while ((n = await reader.ReadAsync(buffer)) > 0)
        {
            int take = Math.Min(n, Math.Max(0, limit - text.Length));
            text.Append(buffer, 0, take); truncated |= take < n;
        }
        return (text.ToString(), truncated);
    }
}
