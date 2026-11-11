using WinStatusBar.Core.Ai;
using Xunit;

namespace WinStatusBar.Tests;

/// <summary>
/// Regras de segurança do executor da barra (auxiliar WinStatusBar.Automation): o que é lógica pura fica no Core e é
/// testado aqui; o uso dentro do auxiliar (que não entra neste projeto de testes) é conferido pelo código-fonte.
/// </summary>
public class ExecutorSafetyTests
{
    private static string Skills() => RepoSource.Read("src", "WinStatusBar.Automation", "DesktopSkills.cs");
    private static string Runtime() => RepoSource.Read("src", "WinStatusBar.Automation", "DesktopRuntime.cs");

    // ---- Lixeira: rede e unidades removíveis não têm (a exclusão seria definitiva e silenciosa)
    [Fact] public void OnlyFixedLocalDrivesHaveARecycleBin()
    {
        Assert.False(DesktopSafety.HasRecycleBin(@"\\servidor\pasta\relatorio.docx"));
        Assert.False(DesktopSafety.HasRecycleBin(@"\\?\UNC\servidor\pasta\relatorio.docx"));
        Assert.False(DesktopSafety.HasRecycleBin(@"Q:\obras\planta.dwg", _ => DriveType.Network));
        Assert.False(DesktopSafety.HasRecycleBin(@"E:\fotos\a.jpg", _ => DriveType.Removable));
        Assert.False(DesktopSafety.HasRecycleBin(@"C:\a.txt", _ => throw new IOException("unidade indisponível")));
        Assert.True(DesktopSafety.HasRecycleBin(@"C:\Users\x\a.txt", _ => DriveType.Fixed));
        Assert.True(DesktopSafety.HasRecycleBin(@"\\?\C:\Users\x\a.txt", _ => DriveType.Fixed));
        Assert.True(DesktopSafety.HasRecycleBin(Path.Combine(Environment.SystemDirectory, "notepad.exe"))); // disco do sistema, de verdade
    }

    [Fact] public void RefusalsNeverAskForAdministrator()
    {
        // "[requer administrador]" faria a barra repetir a operação recusada no auxiliar elevado.
        Assert.DoesNotContain(DesktopStatus.NeedsAdmin, DesktopSafety.NoRecycleBinMessage(@"\\s\p\a.txt"));
        Assert.Contains(@"\\s\p\a.txt", DesktopSafety.NoRecycleBinMessage(@"\\s\p\a.txt"));
        Assert.DoesNotContain(DesktopStatus.NeedsAdmin, DesktopSafety.ProtectedProcess("LenovoQiraCore"));
        Assert.DoesNotContain(DesktopStatus.NeedsAdmin, DesktopSafety.ProtectedProcess("WinStatusBar"));
    }

    [Fact] public void HelperChecksTheRecycleBinBeforeDeletingOrOverwriting()
    {
        string skills = Skills();
        int recycle = skills.IndexOf("private static void RecycleBin(string path)", StringComparison.Ordinal);
        Assert.True(recycle > 0);
        string body = skills[recycle..skills.IndexOf("SHFileOperation(ref op)", recycle, StringComparison.Ordinal)];
        Assert.Contains("RequireRecycleBin(path);", body); // todo envio à Lixeira confere antes (delete e as sobrescritas)
        Assert.Contains("if (!DesktopSafety.HasRecycleBin(path)) throw new BlockedException(", skills);
        Assert.DoesNotContain("0x4000", body); // sem FOF_WANTNUKEWARNING: abriria um diálogo num auxiliar sem janela
    }

    // ---- process kill: partes do Qira e a própria barra
    [Theory]
    [InlineData("LenovoQiraCore")]
    [InlineData("lenovo.pfm.pipe")]
    [InlineData("QuantumAI.Hub.exe")]
    [InlineData("quantumai.hub")]
    [InlineData(" LocalAgent.EXE ")]
    [InlineData("WinStatusBar")]
    [InlineData("winstatusbar.exe")]
    [InlineData("WinStatusBar.Automation")]
    [InlineData("WinStatusBar.CodexIAs.exe")]
    public void ProtectedProcessesAreNotKilled(string name) => Assert.NotNull(DesktopSafety.ProtectedProcess(name));

    [Theory]
    [InlineData("notepad")]
    [InlineData("WINWORD.EXE")]
    [InlineData("WinStatusBarX")]
    [InlineData("")]
    [InlineData(null)]
    public void OrdinaryProcessesStayKillable(string? name) => Assert.Null(DesktopSafety.ProtectedProcess(name));

    [Fact] public void HelperRefusesProtectedProcessesBeforeLookingThemUp()
    {
        string skills = Skills();
        int kill = skills.IndexOf("if (r.Op == \"kill\")", StringComparison.Ordinal);
        Assert.True(kill > 0);
        int guard = skills.IndexOf("DesktopSafety.ProtectedProcess", kill, StringComparison.Ordinal);
        int lookup = skills.IndexOf("Process.GetProcessesByName", kill, StringComparison.Ordinal);
        Assert.True(guard > kill && guard < lookup);
    }

