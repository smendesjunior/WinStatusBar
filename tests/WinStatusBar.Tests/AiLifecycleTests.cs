using WinStatusBar.Core.Ai;
using WinStatusBar.Core.Settings;
using Xunit;

namespace WinStatusBar.Tests;

/// <summary>
/// Recursos das IAs sob demanda (11/11/2026): escolher uma IA não abre nada; a página oculta e o Qira abrem no envio,
/// fecham pouco depois da resposta e, na troca de IA, os da anterior fecham na hora.
/// </summary>
public class AiLifecycleTests
{
    [Fact]
    public void ReleaseWaitComesFromTheSettingAndFallsBackToOneMinute()
    {
        Assert.Equal(60, new AppSettings().AiReleaseSeconds);
        Assert.Equal(TimeSpan.FromSeconds(5), AppSettings.AiRelease(5));
        Assert.Equal(TimeSpan.FromMinutes(15), AppSettings.AiRelease(900));
        Assert.Equal(TimeSpan.FromMinutes(1), AppSettings.AiRelease(0));
        var s = new AppSettings { AiReleaseSeconds = 7 };
        s.Normalize();
        Assert.Equal(60, s.AiReleaseSeconds);
    }

    [Fact]
    public void OnlyTheQiraWeOpenedIsOurs()
    {
        var started = new DateTime(2026, 11, 11, 12, 0, 0, DateTimeKind.Utc);
        string stamp = QiraLifecycle.OwnerStamp(1168, started);
        Assert.True(QiraLifecycle.StampMatches(stamp + "\r\n", 1168, started));
        // PID reaproveitado por outro processo (outra hora de início) ou outro ProcessHub: não é o nosso.
        Assert.False(QiraLifecycle.StampMatches(stamp, 1168, started.AddSeconds(1)));
        Assert.False(QiraLifecycle.StampMatches(stamp, 1169, started));
        Assert.False(QiraLifecycle.StampMatches(null, 1168, started));
        Assert.False(QiraLifecycle.StampMatches("", 1168, started));
    }

    [Fact]
    public void QiraStaysOnWhileThePersonUsesTheLenovoWindow()
    {
        Assert.True(QiraLifecycle.IsAppWindow(1200, 800, minimized: false));
        Assert.True(QiraLifecycle.IsAppWindow(160, 28, minimized: true)); // minimizada: a pessoa ainda a tem aberta
        // Barrinha e bolha (pequenas, que a barra esconde) não seguram o Qira ligado.
        Assert.False(QiraLifecycle.IsAppWindow(600, 80, minimized: false));
        Assert.False(QiraLifecycle.IsAppWindow(64, 64, minimized: false));
    }

    [Fact]
    public void ChoosingAnAiOpensNothingAndSwitchingReleasesThePreviousOne()
    {
        string command = RepoSource.Read("src", "WinStatusBar", "Views", "BarWindow.Command.cs");
        // Abrir o painel com o Qira escolhido subia (e até reiniciava) o Qira inteiro sem nenhuma pergunta.
        Assert.DoesNotContain("Prewarm", command);
        Assert.DoesNotContain("Prewarm", RepoSource.Read("src", "WinStatusBar", "Services", "QiraHubSession.cs"));
        string select = ApiResumeSourceTests.Body(command, "private void SelectDestination(string key)");
        Assert.Contains("AiResources.ReleaseOthers(DestinationKey())", select);
        string resources = RepoSource.Read("src", "WinStatusBar", "Services", "AiResources.cs");
        foreach (string release in new[] { "GeminiWebSession.ReleaseAsync()", "CopilotWebSession.ReleaseAsync()", "QiraHubSession.ReleaseAsync()", "OllamaModels.UnloadAsync" })
            Assert.Contains(release, resources);
    }

    [Fact]
    public void HiddenPagesAndQiraCloseSoonAfterTheAnswer()
    {
        foreach (string file in new[] { "GeminiWebSession.cs", "CopilotWebSession.cs" })
        {
            string source = RepoSource.Read("src", "WinStatusBar", "Services", file);
            Assert.Contains("new(IdleCloser.Configured, () => CloseAsync(TimeSpan.Zero))", source);
            Assert.DoesNotContain("FromMinutes(15)", source);
            // A janela de login fechada e o "Sair" também deixavam a página aberta para sempre.
            Assert.Contains("_ = DetectAsync(); Idle.Restart(_ui); };", source);
        }
        string qira = RepoSource.Read("src", "WinStatusBar", "Services", "QiraHubSession.cs");
        Assert.Contains("await QiraLifecycle.EnsureNucleusAsync(App.Log, ct);", qira);
        Assert.Contains("ScheduleStop();", ApiResumeSourceTests.Body(qira, "private static async Task<AgentPrompt.Result> RunAsync("));
        // As IAs no Codex usam o mesmo Qira e as mesmas páginas: mesma regra, mesma espera.
        string codexQira = RepoSource.Read("src", "WinStatusBar.CodexIAs", "Providers", "QiraProvider.cs");
        Assert.Contains("await QiraLifecycle.EnsureNucleusAsync(Log.Write, ct);", codexQira);
        Assert.Contains("ScheduleStop();", codexQira);
        string codexWeb = RepoSource.Read("src", "WinStatusBar.CodexIAs", "Providers", "WebChatProvider.cs");
        Assert.Contains("AppSettings.AiRelease(BarSettings.Load().AiReleaseSeconds)", codexWeb);
        Assert.DoesNotContain("FromMinutes(15)", codexWeb);
    }

