namespace WinStatusBar.Core.Ai;

/// <summary>
/// Regras de segurança do executor da barra que não dependem do Windows em execução (o auxiliar as aplica antes de
/// agir): onde existe Lixeira, quais processos nunca são encerrados, pasta dentro dela mesma e qual janela um "target"
/// escolhe. Ficam aqui, e não no auxiliar, para serem testadas.
/// </summary>
public static class DesktopSafety
{
    // ------------------------------------------------------------------ Lixeira
    /// <summary>
    /// O caminho fica num local com Lixeira? Só discos fixos locais têm: em pasta de rede (UNC ou unidade mapeada) e em
    /// unidade removível o Windows apaga em definitivo, sem avisar, mesmo quando se pede a Lixeira.
    /// </summary>
    /// <param name="driveTypeOf">Tipo da unidade pela raiz ("C:\"); só os testes informam.</param>
    public static bool HasRecycleBin(string path, Func<string, DriveType>? driveTypeOf = null)
    {
        try
        {
            string full = Path.GetFullPath(path);
            if (full.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase)) return false;
            if (full.StartsWith(@"\\?\", StringComparison.Ordinal) || full.StartsWith(@"\\.\", StringComparison.Ordinal)) full = full[4..];
            if (full.StartsWith(@"\\", StringComparison.Ordinal)) return false;
            string? root = Path.GetPathRoot(full);
            if (string.IsNullOrEmpty(root)) return false;
            return (driveTypeOf?.Invoke(root) ?? new DriveInfo(root).DriveType) == DriveType.Fixed;
        }
        catch { return false; } // sem saber onde fica, não se promete a Lixeira
    }

    /// <summary>Recusa de apagar/sobrescrever onde não há Lixeira (texto para a IA; nada pede administrador).</summary>
    public static string NoRecycleBinMessage(string path)
        => "Este local não tem Lixeira (pasta de rede ou unidade removível): apagar ou sobrescrever aqui seria definitivo. "
         + "Nada foi apagado nem alterado: " + path + ". Só com a confirmação do usuário para a exclusão definitiva, use powershell "
         + "(Remove-Item, com o caminho em \"arguments\").";

    // ------------------------------------------------------------------ processos que nunca são encerrados
    /// <summary>
    /// Motivo para não encerrar o processo pelo nome, ou null se pode: partes do Lenovo Qira (encerrar uma só trava o
    /// resto) e a própria barra com os auxiliares dela.
    /// </summary>
    public static string? ProtectedProcess(string? name)
    {
        string bare = (name ?? "").Trim();
        if (bare.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) bare = bare[..^4].TrimEnd();
        if (bare.Length == 0) return null;
        if (QiraSharedTurn.ProcessNames.Contains(bare, StringComparer.OrdinalIgnoreCase))
            return $"\"{bare}\" é uma parte do Lenovo Qira: encerrar uma parte sozinha trava o resto do Qira. Nada foi encerrado. "
                 + "Para reiniciar o Qira inteiro, o usuário sai dele pela bandeja e abre de novo, ou usa a correção de problemas da barra.";
        if (bare.Equals("WinStatusBar", StringComparison.OrdinalIgnoreCase) || bare.StartsWith("WinStatusBar.", StringComparison.OrdinalIgnoreCase))
            return $"\"{bare}\" é da própria WinStatusBar (a barra e os auxiliares dela). Nada foi encerrado.";
        return null;
    }

    // ------------------------------------------------------------------ pastas
    /// <summary>O caminho fica DENTRO da pasta (em qualquer nível)? O mesmo caminho não conta.</summary>
    public static bool IsInside(string candidate, string folder)
    {
        string inner = Path.TrimEndingDirectorySeparator(Path.GetFullPath(candidate));
        string outer = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
        // A raiz de uma unidade ("C:\") mantém a barra final.
        string prefix = Path.EndsInDirectorySeparator(outer) ? outer : outer + Path.DirectorySeparatorChar;
        return inner.Length > prefix.Length && inner.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    // ------------------------------------------------------------------ janela indicada em "target"
    /// <summary>
    /// Índice da janela que o "target" indica, na ordem recebida (a da frente primeiro), ou -1. Do mais certo para o mais
    /// vago: processo ou título igual → processo que contém → título com a palavra inteira → título que contém. Assim
    /// "Word" é o Word (WINWORD), e não uma aba do navegador com "password" no título.
    /// </summary>
    public static int PickWindow(IReadOnlyList<(string Process, string Title)> windows, string target)
    {
        if (string.IsNullOrEmpty(target)) return -1;
        int First(Func<(string Process, string Title), bool> match)
        {
            for (int i = 0; i < windows.Count; i++) if (match(windows[i])) return i;
            return -1;
        }
        int found = First(w => w.Process.Equals(target, StringComparison.OrdinalIgnoreCase) || w.Title.Equals(target, StringComparison.OrdinalIgnoreCase));
        if (found < 0) found = First(w => w.Process.Contains(target, StringComparison.OrdinalIgnoreCase));
        if (found < 0) found = First(w => ContainsWholeWord(w.Title, target));
        if (found < 0) found = First(w => w.Title.Contains(target, StringComparison.OrdinalIgnoreCase));
        return found;
    }

    /// <summary>O texto aparece sem letra ou número colado antes ou depois ("Word" em "Doc - Word", não em "password").</summary>
    private static bool ContainsWholeWord(string text, string word)
    {
        for (int at = text.IndexOf(word, StringComparison.OrdinalIgnoreCase); at >= 0; at = text.IndexOf(word, at + 1, StringComparison.OrdinalIgnoreCase))
        {
            bool before = at == 0 || !char.IsLetterOrDigit(text[at - 1]);
            int end = at + word.Length;
            bool after = end >= text.Length || !char.IsLetterOrDigit(text[end]);
            if (before && after) return true;
        }
        return false;
    }
}
