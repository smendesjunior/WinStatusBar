namespace WinStatusBar.Core.Ai;

/// <summary>Camada de execução: direta (sem janela), interface (teclado/mouse/controles) ou mista (abre ou foca janelas).</summary>
public enum DesktopLayer { Background, Interface, Window }

/// <summary>
/// Contrato de uma ação do executor, igual para todas as IAs: versão, camada preferida, se repete sem duplicar efeito,
/// como se recupera, quando pede administrador e o prazo. Alimenta a validação, os prazos, as métricas e a documentação.
/// </summary>
public sealed record DesktopActionSpec(string Name, int Version, DesktopLayer Layer, bool LongRunning, string Repeatable,
    string Elevation, string Recovery, string Verification);

public static class DesktopActionCatalog
{
    public static readonly IReadOnlyList<DesktopActionSpec> All =
    [
        // Texto e área de transferência
        new("paste", 2, DesktopLayer.Interface, false, "não (duplica o texto)", "só em janela de administrador", "undo", "lê o valor do campo pela acessibilidade quando disponível"),
        new("type", 1, DesktopLayer.Interface, false, "não", "só em janela de administrador", "undo", "foco conferido a cada caractere"),
        new("keys", 1, DesktopLayer.Interface, false, "depende do atalho", "só em janela de administrador", "depende do atalho", "foco conferido; efeito pela observação"),
        new("clipboard_get", 1, DesktopLayer.Background, false, "sim", "nunca", "—", "formatos e conteúdo lidos"),
        new("clipboard_set", 2, DesktopLayer.Background, false, "sim", "nunca", "conteúdo anterior é devolvido pelo paste", "relê o formato gravado"),
        new("copy_selection", 1, DesktopLayer.Interface, false, "sim", "nunca", "—", "texto copiado devolvido"),
        new("select_all", 1, DesktopLayer.Interface, false, "sim", "nunca", "—", "observação"),
        new("undo", 1, DesktopLayer.Interface, false, "não", "nunca", "keys Ctrl+Y", "observação"),
        // Janelas e aplicativos
        new("list_windows", 1, DesktopLayer.Background, false, "sim", "nunca", "—", "lista real"),
        new("focus", 1, DesktopLayer.Window, false, "sim", "nunca", "—", "janela em primeiro plano conferida"),
        new("activate", 1, DesktopLayer.Window, false, "sim", "nunca", "—", "janela em primeiro plano conferida"),
        new("window", 1, DesktopLayer.Window, false, "sim (exceto close)", "nunca", "restore", "estado da janela"),
        new("move_window", 1, DesktopLayer.Window, false, "sim", "nunca", "move de volta", "geometria"),
        new("open", 2, DesktopLayer.Window, false, "não (abre outra instância)", "nunca", "window close", "espera a janela nova"),
        new("launch", 1, DesktopLayer.Window, false, "não", "nunca", "window close", "espera a janela nova"),
        new("start_search", 1, DesktopLayer.Interface, false, "sim", "nunca", "keys Escape", "pesquisa em primeiro plano"),
        new("settings", 1, DesktopLayer.Window, false, "sim", "nunca", "window close", "janela de Configurações"),
        new("process", 1, DesktopLayer.Background, false, "sim", "processos de outro usuário", "open de novo", "lista/contagem real"),
        new("wait", 1, DesktopLayer.Background, false, "sim", "nunca", "—", "—"),
        new("wait_window", 1, DesktopLayer.Background, false, "sim", "nunca", "—", "janela encontrada"),
        // Observação e controles
        new("observe", 2, DesktopLayer.Background, false, "sim", "nunca", "—", "controles, documento do Office e diálogo"),
        new("observe_foreground", 2, DesktopLayer.Background, false, "sim", "nunca", "—", "controles, documento do Office e diálogo"),
        new("context", 1, DesktopLayer.Background, false, "sim", "nunca", "—", "janela ativa, documento do Office, diálogo e outras janelas"),
        new("find", 1, DesktopLayer.Background, false, "sim", "nunca", "—", "controles listados"),
        new("uia", 1, DesktopLayer.Interface, false, "fill/select sim; click/toggle não", "só em janela de administrador", "depende do controle", "valor relido pela acessibilidade"),
        new("click", 1, DesktopLayer.Interface, false, "não", "só em janela de administrador", "depende", "ponto conferido contra a observação"),
        new("drag", 1, DesktopLayer.Interface, false, "não", "só em janela de administrador", "depende", "ponto conferido"),
        new("scroll", 1, DesktopLayer.Interface, false, "sim", "nunca", "scroll inverso", "—"),
        // Arquivos, Office e sistema (segundo plano)
        new("file", 2, DesktopLayer.Background, true, "leitura sim; escrita não sobrescreve sem overwrite", "pastas protegidas", "exclusão vai para a Lixeira; sobrescrita também", "existência/tamanho conferidos"),
        new("zip", 1, DesktopLayer.Background, true, "não sobrescreve", "pastas protegidas", "file delete", "arquivo gerado conferido"),
        new("office", 2, DesktopLayer.Background, true, "read/state/format sim; insert/append não", "nunca", "undo no app ou não salvar", "relê formatação, imagens e arquivos exportados"),
        new("registry", 1, DesktopLayer.Background, false, "sim", "HKLM/HKCR/HKU (pede UAC)", "gravar o valor anterior (devolvido no resultado)", "relê o valor"),
        new("service", 1, DesktopLayer.Background, false, "sim", "start/stop/restart (pede UAC)", "op inversa", "status relido"),
        new("install", 1, DesktopLayer.Background, true, "sim", "o instalador pode pedir UAC", "uninstall", "código do winget"),
        new("system", 2, DesktopLayer.Background, true, "sim", "nunca", "—", "valores lidos"),
        new("print", 1, DesktopLayer.Background, false, "não (imprime de novo)", "nunca", "fila de impressão", "trabalho enviado"),
        new("download", 1, DesktopLayer.Background, true, "não sobrescreve", "nunca", "file delete", "tamanho conferido"),
        new("powershell", 2, DesktopLayer.Background, true, "depende do script", "elevated:true (pede UAC)", "depende do script", "código de saída + conferência pedida no script"),
        new("run", 2, DesktopLayer.Background, true, "depende do programa", "elevated:true (pede UAC)", "depende", "código de saída e saída"),
        new("request_elevation", 1, DesktopLayer.Background, false, "sim", "sempre (UAC)", "—", "auxiliar elevado conectado"),
    ];

