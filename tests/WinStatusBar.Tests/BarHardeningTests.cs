using System.Text.RegularExpressions;
using WinStatusBar.Core.Ai.Codex;
using WinStatusBar.Core.Clip;
using Xunit;

namespace WinStatusBar.Tests;

/// <summary>
/// Revisão de 11/11/2026: a barra não fecha pela metade, não refaz as barras à toa, não repete uma leitura que falhou
/// a cada segundo e não trabalha sem necessidade (thread da interface, tela apagada, PCs sem o programa consultado).
/// As partes que dependem do WinUI são conferidas pelo código-fonte.
/// </summary>
public class BarHardeningTests
{
    private static string Src(params string[] path) => RepoSource.Read(["src", "WinStatusBar", .. path]);
    private static string Core(params string[] path) => RepoSource.Read(["src", "WinStatusBar.Core", .. path]);
    private static string Body(string src, string sig) => ApiResumeSourceTests.Body(src, sig);
    private static int At(string text, string piece) => text.IndexOf(piece, StringComparison.Ordinal);
    private static int Count(string text, string piece) => Regex.Matches(text, Regex.Escape(piece)).Count;

    // ---------------------------------------------------------------- fechamento vindo de fora

    [Fact]
    public void AnOutsideCloseRequestNeverReachesTheClosedHandlers()
    {
        // Alt+F4 ou WM_CLOSE de outro programa: o Closed era só marcado como tratado, mas os outros tratadores de Closed
        // já tinham desmontado painéis, chat e fundo adaptativo, e a barra continuava na tela pela metade.
        string bar = Src("Views", "BarWindow.xaml.cs");
        Assert.Contains("AppWindow.Closing += (_, e) => { if (!_closing) e.Cancel = true; };", bar);
        Assert.Contains("Closed += (_, e) => { if (!_closing) { e.Handled = true; } };", bar);
        // A própria barra fecha normalmente: marca o fechamento e remove o subclass (que engole o WM_CLOSE) antes do Close().
        string close = Body(bar, "public void CloseBar()");
        int closing = At(close, "_closing = true;"), dispose = At(close, "AppBar?.Dispose();"), last = close.LastIndexOf("Close();", StringComparison.Ordinal);
        Assert.True(closing >= 0 && closing < dispose && dispose < last);
        string appBar = Src("Services", "AppBarService.cs");
        string proc = Body(appBar, "private IntPtr SubclassProc(");
        Assert.Matches(@"case WM_CLOSE:\s*(//[^\n]*\s*)*return IntPtr\.Zero;", proc);
        Assert.Contains("RemoveWindowSubclass(_hwnd, _proc, 0x5742);", Body(appBar, "public void Dispose()"));
    }

    // ---------------------------------------------------------------- monitores

    [Fact]
    public void TheFirstDisplayNoticeAfterStartupDoesNotRebuildTheBars()
    {
        // A chave dos monitores começava vazia: o primeiro aviso de tela, mesmo sem mudança, fechava e recriava as barras.
        string app = Src("App.xaml.cs");
        string launched = Body(app, "protected override void OnLaunched(");
        int bars = At(launched, "RebuildBars();"), seed = At(launched, "_lastMonitorKey = MonitorKey(GetMonitors());");
        Assert.True(bars >= 0 && bars < seed, "a chave é guardada logo depois de criar as barras");
        // Uma mudança de verdade (dispositivo, principal, área ou escala) continua refazendo as barras.
        string changed = Body(app, "private void RebuildBarsIfMonitorsChanged()");
        Assert.Contains("string key = MonitorKey(GetMonitors());", changed);
        Assert.Contains("if (key == _lastMonitorKey) return;", changed);
        Assert.Contains("b.CloseBar();", changed);
        Assert.Contains("$\"{m.Device}:{m.IsPrimary}:{m.Bounds}:{m.Dpi}\"", app);
    }

