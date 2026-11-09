using System.Net;
using System.Net.Sockets;

namespace WinStatusBar.Core.Integration;

/// <summary>Explica em português simples por que uma verificação de atualização falhou.</summary>
public static class UpdateErrorText
{
    private const string Retry = " A barra tenta de novo sozinha em 15 minutos.";
    private const int DiskFull = unchecked((int)0x80070070), HandleDiskFull = unchecked((int)0x80070027);

    public static string Explain(Exception ex, UpdateSourceKind kind)
    {
        bool local = kind == UpdateSourceKind.Local;
        return ex switch
        {
            InvalidDataException => ex.Message,
            IOException io when io.HResult is DiskFull or HandleDiskFull => "Falta espaço no disco para baixar a atualização (cerca de 300 MB).",
            HttpRequestException { StatusCode: HttpStatusCode.NotFound } => "A versão nova ainda está sendo publicada no GitHub." + Retry,
            HttpRequestException or SocketException => "Sem conexão com o GitHub." + Retry,
            OperationCanceledException or TimeoutException => (local ? "A pasta local de atualização demorou demais para responder." : "O GitHub demorou demais para responder.") + Retry,
            DirectoryNotFoundException or FileNotFoundException or IOException or UnauthorizedAccessException when local =>
                "A pasta local de atualização não está acessível." + Retry,
            // Queda no meio do download: HttpIOException (fim prematuro) ou IOException com SocketException dentro.
            HttpIOException or IOException { InnerException: SocketException } => "A conexão com o GitHub caiu durante o download." + Retry,
            _ => "Não foi possível verificar atualizações: " + ex.Message,
        };
    }
}
