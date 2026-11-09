using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using WinStatusBar.Core.Ai;
internal static class Program
{
 [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool AllowSetForegroundWindow(uint pid);
 [STAThread] static int Main(string[] args)
 {
  if(args.Contains("--fixture")){Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);using var form=new Form{Text="",Width=500,Height=220};var box=new TextBox{Name="SmokeInput",AccessibleName="SmokeInput",Dock=DockStyle.Top}; form.Controls.Add(box);form.Shown+=(_,_)=>AllowSetForegroundWindow(uint.Parse(args.Last())); Application.Run(form);return 0;}
  try{Run(args).GetAwaiter().GetResult();return 0;}catch(Exception e){Console.Error.WriteLine(e.Message);return 1;}
 }
 static async Task Run(string[] args)
 {
  string root=Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,"../../../../../../../"));
  string broker=args.FirstOrDefault(a=>a.EndsWith(".exe"))??Path.Combine(root,"src/WinStatusBar.Automation/bin/x64/Release/net10.0-windows10.0.19041.0/win-x64/WinStatusBar.Automation.exe");

  string channel="WinStatusBar.Desktop."+Guid.NewGuid().ToString("N");
  using var process=Process.Start(new ProcessStartInfo(broker){ArgumentList={channel,Environment.ProcessId.ToString()},CreateNoWindow=true})!;
  using var fixture=(args.Contains("--shell-only")||args.Contains("--agent"))?null:Process.Start(new ProcessStartInfo(Environment.ProcessPath!){ArgumentList={"--fixture",process.Id.ToString()}})!;
  using var pipe=new NamedPipeClientStream(".",channel,PipeDirection.InOut,PipeOptions.Asynchronous);
  using var ct=new CancellationTokenSource(TimeSpan.FromMinutes(3));
  try{
   await pipe.ConnectAsync(ct.Token);using var reader=new StreamReader(pipe,new UTF8Encoding(false),false,4096,true);using var writer=new StreamWriter(pipe,new UTF8Encoding(false),4096,true){AutoFlush=true};
   async Task<DesktopActionResult> Call(DesktopActionRequest r){await writer.WriteLineAsync(JsonSerializer.Serialize(r));var result=JsonSerializer.Deserialize<DesktopActionResult>((await reader.ReadLineAsync(ct.Token))!)!;if(!result.Success)throw new Exception(r.Action+": "+result.Text);return result;}
   if(args.Contains("--agent")) {
    int operations=0;bool verified=false;
    async Task<DesktopActionResult> Execute(DesktopActionRequest request,CancellationToken token) {
     if(request.Action is "run" or "launch" or "request_elevation")return new("Neste teste use somente controles da interface.",Success:false);
     operations++; Console.WriteLine("ACTION="+request.Action);
     var response=await Call(request);
     if(request.Action is "observe" or "observe_foreground"){
      var observed=JsonDocument.Parse(response.Text).RootElement;
      verified=observed.GetProperty("controls").EnumerateArray().Any(c=>c.GetProperty("Value").GetString()=="Hello, world!");
     }
     return response;
    }
    var options=AgentPrompt.Options.None with {AllowEdits=true,Desktop=Execute,WorkDir=Path.GetTempPath()};
    var result=await AgentPrompt.RunAsync(AgentPrompt.Target.Codex,"Teste autorizado: traduza 'Olá, mundo!' para inglês e insira SOMENTE a tradução na pesquisa do menu Iniciar, sem pressionar Enter. Use exclusivamente as operações winstatusbar-desktop para controlar a interface. Comece com observe_foreground para obter janela/token reais atuais (não escolha outra janela nem use activate); envie Win+S nessa janela, observe_foreground, prefira uia fill no campo Pesquisar. Confirme o valor por observe antes de concluir. Não use outras ferramentas nem execute comandos.",null,"low",null,options,_=>{},ct.Token);
    Console.WriteLine("AGENT_RESULT="+result.Text);Console.WriteLine("AGENT_ERROR="+result.Error);
    if(result.Error!=null||!verified||operations<3)throw new Exception("FAIL: end-to-end agent did not verify translated query");
    Console.WriteLine("PASS: real Codex -> desktop loop -> Windows Search -> exact translated value");return;
   }
   JsonElement state=default;long window=0;
   for(int i=0;i<30;i++){state=JsonDocument.Parse((await Call(new("list_windows"))).Text).RootElement;foreach(var w in state.GetProperty("windows").EnumerateArray())if(w.GetProperty("process").GetString()=="DesktopControl.Smoke")window=w.GetProperty("id").GetInt64();if((args.Contains("--shell-only")||args.Contains("--agent")))window=state.GetProperty("foreground").GetInt64();if(window!=0)break;await Task.Delay(100);}
   if(window==0)throw new Exception("FAIL: untitled fixture absent from list_windows");
   var observation=JsonDocument.Parse((await Call(new("observe",Window:window))).Text).RootElement;
   if(!(args.Contains("--shell-only")||args.Contains("--agent"))){ await Call(new("type",Window:window,Observation:observation.GetProperty("observation").GetString(),Text:"Teste ação 123"));
   observation=JsonDocument.Parse((await Call(new("observe",Window:window))).Text).RootElement;
   if(!observation.GetProperty("controls").EnumerateArray().Any(c=>c.GetProperty("Value").GetString()=="Teste ação 123"))throw new Exception("FAIL: typed value not verified: "+observation.ToString());
   Console.WriteLine("PASS: untitled window + unicode typing + UIA readback"); }
   if(args.Contains("--shell")||(args.Contains("--shell-only")||args.Contains("--agent"))){
    await Call(new("keys",Window:window,Observation:observation.GetProperty("observation").GetString(),Keys:"Win+S"));
    long shell=0;
    for(int i=0;i<40;i++){state=JsonDocument.Parse((await Call(new("list_windows"))).Text).RootElement;long fg=state.GetProperty("foreground").GetInt64();foreach(var w in state.GetProperty("windows").EnumerateArray())if(w.GetProperty("id").GetInt64()==fg&&w.GetProperty("process").GetString() is "SearchHost" or "SearchApp" or "StartMenuExperienceHost" or "ShellHost")shell=fg;if(shell!=0)break;await Task.Delay(100);}
    if(shell==0)throw new Exception("FAIL: Windows Search not discoverable in foreground: "+state.ToString());
    observation=JsonDocument.Parse((await Call(new("observe",Window:shell))).Text).RootElement;
    var field=observation.GetProperty("controls").EnumerateArray().Single(c=>c.GetProperty("Name").GetString()=="Pesquisar"&&c.GetProperty("Actions").EnumerateArray().Any(a=>a.GetString()=="fill")); await Call(new("uia",Window:shell,Observation:observation.GetProperty("observation").GetString(),ControlId:field.GetProperty("Id").GetInt32(),ControlAction:"fill",Text:"WSB teste tradução verificada"));
    bool verified=false;
    for(int i=0;i<20;i++){observation=JsonDocument.Parse((await Call(new("observe",Window:shell))).Text).RootElement;verified=observation.GetProperty("controls").EnumerateArray().Any(c=>c.GetProperty("Value").GetString()=="WSB teste tradução verificada");if(verified)break;await Task.Delay(100);}
    if(!verified)throw new Exception("FAIL: Windows Search text not verified by UIA");
    Console.WriteLine("PASS: Windows Search + input + exact UIA readback");
    await Call(new("keys",Window:shell,Observation:observation.GetProperty("observation").GetString(),Keys:"Escape"));
   }
  }finally{pipe.Dispose();if(fixture is not null&&!fixture.HasExited){fixture.CloseMainWindow();if(!fixture.WaitForExit(3000))fixture.Kill();}}
 }
}
