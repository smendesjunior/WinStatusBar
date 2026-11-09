using System.Text.RegularExpressions;
using WinStatusBar.Services;
using Xunit;

namespace WinStatusBar.Tests;

/// <summary>
/// Saída da barra (09/11/2026): o processo ficava vivo depois de "Encerrando." porque o WinUI, no modo padrão
/// (OnLastWindowClose), só termina quando a última janela fecha, e os timers das cotas recriavam janelas WebView2 ocultas.
/// </summary>
public class ExitPathTests
{
    [Fact]
    public void WatchdogRunsTheForcedExitOnceAfterTheTimeoutEvenIfStartedAgain()
    {
        int calls = 0;
        bool background = false;
        using var done = new ManualResetEventSlim();
        var watchdog = new ExitWatchdog(TimeSpan.FromMilliseconds(200), () =>
        {
            background = Thread.CurrentThread.IsBackground;
            Interlocked.Increment(ref calls);
            done.Set();
        });
        Assert.False(watchdog.Started);

        watchdog.Start();
        watchdog.Start(); // sinal de saída e menu da bandeja ao mesmo tempo
        Assert.True(watchdog.Started);
        Assert.Equal(0, Volatile.Read(ref calls)); // ainda dentro do prazo

        Assert.True(done.Wait(TimeSpan.FromSeconds(5)));
        Thread.Sleep(300);
        Assert.Equal(1, Volatile.Read(ref calls));
        Assert.True(background); // a própria vigia nunca segura o processo aberto
    }

    [Fact]
    public void WatchdogDoesNothingUntilStarted()
    {
        int calls = 0;
        _ = new ExitWatchdog(TimeSpan.FromMilliseconds(50), () => Interlocked.Increment(ref calls));
        Thread.Sleep(300);
        Assert.Equal(0, Volatile.Read(ref calls));
    }

    [Fact]
    public void ExitAppStopsTheHiddenWindowTimersThenEndsTheProcessItself()
    {
        string exitApp = ExitAppSource();
        int watchdog = exitApp.IndexOf("_exitWatchdog.Start()", StringComparison.Ordinal);
        int log = exitApp.IndexOf("Log(\"Encerrando.\")", StringComparison.Ordinal);
        int explicitShutdown = exitApp.IndexOf("DispatcherShutdownMode.OnExplicitShutdown", StringComparison.Ordinal);
        int exit = exitApp.LastIndexOf("Exit();", StringComparison.Ordinal);
        int terminate = exitApp.IndexOf("TerminateSelf(0)", StringComparison.Ordinal);
        Assert.True(watchdog >= 0 && watchdog < log, "o limite de segurança começa antes de qualquer etapa da saída");
        Assert.True(explicitShutdown >= 0 && explicitShutdown < exit, "Exit() precisa do modo explícito para postar WM_QUIT mesmo com janelas abertas");
        // Depois do Exit() (janelas fechadas e gravações feitas) o processo termina na hora, sem o desmonte do WinUI,
        // em que continuações atrasadas sem o XAML derrubavam o processo (0xc000027b) e seguravam a saída por até 25 s.
        Assert.True(terminate > exit, "o processo termina logo depois do Exit()");
        foreach (string stop in new[] { "GoogleUsageSession.Stop()", "MicrosoftCreditsSession.Stop()", "_apiUsageTimer?.Stop()" })
            Assert.True(exitApp.IndexOf(stop, StringComparison.Ordinal) is >= 0 and var at && at < explicitShutdown, stop + " antes do fim do laço");
    }

    private static string ExitAppSource()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "WinStatusBar.sln"))) dir = dir.Parent;
        Assert.NotNull(dir);
        string app = File.ReadAllText(Path.Combine(dir!.FullName, "src", "WinStatusBar", "App.xaml.cs"));
        var match = Regex.Match(app, @"public void ExitApp\(\)\s*\{(?<body>.*?)\n    \}", RegexOptions.Singleline);
        Assert.True(match.Success, "ExitApp não encontrado em App.xaml.cs");
        return match.Groups["body"].Value;
    }
}
