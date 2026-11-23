using WinStatusBar.Core.Ai.Claude;
using Xunit;

namespace WinStatusBar.Tests.Ai;

public class ClaudeCredentialsTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.FromUnixTimeMilliseconds(1_800_000_000_000);

    [Theory]
    [InlineData("""{"claudeAiOauth":{"accessToken":"a","expiresAt":0}}""", true)]
    [InlineData("""{"claudeAiOauth":{"accessToken":"a","expiresAt":1800000060000}}""", true)]
    // Vence em 4 min: o Claude Code (margem de 5 min) já renovaria ao abrir; a barra não pode abri-lo.
    [InlineData("""{"claudeAiOauth":{"accessToken":"a","refreshToken":"r","expiresAt":1800000240000}}""", true)]
    [InlineData("""{"claudeAiOauth":{"accessToken":"a","expiresAt":1800003600000}}""", false)]
    [InlineData("""{"claudeAiOauth":{"accessToken":"a"}}""", false)]
    // Login apagado pelo Claude Code depois de "invalid_grant".
    [InlineData("""{"claudeAiOauth":{"accessToken":"","refreshToken":"","expiresAt":0}}""", true)]
    [InlineData("{}", false)]
    [InlineData("not json", false)]
    [InlineData(null, false)]
    public void NeedsRefresh_only_for_expired_oauth(string? json, bool expected)
        => Assert.Equal(expected, ClaudeCredentials.NeedsRefresh(json, Now));

    [Theory]
    [InlineData("""{"claudeAiOauth":{"accessToken":"a","refreshToken":"r","expiresAt":1800003600000}}""", ClaudeCredentials.LoginState.Valid)]
    [InlineData("""{"claudeAiOauth":{"accessToken":"a","refreshToken":"r","expiresAt":0}}""", ClaudeCredentials.LoginState.NeedsRefresh)]
    [InlineData("""{"claudeAiOauth":{"accessToken":"a","refreshToken":"r","expiresAt":1800000300000}}""", ClaudeCredentials.LoginState.NeedsRefresh)]
    [InlineData("""{"claudeAiOauth":{"accessToken":"","refreshToken":"","expiresAt":0}}""", ClaudeCredentials.LoginState.SignedOut)]
    [InlineData("""{"claudeAiOauth":{"accessToken":"a","expiresAt":0}}""", ClaudeCredentials.LoginState.SignedOut)]
    [InlineData("""{"mcpOAuth":{}}""", ClaudeCredentials.LoginState.None)]
    public void State_tells_valid_expiring_and_signed_out(string json, ClaudeCredentials.LoginState expected)
        => Assert.Equal(expected, ClaudeCredentials.State(json, Now));

    [Fact]
    public async Task LetExitAsync_waits_for_exit_after_closing_input_and_never_kills()
    {
        // "more" sai sozinho quando a entrada fecha, como o Claude Code no modo stream-json.
        var psi = new System.Diagnostics.ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "more.com"))
        { RedirectStandardInput = true, RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
        using var p = System.Diagnostics.Process.Start(psi)!;
        await ClaudeCredentials.LetExitAsync(p, TimeSpan.FromSeconds(20));
        Assert.True(p.HasExited);
        Assert.Equal(0, p.ExitCode); // saiu sozinho (morto à força daria -1)
    }

    [Fact]
    public async Task LetExitAsync_leaves_a_slow_process_running_instead_of_killing_it()
    {
        // Processo lento que sai sozinho com código 0, sem depender de rede (o ICMP no loopback pode estar bloqueado).
        var psi = new System.Diagnostics.ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe"), "-NoProfile -NonInteractive -Command \"Start-Sleep -Seconds 3; exit 0\"")
        { RedirectStandardInput = true, RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
        using var p = System.Diagnostics.Process.Start(psi)!;
        try
        {
            await ClaudeCredentials.LetExitAsync(p, TimeSpan.FromMilliseconds(300));
            Assert.False(p.HasExited);
            await p.WaitForExitAsync(new CancellationTokenSource(TimeSpan.FromSeconds(20)).Token);
            Assert.Equal(0, p.ExitCode);
        }
        finally { try { if (!p.HasExited) p.Kill(); } catch { } }
    }
}
