using WinStatusBar.Core.Ai;
using Xunit;
namespace WinStatusBar.Tests;

public class DesktopExperienceTests : IDisposable
{
    private readonly string _file = Path.Combine(Path.GetTempPath(), "wsb-exp-" + Guid.NewGuid().ToString("N") + ".json");
    public void Dispose() { try { File.Delete(_file); } catch { } }

    private static readonly DesktopActionRequest[] NotepadSteps =
    [
        new("open", Path: @"C:\a\nota.txt"), new("keys", Keys: "Ctrl+End"), new("paste", Text: "Olá"), new("keys", Keys: "Ctrl+S"),
    ];

    [Fact] public void RemembersAndRecallsSimilarRequestsIgnoringPaths()
    {
        var memory = new DesktopExperience(_file);
        Assert.True(memory.Remember(@"Abra C:\x\nota.txt no Bloco de Notas, cole a linha 'Olá' no fim e salve", "Notepad", NotepadSteps, ["run · notepad.exe → excedeu 60 s"]));
        var reloaded = new DesktopExperience(_file); // persistido
        var hit = Assert.Single(reloaded.Recall(@"Abra D:\outra\lista.txt no Bloco de Notas, cole 'tchau' no fim e salve", "Notepad"));
        Assert.Contains("\"action\":\"open\"", hit.Steps[0]);
        Assert.Empty(reloaded.Recall("Qual a previsão do tempo amanhã?", null));
        string text = DesktopExperience.Format([hit]);
        Assert.Contains("Caminhos que já deram certo", text);
        Assert.Contains("evite", text);
        Assert.Contains("open", text);
    }

    [Fact] public void SameTaskUpdatesInsteadOfDuplicating()
    {
        var memory = new DesktopExperience(_file);
        memory.Remember("Justifique o documento do Word inteiro e salve", "WINWORD", [new("office", Application: "word", Op: "format", Scope: "document", Align: "justify")], []);
        memory.Remember("Justifique o documento do Word inteiro e salve", "WINWORD", [new("office", Application: "word", Op: "format", Scope: "document", Align: "justify"), new("office", Application: "word", Op: "save")], []);
        var only = Assert.Single(memory.All);
        Assert.Equal(2, only.Uses);
        Assert.Equal(2, only.Steps.Length);
    }

    [Fact] public void NeverStoresSecretsOrReadOnlyRuns()
    {
        var memory = new DesktopExperience(_file);
        Assert.False(memory.Remember("Digite minha senha no site", "msedge", [new("paste", Text: "x")], []));
        Assert.False(memory.Remember("Liste as janelas", null, [new("list_windows")], []));
        Assert.Empty(memory.All);
    }

