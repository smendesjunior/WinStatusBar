using WinStatusBar.Core.Ai.Local;
using Xunit;

namespace WinStatusBar.Tests;

public class LocalAiTests
{
    private static SystemSnapshot Snap(ProcInfo[] procs, TcpConn[] conns) =>
        new() { Processes = procs.ToDictionary(p => p.Pid), Connections = conns };

    [Fact]
    public void FindsOllamaServerProcesses_NotTheTrayApp()
    {
        // Ollama real: "ollama app" (bandeja) → ollama serve → lib\ollama\llama-server.exe (runner que faz a inferência)
        var procs = new[] { new ProcInfo(43440, 1, "ollama app"), new ProcInfo(40156, 43440, "ollama"), new ProcInfo(39600, 40156, "llama-server"), new ProcInfo(5, 1, "explorer") };
        var (kind, pids) = LocalAiProvider.FindServer(procs, []);
        Assert.Equal("Ollama", kind!.Display);
        Assert.Equal(new uint[] { 39600, 40156 }, pids.Order());
    }

    [Fact]
    public void FindsExtraConfiguredServer()
    {
        var (kind, pids) = LocalAiProvider.FindServer([new ProcInfo(9, 1, "meu-servidor")], ["meu-servidor.exe"]);
        Assert.Equal("meu-servidor", kind!.Display);
        Assert.Single(pids);
        Assert.Null(LocalAiProvider.FindServer([new ProcInfo(9, 1, "notepad")], []).kind);
    }

    [Fact]
    public void OfficeAddinThroughLocalProxy_IsAttributedToWord()
    {
        // WINWORD → msedgewebview2 (browser) → msedgewebview2 (renderer) → https://localhost:3000 (pythonw servidor.py) → Ollama :11434
        var procs = new[]
        {
            new ProcInfo(100, 1, "WINWORD"), new ProcInfo(110, 100, "msedgewebview2"), new ProcInfo(111, 110, "msedgewebview2"),
            new ProcInfo(200, 2, "pythonw"), new ProcInfo(300, 3, "ollama"),
        };
        var conns = new[]
        {
            new TcpConn(300, 11434, 0, Listening: true, Established: false, Loopback: true),
            new TcpConn(200, 3000, 0, true, false, true),
            new TcpConn(200, 52001, 11434, false, true, true), // proxy → ollama
            new TcpConn(300, 11434, 52001, false, true, true), // lado do servidor
            new TcpConn(111, 52100, 3000, false, true, true),  // WebView2 → proxy
            new TcpConn(200, 3000, 52100, false, true, true),
        };
        Assert.Equal(["Word"], LocalAiProvider.ResolveClients(Snap(procs, conns), [300]));
    }

    [Fact]
    public void CodexUsingLocalModel_IsAttributedToCodex()
    {
        var procs = new[] { new ProcInfo(10, 1, "Codex"), new ProcInfo(11, 10, "codex"), new ProcInfo(300, 3, "ollama") };
        var conns = new[] { new TcpConn(300, 11434, 0, true, false, true), new TcpConn(11, 50000, 11434, false, true, true), new TcpConn(300, 11434, 50000, false, true, true) };
        Assert.Equal(["Codex"], LocalAiProvider.ResolveClients(Snap(procs, conns), [300]));
    }

    [Fact]
    public void UnknownScriptClient_UsesFriendlyGenericName()
    {
        var procs = new[] { new ProcInfo(5, 1, "explorer"), new ProcInfo(20, 5, "powershell"), new ProcInfo(300, 3, "ollama") };
        var conns = new[] { new TcpConn(300, 11434, 0, true, false, true), new TcpConn(20, 50000, 11434, false, true, true), new TcpConn(300, 11434, 50000, false, true, true) };
        Assert.Equal(["PowerShell"], LocalAiProvider.ResolveClients(Snap(procs, conns), [300]));
    }

    [Fact]
    public void WorksWithoutListenEntry_AndExcludesSelfAndOutbound()
    {
        // Caso real observado: o socket LISTEN do Ollama não aparece na tabela; só os pares ESTABLISHED.
        var procs = new[] { new ProcInfo(5, 1, "explorer"), new ProcInfo(20, 5, "powershell"), new ProcInfo(47372, 5, "WinStatusBar"), new ProcInfo(300, 3, "ollama") };
        var conns = new[]
        {
            new TcpConn(300, 11434, 59002, false, true, true), new TcpConn(20, 59002, 11434, false, true, true),        // cliente real
            new TcpConn(300, 11434, 59933, false, true, true), new TcpConn(47372, 59933, 11434, false, true, true),     // o próprio WinStatusBar (/api/ps)
            new TcpConn(300, 60001, 443, false, true, false),                                                           // saída para a internet (pull)
        };
        Assert.Equal(["PowerShell"], LocalAiProvider.ResolveClients(Snap(procs, conns), [300], selfPid: 47372));
    }

    [Fact]
    public void NoClientConnections_ReturnsEmpty()
    {
        var procs = new[] { new ProcInfo(300, 3, "ollama") };
        Assert.Empty(LocalAiProvider.ResolveClients(Snap(procs, [new TcpConn(300, 11434, 0, true, false, true)]), [300]));
    }

    [Fact]
    public void GpuForPids_SumsProcessPerEngineAndTakesMax()
    {
        var engines = new List<KeyValuePair<string, double>>
        {
            new("pid_300_luid_0x00000000_0x23019699_phys_0_eng_0_engtype_3D", 40),
            new("pid_301_luid_0x00000000_0x23019699_phys_0_eng_0_engtype_3D", 15),
            new("pid_300_luid_0x00000000_0x23019699_phys_0_eng_1_engtype_Compute", 70),
            new("pid_999_luid_0x00000000_0x23019699_phys_0_eng_1_engtype_Compute", 99), // outro processo
        };
        Assert.Equal(70, LocalAiProvider.GpuForPids(engines, [300, 301]));
        Assert.Equal(0, LocalAiProvider.GpuForPids(engines, [12345]));
    }

    [Theory]
    [InlineData(50.0, 0.0, true)]
    [InlineData(0.0, 2.0, true)]
    [InlineData(2.0, 0.1, false)]
    public void BusyThresholds(double gpu, double cores, bool expected) => Assert.Equal(expected, LocalAiProvider.IsBusy(gpu, cores, 8, 0.6));

    [Fact]
    public void OllamaPs_PicksMostRecentlyUsedModel()
    {
        string json = """
        {"models":[
          {"name":"gemma4:26b-a4b-it-qat","size_vram":5000000000,"expires_at":"2026-11-04T14:00:00-03:00"},
          {"name":"gemma4:26b-sem-pensamento","size_vram":5000000000,"expires_at":"2026-11-04T14:05:00-03:00"}]}
        """;
        Assert.Equal(("gemma4:26b-sem-pensamento", 2), LocalAiProvider.ParseOllamaPs(json));
        Assert.Equal(((string?)null, 0), LocalAiProvider.ParseOllamaPs("""{"models":[]}"""));
    }

    [Fact]
    public void TcpPortIsNetworkByteOrder()
    {
        Assert.Equal(11434, SystemSnapshot.Port(0x0000AA2C)); // 11434 = 0x2CAA → bytes [2C, AA]
    }

    [Fact]
    public void RealSnapshot_ReadsProcessesAndTcp()
    {
        var s = SystemSnapshot.Capture(includeTcp: true);
        Assert.Contains(s.Processes.Values, p => p.Pid == (uint)Environment.ProcessId);
        Assert.NotEmpty(s.Connections);
    }
}
