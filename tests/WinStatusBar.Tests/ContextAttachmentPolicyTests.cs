using WinStatusBar.Services;
using Xunit;
namespace WinStatusBar.Tests;
public class ContextAttachmentPolicyTests
{
    [Fact] public void KeepsContextAvailableForFollowupButConsumesOrdinaryAttachments()
    {
        var attachments = new List<string> { "window.png", "selection.txt", "user.pdf" };
        ContextAttachmentPolicy.AfterSend(attachments, true, "window.png", "selection.txt");
        Assert.Equal(new[] { "window.png", "selection.txt" }, attachments);
        ContextAttachmentPolicy.AfterSend(attachments, true, "window.png", "selection.txt");
        Assert.Equal(2, attachments.Count);
    }
    [Fact] public void DisabledContextIsNotResent()
    {
        var attachments = new List<string> { "window.png", "selection.txt" };
        ContextAttachmentPolicy.AfterSend(attachments, false, "window.png", "selection.txt");
        Assert.Empty(attachments);
    }
    [Fact] public void RemovedContextIsNotRestoredFromStaleMetadata()
    {
        var attachments = new List<string> { "user.pdf" };
        ContextAttachmentPolicy.AfterSend(attachments, true, "window.png", "selection.txt");
        Assert.Empty(attachments);
    }
}

/// <summary>Limpeza do cache de contexto (capturas da janela e texto do documento ativo), numa pasta temporária.</summary>
public class ContextCacheTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "wsb-context-" + Guid.NewGuid().ToString("N"), "cache", "context");
    private readonly DateTime _now = DateTime.UtcNow;

    public ContextCacheTests() => Directory.CreateDirectory(_root);
    public void Dispose() { try { Directory.Delete(Path.GetDirectoryName(Path.GetDirectoryName(_root)!)!, true); } catch (IOException) { } }

    private static string NewName() => Guid.NewGuid().ToString("N");

    private string FileAged(string relative, double days)
    {
        string path = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "conteúdo");
        File.SetLastWriteTimeUtc(path, _now.AddDays(-days));
        return path;
    }

    private string FolderAged(string name, double days)
    {
        string path = Path.Combine(_root, name);
        Directory.CreateDirectory(path);
        Age(path, days);
        return path;
    }

    private void Age(string folder, double days)
    {
        Directory.SetCreationTimeUtc(folder, _now.AddDays(-days));
        Directory.SetLastWriteTimeUtc(folder, _now.AddDays(-days));
    }

    [Fact] public void DeletesCapturesAndTextsOlderThanSevenDaysAndKeepsRecentOnes()
    {
        string oldPng = FileAged(NewName() + ".png", 8), newPng = FileAged(NewName() + ".png", 6);
        string oldDir = NewName(), newDir = NewName();
        string oldText = FileAged(Path.Combine(oldDir, "texto-da-pagina.txt"), 30);
        string oldSheet = FileAged(Path.Combine(oldDir, "dados-da-planilha.txt"), 30);
        string newText = FileAged(Path.Combine(newDir, "texto-do-documento.txt"), 1);

        var (files, folders) = ContextCache.Purge(_root, _now);

        Assert.Equal((3, 0), (files, folders));
        Assert.False(File.Exists(oldPng)); Assert.False(File.Exists(oldText)); Assert.False(File.Exists(oldSheet));
        Assert.True(File.Exists(newPng)); Assert.True(File.Exists(newText));
        // A pasta fica: uma conversa pode ter essa pasta como pasta de trabalho (o Claude só retoma a sessão nela).
        Assert.True(Directory.Exists(Path.Combine(_root, oldDir)));
    }

    [Fact] public void RemovesAFolderOnlyWhenItIsEmptyAndOlderThanSixtyDays()
    {
        string emptyOld = FolderAged(NewName(), 61), emptyRecent = FolderAged(NewName(), 59);
        string withText = NewName();
        string text = FileAged(Path.Combine(withText, "texto-da-janela.txt"), 90);
        Age(Path.Combine(_root, withText), 90);

        Assert.Equal((1, 1), ContextCache.Purge(_root, _now));
        Assert.False(Directory.Exists(emptyOld));
        Assert.True(Directory.Exists(emptyRecent));
        Assert.False(File.Exists(text));
        Assert.True(Directory.Exists(Path.Combine(_root, withText))); // esvaziada agora: ainda não está parada há 60 dias

        Assert.Equal((0, 2), ContextCache.Purge(_root, _now.AddDays(61)));
        Assert.False(Directory.Exists(emptyRecent));
        Assert.False(Directory.Exists(Path.Combine(_root, withText)));
        Assert.True(Directory.Exists(_root));
    }

    [Fact] public void NeverTouchesTheRootOrFilesTheBarDidNotWrite()
    {
        Age(_root, 400);
        string note = FileAged("anotacoes.txt", 400);                              // não é captura
        string named = FileAged("janela.png", 400);                                // PNG sem nome de GUID
        string output = FileAged(Path.Combine(NewName(), "resumo.docx"), 400);     // algo criado por uma IA na pasta da conversa
        string nested = FileAged(Path.Combine(NewName(), "sub", "texto-da-pagina.txt"), 400);
        string other = FileAged(Path.Combine("projeto", "texto-da-pagina.txt"), 400); // pasta sem nome de GUID
        Age(Path.GetDirectoryName(output)!, 400);

        Assert.Equal((0, 0), ContextCache.Purge(_root, _now));
        foreach (string kept in new[] { note, named, output, nested, other }) Assert.True(File.Exists(kept), kept);
        Assert.True(Directory.Exists(_root));
    }

    [Fact] public void MissingOrBlankRootIsIgnored()
    {
        string missing = Path.Combine(_root, "nao-existe");
        Assert.Equal((0, 0), ContextCache.Purge(missing, _now));
        Assert.False(Directory.Exists(missing));
        Assert.Equal((0, 0), ContextCache.Purge("", _now));
    }

    [Fact] public void TextFileNamesFollowTheSourceKind()
    {
        Assert.Equal("texto-do-documento.txt", ContextCache.TextFileName("word"));
        Assert.Equal("dados-da-planilha.txt", ContextCache.TextFileName("excel"));
        Assert.Equal("texto-da-pagina.txt", ContextCache.TextFileName("web"));
        Assert.Equal("texto-da-janela.txt", ContextCache.TextFileName(null));
        Assert.EndsWith(Path.Combine("WinStatusBar", "cache", "context"), ContextCache.Root);
    }
}
