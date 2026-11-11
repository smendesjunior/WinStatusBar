using WinStatusBar.Core;

namespace WinStatusBar.Tests;

/// <summary>
/// Troca o tradutor do Core por um dicionário só no teste que abriu o escopo (os outros testes rodam em paralelo e
/// continuam com o tradutor anterior) e devolve o anterior ao sair.
/// </summary>
internal sealed class TranslationScope : IDisposable
{
    private static readonly object Gate = new();
    private static readonly AsyncLocal<IReadOnlyDictionary<string, string>?> Active = new();
    private readonly Func<string, string> _previous;

    private TranslationScope(IReadOnlyDictionary<string, string> map)
    {
        Monitor.Enter(Gate);
        var previous = _previous = LocalizedText.Translate;
        Active.Value = map;
        LocalizedText.Translate = text => Active.Value is { } active ? active.GetValueOrDefault(text, text) : previous(text);
    }

    /// <summary>Use em teste síncrono: o escopo é devolvido na mesma thread que o abriu.</summary>
    public static TranslationScope With(IReadOnlyDictionary<string, string> map) => new(map);

    public void Dispose()
    {
        LocalizedText.Translate = _previous;
        Active.Value = null;
        Monitor.Exit(Gate);
    }
}