    [Fact]
    public void WindowEventsDoNotEnumerateTheMonitors()
    {
        string adaptive = Src("Views", "BarWindow.Adaptive.cs");
        string onMonitor = Body(adaptive, "private bool OnThisMonitor(IntPtr hwnd)");
        Assert.Contains("(_lastMonitor ?? CurrentMonitor()) is not { } m", onMonitor);
        Assert.Contains("return true; // sem dados: confere", onMonitor);
        Assert.Contains("_lastMonitor = CurrentMonitor();", Body(adaptive, "private void AdaptiveTick()"));
    }

    // ---------------------------------------------------------------- medição da barra

    [Fact]
    public void LayoutCheckKeepsTheStableTextWidthsAndLeavesTheMoreMenuAlone()
    {
        string bar = Src("Views", "BarWindow.xaml.cs");
        string overflow = Body(bar, "private void UpdateOverflow()");
        // A largura mínima dos textos só é zerada depois de reduzir a fonte (telas estreitas), não a cada medição.
        Assert.Equal(1, Count(overflow, "panel.MinWidth = 0"));
        Assert.True(At(overflow, "panel.MinWidth = 0") > At(overflow, "ApplyNativeTextScale(module, ratio)"));
        // O menu "…" é remontado ao abrir e sempre que as colunas mudam.
        Assert.DoesNotContain("RebuildMoreMenu()", overflow);
        Assert.Contains("RebuildMoreMenu();", Body(bar, "private void BuildColumns()"));
        Assert.Contains("RebuildMoreMenu();", Body(bar, "private void OnMoreOpening("));
        Assert.Contains("MoreMenu.Opening += OnMoreOpening;", Body(bar, "private void RebuildMoreMenu()"));
        // Tela apagada ou sessão bloqueada: nada a medir.
        Assert.Contains("_layoutTimer.Tick += (_, _) => { if (!Core.Presence.Away) UpdateOverflow(); };", bar);
    }

    // ---------------------------------------------------------------- coleta

    [Fact]
    public void AFailedTemperatureReadWaitsForTheNextIntervalAndDoesNotSkipTheCycle()
    {
        string hub = Src("Services", "MonitoringHub.cs");
        string loop = Body(hub, "private async Task LoopAsync(");
        int first = At(loop, "firstTemperature = false;"), restart = At(loop, "temperatureClock.Restart()"), sample = At(loop, "temperatures.SampleAsync(");
        Assert.True(first >= 0 && first < sample, "a primeira leitura deixa de ser a primeira antes de ler");
        Assert.True(restart >= 0 && restart < sample, "o relógio anda antes da leitura");
        int caught = At(loop, "catch (Exception ex) when (ex is not OperationCanceledException) { App.Log(\"Temperaturas: \" + ex.Message); }");
        Assert.True(caught > sample);
        Assert.True(caught < At(loop, "Local.Poll("), "a falha fica na leitura: IA local e envelhecimento seguem no mesmo ciclo");
        // Ao voltar da tela apagada e ao trocar de hardware, a leitura continua sendo refeita na hora.
        Assert.Contains("firstGpu = firstBattery = firstTemperature = true;", loop);
        Assert.Contains("if (hardwareChanged) { firstTemperature = true; LastTemperature = null; }", loop);

        string provider = Core("Metrics", "TemperatureProvider.cs");
        string read = Body(provider, "public async Task<TemperatureSample> SampleAsync(");
        Assert.Contains("catch (System.Runtime.InteropServices.COMException)", read);
        Assert.Contains("catch (Exception ex) when (ex is FormatException or InvalidCastException or OverflowException) { continue; }", read);
    }

    [Fact]
    public void AMissingLenovoSensorClassIsNotQueriedEveryTenSeconds()
    {
        string provider = Core("Metrics", "TemperatureProvider.cs");
        string lenovo = Body(provider, "public static double? ReadLenovoCpu()");
        Assert.Contains("if (MissingNamespaces.TryGetValue(LenovoKey, out var until) && DateTime.UtcNow < until) return null;", lenovo);
        Assert.Contains("catch (ManagementException ex) when (IsMissing(ex))", lenovo);
        Assert.Contains("MissingNamespaces[LenovoKey] = DateTime.UtcNow + MissingRetry;", lenovo);
        // Só a ausência da classe entra na espera: falha passageira ou falta de permissão tenta de novo na leitura seguinte.
        Assert.Equal(1, Count(lenovo, "MissingNamespaces[LenovoKey] ="));
        Assert.Contains("ManagementStatus.InvalidNamespace or ManagementStatus.InvalidClass or ManagementStatus.NotFound", provider);
    }

