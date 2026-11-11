using WinStatusBar.Core.Ai;
using WinStatusBar.Core.Ai.Codex;
using Xunit;
using T = WinStatusBar.Core.Ai.AgentPrompt.Target;

namespace WinStatusBar.Tests;

/// <summary>Correções do chat da barra (envio, fluxo, diálogos, Claude pela linha de comando, busca do Codex).</summary>
public class ChatRegressionTests
{
    private static string View(string file) => RepoSource.Read("src", "WinStatusBar", "Views", file);

    // ---- Claude pela linha de comando
    [Fact]
    public void ClaudeArguments_KeepThePromptAsAnArgumentInTheCommonCase()
    {
        var options = new AgentPrompt.Options([], Web: true, AllowEdits: true, ResumeSession: "s1", History: []) { SystemPrompt = "sistema", AllowedTools = ["mcp__drive"] };
        var (args, stdin) = AgentPrompt.ClaudeArguments("olá\nmundo \"x\" & %y%", "claude-x", "high", options, ["D:\\b"], viaCmd: false);
        Assert.Null(stdin);
        Assert.Equal(["-p", "olá\nmundo \"x\" & %y%", "--output-format", "stream-json", "--verbose", "--include-partial-messages",
            "--append-system-prompt", "sistema", "--resume", "s1", "--model", "claude-x", "--effort", "high", "--add-dir", "D:\\b",
            "--allowedTools", "WebSearch", "WebFetch", "mcp__drive", "--permission-mode", "acceptEdits"], args);

        var (plain, plainStdin) = AgentPrompt.ClaudeArguments("oi", null, null, AgentPrompt.Options.None, [], viaCmd: false);
        Assert.Null(plainStdin);
        Assert.Equal(["-p", "oi", "--output-format", "stream-json", "--verbose", "--include-partial-messages"], plain);

        var (ephemeral, _) = AgentPrompt.ClaudeArguments("resuma", "m", "low", new AgentPrompt.Options([], false, false, null, [], Ephemeral: true), [], viaCmd: false);
        Assert.Equal(["-p", "resuma", "--output-format", "stream-json", "--verbose", "--include-partial-messages", "--model", "m", "--effort", "low",
            "--no-session-persistence", "--strict-mcp-config", "--settings", "{\"disableAllHooks\":true}"], ephemeral);
    }

    [Fact]
    public void ClaudeArguments_SendThePromptByStdinThroughCmdOrWhenTooLong()
    {
        // Só existe o claude.cmd: pelo cmd.exe, quebra de linha cortava o comando e aspas, & e % eram reinterpretados.
        var (viaCmd, cmdStdin) = AgentPrompt.ClaudeArguments("linha 1\nlinha 2 & \"x\" %PATH%", "claude-x", null, AgentPrompt.Options.None, [], viaCmd: true);
        Assert.Equal("linha 1\nlinha 2 & \"x\" %PATH%", cmdStdin);
        Assert.Equal(["-p", "--output-format", "stream-json", "--verbose", "--include-partial-messages", "--model", "claude-x"], viaCmd);

        // Acima de 32.767 caracteres o Windows nem inicia o processo.
        string big = new('x', 40000);
        var (longArgs, longStdin) = AgentPrompt.ClaudeArguments(big, null, null, AgentPrompt.Options.None, [], viaCmd: false);
        Assert.Equal(big, longStdin);
        Assert.Equal(["-p", "--output-format", "stream-json", "--verbose", "--include-partial-messages"], longArgs);

        // No limite, segue como argumento; pedido curto com muito texto nos outros argumentos também vai pela entrada padrão.
        var (_, atLimit) = AgentPrompt.ClaudeArguments(new string('x', 10000), null, null, AgentPrompt.Options.None, [], viaCmd: false);
        Assert.Null(atLimit);
        var (_, bigSystem) = AgentPrompt.ClaudeArguments("oi", null, null, new AgentPrompt.Options([], false, false, null, []) { SystemPrompt = new string('s', 25000) }, [], viaCmd: false);
        Assert.Equal("oi", bigSystem);
    }

