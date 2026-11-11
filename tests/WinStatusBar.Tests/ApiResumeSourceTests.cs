using System.Text.RegularExpressions;
using Xunit;

namespace WinStatusBar.Tests;

/// <summary>Partes da continuação pela API que dependem do WinUI ou do Windows: confere o código-fonte.</summary>
public class ApiResumeSourceTests
{
    internal static string Body(string src, string sig)
    {
        var match = Regex.Match(src, Regex.Escape(sig) + @"[^{]*\{(?<b>.*?)\n    \}", RegexOptions.Singleline);
        Assert.True(match.Success, $"não achei: {sig}");
        return match.Groups["b"].Value;
    }

    private static string Src(params string[] path) => RepoSource.Read(["src", "WinStatusBar", .. path]);

    [Fact]
    public void BalloonClickComesFromNinBalloonUserClick()
    {
        string tray = Src("Services", "TrayIconService.cs");
        Assert.Contains("NIN_BALLOONUSERCLICK = WM_USER + 5", tray);
        Assert.Contains("0x405", tray);
        string callback = tray[tray.IndexOf("if (msg == CallbackMsg)", StringComparison.Ordinal)..];
        callback = callback[..callback.IndexOf("return IntPtr.Zero;", StringComparison.Ordinal)];
        Assert.Contains("ev == NIN_BALLOONUSERCLICK", callback);
        Assert.Contains("BalloonClicked?.Invoke()", callback);
    }

    [Fact]
    public void CodexThreadOpensTheCodexLink()
    {
        string launcher = Src("Services", "WindowsLauncherService.cs");
        Assert.Contains("codex://threads/", launcher);
        string body = Body(launcher, "public static void CodexThread(string threadId)");
        Assert.Contains("Shell(\"codex://threads/\" + threadId)", body);
        Assert.Contains("^[0-9A-Za-z-]{8,64}$", body);
    }

    private static string Svc() => Src("Services", "CodexApiResumeService.cs");

    [Fact]
    public void TheQuotaMenuStartsFromTheBarsBlockScan() =>
        Assert.Contains("LastScan => _lastScan ?? _hub.Codex.LastStopScan", Svc());

    [Fact]
    public void KeepsThePcAwakeButNotTheScreen()
    {
        string svc = Svc();
        Assert.Contains("ES_CONTINUOUS | ES_SYSTEM_REQUIRED", svc);
        Assert.Contains("0x80000000", svc);
        Assert.Contains("0x00000001", svc);
        Assert.DoesNotContain("ES_DISPLAY_REQUIRED", svc);
        Assert.DoesNotContain("0x00000002", svc);
    }

    [Fact]
    public void ScreenOffOnlyChangesTheRhythm()
    {
        string svc = Svc();
        Assert.Contains("ScreenOn => !Presence.Away", svc);
        Assert.DoesNotContain("WhenVisibleAsync", svc);
        Assert.DoesNotContain("WaitVisible", svc);
        var refreshes = svc.Split('\n').Where(l => l.Contains("OpenAiDashboardView.Refresh()")).ToList();
        Assert.NotEmpty(refreshes);
        Assert.All(refreshes, l => Assert.True(l.Contains("OpenAiDashboardView.Enabled") && l.Contains("!Presence.Away"), l));
    }

    [Fact]
    public void KeyNeverReachesLogsOrTheBarEnvironment()
    {
        string svc = Svc();
        Assert.DoesNotContain("SetEnvironmentVariable", svc);
        Assert.DoesNotMatch(new Regex(@"Log\([^;]*\b(apiKey|key)\b"), svc);
    }

    [Fact]
    public void KeyPresenceDoesNotLoadTheKey()
    {
        string body = Body(Svc(), "public bool HasKey");
        Assert.Contains("OpenAiApiKey.Exists(", body);
        Assert.Contains("ApiKeyStore.Exists(", body);
        Assert.DoesNotContain("ApiKeyStore.Get(", body);
        Assert.DoesNotContain("OpenAiApiKey.Read(", body);
    }

    [Fact]
    public void StoreBuildAndHelper()
    {
        string svc = Svc();
        Assert.Contains("PackageContext.IsPackaged", svc);
        Assert.Contains("ConsoleInterrupt.HelperArgs(", svc);
    }

    [Fact]
    public void ShutdownNeverWaitsNorKills()
    {
        string body = Body(Svc(), "public void Shutdown()");
        Assert.Contains("Supervisor.ShutdownAsync()", body);
        Assert.DoesNotContain(".Wait(", body);
        Assert.DoesNotContain("await ", body);
        Assert.DoesNotContain("KillTree", body);
    }