    // ---------------------------------------------------------------- atualização dos agentes

    [Fact]
    public void AgentRefreshDoesItsLookupsOnce()
    {
        string hub = Src("Services", "MonitoringHub.cs");
        // Janela em primeiro plano: uma consulta de processo por atualização (declaração + uma chamada).
        Assert.Equal(2, Count(hub, "ForegroundAgent()"));
        Assert.Contains("BuildAgentOptions(all, s, IconOf, candidates, foreground);", hub);
        // Cor de destaque das bolinhas: um UISettings só.
        Assert.Equal(1, Count(hub, "new Windows.UI.ViewManagement.UISettings()"));
        Assert.Contains("(_accentSettings ??= new Windows.UI.ViewManagement.UISettings())", hub);
        // A lista de sessões do modelo não tinha leitor: não é mais preenchida.
        Assert.DoesNotContain("vm.Sessions", hub);
        Assert.DoesNotContain("new SessionItem", hub);
    }

    [Fact]
    public void TrayTooltipIsSentOnlyWhenItChanges()
    {
        string tray = Src("Services", "TrayIconService.cs");
        string set = Body(tray, "public void SetTooltip(string text)");
        Assert.Contains("if (tip == _shownTip) return;", set);
        Assert.Contains("_shownTip = Shell_NotifyIconW(NIM_MODIFY, ref d) ? tip : null;", set);
        // Ícone registrado de novo (Explorer reiniciado) ou recarregado: volta a dica padrão, então a próxima é enviada.
        Assert.Contains("_shownTip = null;", Body(tray, "private void Add()"));
        Assert.Contains("if (_added) { var d = Data(); _shownTip = null; Shell_NotifyIconW(NIM_MODIFY, ref d); }", tray);
    }

    // ---------------------------------------------------------------- inicialização automática

    [Fact]
    public void StartupTaskIsCheckedOffTheInterfaceThreadAndOnlyWhenTheOptionChanges()
    {
        // A conferência abre o schtasks.exe e espera: rodava na thread da interface ao abrir e a cada ajuste aplicado.
        string app = Src("App.xaml.cs");
        string launched = Body(app, "protected override void OnLaunched(");
        Assert.Contains("QueueStartupSync();", launched);
        Assert.DoesNotContain("SyncStartup();", launched);
        string apply = Body(app, "public void ApplySettings()");
        Assert.Contains("QueueStartupSync();", apply);
        Assert.DoesNotContain("SyncStartup();", apply);
        string queue = Body(app, "private void QueueStartupSync()");
        Assert.Contains("if (_startupSynced == wanted) return;", queue);
        Assert.Contains("Task.Run(SyncStartup)", queue);
        // A conferência em si não mudou: instalada, e o estado difere da opção → ajusta. Uma por vez.
        string sync = Body(app, "private void SyncStartup()");
        Assert.Contains("lock (StartupGate)", sync);
        Assert.Contains("StartupService.IsEnabled() != Settings.Current.StartWithWindows", sync);
        Assert.Contains("StartupService.Set(Settings.Current.StartWithWindows, ExePath);", sync);
    }

    // ---------------------------------------------------------------- Qira

    [Fact]
    public void QiraWindowsAreNotSearchedOnPcsWithoutQira()
    {
        string qira = Src("Services", "QiraAppBridge.cs");
        string windows = Body(qira, "internal static List<IntPtr> Windows()");
        int guard = At(windows, "if (!InstalledCached) return [];"), scan = At(windows, "Process.GetProcessesByName(");
        Assert.True(guard >= 0 && guard < scan);
        Assert.Contains("now - checkedAt >= 60_000", qira);
        // Sem o Qira instalado não há janela; com ele, a busca continua a mesma (a lista pode vir vazia).
        Assert.NotNull(Services.QiraAppBridge.Windows());
        if (!Services.QiraAppBridge.IsInstalled) Assert.Empty(Services.QiraAppBridge.Windows());
    }