    [Fact]
    public void ClaudeStdinPromptIsWrittenWithoutBomBeforeTheInputCloses()
    {
        string run = RepoSource.Read("src", "WinStatusBar.Core", "Ai", "AgentPrompt.cs");
        Assert.Contains("if (stdinPrompt is not null) psi.StandardInputEncoding = new UTF8Encoding(false);", run);
        Assert.Contains("await proc.StandardInput.WriteAsync(stdinPrompt.AsMemory(), ct); proc.StandardInput.Close();", run);
        Assert.Contains("else proc.StandardInput.Close();", run);
    }

    [Fact]
    public void ClaudeErrorResultIsNotKeptAsTheAnswer()
    {
        // "result" com is_error: o texto do provedor era mostrado como erro E guardado como a resposta da IA.
        Assert.Equal("", AgentPrompt.CliAnswer(T.Claude, "Credit balance is too low", "Credit balance is too low", ""));
        Assert.Equal("Parte que chegou.", AgentPrompt.CliAnswer(T.Claude, "API Error: 529", "API Error: 529", "Parte que chegou.\n\n"));
        Assert.Equal("Resposta final.", AgentPrompt.CliAnswer(T.Claude, null, "Resposta final.", "Resposta em fluxo."));
        // Os outros destinos por linha de comando continuam como eram.
        Assert.Equal("mensagem do agente", AgentPrompt.CliAnswer(T.Codex, "turn.failed", "mensagem do agente", ""));
    }

    // ---- Executável do Codex
    [Fact]
    public void CodexExecutableSearchIsRememberedForAWhile()
    {
        long now = 0; int searches = 0; string? result = @"C:\codex\codex.exe"; bool exists = true;
        var memo = new CodexAppServerClient.PathMemo(() => { searches++; return result; }, TimeSpan.FromMinutes(2), () => now, _ => exists);
        Assert.Equal(result, memo.Get());
        now = 119_000;
        Assert.Equal(@"C:\codex\codex.exe", memo.Get());
        Assert.Equal(1, searches);
        // O arquivo guardado sumiu (atualização do Codex troca a pasta da versão): busca de novo na hora.
        exists = false; result = @"C:\codex\2\codex.exe";
        Assert.Equal(@"C:\codex\2\codex.exe", memo.Get());
        Assert.Equal(2, searches);
        // Vencido o prazo: busca de novo.
        exists = true; now += 120_000;
        memo.Get();
        Assert.Equal(3, searches);
    }

    [Fact]
    public void CodexExecutableNotFoundIsRememberedToo()
    {
        long now = 0; int searches = 0; string? result = null;
        var memo = new CodexAppServerClient.PathMemo(() => { searches++; return result; }, TimeSpan.FromMinutes(2), () => now, _ => true);
        Assert.Null(memo.Get());
        now = 60_000; result = @"C:\codex\codex.exe";
        Assert.Null(memo.Get());
        Assert.Equal(1, searches);
        now = 120_000;
        Assert.Equal(result, memo.Get());
    }

    [Fact]
    public void CodexFolderNamesAreNotTranslated()
    {
        string client = RepoSource.Read("src", "WinStatusBar.Core", "Ai", "Codex", "CodexAppServerClient.cs");
        // "Links" passava pelo tradutor ("Enlaces", "Liens"…) e a pasta do WinGet não era lida nesses idiomas.
        Assert.DoesNotContain("LocalizedText.T(\"Links\")", client);
        Assert.DoesNotContain("LocalizedText.T(\"Codex\")", client);
        Assert.Contains("Path.Combine(local, \"Microsoft\", \"WinGet\", \"Links\")", client);
        Assert.Contains("\"OpenAI\", \"Codex\", \"bin\")", client);
        Assert.Contains("public static string? FindCodexExecutable() => CodexExecutable.Get();", client);
    }

    // ---- Envio (partes que dependem do WinUI: confere o código-fonte)
    [Fact]
    public void SecondEnterWhileTheFirstIsBeingPreparedIsIgnored()
    {
        string command = View("BarWindow.Command.cs");
        string submit = ApiResumeSourceTests.Body(command, "private async void SubmitCommand(string? overridePrompt = null)");
        Assert.Contains("if (_commandLabel is null || _running || _submitting is not null) return;", submit);
        // A senha é tomada depois do teste de pedido vazio e devolvida só por quem a tomou (a continuação dos
        // conectores chama SubmitCommand de dentro do envio anterior e já tem a própria senha).
        Assert.True(submit.IndexOf("if (prompt.Length == 0) return;", StringComparison.Ordinal) < submit.IndexOf("var ticket = _submitting = new object();", StringComparison.Ordinal));
        Assert.Contains("try { await SubmitCommandAsync(prompt, ticket); }", submit);
        Assert.Contains("finally { if (ReferenceEquals(_submitting, ticket)) _submitting = null; }", submit);
        // Devolvida na linha em que _running liga: dali em diante quem segura o segundo envio é o _running.
        Assert.Contains("_running = true; _interrupted = false; if (ReferenceEquals(_submitting, ticket)) _submitting = null;", command);
    }