    [Fact]
    public void TheBarNeverAttachesToAConsole()
    {
        string root = Path.Combine(RepoSource.Root(), "src", "WinStatusBar");
        foreach (var file in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
                     .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")))
        {
            string text = File.ReadAllText(file);
            Assert.False(text.Contains("ConsoleInterrupt.Send(") || text.Contains("AttachConsole"), file);
        }
    }

    private static string App() => Src("App.xaml.cs");

    [Fact]
    public void ServiceStartsAfterTheHubAndBeforeTheTray()
    {
        string app = App();
        int hub = app.IndexOf("Hub.Start();", StringComparison.Ordinal);
        int create = app.IndexOf("ApiResume = new CodexApiResumeService(Hub, Settings, _dispatcher);", StringComparison.Ordinal);
        int start = app.IndexOf("ApiResume.Start();", StringComparison.Ordinal);
        int tray = app.IndexOf("_tray = new TrayIconService(IconPath);", StringComparison.Ordinal);
        Assert.True(hub >= 0 && hub < create && create < start && start < tray, $"{hub} {create} {start} {tray}");
    }

    [Fact]
    public void ApiResumePropertyIsInternal() =>
        Assert.Contains("internal CodexApiResumeService ApiResume { get; private set; }", App());

    [Fact]
    public void NotifyFollowsTheNotificationsOption()
    {
        string app = App();
        string body = Body(app, "public void Notify(");
        Assert.Contains("Settings.Current.Notifications", body);
        Assert.Contains("_balloonClick = onClick", body);
        Assert.Contains("_tray?.ShowBalloon(title, message)", body);
        Assert.Contains("_tray.BalloonClicked +=", app);
    }

    [Fact]
    public void ShutdownIsInsideTheFirstTimersStep()
    {
        string exit = Body(App(), "public void ExitApp()");
        int first = exit.IndexOf("ExitStep(", StringComparison.Ordinal);
        Assert.True(exit[first..].StartsWith("ExitStep(\"timers\"", StringComparison.Ordinal));
        int next = exit.IndexOf("ExitStep(", first + 1, StringComparison.Ordinal);
        Assert.Contains("ApiResume?.Shutdown();", exit[first..(next < 0 ? exit.Length : next)]);
    }

    [Fact]
    public void ExitNeverKillsCodex()
    {
        string exit = Body(App(), "public void ExitApp()");
        Assert.DoesNotContain("KillTree", exit);
        Assert.DoesNotContain("codex.exe", exit);
        Assert.DoesNotContain("GetProcessesByName(\"codex", App());
    }

    [Fact]
    public void ChoiceHappensInsideTheQuotaMenuStartingUnchecked()
    {
        string panel = Src("Views", "ContinueViaApiPanel.cs");
        foreach (var piece in new[] { "IsChecked = false", "c.CanMark", "ApiResumeText.ShowParallelChoice(", "svc.PrepareAsync(", "svc.Confirm(", "ApiResumeInline.Hold(" })
            Assert.Contains(piece, panel);
        Assert.Contains("ContinueViaApiPanel.Build(", Src("Views", "CodexApiResumeView.cs"));
    }

    /// <summary>A barra tem só a altura da faixa: uma janela de diálogo dela fica invisível e bloqueia os cliques.</summary>
    [Fact]
    public void NoDialogWindowOverTheBar()
    {
        Assert.False(File.Exists(Path.Combine(RepoSource.Root(), "src", "WinStatusBar", "Views", "ContinueViaApiDialog.cs")));
        foreach (var file in new[] { "CodexApiResumeView.cs", "ContinueViaApiPanel.cs", "ApiResumeInline.cs" })
            Assert.DoesNotContain("ContentDialog", Src("Views", file));
    }

    [Fact]
    public void InlineHoldIsReleasedWhenTheMenuCloses()
    {
        string inline = Src("Views", "ApiResumeInline.cs");
        Assert.Contains("Unloaded +=", inline);
        Assert.Contains("Interlocked.Decrement", inline);
        Assert.Contains("ApiResumeInline.Hold(", Src("Views", "CodexApiResumeView.cs"));
    }

    /// <summary>O detalhe técnico do bloqueio fica só no registro, fora da faixa amarela.</summary>
    [Fact]
    public void BlockBannerHasNoTechnicalLine() =>
        Assert.DoesNotContain("banner.Technical", Src("Views", "QuotaDetailsView.cs"));

    [Fact]
    public void ActivityCardWaitsWhileConfirming() =>
        Assert.Contains("if (nextApiKey != apiKey && !ApiResumeInline.Busy)", Src("Views", "BarWindow.Activity.cs"));

    [Fact]
    public void SectionOnlyWithBlockOrJobs()
    {
        string view = Src("Views", "CodexApiResumeView.cs");
        Assert.Contains("ApiResumeText.ShowSection(svc.CodexBlocked, svc.Supervisor.HasUnfinished)", view);
        // Opção desligada (Configurações › Codex e modelos): some, menos com continuações em andamento.
        Assert.Contains("if (!svc.Enabled && !svc.Supervisor.HasUnfinished) return null;", view);
        // Rodapé do menu da cota, na largura das duas colunas (e não mais no meio da coluna do Codex).
        Assert.DoesNotContain("CodexApiResumeView.Section(", Src("Views", "QuotaDetailsView.cs"));
        string bar = Src("Views", "BarWindow.xaml.cs");
        Assert.Contains("CodexApiResumeView.Section(App.Current.ApiResume, Root.RequestedTheme, resumeWidth, columnSpacing, divider: false)", bar);
        // O aviso de bloqueio da conta vai junto, no mesmo cartão do rodapé (separado das cotas acima).
        Assert.Contains("QuotaDetailsView.BlockFooter(_hub, Root.RequestedTheme, inner)", bar);
        Assert.Contains("CardBackgroundFillColorSecondaryBrush", bar);
        Assert.Contains("resumeWidth = QuotaDetailsView.ContentWidth + columnSpacing + api.Width;", bar);
    }

    [Fact]
    public void NoDialogWindowsFromTheBarMenus()
    {
        // A barra e os menus dela ficam numa janela da altura da faixa: um ContentDialog dela fica invisível e trava os cliques.
        foreach (var file in new[] { "QuotaDetailsView.cs", "OpenAiDashboardView.cs", "ApiUsageView.cs", Path.Combine("Panels", "RecentFolderFlyout.cs"), "CodexApiResumeView.cs", "ContinueViaApiPanel.cs" })
            Assert.DoesNotContain("ContentDialog", Src("Views", file));
        Assert.Contains("InlinePrompt.AskAsync(reset, \"Usar este reset do Codex?\"", Src("Views", "QuotaDetailsView.cs"));
        Assert.Contains("InlinePrompt.AskAsync(row, \"Sair da conta de faturamento?\"", Src("Views", "OpenAiDashboardView.cs"));
        Assert.Contains("InlinePrompt.AskAsync(search, \"Renomear\"", Src("Views", Path.Combine("Panels", "RecentFolderFlyout.cs")));
    }

    [Fact]
    public void QuotaMenuPausesWhileTheDialogIsOpen() =>
        Assert.Contains("internal static bool ResetPending => _resetPending || ApiResumeInline.Busy;", Src("Views", "QuotaDetailsView.cs"));

    [Theory]
    [InlineData("CodexApiResumeView.cs")]
    [InlineData("ContinueViaApiPanel.cs")]
    public void ViewsNeverSubscribeToEvents(string file)
    {
        string[] allowed = ["Click", "Checked", "Unchecked", "SelectionChanged"];
        foreach (Match m in Regex.Matches(Src("Views", file), @"\.(\w+) \+="))
            Assert.Contains(m.Groups[1].Value, allowed);
    }

    [Fact]
    public void ButtonsComeFromTheCoreRules()
    {
        string view = Src("Views", "CodexApiResumeView.cs");
        Assert.Contains("ApiResumeText.Actions(", view);
        Assert.Contains("ApiResumeText.ActionLabel(", view);
    }

    [Fact]
    public void ActivityShowsTheApiCardBeforeThePlanEarlyReturn()
    {
        string activity = Src("Views", "BarWindow.Activity.cs");
        Assert.Contains("svc.JobFor(selected)", activity);
        Assert.True(activity.IndexOf("svc.JobFor(selected)", StringComparison.Ordinal) < activity.IndexOf("if (planKey == key) return;", StringComparison.Ordinal));
        foreach (var piece in new[] { "ApiResumeText.CardTitle", "ApiResumeText.Banner(", "_hub.SelectAgent(", "apiKey", "CodexApiResumeView.JobCard(" })
            Assert.Contains(piece, activity);
    }

    [Fact]
    public void EndOfAJobRescansOnlyOnce()
    {
        string body = Body(Svc(), "private void OnSupervisorChanged()");
        Assert.Contains("_rescannedEnds.Add(", body);
    }

    [Fact]
    public void AgentMenuAsksTheVaultForTheGeminiKeyOnlyEvery30Seconds()
    {
        // A coluna da direita do menu de agentes roda a cada atualização (5 s ou menos, na thread da interface):
        // perguntava ao Cofre e decifrava a chave do Gemini uma ou duas vezes por atualização só para saber se existia.
        string hub = Src("Services", "MonitoringHub.cs");
        string right = Body(hub, "private Right ExtraRight(");
        Assert.DoesNotContain("ApiKeyStore.Get(", right);
        Assert.Contains("_geminiKeyPresent", right);
        string extras = Body(hub, "private List<(string Id, string Name, ImageSource? Logo)> ExtraAis()");
        Assert.Contains("_geminiKeyPresent = ApiKeyStore.Exists(AdditionalAiAccounts.Gemini.Resource);", extras);
        Assert.True(extras.IndexOf("TimeSpan.FromSeconds(30)", StringComparison.Ordinal) < extras.IndexOf("_geminiKeyPresent =", StringComparison.Ordinal));
    }

    [Fact]
    public void VaultPresenceNeverReadsThePassword()
    {
        string body = Body(Src("Services", "ApiKeyStore.cs"), "public static bool Exists(string resource)");
        Assert.Contains("FindAllByResource", body);
        Assert.DoesNotContain("RetrievePassword", body);
    }
}