    [Fact]
    public void QiraIsNeverStoppedOnePartAtATime()
    {
        // Encerrar uma parte só trava o resto (08/11/2026): o encerramento percorre a lista comum inteira, com a vez na mão.
        string source = RepoSource.Read("src", "WinStatusBar.Core", "Ai", "QiraLifecycle.cs");
        string stop = ApiResumeSourceTests.Body(source, "public static async Task<bool> StopIfIdleAsync(");
        Assert.Contains("QiraSharedTurn.AcquireAsync(TimeSpan.Zero", stop);
        Assert.Contains("parts = Parts();", stop);
        Assert.Contains("foreach (var p in parts) { try { p.Kill(); } catch { } }", stop);
        Assert.Contains("if (!Owned", stop);
    }

    [Fact]
    public void CodexQuotaFindsTheExecutableAgainAfterAnAppUpdate()
    {
        // O app do Codex troca a pasta da versão ao se atualizar; o caminho guardado na abertura deixava de existir e a
        // cota mostrava "An error occurred trying to start process" até a barra reiniciar (11/11/2026).
        string query = ApiResumeSourceTests.Body(RepoSource.Read("src", "WinStatusBar.Core", "Ai", "Codex", "CodexProvider.cs"), "private async Task QueryQuotaAsync(");
        Assert.Contains("Executable = CodexAppServerClient.FindCodexExecutable();", query);
        Assert.DoesNotContain("Executable ??=", query);
        var explained = AiErrorExplainer.Explain(AgentPrompt.Target.Codex,
            @"An error occurred trying to start process 'C:\x\codex.exe' with working directory 'C:\y'. O sistema não pode encontrar o arquivo especificado.", null);
        Assert.Equal("programa ausente", explained.Short);
    }
}

/// <summary>Pedidos de 11/11/2026 no painel de rede e nas configurações.</summary>
public class SmallUiRequestsTests
{
    [Fact]
    public void HotspotButtonShowsItIsOnAndHowManyDevices()
    {
        Assert.Equal("Hotspot móvel", Services.HotspotState.Label(null));
        Assert.Equal("Hotspot móvel", Services.HotspotState.Label(new(false, 0, 8)));
        Assert.Equal("Hotspot · 0", Services.HotspotState.Label(new(true, 0, 8)));
        Assert.Equal("Hotspot · 3", Services.HotspotState.Label(new(true, 3, 8)));
        Assert.Contains("3 dispositivos conectados", Services.HotspotState.Tooltip(new(true, 3, 8)));
        Assert.Contains("1 dispositivo conectado", Services.HotspotState.Tooltip(new(true, 1, 8)));
        // Ligado: o mesmo destaque (azul) do Wi-Fi e do Bluetooth conectados.
        string panel = RepoSource.Read("src", "WinStatusBar", "Views", "Panels", "NetworkDetailsPanel.cs");
        Assert.Contains("if (state is { On: true }) _hotspotButton.Style = _ethernetButton.Style;", panel);
    }

    [Theory]
    [InlineData(true, false, true, 0x78, "Ctrl+Shift+F9")]
    [InlineData(false, true, false, 0x20, "Alt+Space")]
    [InlineData(true, true, true, 0x41, "Ctrl+Alt+Shift+A")]
    [InlineData(true, false, false, 0x35, "Ctrl+5")]
    [InlineData(false, false, false, 0x41, null)]   // sem modificador não é atalho
    [InlineData(true, false, false, 0x11, null)]    // só o Ctrl ainda pressionado
    [InlineData(true, false, false, 0x0D, null)]    // Enter não é aceito pelo envio
    public void VoiceShortcutIsRecordedFromThePressedKeys(bool ctrl, bool alt, bool shift, int key, string? expected)
        => Assert.Equal(expected, Services.VoiceShortcut.Format(ctrl, alt, shift, key));

    [Fact]
    public void ClaudeConnectorListIsParsedIntoNameOriginAndState()
    {
        string output = string.Join("\n",
            "Checking MCP server health...",
            "",
            "claude.ai Google Drive: https://drivemcp.googleapis.com/mcp/v1 - ✓ Connected",
            "claude.ai Microsoft 365: https://microsoft365.mcp.claude.com/mcp - ! Needs authentication",
            "plugin:pdf-viewer:pdf: npx -y @modelcontextprotocol/server-pdf --stdio - ✓ Connected",
            @"meu-servidor: node C:\x\server.js - ✗ Failed to connect");
        var servers = ConnectorPermissions.ParseMcpList(output);
        Assert.Equal(4, servers.Count);
        Assert.Equal(("claude.ai Google Drive", "Google Drive", "Conta claude.ai", ConnectorPermissions.ServerState.Connected),
            (servers[0].Name, servers[0].Display, servers[0].Origin, servers[0].State));
        Assert.Equal("https://drivemcp.googleapis.com/mcp/v1", servers[0].Detail);
        Assert.Equal(ConnectorPermissions.ServerState.NeedsSignIn, servers[1].State);
        Assert.Equal(("pdf-viewer · pdf", "Plugin", "npx -y @modelcontextprotocol/server-pdf --stdio"), (servers[2].Display, servers[2].Origin, servers[2].Detail));
        Assert.Equal((ConnectorPermissions.ServerState.Failed, "Servidor MCP"), (servers[3].State, servers[3].Origin));
        // "Detectar" libera só os conectados, pela mesma regra que o chat grava.
        Assert.Equal("mcp__claude_ai_Google_Drive", ConnectorPermissions.RuleForServer(servers[0].Name));
    }
}
