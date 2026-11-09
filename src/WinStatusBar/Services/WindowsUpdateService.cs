using System.Diagnostics;
using System.Runtime.InteropServices;
using Windows.Management.Update;
using WinStatusBar.Core.Metrics;

namespace WinStatusBar.Services;

/// <summary>Reads the orchestrator without scanning. Only Check/Install request work.</summary>
internal sealed class WindowsUpdateService
{
    public static WindowsUpdateService Instance { get; } = new();
    private readonly WindowsUpdateManager? _manager;
    private readonly object _gate = new();
    private readonly Dictionary<string, WindowsUpdateActivity> _progress = new();
    private WindowsUpdateStatus _last = new(WindowsUpdateState.Unknown);
    private string? _requestError;
    private DateTime? _requested;
    public event Action? Changed;
    public bool IsScanning => _manager?.IsScanning == true;
    public bool IsWorking => _manager?.IsWorking == true;
    public bool IsRequesting { get { lock(_gate) return _requested is not null; } }
    public WindowsUpdateActivity? Activity(string? id)
    {
        lock(_gate) return id is null ? null : _progress.FirstOrDefault(x=>x.Key==id || x.Key.StartsWith(id+":",StringComparison.OrdinalIgnoreCase)).Value;
    }
    private WindowsUpdateService()
    {
        try
        {
            _manager = new("WinStatusBar");
            _manager.ScanningStateChanged += (_,_) => Notify();
            _manager.WorkingStateChanged += (_,_) => Notify();
            _manager.ScanCompleted += (_,_) => Notify();
            _manager.ProgressChanged += (_,args) =>
            {
                var p=args.ActionProgress;
                lock(_gate) _progress[args.Update.UpdateId]=WindowsUpdateActivity.FromNative(p.Action.ToString(),p.Progress);
                Notify();
            };
            _manager.ActionCompleted += (_,args) =>
            {
                lock(_gate) _progress.Remove(args.Update.UpdateId);
                Notify();
            };
            _manager.AttentionRequiredReasonChanged += (_,_) => Notify();
        }
        catch(Exception ex) { App.Log("Windows Update monitor: "+ex.Message); }
    }
    private void Notify() { Changed?.Invoke(); }
    public WindowsUpdateStatus Read()
    {
        if (_manager is null) return WindowsUpdateStatusReader.LastKnown ?? _last;
        try
        {
            bool reboot=false;
            var pending = _manager.GetApplicableUpdates().Select(u=>
            {
                var activity=u.ActionProgress;
                if (activity is not null)
                    lock(_gate) _progress[u.UpdateId]=WindowsUpdateActivity.FromNative(activity.Action.ToString(),activity.Progress);
                // The orchestrator's current action is the next outstanding stage.
                string action=u.CurrentAction.ToString();
                int? errorCode=u.ActionResult is { Succeeded: false } failed && failed.ExtendedError is Exception error ? error.HResult : null;
                reboot |= u.AttentionRequiredInfo?.Reason.ToString() is "ReadyToReboot" or "ExternalReboot"; // mesmos rótulos de "Reinicialização pendente"
                string status=Attention(u.AttentionRequiredInfo?.Reason.ToString()) ?? action;
                var pair=u.UpdateId.Split(':');
                int revision=pair.Length>1 && int.TryParse(pair[^1],out int rev) ? rev : 0;
                return new WindowsUpdateEntry(u.Title,status)
                {UpdateId=pair[0],Revision=revision,ErrorCode=errorCode,Downloaded=action is "Install" or "Commit"};
            }).ToArray();
            var date=_manager.LastSuccessfulScanTimestamp;
            DateTime? last=date is DateTimeOffset dt && dt.Year>2000 ? dt.LocalDateTime : null;
            lock(_gate)
            {
                if (IsWorking || IsScanning) _requested=null;
                else if(_requested is DateTime requested && DateTime.UtcNow-requested>TimeSpan.FromSeconds(90))
                {_requested=null;_requestError="O Windows ainda não confirmou o início. Tente novamente.";}
                _last=WindowsUpdatePresentation.FromNative(pending,last,reboot) with {Error=_requestError};
                return _last;
            }
        }
        catch(Exception ex){return _last with {Error=$"Não foi possível ler o status (0x{ex.HResult:X8})."};}
    }
    /// <summary>Horário ativo do Windows Update (mesmo valor das Configurações), ou null se não houver.</summary>
    public static (int Start, int End)? ActiveHours()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\WindowsUpdate\UX\Settings");
            return key?.GetValue("ActiveHoursStart") is int start && key.GetValue("ActiveHoursEnd") is int end
                && start is >= 0 and < 24 && end is >= 0 and < 24 && start != end ? (start, end) : null;
        }
        catch { return null; }
    }

    private static string? Attention(string? reason) => reason switch
    {
        "NeedNetwork" => "Aguardando conexão",
        "NeedNonMeteredNetwork" => "Aguardando rede não limitada",
        "NeedMoreSpace" => "Espaço insuficiente",
        "BatterySaverEnabled" => "Economia de bateria ativada",
        "BlockedByBattery" => "Conecte o carregador",
        "ReadyToReboot" or "ExternalReboot" => "Reinicialização pendente",
        "NeedUserInteractionForEula" => "Aceitação de termos pendente",
        "NeedUserAgreementForMeteredNetwork" => "Autorize o download na conexão limitada",
        "CompatibilityError" => "Problema de compatibilidade",
        "BlockedByAdminApproval" => "Aguardando aprovação da organização",
        "BlockedByActiveHours" => "Aguardando fim do horário ativo",
        "ScheduledForMaintenance" or "PolicyScheduledInstallTime" => "Instalação agendada",
        "OtherUpdate" or "Exclusivity" => "Aguardando outra atualização",
        "BlockedByFailure" or "BlockedByPostRebootFailure" => "Falha na atualização",
        "NeedUserInteraction" or "NeedUserAgreementForPolicy" => "Sua atenção é necessária",
        _ => null,
    };
    public void Check()
    {
        if(_manager is null) { _=CheckLegacyAsync();return; }
        lock(_gate){_requestError=null;_requested=DateTime.UtcNow;}
        try{_manager.StartScan(true);}
        catch(Exception ex){Fail(ex);}
        Notify();
    }
    private async Task CheckLegacyAsync()
    {
        lock(_gate){_requested=DateTime.UtcNow;}
        _last=await WindowsUpdateStatusReader.ReadAsync(true);
        lock(_gate){_requested=null;}
        Notify();
    }
    public async Task InstallAsync()
    {
        lock(_gate){_requestError=null;_requested=DateTime.UtcNow;}
        Notify();
        try
        {
            // Current Windows builds removed StartDownload/StartInstall (E_INVALIDARG).
            // InteractiveScan is the native user-initiated processing request. It affects
            // all eligible updates; the UI must never promise a selected subset.
            using var process=Process.Start(new ProcessStartInfo(Path.Combine(Environment.SystemDirectory,"UsoClient.exe"))
            {Arguments="StartInteractiveScan",UseShellExecute=false,CreateNoWindow=true})
                ?? throw new InvalidOperationException("Não foi possível enviar a solicitação.");
            await process.WaitForExitAsync();
            if(process.ExitCode!=0)throw new COMException("Solicitação recusada pelo Windows.",process.ExitCode);
        }
        catch(Exception ex){Fail(ex);}
        Notify();
    }
    private void Fail(Exception ex)
    {
        lock(_gate){_requested=null;_requestError=$"O Windows não iniciou a operação (0x{ex.HResult:X8}).";}
        App.Log("Windows Update: "+ex);
    }
}