    [Fact]
    public void StopIsRecognizedWhateverTheUiLanguage()
    {
        string command = View("BarWindow.Command.cs");
        Assert.DoesNotContain("== L.T(\"Interrompido\")", command);
        Assert.DoesNotContain("!= L.T(\"Interrompido\")", command);
        Assert.Contains("_interrupted = AiErrorExplainer.IsInterrupted(result.Error);", command);
        Assert.Contains("bool stopped = AiErrorExplainer.IsInterrupted(err);", command);
        Assert.Contains("cts.IsCancellationRequested || AiErrorExplainer.IsInterrupted(result.Error)", command);
        Assert.Contains("!_interrupted && !AiErrorExplainer.IsInterrupted(result.Error)", command);
    }

    [Fact]
    public void StreamedTextIsCoalescedAndOnlyFollowedWhileTheReaderIsAtTheEnd()
    {
        string command = View("BarWindow.Command.cs");
        // Um trecho por vez na fila da interface, sempre com o texto mais recente.
        Assert.Contains("Volatile.Write(ref pending.Latest, partial);", command);
        Assert.Contains("if (Interlocked.Exchange(ref pending.Queued, 1) == 0 && !DispatcherQueue.TryEnqueue(ShowPartial)) Volatile.Write(ref pending.Queued, 0);", command);
        Assert.Contains("if (Volatile.Read(ref pending.Latest) is not { } latest || _commandCts != cts) return;", command);
        // A rolagem só acompanha enquanto a pessoa não subiu para ler.
        string follow = ApiResumeSourceTests.Body(command, "private void FollowChatEnd()");
        Assert.Contains("if (!_chatFollow || _chatScroll is not { } scroll) return;", follow);
        string moved = ApiResumeSourceTests.Body(command, "private void OnChatViewChanged(object? sender, ScrollViewerViewChangedEventArgs e)");
        Assert.Contains("_chatFollow = scroll.VerticalOffset >= Math.Min(_chatFollowFloor, scroll.ScrollableHeight) - 40;", moved);
        Assert.Contains("_chatScroll.ViewChanged += OnChatViewChanged;", command);
        Assert.Contains("answer.Stream(latest);", command);
        Assert.DoesNotContain("answer.Stream(partial);", command);
        // O intervalo de desenho do texto não mudou.
        Assert.Contains("_timer.Interval = TimeSpan.FromMilliseconds(150);", command);
    }

    [Fact]
    public void DiscardedAnswerStopsItsRenderTimer()
    {
        string command = View("BarWindow.Command.cs");
        int end = command.IndexOf("public void EndProgress()", StringComparison.Ordinal);
        string body = command[end..command.IndexOf("public void Stream(string text)", end, StringComparison.Ordinal)];
        Assert.Contains("_ended = true; _timer.Stop();", body);
        int stream = command.IndexOf("public void Stream(string text)", StringComparison.Ordinal);
        Assert.Contains("if (_ended) return;", command[stream..command.IndexOf("public void Finish(string text)", stream, StringComparison.Ordinal)]);
        // Conversa nova (e outra aberta do histórico, que passa por ela) encerra a resposta em andamento.
        Assert.Contains("_currentAnswer?.EndProgress();", ApiResumeSourceTests.Body(command, "private void NewConversation()"));
    }

    [Fact]
    public void HistorySummaryIsCancelledWhenStaleAndNeverOverwritesANewerOne()
    {
        string command = View("BarWindow.Command.cs");
        Assert.Contains("if (_summaryConv == conv) _summaryCts?.Cancel();", ApiResumeSourceTests.Body(command, "private async Task SubmitCommandAsync(string prompt, object ticket)"));
        string summary = ApiResumeSourceTests.Body(command, "private async void SummarizeConversation(Conversation conv)");
        Assert.Contains("if (_summaryConv == conv) _summaryCts?.Cancel();", summary);
        Assert.Contains("if (count <= conv.AiSummaryFor) return;", summary);
        Assert.True(summary.IndexOf("if (count <= conv.AiSummaryFor) return;", StringComparison.Ordinal) < summary.IndexOf("conv.AiSummary = ", StringComparison.Ordinal));
        Assert.Contains("if (_summaryCts == cts) { _summaryCts = null; _summaryConv = null; }", summary);
    }

