using WinStatusBar.Core.Integration;
using Xunit;

namespace WinStatusBar.Tests;

/// <summary>Etapa 3 do assistente de instalação: o motivo da falha em português simples (o instalador usa
/// UseSystemResourceKeys, então as mensagens do .NET chegam como chaves de recurso, não como texto).</summary>
public class SetupErrorTextTests
{
    [Fact]
    public void ExplainsAccessDenied() =>
        Assert.Equal("O Windows negou acesso a um arquivo da instalação. Feche a barra e os programas que possam estar usando a pasta dela e tente de novo.",
            SetupErrorText.Explain(new UnauthorizedAccessException("UnauthorizedAccess_IODenied_Path, C:\\x"), hadInstall: false, filesReplaced: false));

    [Fact]
    public void ExplainsFileInUse() =>
        Assert.Equal("Um arquivo da barra está em uso por outro programa. Feche a barra e tente de novo.",
            SetupErrorText.Explain(new IOException("IO_SharingViolation_File", unchecked((int)0x80070020)), hadInstall: false, filesReplaced: false));

    [Fact]
    public void ExplainsDiskFull() =>
        Assert.Equal("Falta espaço no disco para instalar. Deixe pelo menos 1 GB livre e tente de novo.",
            SetupErrorText.Explain(new IOException("IO_DiskFull", unchecked((int)0x80070070)), hadInstall: false, filesReplaced: false));

    [Fact]
    public void KeepsOwnPortugueseMessage() =>
        Assert.Equal("Pacote do aplicativo ausente no instalador.",
            SetupErrorText.Explain(new InvalidOperationException("Pacote do aplicativo ausente no instalador."), hadInstall: false, filesReplaced: false));

    [Fact]
    public void HidesFrameworkResourceKeys() =>
        Assert.Equal("A instalação falhou por um erro inesperado.",
            SetupErrorText.Explain(new InvalidOperationException("InvalidOperation_EnumFailedVersion"), hadInstall: false, filesReplaced: false));

    [Fact]
    public void SaysNothingChangedOnlyBeforeTheFilesWereReplaced()
    {
        var error = new InvalidOperationException("Pacote do aplicativo ausente no instalador.");
        Assert.Equal("Pacote do aplicativo ausente no instalador. Nada foi alterado na instalação anterior.",
            SetupErrorText.Explain(error, hadInstall: true, filesReplaced: false));
        Assert.Equal("Pacote do aplicativo ausente no instalador. Os arquivos novos já foram copiados; abra o instalador de novo para concluir.",
            SetupErrorText.Explain(error, hadInstall: true, filesReplaced: true));
    }
}
