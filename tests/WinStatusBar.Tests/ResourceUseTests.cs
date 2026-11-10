using System.Text;
using WinStatusBar.Core;
using WinStatusBar.Core.Ai;
using WinStatusBar.Core.Metrics;
using Xunit;

namespace WinStatusBar.Tests;

/// <summary>Uso de CPU e memória da barra (10/11/2026): pausa sem ninguém vendo, GPU pelo kernel gráfico e leituras baratas.</summary>
[Collection(nameof(PresenceCollection))]
public class ResourceUseTests
{
    [Fact]
    public async Task PresencePausesWhileTheScreenIsOffOrLockedAndResumesRightAway()
    {
        Presence.Reset();
        try
        {
            var changes = new List<bool>();
            void OnChanged(bool away) { lock (changes) changes.Add(away); }
            Presence.Changed += OnChanged;
            try
            {
                Assert.False(Presence.Away);
                Assert.True(Presence.WhenVisibleAsync(CancellationToken.None).IsCompleted);

                Presence.SetDisplay(on: false);
                Assert.True(Presence.Away);
                var waiting = Presence.WhenVisibleAsync(CancellationToken.None);
                Assert.False(waiting.IsCompleted);

                Presence.SetLocked(true);   // continua ausente: sem aviso repetido
                Presence.SetDisplay(on: true); // tela acesa, mas bloqueada: ainda ausente
                Assert.True(Presence.Away);
                Assert.False(waiting.IsCompleted);

                Presence.SetLocked(false);
                await waiting.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.False(Presence.Away);
                lock (changes) Assert.Equal([true, false], changes);
            }
            finally { Presence.Changed -= OnChanged; }
        }
        finally { Presence.Reset(); }
    }

    [Fact]
    public void PresenceWaitStopsWhenTheLoopIsCancelled()
    {
        Presence.Reset();
        try
        {
            Presence.SetDisplay(on: false);
            using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
            Assert.False(Presence.WaitVisible(cts.Token));
        }
        finally { Presence.Reset(); }
    }

    [Fact]
    public void PeriodicLoopsWaitForSomeoneToSeeTheBar()
    {
        // Sensores, janelas das IAs, cotas pelo navegador, cota do Codex, acompanhamento da tarefa e fundo adaptativo.
        foreach (var file in new[]
        {
            "src/WinStatusBar/Services/MonitoringHub.cs", "src/WinStatusBar/Services/ChatActivityProvider.cs",
            "src/WinStatusBar/Services/GoogleUsageSession.cs", "src/WinStatusBar/Services/MicrosoftCreditsSession.cs",
            "src/WinStatusBar/Services/TaskBriefingService.cs", "src/WinStatusBar/Views/BarWindow.Adaptive.cs",
            "src/WinStatusBar/App.xaml.cs", "src/WinStatusBar.Core/Ai/Codex/CodexProvider.cs",
        })
            Assert.Contains("Presence.", RepoSource.Read(file));
        // Os avisos do Windows chegam pela janela da bandeja.
        string tray = RepoSource.Read("src/WinStatusBar/Services/TrayIconService.cs");
        Assert.Contains("RegisterPowerSettingNotification", tray);
        Assert.Contains("WTSRegisterSessionNotification", tray);
    }

    [Fact]
    public void ContinuousBarAnimationsRunOnTheSharedFrameClock()
    {
        // Animações sem fim do compositor ou do XAML mantinham a composição na taxa da tela (165 Hz) enquanto uma IA
        // trabalhava; o relógio único redesenha ~32 vezes por segundo.
        foreach (var file in new[]
        {
            "src/WinStatusBar/Views/ContourGlow.cs", "src/WinStatusBar/Views/ClaudeWorkingDots.cs",
            "src/WinStatusBar/Views/OllamaLogo.cs", "src/WinStatusBar/Services/AgentMotion.cs",
        })
        {
            string source = RepoSource.Read(file);
            Assert.Contains("FrameClock.Add", source);
            Assert.DoesNotContain("Forever", source);
        }
    }

    [Fact]
    public void ChatWindowsAreProbedOftenOnlyWhenInUseOrWorking()
    {
        var now = new DateTime(2026, 11, 10, 6, 0, 0, DateTimeKind.Utc);
        var never = DateTime.MinValue;
        // App em uso agora ou há pouco: a cada volta (2,5 s).
        Assert.True(Services.ChatProbeSchedule.ShouldProbe(now, now.AddSeconds(-2.5), now, running: false));
        Assert.True(Services.ChatProbeSchedule.ShouldProbe(now, now.AddSeconds(-2.5), now.AddSeconds(-90), running: false));
        // Conversa em andamento: acompanha o fim mesmo com o app em segundo plano.
        Assert.True(Services.ChatProbeSchedule.ShouldProbe(now, now.AddSeconds(-2.5), never, running: true));
        // App aberto, mas parado em segundo plano: só a cada 10 s.
        Assert.False(Services.ChatProbeSchedule.ShouldProbe(now, now.AddSeconds(-5), now.AddMinutes(-3), running: false));
        Assert.True(Services.ChatProbeSchedule.ShouldProbe(now, now.AddSeconds(-10), now.AddMinutes(-3), running: false));
        Assert.True(Services.ChatProbeSchedule.ShouldProbe(now, never, never, running: false));
    }

