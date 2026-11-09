using System.Text.RegularExpressions;

namespace WinStatusBar.Core.Integration;

/// <summary>
/// Motivo de uma falha do instalador em português simples, para a etapa 3 do assistente. O instalador é publicado com
/// UseSystemResourceKeys: as mensagens do .NET chegam como chaves ("UnauthorizedAccess_IODenied_Path, …"), então só as
/// mensagens próprias do instalador (em português) são mostradas como vieram.
/// </summary>
public static partial class SetupErrorText
{
    private const int SharingViolation = unchecked((int)0x80070020), LockViolation = unchecked((int)0x80070021);
    private const int DiskFull = unchecked((int)0x80070070), HandleDiskFull = unchecked((int)0x80070027);

    public static string Explain(Exception ex, bool hadInstall, bool filesReplaced)
    {
        string reason = ex switch
        {
            UnauthorizedAccessException => "O Windows negou acesso a um arquivo da instalação. Feche a barra e os programas que possam estar usando a pasta dela e tente de novo.",
            IOException io when io.HResult is SharingViolation or LockViolation => "Um arquivo da barra está em uso por outro programa. Feche a barra e tente de novo.",
            IOException io when io.HResult is DiskFull or HandleDiskFull => "Falta espaço no disco para instalar. Deixe pelo menos 1 GB livre e tente de novo.",
            InvalidOperationException or InvalidDataException when IsOwnMessage(ex.Message) => ex.Message,
            _ => "A instalação falhou por um erro inesperado.",
        };
        if (filesReplaced) return reason + " Os arquivos novos já foram copiados; abra o instalador de novo para concluir.";
        return hadInstall ? reason + " Nada foi alterado na instalação anterior." : reason;
    }

    /// <summary>Mensagem escrita pelo instalador (frase), e não uma chave de recurso do .NET.</summary>
    private static bool IsOwnMessage(string message) => message.Contains(' ') && !ResourceKey().IsMatch(message);

    [GeneratedRegex(@"^[A-Za-z0-9]+_[A-Za-z0-9_]+")] private static partial Regex ResourceKey();
}
