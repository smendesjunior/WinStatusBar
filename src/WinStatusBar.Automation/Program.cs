using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using WinStatusBar.Core.Ai;
namespace WinStatusBar.Automation;
internal static class Program
{
    [DllImport("kernel32.dll", SetLastError=true)] private static extern bool GetNamedPipeClientProcessId(Microsoft.Win32.SafeHandles.SafePipeHandle pipe, out uint processId);
    [STAThread]
    private static void Main(string[] args)
    {
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        RunAsync(args).GetAwaiter().GetResult();
    }
    private static async Task RunAsync(string[] args)
    {
        // Ctrl+C seguro num codex.exe da continuação pela API: a barra nunca mexe no próprio console.
        if (args.Length > 0 && args[0] == WinStatusBar.Core.Ai.Codex.ApiResume.ConsoleInterrupt.Command)
        {
            Environment.ExitCode = !WinStatusBar.Core.Ai.Codex.ApiResume.ConsoleInterrupt.TryParse(args, out int pid, out var createdAt, out string image) ? 2
                : !WinStatusBar.Core.Ai.Codex.ApiResume.DetachedProcess.IsSame(pid, createdAt, image) ? 3
                : WinStatusBar.Core.Ai.Codex.ApiResume.ConsoleInterrupt.ExitCode(WinStatusBar.Core.Ai.Codex.ApiResume.ConsoleInterrupt.Send(pid));
            return;
        }
        if (args.Length != 2 || !args[0].StartsWith("WinStatusBar.Desktop.",StringComparison.Ordinal) || !int.TryParse(args[1],out int parent)) return;
        // Sessão de controle de até 4 h (antes 30 min): tarefas longas não perdem o auxiliar no meio.
        using var lifetime=new CancellationTokenSource(TimeSpan.FromHours(4));
        using var server=new NamedPipeServerStream(args[0],PipeDirection.InOut,1,PipeTransmissionMode.Byte,PipeOptions.Asynchronous|PipeOptions.CurrentUserOnly);
        try
        {
            using var connect=CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);connect.CancelAfter(TimeSpan.FromSeconds(60));
            await server.WaitForConnectionAsync(connect.Token);
            if(!GetNamedPipeClientProcessId(server.SafePipeHandle,out uint client)||client!=parent) return;
            using var reader=new StreamReader(server,new UTF8Encoding(false),false,4096,true);
            using var writer=new StreamWriter(server,new UTF8Encoding(false),4096,true){AutoFlush=true};
            var desktop=new DesktopRuntime();
            var next=reader.ReadLineAsync(lifetime.Token).AsTask();
            while(await next is { } line)
            {
                DesktopActionRequest? request=null;
                try{request=JsonSerializer.Deserialize<DesktopActionRequest>(line);}catch{}
                int seconds=request?.EffectiveTimeoutSeconds??DesktopActionRequest.DefaultTimeoutSeconds;
                using var step=CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);step.CancelAfter(TimeSpan.FromSeconds(seconds));
                next=reader.ReadLineAsync(lifetime.Token).AsTask();
                var work=Task.Run(async()=>
                {
                    try { return await desktop.ExecuteAsync(request??throw new InvalidOperationException("Pedido vazio ou inválido"),step.Token); }
                    catch(Exception ex) when(ex is UnauthorizedAccessException or System.Security.SecurityException
                        or System.ComponentModel.Win32Exception { NativeErrorCode: 5 })
                    {
                        // Negado por privilégio, antes de qualquer efeito: a barra pode repetir no auxiliar elevado (UAC).
                        return new DesktopActionResult(ex.Message+" "+DesktopStatus.NeedsAdmin,Success:false,Status:DesktopStatus.Blocked);
                    }
                    catch(Exception ex){return new DesktopActionResult(ex.Message,Success:false);}
                },step.Token);
                // A cancellation token cannot interrupt a synchronous UIA/COM call. Race the actual
                // work against both the pipe and a deadline, then exit this disposable broker even
                // if the worker remains blocked. Thread-pool workers do not keep Main alive.
                var deadline=Task.Delay(Timeout.InfiniteTimeSpan,step.Token);
                Task winner=await Task.WhenAny(work,next,deadline);
                if(winner!=work)
                {
                    try{await step.CancelAsync().WaitAsync(TimeSpan.FromSeconds(1));}catch{}
                    try{await work.WaitAsync(TimeSpan.FromSeconds(1));}catch{}
                    if(winner==deadline && !lifetime.IsCancellationRequested && !next.IsCompleted)
                    {
                        using var replyTimeout=new CancellationTokenSource(TimeSpan.FromSeconds(1));
                        await writer.WriteLineAsync(JsonSerializer.Serialize(new DesktopActionResult(
                            $"A operação excedeu {seconds} segundos. O auxiliar foi encerrado; o resultado não foi confirmado. Para comandos longos, use timeoutSeconds (até {DesktopActionRequest.MaxTimeoutSeconds}).",Success:false)).AsMemory(),replyTimeout.Token);
                    }
                    break;
                }
                await writer.WriteLineAsync(JsonSerializer.Serialize(await work).AsMemory(),lifetime.Token);
            }
        }
        catch { /* Disconnected, declined UAC or cancelled: no orphan control session. */ }
    }
}
