using System.Globalization;

namespace WinStatusBar.Core.Ai;

/// <summary>Regra de idioma enviada com cada mensagem às IAs da barra (oculta no histórico visível).</summary>
public static class PromptLanguage
{
    public const string Marker = "\n\n[Idioma da resposta:";

    /// <summary>Responder no idioma da mensagem; se for ambíguo (ex.: "oi"), no idioma do sistema.</summary>
    public static string Hint(CultureInfo? culture = null)
    {
        culture ??= CultureInfo.CurrentUICulture;
        string fallback = culture.TwoLetterISOLanguageName == "pt" ? "português do Brasil" : culture.EnglishName;
        return Marker + " o mesmo idioma da mensagem do usuário; se não for possível identificar (mensagem curta ou ambígua), responda em " + fallback + ".]";
    }
}
