using WinStatusBar.Clip;
using Xunit;

namespace WinStatusBar.Tests;

/// <summary>
/// Limite de espaço do histórico da área de transferência: vale para imagens e cópias de arquivos dos itens não
/// fixados. O texto e os fixados não contam nem fazem as imagens dos outros serem apagadas.
/// </summary>
public class ClipStorageLimitTests
{
    private sealed record Item(string Name, long Media, bool Pinned = false);

    private const long Mb = 1048576L;

    private static List<string> Victims(long limit, params Item[] oldestFirst)
        => ClipStorageLimit.SelectVictims(oldestFirst, i => i.Pinned, i => i.Media, limit).Select(i => i.Name).ToList();

    [Fact]
    public void UnderTheLimit_NothingIsRemoved()
        => Assert.Empty(Victims(10 * Mb, new Item("a", 4 * Mb), new Item("b", 6 * Mb)));

    [Fact]
    public void OverTheLimit_TheOldestMediaItemsGoUntilItFits()
        => Assert.Equal(["a", "b"], Victims(10 * Mb, new Item("a", 4 * Mb), new Item("b", 3 * Mb), new Item("c", 5 * Mb), new Item("d", 5 * Mb)));

    [Fact]
    public void TextOnlyItems_NeverCountAndAreNeverRemoved()
    {
        // Antes: o texto entrava no total; com muito texto, toda imagem não fixada era apagada a cada nova cópia.
        Assert.Empty(Victims(10 * Mb, new Item("texto-1", 0), new Item("imagem", 8 * Mb), new Item("texto-2", 0)));
        // Acima do limite: os de texto (0 de mídia) ficam; sai só o que libera espaço.
        Assert.Equal(["imagem-1"], Victims(10 * Mb, new Item("texto-1", 0), new Item("imagem-1", 6 * Mb), new Item("texto-2", 0), new Item("imagem-2", 6 * Mb)));
    }

    [Fact]
    public void PinnedItems_NeverCountAndAreNeverRemoved()
    {
        // Antes: os fixados entravam no total; acima do limite só por causa deles, todo o resto era apagado.
        Assert.Empty(Victims(10 * Mb, new Item("fixado", 50 * Mb, Pinned: true), new Item("imagem", 8 * Mb)));
        Assert.Equal(["a"], Victims(10 * Mb, new Item("fixado", 50 * Mb, Pinned: true), new Item("a", 6 * Mb), new Item("b", 6 * Mb)));
    }

    [Fact]
    public void EvictionOrderIsTheOrderGiven_AndStopsAsSoonAsItFits()
        => Assert.Equal(["grande"], Victims(10 * Mb, new Item("grande", 9 * Mb), new Item("pequena-1", Mb), new Item("pequena-2", Mb)));

    [Fact]
    public void NegativeSizesAreTreatedAsZero_AndAnEmptyHistoryIsFine()
    {
        Assert.Empty(Victims(Mb, new Item("estranho", -5 * Mb)));
        Assert.Empty(Victims(Mb));
    }
}
