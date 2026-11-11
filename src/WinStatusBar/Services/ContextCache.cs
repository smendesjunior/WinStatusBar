namespace WinStatusBar.Services;

/// <summary>
/// Área privada onde o contexto do chat é gravado (%LOCALAPPDATA%\WinStatusBar\cache\context): a imagem da janela
/// (<c>&lt;guid&gt;.png</c>) e o texto do documento (<c>&lt;guid&gt;\texto-….txt</c>). Nada apagava esses arquivos.
/// A limpeza remove só o que o próprio WinStatusBar gravou ali e já está velho; as pastas <c>&lt;guid&gt;</c> ficam,
/// porque uma conversa pode ter essa pasta como pasta de trabalho e o Claude só retoma a sessão na mesma pasta.
/// </summary>
internal static class ContextCache
{
    /// <summary>Capturas e textos do contexto mais velhos que isto são apagados.</summary>
    public static readonly TimeSpan FileMaxAge = TimeSpan.FromDays(7);
    /// <summary>Uma pasta de contexto só sai quando está vazia e parada há mais tempo que isto.</summary>
    public static readonly TimeSpan EmptyFolderMaxAge = TimeSpan.FromDays(60);

    public static string Root => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WinStatusBar", "cache", "context");

    /// <summary>Nome do .txt com o texto lido do app, conforme a origem.</summary>
    public static string TextFileName(string? kind) => kind switch
    {
        "word" => "texto-do-documento.txt",
        "excel" => "dados-da-planilha.txt",
        "web" => "texto-da-pagina.txt",
        _ => "texto-da-janela.txt",
    };

    private static readonly string[] TextFiles = [TextFileName("word"), TextFileName("excel"), TextFileName("web"), TextFileName(null)];

    /// <summary>
    /// Apaga, dentro de <paramref name="root"/>, as capturas (<c>&lt;guid&gt;.png</c>) e os textos de contexto
    /// (nas pastas <c>&lt;guid&gt;</c>) com mais de 7 dias, e remove uma pasta <c>&lt;guid&gt;</c> apenas quando ela está
    /// vazia e parada há mais de 60 dias. Nunca apaga nem move a própria <paramref name="root"/>, nem arquivos que
    /// não foram gravados pelo WinStatusBar (uma IA pode ter criado algo ali, se era a pasta da conversa).
    /// Qualquer falha é ignorada. Devolve quantos arquivos e pastas saíram.
    /// </summary>
    public static (int Files, int Folders) Purge(string root, DateTime nowUtc)
    {
        int files = 0, folders = 0;
        try
        {
            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root)) return (0, 0);
            DateTime fileCutoff = nowUtc - FileMaxAge, folderCutoff = nowUtc - EmptyFolderMaxAge;

            foreach (string file in Directory.EnumerateFiles(root, "*.png", SearchOption.TopDirectoryOnly).ToList())
                if (IsGuidName(Path.GetFileNameWithoutExtension(file)) && DeleteIfOld(file, fileCutoff)) files++;

            foreach (string dir in Directory.EnumerateDirectories(root, "*", SearchOption.TopDirectoryOnly).ToList())
            {
                try
                {
                    if (!IsGuidName(Path.GetFileName(dir))) continue;
                    if ((File.GetAttributes(dir) & FileAttributes.ReparsePoint) != 0) continue; // nunca segue junções
                    foreach (string name in TextFiles)
                        if (DeleteIfOld(Path.Combine(dir, name), fileCutoff)) files++;
                    if (Directory.EnumerateFileSystemEntries(dir).Any()) continue;
                    if (Directory.GetCreationTimeUtc(dir) >= folderCutoff || Directory.GetLastWriteTimeUtc(dir) >= folderCutoff) continue;
                    Directory.Delete(dir, recursive: false);
                    folders++;
                }
                catch { }
            }
        }
        catch { }
        return (files, folders);
    }

    private static bool DeleteIfOld(string file, DateTime cutoffUtc)
    {
        try
        {
            if (!File.Exists(file) || File.GetLastWriteTimeUtc(file) >= cutoffUtc) return false;
            File.Delete(file);
            return true;
        }
        catch { return false; }
    }

    /// <summary>Nome no formato de <c>Guid.ToString("N")</c>: 32 dígitos hexadecimais.</summary>
    private static bool IsGuidName(string? name)
    {
        if (name is not { Length: 32 }) return false;
        foreach (char c in name) if (!Uri.IsHexDigit(c)) return false;
        return true;
    }
}
