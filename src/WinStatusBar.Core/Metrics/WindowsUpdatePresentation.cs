namespace WinStatusBar.Core.Metrics;

public sealed record WindowsUpdateActivity(string Action, int? Percent)
{
    public static WindowsUpdateActivity FromNative(string action, double progress) => new(action, double.IsFinite(progress) ? (int)Math.Round(Math.Clamp(progress,0,1)*100) : null);
    public string Label => (Action switch { "Download" => "Baixando", "Install" => "Instalando", "Commit" => "Concluindo", _ => "Preparando" })
        + (Percent is int p ? $" — {Math.Clamp(p,0,100)}%" : "…");
}
public sealed class WindowsUpdateManualController(Func<WindowsUpdateStatus> read, Action scan)
{
    public WindowsUpdateStatus Open() => read();
    public void Check() => scan();
}
public static class WindowsUpdatePresentation
{
    public static WindowsUpdateStatus FromNative(IReadOnlyList<WindowsUpdateEntry> pending,DateTime? last,bool updateNeedsRestart)
    {
        var state=WindowsUpdateStatus.FromResult(updateNeedsRestart,true,pending.Count,last);
        if(!updateNeedsRestart && pending.Any(x=>x.ErrorCode is not null))state=state with {State=WindowsUpdateState.Failed};
        return state with {Pending=pending};
    }
    public static bool NeedsRestart(WindowsUpdateStatus status) => status.State==WindowsUpdateState.RestartRequired;
    public static string ErrorMessage(int code) => code switch
    {
        unchecked((int)0xC1900401) => "Não encontramos nenhuma atualização publicada para sua edição neste momento. Tentaremos novamente quando a próxima atualização programada for publicada.",
        _ => $"Não foi possível concluir a atualização (0x{code:X8}).",
    };    public static string Heading(WindowsUpdateStatus s) => s.Error is not null && s.State != WindowsUpdateState.RestartRequired ? "Ocorreu um erro" : s.State switch
    {
        WindowsUpdateState.Failed => "Atualizações que falharam",
        WindowsUpdateState.RestartRequired => "Reinicialização necessária",
        WindowsUpdateState.UpToDate => "Você está atualizado",
        WindowsUpdateState.UpdatesAvailable => s.Pending.Any(x=>!x.Downloaded) ? "Atualizações disponíveis para download" : "Atualizações prontas para instalar",
        _ => s.Error is null ? "Verificar se há atualizações" : "Ocorreu um erro",
    };
}


