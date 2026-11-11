using Xunit;

namespace WinStatusBar.Tests;

/// <summary>
/// Memória (09/11/2026): os navegadores WebView2 ocultos da barra (cotas do Gemini, créditos da Microsoft, painel da
/// OpenAI, conversas do Copilot e do Gemini pelo site) ficavam abertos o tempo todo, cerca de 1,3 GB. Agora abrem sob
/// demanda e fecham quando ninguém precisa deles.
/// </summary>
public class HiddenWebPagesTests
{
    [Theory]
    [InlineData("GoogleUsageSession.cs")]
    [InlineData("MicrosoftCreditsSession.cs")]
    public void QuotaReadersCloseTheirBrowserAfterEachRead(string file)
    {
        string code = RepoSource.Read("src", "WinStatusBar", "Services", file);
        Assert.Contains("finally { _busy = false; CloseIfIdle(); }", code);
        // Não fecha com a janela de login aberta nem no meio de uma leitura.
        Assert.Contains("if (_busy || _interactive", code);
    }

    [Fact]
    public void GeminiQuotaReaderStaysOpenWhileTheLiveMenuIsOpen()
    {
        string code = RepoSource.Read("src", "WinStatusBar", "Services", "GoogleUsageSession.cs");
        Assert.Contains("_live > 0", code);
        Assert.Contains("_timer.Interval = _interval; CloseIfIdle();", code); // fecha quando o menu fecha
    }

    [Fact]
    public void OpenAiDashboardClosesInsteadOfHiding()
    {
        string code = RepoSource.Read("src", "WinStatusBar", "Views", "OpenAiDashboardView.cs");
        Assert.DoesNotContain("AppWindow.Hide()", code);
        Assert.Equal(2, code.Split("CloseBackgroundWindow();").Length - 1); // sincronizado e sem login (45 s)
    }

    [Theory]
    [InlineData("CopilotWebSession.cs")]
    [InlineData("GeminiWebSession.cs")]
    public void ChatPagesCloseSoonAfterTheLastUse(string file)
    {
        string code = RepoSource.Read("src", "WinStatusBar", "Services", file);
        // 11/11/2026: a espera vem das configurações (1 min por padrão), não mais 15 min fixos.
        Assert.Contains("new(IdleCloser.Configured, () => CloseAsync(TimeSpan.Zero))", code);
        Assert.Contains("finally { Gate.Release(); Idle.Restart(_ui); }", code);
    }
}
