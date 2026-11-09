using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Runtime.InteropServices;
using WinStatusBar.Core.Ai;
namespace WinStatusBar.Services;
/// <summary>One local broker per user request; elevation is requested through Windows UAC, never bypassed.</summary>
internal sealed class DesktopControlClient : IDisposable
{
    [DllImport("user32.dll")] private static extern bool AllowSetForegroundWindow(uint processId);
    private NamedPipeClientStream? _pipe;
    private StreamReader? _reader;
    private StreamWriter? _writer;
    private Process? _process;
    private bool _elevated, _declined;
    private DesktopControlClient? _normal;
    private readonly Func<bool> _enabled;
    private readonly Func<CancellationToken,Task<bool>> _approveElevation;
    public DesktopControlClient(Func<bool> enabled,Func<CancellationToken,Task<bool>> approveElevation){_enabled=enabled;_approveElevation=approveElevation;}
    public async Task<DesktopActionResult> ExecuteAsync(DesktopActionRequest request,CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if(!_enabled())return new("Controle do computador desativado pelo usuário.",Success:false);
        // Rede (unidades mapeadas e credenciais) pertence à sessão do usuário: nunca vai para o auxiliar elevado.
        bool networkSession = request.Action=="system" && request.Op?.StartsWith("network_",StringComparison.Ordinal)==true;
        if(networkSession && request.Elevated==true)request=request with { Elevated=false };
        bool wantsAdmin = request.Action=="request_elevation" || DesktopActionCatalog.NeedsElevation(request);
        // Depois do UAC, só o que exige administrador roda elevado; o resto (Office, área de transferência, arquivos do
        // usuário, rede, janelas comuns) continua no auxiliar normal, na integridade do usuário.
        if(_elevated && !wantsAdmin)
        {
            _normal ??= new DesktopControlClient(_enabled,_approveElevation);
            return await _normal.ExecuteAsync(request,ct);
        }
        if(wantsAdmin && !_elevated && request.Action!="request_elevation")
        {
            var elevated=await ExecuteAsync(new DesktopActionRequest("request_elevation"),ct);
            if(!elevated.Success)return elevated with { Text=elevated.Text+" Nada foi executado como administrador ("+DesktopAgentLoop.Describe(request)+")." };
        }
        if(!_elevated && !wantsAdmin && !_declined && DesktopActionCatalog.RetryElevatedOnDenied(request))
        {
            // Operação atômica negada pelo Windows antes de qualquer efeito: repete uma vez no auxiliar elevado (UAC nativo).
            var first=await SendAsync(request,ct);
            if(first.Status!=DesktopStatus.Blocked||!first.Text.Contains(DesktopStatus.NeedsAdmin,StringComparison.Ordinal))return first;
            var admin=await ExecuteAsync(new DesktopActionRequest("request_elevation"),ct);
            if(!admin.Success)return first with { Text=first.Text+" "+admin.Text };
            var retried=await ExecuteAsync(request with { Elevated=true },ct);
            return retried with { Text="(repetida como administrador após o UAC) "+retried.Text };
        }
        if(request.Action=="request_elevation")
        {
            if(_elevated)return new("O auxiliar já está elevado. As operações com \"elevated\":true rodam como administrador; as demais seguem sem elevação.");
            if(_declined)return new("Elevação recusada nesta execução.",Success:false,Status:DesktopStatus.Blocked);
            // A autorização é o modo Editar mais o escopo da tarefa; o prompt nativo do UAC é a aprovação (sem diálogo extra no chat).
            Close();
            try{await ConnectAsync(true,ct);_elevated=true;return new("Auxiliar de administrador autorizado pelo UAC. Operações com \"elevated\":true rodam elevadas; as demais seguem sem elevação.");}
            catch(System.ComponentModel.Win32Exception ex)when(ex.NativeErrorCode==1223){_declined=true;return new("UAC cancelado pelo usuário. Não tente contornar.",Success:false,Status:DesktopStatus.Blocked);}
        }
        return await SendAsync(request,ct);
    }
    private async Task<DesktopActionResult> SendAsync(DesktopActionRequest request,CancellationToken ct)
    {
        for(int attempt=0;;attempt++)
        {
            bool written=false,wasElevated=_elevated;
            try
            {
                if(_pipe is null)await ConnectAsync(false,ct);
                if(!_enabled())return new("Controle desativado antes da execução.",Success:false,Status:DesktopStatus.Blocked);
                if(_process is not null)AllowSetForegroundWindow((uint)_process.Id);
                using var deadline=CancellationTokenSource.CreateLinkedTokenSource(ct);
                deadline.CancelAfter(TimeSpan.FromSeconds(request.EffectiveTimeoutSeconds + 10)); // Broker deadline + bounded shutdown/reply.
                await _writer!.WriteLineAsync(JsonSerializer.Serialize(request).AsMemory(),deadline.Token);await _writer.FlushAsync(deadline.Token);
                written=true;
                string? line=await _reader!.ReadLineAsync(deadline.Token);
                if(line is null){Close();return new("O auxiliar encerrou a conexão. O resultado da ação não foi confirmado.",Success:false);}
                var result=JsonSerializer.Deserialize<DesktopActionResult>(line)??new("Resposta inválida do auxiliar.",Success:false);
                // Depois de estourar o prazo o auxiliar se encerra: fecha já, para o próximo pedido abrir outro sem erro de canal.
                if(result.Text.StartsWith("A operação excedeu",StringComparison.Ordinal))Close();
                return result;
            }
            catch(IOException) when(!written&&attempt==0&&!wasElevated)
            {
                // Canal quebrado ANTES de entregar o pedido (auxiliar encerrado): nada foi executado; reconecta e reenvia uma vez.
                Close();
            }
            catch(OperationCanceledException) when(!ct.IsCancellationRequested)
            {Close();return new("O auxiliar não respondeu no prazo e a conexão foi encerrada. O resultado da ação não foi confirmado.",Success:false);}
            catch{Close();throw;}
        }
    }
    private async Task ConnectAsync(bool elevated,CancellationToken ct)
    {
        string executable=Path.Combine(AppContext.BaseDirectory,"Automation","WinStatusBar.Automation.exe");
        if(!File.Exists(executable))throw new FileNotFoundException("Auxiliar de controle não instalado. Atualize o WinStatusBar pelo instalador EXE.",executable);
        string channel="WinStatusBar.Desktop."+Guid.NewGuid().ToString("N");
        var psi=new ProcessStartInfo(executable){UseShellExecute=elevated,CreateNoWindow=!elevated,WindowStyle=ProcessWindowStyle.Hidden};
        if(elevated)psi.Verb="runas";
        psi.ArgumentList.Add(channel);psi.ArgumentList.Add(Environment.ProcessId.ToString());
        _process=Process.Start(psi)??throw new IOException("Não foi possível abrir o auxiliar.");
        AllowSetForegroundWindow((uint)_process.Id);
        _pipe=new NamedPipeClientStream(".",channel,PipeDirection.InOut,PipeOptions.Asynchronous|PipeOptions.CurrentUserOnly);
        try{await _pipe.ConnectAsync(60000,ct);}
        catch{Close();throw;}
        _reader=new StreamReader(_pipe,new UTF8Encoding(false),false,4096,true);
        _writer=new StreamWriter(_pipe,new UTF8Encoding(false),4096,true){AutoFlush=true};
    }
    private void Close()
    {
        // Closing the pipe cancels a running action inside the elevated broker as well.
        try{_pipe?.Dispose();}catch{} _pipe=null;try{_reader?.Dispose();}catch{} _reader=null;try{_writer?.Dispose();}catch{} _writer=null;
        var process=_process;_process=null;_elevated=false;
        if(process is not null) _=ReapAsync(process);
    }
    private static async Task ReapAsync(Process process)
    {
        using(process)
        {
            try{await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(4));}
            catch
            {
                // Best effort for a crashed/unresponsive unelevated helper. Elevated helpers own
                // their deadline and exit themselves; no UAC bypass or privileged termination.
                try{if(!process.HasExited)process.Kill();}catch{}
            }
        }
    }
    public void Dispose(){_normal?.Dispose();_normal=null;Close();}
}
