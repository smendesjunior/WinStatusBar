namespace WinStatusBar.Core.Ai;

/// <summary>Only the chat generation control is evidence of Qira answering.</summary>
public static class QiraActivity
{
    public static bool IsGenerating(string? automationId, string? name, bool visible, bool enabled, bool button)
        => visible && enabled && button && (automationId == "btn_stop_fetch"
            || name is "Stop generating" or "Parar geração" or "Parar de gerar");
}