    private static readonly Dictionary<string, DesktopActionSpec> ByName = All.ToDictionary(a => a.Name, StringComparer.Ordinal);
    public static bool Contains(string action) => ByName.ContainsKey(action);
    public static DesktopActionSpec? Get(string action) => ByName.GetValueOrDefault(action);

    /// <summary>Precisa do auxiliar de administrador antes de tentar (o resto tenta sem e só eleva se o Windows negar).</summary>
    public static bool NeedsElevation(DesktopActionRequest r)
        => r.Elevated == true
        || r.Action == "service" && r.Op is "start" or "stop" or "restart"
        || r.Action == "registry" && r.Op is "write" or "delete" && (r.Path ?? "").Split('\\')[0].ToUpperInvariant() is "HKLM" or "HKEY_LOCAL_MACHINE" or "HKCR" or "HKEY_CLASSES_ROOT" or "HKU" or "HKEY_USERS";

    /// <summary>Falha "acesso negado" sem efeito parcial: pode ser repetida no auxiliar elevado com segurança.</summary>
    public static bool RetryElevatedOnDenied(DesktopActionRequest r)
        => r.Elevated != true && (r.Action is "registry" or "service" || r.Action == "process" && r.Op == "kill"
            || r.Action == "file" && r.Op is "write" or "append" or "move" or "rename" or "delete" or "mkdir" && r.Scope != "verified");
}

/// <summary>Resultados possíveis de uma operação.</summary>
public static class DesktopStatus
{
    public const string Done = "concluída", Partial = "parcial", Failed = "falhou", Blocked = "bloqueada", Cancelled = "cancelada";
    /// <summary>Marca de "o Windows negou por falta de administrador".</summary>
    public const string NeedsAdmin = "[requer administrador]";
}
