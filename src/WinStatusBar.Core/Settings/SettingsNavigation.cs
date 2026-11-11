namespace WinStatusBar.Core.Settings;

/// <summary>
/// Páginas da janela de Configurações. Em 11/11/2026 as 13 páginas viraram 10: Atalhos entrou em "Barra e aparência",
/// Bateria em "Rede e bateria", Atualizações e Diagnóstico em "Sobre e suporte", e as contas das IAs ganharam a página
/// "IAs e contas". Os nomes das páginas antigas continuam aceitos (menu "…" da barra, bandeja e atalhos internos).
/// </summary>
public static class SettingsNavigation
{
    /// <summary>Itens do menu, na ordem em que aparecem.</summary>
    public static readonly IReadOnlyList<string> Menu =
        ["system", "appearance", "accounts", "ai", "command", "models", "gpu", "connections", "clip"];

    /// <summary>Item fixo no rodapé do menu.</summary>
    public const string Footer = "about";

    /// <summary>Página que mostra o que foi pedido; um nome antigo leva à página que o absorveu e um desconhecido, a Geral.</summary>
    public static string Resolve(string? tag) => tag switch
    {
        "shortcuts" => "appearance",
        "battery" => "connections",
        "updates" or "diagnostics" => Footer,
        Footer => Footer,
        not null when Menu.Contains(tag) => tag,
        _ => "system",
    };
}