    // ---------------------------------------------------------------- registro

    [Fact]
    public void LogLinesAreMaskedOnceAndOrdinaryLinesStayAsTheyAre()
    {
        string log = Body(Src("App.xaml.cs"), "public static void Log(string message)");
        Assert.Equal(1, Count(log, "SecretMask.Mask("));
        Assert.Contains("global::WinStatusBar.Core.Clip.SecretMask.Mask(message)", log);

        // Linhas comuns do registro (identificadores, hashes, caminhos, números) passam sem alteração.
        foreach (string line in new[]
        {
            "Codex estado: sessão=019a6c1e-7d3a-7b12-9c44-0f2e5a6b7c8d turno=turn_42 Running -> Completed; offset=182734; fonte=rollout",
            "Atualização: SHA-256 9F86D081884C7D659A2FEAA0C55AD015A3BF4F1B2B0B822CD15D6C15B0F00A08 conferido (48211968 bytes)",
            "Fundo adaptativo (\\\\.\\DISPLAY1): Material Mica · 0x00000000000A0F3C",
            @"Claude Code detectado: C:\Users\alguem\AppData\Local\Programs\WinStatusBar\WinStatusBar.Collector.exe",
            "Indicador de progresso: sessão=task-0123456789abcdefghijklmnop; turno=; texto=42%; número=Visible; indeterminado=False",
            "Iniciado. Monitores: 2. Tema escuro: True.",
        })
            Assert.Equal(line, SecretMask.Mask(line));

        // Uma chave que escape numa mensagem de erro não vai inteira para o arquivo.
        string key = "sk-proj-" + new string('k', 44) + "Zz90";
        string masked = SecretMask.Mask("Consulta recusada (401): Incorrect API key provided: " + key);
        Assert.DoesNotContain(key, masked);
        Assert.StartsWith("Consulta recusada (401): Incorrect API key provided: sk-", masked);
    }

    // ---------------------------------------------------------------- Codex