    // ---- copiar/mover uma pasta para dentro dela mesma
    [Fact] public void FolderCannotGoInsideItself()
    {
        Assert.True(DesktopSafety.IsInside(@"C:\dados\projeto\projeto", @"C:\dados\projeto"));
        Assert.True(DesktopSafety.IsInside(@"C:\dados\PROJETO\sub\copia", @"C:\dados\projeto\"));
        Assert.True(DesktopSafety.IsInside(@"C:\x", @"C:\"));
        Assert.False(DesktopSafety.IsInside(@"C:\dados\projeto", @"C:\dados\projeto"));      // o mesmo item tem a própria mensagem
        Assert.False(DesktopSafety.IsInside(@"C:\dados\projeto-copia", @"C:\dados\projeto")); // só o começo do nome coincide
        Assert.False(DesktopSafety.IsInside(@"C:\dados", @"C:\dados\projeto"));
        Assert.False(DesktopSafety.IsInside(@"D:\dados\projeto\x", @"C:\dados\projeto"));
    }

    [Fact] public void HelperRefusesFolderIntoItselfBeforeTouchingTheDestination()
    {
        string skills = Skills();
        int destination = skills.IndexOf("private static string Destination(", StringComparison.Ordinal);
        Assert.True(destination > 0);
        int guard = skills.IndexOf("DesktopSafety.IsInside", destination, StringComparison.Ordinal);
        int recycle = skills.IndexOf("RecycleBin(dest)", destination, StringComparison.Ordinal);
        int create = skills.IndexOf("Directory.CreateDirectory", destination, StringComparison.Ordinal);
        Assert.True(guard > destination && guard < recycle && guard < create);
    }

    // ---- download: não trunca o que já existe e não deixa arquivo pela metade no lugar final
    [Fact] public void DownloadGoesToATemporaryFileAndNeverOverwritesSilently()
    {
        string skills = Skills();
        int start = skills.IndexOf("case \"download\":", StringComparison.Ordinal);
        string body = skills[start..skills.IndexOf("case \"powershell\":", start, StringComparison.Ordinal)];
        Assert.DoesNotContain("File.Create(to)", body);
        Assert.Contains("r.Overwrite", body);
        Assert.Contains(".part", body);
        Assert.Contains("File.Move(", body);
        Assert.Contains("File.Delete(", body);
        Assert.Contains("RecycleBin(to)", body);
    }

    // ---- prazo do auxiliar por operação
    [Fact] public void WaitWindowAndSlowSystemOperationsOutliveTheirOwnLimits()
    {
        static int Seconds(string action, string? op = null, int? timeout = null) => new DesktopActionRequest(action, Op: op, TimeoutSeconds: timeout).EffectiveTimeoutSeconds;
        // wait_window: a espera pedida (1..300 s, padrão 20) mais uma folga, nunca menos que o prazo comum.
        Assert.Equal(60, Seconds("wait_window"));
        Assert.Equal(60, Seconds("wait_window", timeout: 30));
        Assert.Equal(135, Seconds("wait_window", timeout: 120));
        Assert.Equal(315, Seconds("wait_window", timeout: 300));
        Assert.Equal(315, Seconds("wait_window", timeout: 5000));
        Assert.Equal(120, DesktopActionRequest.WaitWindowSeconds(120));
        Assert.Equal(20, DesktopActionRequest.WaitWindowSeconds(null));
        Assert.Equal(300, DesktopActionRequest.WaitWindowSeconds(5000));
        // system sem timeoutSeconds: cada operação lenta cobre os próprios limites internos.
        Assert.Equal(60, Seconds("system", "info"));
        Assert.Equal(300, Seconds("system", "recipe"));          // network.map: 60 + 120 + 60
        Assert.Equal(125, Seconds("system", "revit_request"));   // prazo interno padrão de 110 s
        Assert.Equal(180, Seconds("system", "network_map"));     // diálogo nativo de credenciais
        Assert.Equal(180, Seconds("system", "network_reconnect"));
        // timeoutSeconds informado continua valendo; o Revit ganha a folga para responder "consulte o mesmo pedido".
        Assert.Equal(400, Seconds("system", "recipe", 400));
        Assert.Equal(215, Seconds("system", "revit_request", 200));
        Assert.Equal(DesktopActionRequest.MaxTimeoutSeconds, Seconds("system", "revit_request", 1800));
        Assert.Equal(30, Seconds("system", "network_map", 30));
        // O que já valia continua igual.
        Assert.Equal(60, Seconds("wait", timeout: 900));
        Assert.Equal(60, Seconds("install", "install"));
        Assert.Equal(600, Seconds("install", "install", 600));
        Assert.Equal(5, Seconds("powershell", timeout: 1));
    }

    [Fact] public void HelperWaitsForAWindowWithTheSameLimitTheDeadlineAssumes()
        => Assert.Contains("DesktopActionRequest.WaitWindowSeconds(r.TimeoutSeconds)", Skills());

    // ---- "target": qual janela
    private static int Pick(string target, params (string Process, string Title)[] windows) => DesktopSafety.PickWindow(windows, target);

    [Fact] public void TargetPrefersTheProgramOverATitleThatMerelyContainsTheWord()
    {
        // O navegador está na frente com "password" no título; "Word" é o Word.
        Assert.Equal(1, Pick("Word", ("chrome", "Reset your password - Google Chrome"), ("WINWORD", "Relatório.docx - Word")));
        Assert.Equal(1, Pick("word", ("msedge", "Wordle - Microsoft Edge"), ("WINWORD", "Documento1 - Word")));
        // Sem processo com o nome, vale o título com a palavra inteira antes de um pedaço de palavra.
        Assert.Equal(1, Pick("Word", ("chrome", "Password manager"), ("chrome", "Microsoft Word help - Google Chrome")));
        Assert.Equal(0, Pick("word", ("chrome", "Password manager")));
    }

    [Fact] public void TargetKeepsExactMatchesAndZOrder()
    {
        Assert.Equal(1, Pick("excel", ("notepad", "Dicas de Excel avançado"), ("EXCEL", "Pasta1 - Excel")));
        Assert.Equal(1, Pick("Calculadora", ("chrome", "Calculadora de juros - Google Chrome"), ("ApplicationFrameHost", "Calculadora")));
        Assert.Equal(1, Pick("Bloco", ("explorer", "Downloads"), ("Notepad", "Sem título - Bloco de Notas")));
        Assert.Equal(0, Pick("chrome", ("chrome", "A"), ("chrome", "B")));               // a de cima
        Assert.Equal(1, Pick("Salvar como", ("WINWORD", "Doc - Word"), ("WINWORD", "Salvar como")));
        Assert.Equal(-1, Pick("Revit", ("chrome", "A"), ("WINWORD", "Doc - Word")));
        Assert.Equal(-1, Pick("", ("chrome", "A")));
    }

    [Fact] public void HelperResolvesTargetsWithTheSharedRanking() => Assert.Contains("DesktopSafety.PickWindow", Runtime());

    // ---- uia por controlId
    [Fact] public void ControlIdIsResolvedAgainstWhatWasShownNotAgainstAFreshCapture()
    {
        string runtime = Runtime();
        int uia = runtime.IndexOf("if(r.Action==\"uia\")", StringComparison.Ordinal);
        Assert.True(uia > 0);
        string body = runtime[uia..runtime.IndexOf("if(r.Action==\"activate\")", uia, StringComparison.Ordinal)];
        Assert.Contains("ContextActions.Rebind", body);
        Assert.Contains("_shown", body);
        // find também mostra números: o retrato dele precisa ser lembrado.
        string skills = Skills();
        int find = skills.IndexOf("case \"find\":", StringComparison.Ordinal);
        Assert.Contains("Remember(", skills[find..skills.IndexOf("case \"file\":", find, StringComparison.Ordinal)]);
    }

    // ---- entrada sem observação: sem captura completa dos controles só para ler processo e geometria
    [Fact] public void InputWithoutObservationDoesNotCaptureTheWholeWindow()
    {
        string runtime = Runtime();
        int start = runtime.IndexOf("if(r.Action==\"activate\")", StringComparison.Ordinal);
        string body = runtime[start..runtime.IndexOf("switch(r.Action)", start, StringComparison.Ordinal)];
        Assert.DoesNotContain("Capture(hwnd", body);
        Assert.Contains("observe novamente", body); // a conferência do processo da observação informada continua
    }

    // ---- saída de comandos: cortada antes de voltar à IA
    [Fact] public void CommandOutputIsCutBeforeGoingBackToTheModel()
    {
        string commands = RepoSource.Read("src", "WinStatusBar.Automation", "Expansion", "StructuredCommands.cs");
        Assert.Contains("12000", commands.Replace("_", ""));
        Assert.Contains("4000", commands.Replace("_", ""));
        Assert.Contains("truncado; filtre no script", commands);
    }

    // ---- Office: mesma leitura do aplicativo nas duas camadas e op conferida antes de abrir o programa
    [Fact] public void OfficeOperationIsValidatedBeforeAnyApplicationStarts()
    {
        string skills = Skills();
        int office = skills.IndexOf("private static DesktopActionResult OfficeOp(", StringComparison.Ordinal);
        int validate = skills.IndexOf("ValidOfficeOp(", office, StringComparison.Ordinal);
        int create = skills.IndexOf("OfficeApp(\"Word.Application\", create: true)", office, StringComparison.Ordinal);
        Assert.True(validate > office && validate < create);
        string expansion = RepoSource.Read("src", "WinStatusBar.Automation", "Expansion", "OfficeExpansion.cs");
        Assert.DoesNotContain("r.Application == \"word\"", expansion);
        Assert.DoesNotContain("r.Application == \"excel\"", expansion);
    }
}
