namespace WinStatusBar.Clip;

/// <summary>
/// Limite de "espaço em disco" do histórico da área de transferência. Como diz a tela de configurações, o limite é
/// das imagens e das cópias de arquivos: o texto dos itens e os itens fixados não entram na conta. Antes, o total
/// incluía texto e fixados, mas só itens com imagem ou cópia podiam sair: quando o texto (ou os fixados) passava do
/// limite sozinho, cada nova cópia apagava todas as imagens e arquivos não fixados e o total continuava acima.
/// </summary>
public static class ClipStorageLimit
{
    /// <summary>
    /// Itens a remover para a mídia dos não fixados caber em <paramref name="limitBytes"/>.
    /// </summary>
    /// <param name="itemsInEvictionOrder">Itens na ordem em que podem sair (o usado há mais tempo primeiro).</param>
    /// <param name="isPinned">Fixados nunca saem nem contam.</param>
    /// <param name="mediaBytes">Bytes em disco da imagem e das cópias de arquivos do item (sem o texto).</param>
    public static List<T> SelectVictims<T>(IEnumerable<T> itemsInEvictionOrder, Func<T, bool> isPinned, Func<T, long> mediaBytes, long limitBytes)
    {
        var victims = new List<T>();
        var candidates = new List<(T Item, long Bytes)>();
        long used = 0;
        foreach (var item in itemsInEvictionOrder)
        {
            if (isPinned(item)) continue;
            long bytes = Math.Max(0, mediaBytes(item));
            used += bytes;
            candidates.Add((item, bytes));
        }
        foreach (var (item, bytes) in candidates)
        {
            if (used <= limitBytes) break;
            if (bytes == 0) continue; // tirar um item sem mídia não libera nada
            used -= bytes;
            victims.Add(item);
        }
        return victims;
    }
}