    [Fact]
    public void EngineInstanceNamesGiveTheEngineType()
    {
        const string luid = "luid_0x00000000_0x00011933";
        Assert.True(GpuKernelStats.TryParseEngine($"pid_4321_{luid}_phys_0_eng_3_engtype_VideoDecode", luid, out int node, out string type));
        Assert.Equal((3, "VideoDecode"), (node, type));
        Assert.True(GpuKernelStats.TryParseEngine($"pid_1_{luid}_phys_0_eng_12_engtype_", luid, out node, out type));
        Assert.Equal((12, ""), (node, type));
        Assert.False(GpuKernelStats.TryParseEngine($"pid_1_luid_0x00000000_0x00011558_phys_0_eng_0_engtype_3D", luid, out _, out _));
    }

    [Fact]
    public void KernelEngineValuesWorkWithTheExistingUtilizationMath()
    {
        // Formato das leituras pelo kernel gráfico: uma por motor, sem PID.
        const string luid = "luid_0x00000000_0x0000ABCD";
        var values = new List<KeyValuePair<string, double>>
        {
            new($"{luid}_phys_0_eng_0_engtype_3D", 42),
            new($"{luid}_phys_0_eng_1_engtype_Copy", 7),
            new($"{luid}_phys_0_eng_2_engtype_VideoDecode", 12),
        };
        Assert.Equal(42, GpuMetricsProvider.ComputeEngineUtilization(values, luid));
        var types = GpuMetricsProvider.EngineTypeUtilization(values, luid);
        Assert.Equal(42, types["3D"]); Assert.Equal(7, types["Copy"]); Assert.Equal(12, types["VideoDecode"]);
    }

    [Fact]
    public void KernelStatisticsReadTheRealGpu()
    {
        var adapters = GpuMetricsProvider.EnumerateAdapters();
        var stats = new GpuKernelStats(() => []);
        var supported = adapters.Where(a => stats.Supports(a.Luid)).ToList();
        if (supported.Count == 0) return; // máquina sem estatísticas WDDM (VM, sessão remota antiga)
        var adapter = supported[0];
        Assert.Null(stats.Engines(adapter)); // primeira leitura só marca o ponto de partida
        Thread.Sleep(200);
        var engines = stats.Engines(adapter);
        Assert.NotNull(engines);
        Assert.NotEmpty(engines);
        Assert.All(engines, e => { Assert.StartsWith(adapter.LuidKey, e.Key); Assert.InRange(e.Value, 0, 100); });
        // Uso por processo (o próprio teste): zero na primeira leitura, valor válido na segunda.
        var self = new HashSet<uint> { (uint)Environment.ProcessId };
        var luids = supported.Select(a => a.Luid).ToList();
        Assert.Equal(0, stats.ProcessUtilization(luids, self));
        Thread.Sleep(100);
        Assert.InRange(stats.ProcessUtilization(luids, self) ?? -1, 0, 100);
    }

    [Fact]
    public void ProcessThreadAndHandleCountsComeFromOneSystemCall()
    {
        using var history = new CpuCoreHistory();
        history.Sample();
        Assert.True(history.ProcessCount > 10);
        Assert.True(history.ThreadCount > history.ProcessCount);
        Assert.True(history.HandleCount > history.ThreadCount);
        // Nenhum contador PDH que percorre todos os processos (só o comentário que explica a troca cita os nomes).
        string source = RepoSource.Read("src/WinStatusBar.Core/Metrics/CpuCoreHistory.cs");
        Assert.DoesNotContain("@\"\\System\\", source);
        Assert.DoesNotContain("@\"\\Process(", source);
    }

    [Fact]
    public void TranscriptLinesAreInterpretedOnceAndHugeToolOutputsAreNotKept()
    {
        string path = Path.Combine(Path.GetTempPath(), $"wsb-digest-{Guid.NewGuid():N}.jsonl");
        try
        {
            const string user = """{"type":"user","timestamp":"2026-11-10T06:00:00Z","message":{"role":"user","content":"Otimize a barra"}}""";
            // Codex: plano por chamada de função (argumentos em texto JSON) dentro de response_item.
            const string codexPlan = """{"type":"response_item","timestamp":"2026-11-10T06:00:05Z","payload":{"type":"function_call","name":"update_plan","arguments":"{\"plan\":[{\"step\":\"Medir\",\"status\":\"in_progress\"},{\"step\":\"Otimizar\",\"status\":\"pending\"}]}"}}""";
            string tool = "{\"type\":\"user\",\"message\":{\"role\":\"user\",\"content\":[{\"type\":\"tool_result\",\"content\":\"" + new string('x', 300 * 1024) + "\"}]}}";
            const string answer = """{"type":"assistant","timestamp":"2026-11-10T06:01:00Z","message":{"role":"assistant","content":[{"type":"text","text":"Medido: 16% de um núcleo"}]}}""";
            File.WriteAllText(path, string.Join("\n", user, codexPlan, tool, answer) + "\n", new UTF8Encoding(false));
            var digest = ActivityDigest.Read(path);
            Assert.Equal("Otimize a barra", digest.Request);
            Assert.Equal(["Medir", "Otimizar"], digest.Steps.Select(s => s.Text));
            Assert.Equal("Medido: 16% de um núcleo", digest.Updates.Last().Text);
            Assert.Equal(DateTimeOffset.Parse("2026-11-10T06:00:00Z"), digest.StartedAt);
            // A mesma conversa lida de uma vez (Parse) dá o mesmo resultado que a leitura incremental.
            var parsed = ActivityDigest.Parse(File.ReadAllLines(path));
            Assert.Equal(digest.Steps, parsed.Steps);
            Assert.Equal(digest.Updates, parsed.Updates);
            Assert.Equal(digest.Request, parsed.Request);
        }
        finally { File.Delete(path); }
    }
}

[CollectionDefinition(nameof(PresenceCollection), DisableParallelization = true)]
public class PresenceCollection { }