    [Fact] public void LongTextsAreShortenedAndSizeIsCapped()
    {
        var memory = new DesktopExperience(_file);
        memory.Remember("Cole o relatório longo no Word", "WINWORD", [new("paste", Text: new string('x', 5000))], []);
        Assert.True(memory.All[0].Steps[0].Length < 200);
        for (int i = 0; i < DesktopExperience.MaxRecipes + 10; i++)
            memory.Remember($"Tarefa distinta número{i} alfa{i} beta{i} gama{i}", "app" + i, [new("file", Op: "mkdir", Path: @"C:\t\" + i)], []);
        Assert.Equal(DesktopExperience.MaxRecipes, memory.All.Count);
    }

    [Fact] public void EveryFreeTextFieldIsShortenedAndCheckedForSecrets()
    {
        var memory = new DesktopExperience(_file);
        string big = new('y', 3000);
        Assert.True(memory.Remember("Rode o script de conversão das plantas", null,
            [new("powershell", Text: "param($a) $a", Arguments: [big, "curto"]), new("registry", Op: "write", Path: @"HKCU\Software\X", Name: "V", Value: big),
             new("office", Application: "word", Op: "replace", Find: big, Replace: big)], []));
        var steps = memory.All[0].Steps;
        Assert.All(steps, s => Assert.True(s.Length < 400, s.Length.ToString()));
        Assert.Contains("curto", steps[0]);
        // Segredo fora de "text" (argumento, valor, find/replace): nada é guardado.
        Assert.False(memory.Remember("Configure o acesso do serviço de mapas", null, [new("powershell", Text: "param($a) $a", Arguments: ["token=abc123"])], []));
        Assert.False(memory.Remember("Grave a configuração do serviço de mapas", null, [new("registry", Op: "write", Path: @"HKCU\Software\X", Name: "V", Value: "senha 123")], []));
        Assert.False(memory.Remember("Troque o trecho no contrato de mapas", null, [new("office", Application: "word", Op: "replace", Find: "a", Replace: "api key 9")], []));
        Assert.Single(memory.All);
    }

    [Fact] public void DamagedFileEntriesAreDroppedInsteadOfBreakingTheExecutor()
    {
        File.WriteAllText(_file, """
            [null,
             {"Request":null,"App":"x","Steps":["a"],"Avoid":[],"Uses":1,"LastUsed":"2026-11-01T10:00:00"},
             {"Request":"Sem passos","App":"x","Steps":null,"Avoid":[],"Uses":1,"LastUsed":"2026-11-01T10:00:00"},
             {"Request":"Sem desvios","App":"x","Steps":["a"],"Avoid":null,"Uses":1,"LastUsed":"2026-11-01T10:00:00"},
             {"Request":"Abra a nota no Bloco de Notas e salve","App":"Notepad","Steps":["{\"action\":\"open\"}"],"Avoid":[],"Uses":2,"LastUsed":"2026-11-01T10:00:00"}]
            """);
        var memory = new DesktopExperience(_file);
        var only = Assert.Single(memory.All);
        Assert.Equal("Notepad", only.App);
        Assert.Single(memory.Recall("Abra a nota no Bloco de Notas e salve tudo", "Notepad"));
        Assert.True(memory.Remember("Crie a pasta de entregas do mês", null, [new("file", Op: "mkdir", Path: @"C:\t\e")], []));
        Assert.Equal(2, new DesktopExperience(_file).All.Count); // formato do arquivo igual: relido sem perda
    }

    [Fact] public async Task LoopRecallsOnFirstRoundAndRecordsOnlyVerifiedPaths()
    {
        var previous = DesktopAgentLoop.Memory;
        try
        {
            var memory = DesktopAgentLoop.Memory = new DesktopExperience(_file);
            memory.Remember("Abra a nota no Bloco de Notas e cole a linha no fim", "Notepad", NotepadSteps, []);
            string? firstSystem = null; int calls = 0;
            const string failing = "```winstatusbar-desktop\n{\"steps\":[{\"action\":\"keys\",\"keys\":\"Ctrl+O\"},{\"action\":\"paste\",\"text\":\"x\"}]}\n```";
            const string working = "```winstatusbar-desktop\n{\"steps\":[{\"action\":\"file\",\"op\":\"append\",\"path\":\"C:\\\\a\\\\b.txt\",\"text\":\"linha\"}],\"final\":\"Feito.\"}\n```";
            await DesktopAgentLoop.RunAsync("Acrescente a linha no fim do arquivo b.txt pelo Bloco de Notas", AgentPrompt.Options.None,
                (p, o, s, ct) => { if (++calls == 1) firstSystem = o.SystemPrompt; return Task.FromResult(new AgentPrompt.Result(calls == 1 ? failing : working, "s", null)); },
                (r, ct) => Task.FromResult(r.Action switch
                {
                    "context" => new DesktopActionResult("Janela ativa: Notepad — \"b.txt\" [1]"),
                    "paste" => new DesktopActionResult("O foco mudou", Success: false),
                    _ => new DesktopActionResult("ok"),
                }), _ => { }, CancellationToken.None);
            Assert.Contains("Caminhos que já deram certo", firstSystem);
            var learned = memory.All.First(r => r.Request.StartsWith("Acrescente"));
            Assert.DoesNotContain(learned.Steps, s => s.Contains("Ctrl+O")); // passos do lote que falhou ficam fora
            Assert.Contains(learned.Steps, s => s.Contains("\"op\":\"append\""));
            Assert.Contains(learned.Avoid, a => a.StartsWith("paste"));
            Assert.Equal("Notepad", learned.App);
        }
        finally { DesktopAgentLoop.Memory = previous; }
    }
}