    [Fact]
    public void MenusThatReadFromDiskLogTheirFailures()
    {
        string command = View("BarWindow.Command.cs");
        Assert.Contains("try { projects = await Task.Run(() => AgentPrompt.Projects(t)); }", ApiResumeSourceTests.Body(command, "private async void ShowProjectMenu()"));
        Assert.Contains("try { list = await Task.Run(() => ConversationStore.List(80)); }", ApiResumeSourceTests.Body(command, "private async void ShowHistoryMenu(FrameworkElement anchor)"));
        Assert.Contains("catch (Exception ex) { App.Log(\"Projetos do agente: não foi possível ler · \" + ex.Message); return; }", command);
        Assert.Contains("catch (Exception ex) { App.Log(\"Histórico de conversas: não foi possível ler · \" + ex.Message); return; }", command);
        Assert.Contains("foreach (var process in found) process.Dispose();", command);
    }

    // ---- Perguntas do chat (ContentDialog)
    [Fact]
    public void ChatDialogsAreSerializedAndNeverAnchoredOnTheBar()
    {
        string plugins = View("BarWindow.Plugins.cs");
        // A janela da barra tem 72 px: um diálogo nela fica invisível e ainda assim modal.
        Assert.DoesNotContain("Root.XamlRoot", plugins);
        Assert.Contains("private XamlRoot? DialogRoot=>_panelInput?.XamlRoot;", plugins);
        Assert.Equal(3, plugins.Split("new ContentDialog{XamlRoot=root,").Length - 1);
        // Um diálogo por vez: as três perguntas esperam a mesma vez e a devolvem no finally.
        Assert.Contains("private readonly SemaphoreSlim _dialogGate=new(1,1);", plugins);
        Assert.Equal(2, plugins.Split("await _dialogGate.WaitAsync(ct);entered=true;").Length - 1);
        Assert.Equal(1, plugins.Split("await _dialogGate.WaitAsync();entered=true;").Length - 1);
        Assert.Equal(3, plugins.Split("finally{if(entered)_dialogGate.Release();}").Length - 1);
        // Sem o painel: como recusado, sem exceção.
        Assert.Contains("if(DialogRoot is not {} root){completion.TrySetResult(false);return;}", plugins);
        Assert.Contains("if(DialogRoot is not {} root){completion.TrySetResult((false,false));return;}", plugins);
        Assert.Contains("if(DialogRoot is not {} root){completion.TrySetResult(new JsonObject{[\"answers\"]=new JsonObject()});return;}", plugins);
        // O cancelamento e o botão padrão da autorização continuam.
        Assert.Equal(2, plugins.Split("using var registration=ct.Register(()=>DispatcherQueue.TryEnqueue(()=>dialog?.Hide()));").Length - 1);
        Assert.Contains("PrimaryButtonText=\"Autorizar\",CloseButtonText=\"Cancelar\",DefaultButton=ContentDialogButton.Close", plugins);
    }

    // ---- Imagens do chat
    [Fact]
    public void OldDownloadedChatImagesAreDeletedBeforeANewDownload()
    {
        string images = View("ChatImageActions.cs");
        string local = ApiResumeSourceTests.Body(images, "private static async Task<string> LocalFileAsync(Uri uri)");
        Assert.True(local.IndexOf("DeleteOldDownloads();", StringComparison.Ordinal) is > 0 and var at && at < local.IndexOf("Http.GetByteArrayAsync(uri)", StringComparison.Ordinal));
        string delete = ApiResumeSourceTests.Body(images, "private static void DeleteOldDownloads()");
        Assert.Contains("var limit = DateTime.UtcNow.AddDays(-1);", delete);
        Assert.Contains("Directory.EnumerateFiles(Path.GetTempPath(), \"wsb-imagem-*\")", delete);
        Assert.Contains("if (File.GetLastWriteTimeUtc(old) < limit) File.Delete(old);", delete);
    }
}
