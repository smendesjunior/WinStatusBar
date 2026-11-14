namespace WinStatusBar.Core.Ai;

/// <summary>
/// Caminhos das pastas do usuário como o Windows as usa de fato. Com o OneDrive (ou a pessoa) movendo a Área de
/// Trabalho, Documentos e Imagens, "C:\Users\x\Desktop" não é a Área de Trabalho que aparece na tela: um arquivo
/// gravado lá some da vista (caso real de 14/11/2026: a IA pediu "…\Desktop\Novo Documento de Texto.txt" e a Área de
/// Trabalho era "…\OneDrive\Desktop"). Este ajuste troca o começo do caminho pela pasta real.
/// </summary>
public static class KnownFolderPath
{
    private static readonly (string[] Names, Environment.SpecialFolder Folder)[] Folders =
    [
        (["Desktop", "Área de Trabalho", "Area de Trabalho"], Environment.SpecialFolder.DesktopDirectory),
        (["Documents", "Documentos", "My Documents", "Meus Documentos"], Environment.SpecialFolder.MyDocuments),
        (["Pictures", "Imagens", "My Pictures"], Environment.SpecialFolder.MyPictures),
        (["Music", "Músicas", "Musicas"], Environment.SpecialFolder.MyMusic),
        (["Videos", "Vídeos"], Environment.SpecialFolder.MyVideos),
    ];

    /// <summary>Expande %VARIÁVEIS% e leva "&lt;perfil&gt;\Desktop\…" (e afins) para a pasta real quando ela está em outro lugar.</summary>
    public static string Resolve(string path) => Resolve(path, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), f => Environment.GetFolderPath(f));

    /// <summary>Testável: perfil e pastas reais informados.</summary>
    public static string Resolve(string path, string profile, Func<Environment.SpecialFolder, string> real)
    {
        string expanded = Environment.ExpandEnvironmentVariables(path.Trim().Trim('"'));
        if (expanded.StartsWith('~')) expanded = profile + expanded[1..];
        string prefix = profile.TrimEnd('\\', '/') + "\\";
        string normalized = expanded.Replace('/', '\\');
        if (!normalized.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return expanded;
        string rest = normalized[prefix.Length..];
        int cut = rest.IndexOf('\\');
        string first = cut < 0 ? rest : rest[..cut];
        string tail = cut < 0 ? "" : rest[cut..];
        foreach (var (names, folder) in Folders)
        {
            if (!names.Any(n => string.Equals(n, first, StringComparison.OrdinalIgnoreCase))) continue;
            string target = real(folder);
            if (string.IsNullOrEmpty(target)) return expanded;
            string asked = prefix + first;
            // Só troca quando a pasta real é outra (redirecionada); no perfil padrão nada muda.
            return string.Equals(Path.TrimEndingDirectorySeparator(target), asked, StringComparison.OrdinalIgnoreCase) ? expanded : target.TrimEnd('\\') + tail;
        }
        return expanded;
    }
}