    [Fact]
    public void AnUnchangedTitleIndexIsNotReadAgain()
    {
        string path = Path.Combine(Path.GetTempPath(), $"wsb-index-{Guid.NewGuid():N}.jsonl");
        try
        {
            Assert.Null(SessionTitleIndex.StampOf(path)); // sem arquivo
            Assert.True(SessionTitleIndex.NeedsRead(null, null));

            File.WriteAllText(path, """{"id":"a","thread_name":"Primeira"}""" + "\n");
            var first = SessionTitleIndex.StampOf(path);
            Assert.NotNull(first);
            Assert.True(SessionTitleIndex.NeedsRead(null, first), "nunca lido: lê");
            Assert.Equal("Primeira", SessionTitleIndex.Read(path)!["a"]);

            // Mesmo arquivo na conferência seguinte: não relê.
            Assert.False(SessionTitleIndex.NeedsRead(first, SessionTitleIndex.StampOf(path)));

            // Uma linha a mais (mesmo com o arquivo ainda aberto por quem escreve): relê.
            using (var writer = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete))
            {
                writer.Write("""{"id":"b","thread_name":"Segunda"}"""u8);
                writer.WriteByte((byte)'\n');
                writer.Flush();
                var grown = SessionTitleIndex.StampOf(path);
                Assert.NotNull(grown);
                Assert.True(grown!.Value.Length > first!.Value.Length);
                Assert.True(SessionTitleIndex.NeedsRead(first, grown));
            }

            // Mesmo tamanho, outra data (título renomeado com o mesmo número de letras): relê.
            var same = SessionTitleIndex.StampOf(path)!.Value;
            Assert.True(SessionTitleIndex.NeedsRead(same, same with { LastWriteUtc = same.LastWriteUtc.AddSeconds(1) }));
            // Arquivo sumiu: a leitura decide (e devolve "sem dados", sem apagar os títulos conhecidos).
            Assert.True(SessionTitleIndex.NeedsRead(same, null));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void CodexPeriodicCheckSkipsWhatCannotHaveChanged()
    {
        string provider = Core("Ai", "Codex", "CodexProvider.cs");
        string reconcile = Body(provider, "private void Reconcile(bool force = false)");
        // O índice de títulos só é relido na conferência periódica quando mudou; os avisos do arquivo sempre releem.
        Assert.Contains("if (LoadTitles(onlyIfChanged: true)) Raise();", reconcile);
        string titles = Body(provider, "private bool LoadTitles(bool onlyIfChanged = false)");
        Assert.Contains("if (onlyIfChanged && !SessionTitleIndex.NeedsRead(_titlesStamp, before)) return false;", titles);
        // Um índice vazio ou instável não apaga os títulos conhecidos nem conta como lido.
        int empty = At(titles, "if (map.Count == 0 && _titles.Count > 0) return false;"), stamped = At(titles, "_titlesStamp = read;");
        Assert.True(At(titles, "if (map is null) return false;") is >= 0 and var unreadable && unreadable < stamped);
        Assert.True(empty >= 0 && empty < stamped);
        // Nada na fila: o temporizador de 300 ms não copia a fila nem disputa a trava.
        Assert.StartsWith("if (_dirty.IsEmpty) return;", Body(provider, "private void ProcessDirty()").TrimStart());
        // Trocar o cancelamento descarta o anterior.
        Assert.Contains("cts?.Cancel(); cts?.Dispose();", Body(provider, "public void Stop()"));
    }

    [Fact]
    public void CodexPeriodicCheckWaitsWhileNobodyIsLookingAndNothingIsRunning()
    {
        string provider = Core("Ai", "Codex", "CodexProvider.cs");
        Assert.Contains("if (!Enabled || !force && PausedWhileAway() ||", Body(provider, "private void Reconcile(bool force = false)"));
        string paused = Body(provider, "private bool PausedWhileAway()");
        Assert.Contains("if (!Presence.Away || ReconcileWhileAway?.Invoke() == true) return false;", paused);
        Assert.Contains("!_sessions.Values.Any(s => s.State.IsActive())", paused);
        // Ao acender a tela: uma conferência na hora, fora da thread que avisou.
        string back = Body(provider, "private void OnPresenceChanged(bool away)");
        Assert.Contains("if (!away && Enabled) _ = Task.Run(() => { Reconcile(); ProcessDirty(); });", back);
        Assert.Contains("Presence.Changed += OnPresenceChanged;", Body(provider, "public void Start()"));
        Assert.Contains("Presence.Changed -= OnPresenceChanged;", Body(provider, "public void Stop()"));
        // Avisos de arquivo perdidos: confere mesmo com a tela apagada.
        Assert.Contains("_watcher.Error += (_, _) => Reconcile(force: true);", provider);
        // A continuação pela API roda com a tela apagada e lê os tokens da conversa: com uma em aberto, não pausa.
        Assert.Contains("Hub.Codex.ReconcileWhileAway = () => ApiResume?.Supervisor.HasUnfinished == true;", Src("App.xaml.cs"));
    }

    [Fact]
    public void CodexPeriodicCheckAsksEachRolloutForItsRealSize()
    {
        // Com o registro ainda aberto pelo Codex, a listagem da pasta devolve o tamanho e a data de antes da abertura
        // (conferido em 11/11/2026): a conferência tem de perguntar a cada arquivo, senão a conversa para de atualizar.
        string reconcile = Body(Core("Ai", "Codex", "CodexProvider.cs"), "private void Reconcile(bool force = false)");
        Assert.Contains("File.GetLastWriteTimeUtc(path) > DateTime.UtcNow.AddHours(-12)", reconcile);
        Assert.Contains("new FileInfo(path).Length != offset", reconcile);

        string dir = Path.Combine(Path.GetTempPath(), $"wsb-rollout-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            string path = Path.Combine(dir, "rollout.jsonl");
            File.WriteAllText(path, "{}\n");
            using var writer = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
            writer.Write(new byte[4096]);
            writer.Flush();
            // A pergunta direta ao arquivo sempre devolve o tamanho de agora, mesmo com quem escreve de arquivo aberto.
            Assert.Equal(3 + 4096, new FileInfo(path).Length);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }
}
