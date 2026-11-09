namespace WinStatusBar.Core.Ai;

/// <summary>
/// Navegador interno (WebView2) das páginas escondidas da barra e das IAs no Codex (Gemini, Copilot, cotas e créditos).
/// Fora da tela, o WebView2 trata a página como "em segundo plano" e desacelera timers e a pintura; o Gemini escreve a
/// resposta com uma animação, que quase para assim (no modo Pro, um "ok" levava 2 minutos). Estes argumentos desligam
/// essa desaceleração. Precisam ser os mesmos em todos que abrem os perfis GoogleWeb e CopilotWeb (barra e componente):
/// o WebView2 recusa abrir o mesmo perfil com opções diferentes (ERROR_INVALID_STATE).
/// </summary>
public static class HiddenWebPage
{
    public const string BrowserArguments =
        "--disable-features=CalculateNativeWinOcclusion --disable-background-timer-throttling --disable-renderer-backgrounding --disable-backgrounding-occluded-windows";
}
