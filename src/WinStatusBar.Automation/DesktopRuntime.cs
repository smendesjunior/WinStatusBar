using System.Diagnostics;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using WinStatusBar.Core.Ai;
using WinStatusBar.Services;
namespace WinStatusBar.Automation;

/// <summary>
/// Executor real das operações no Windows (processo auxiliar, elevado só com UAC). Núcleo: janelas, observação,
/// teclado, mouse e controles acessíveis, com conferência de foco e geometria. Ações prontas ficam em DesktopSkills.
/// Observação é automática: sem "window"/"observation", usa a janela em primeiro plano ou a indicada em "target".
/// </summary>
internal sealed partial class DesktopRuntime
{
    private sealed record Seen(IntPtr Hwnd,uint Process,Rect Bounds,DateTime Time,ContextActions.Snapshot? Controls);
    private sealed record WindowInfo(IntPtr Hwnd,string Title,string ClassName,string Process,Rect Bounds);
    private readonly WinStatusBar.ExecutorExpansion.ExtensionRouter _expansion = new();
    private readonly WinStatusBar.ExecutorExpansion.RecipeDispatcher _recipes = new();
    private readonly Dictionary<string,Seen> _observations=[];
    private readonly Dictionary<long,WindowInfo> _windows=[];
    /// <summary>Último retrato de controles MOSTRADO à IA em cada janela (observe/find): os números "#N" valem só nele.</summary>
    private readonly Dictionary<long,ContextActions.Snapshot> _shown=[];
    private void Remember(ContextActions.Snapshot? snapshot)
    {
        if(snapshot is null)return;
        if(_shown.Count>=16&&!_shown.ContainsKey(snapshot.Window.ToInt64()))_shown.Clear();
        _shown[snapshot.Window.ToInt64()]=snapshot;
    }

    public async Task<DesktopActionResult> ExecuteAsync(DesktopActionRequest r,CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if(r.Action=="run"&&r.Elevated!=true&&GuiExecutable(r.Application) is { } gui)
        {
            var psi=new ProcessStartInfo(gui){UseShellExecute=true};foreach(var arg in r.Arguments??[])psi.ArgumentList.Add(arg);
            if(r.Cwd is { Length: >0 } cwd)psi.WorkingDirectory=cwd;
            var opened=await OpenAndWaitAsync(psi,ct);
            return opened with{Text="Programa com janela: aberto sem aguardar o fechamento (run é para comandos de console; use open). "+opened.Text};
        }
        if(await ExpandedPasteAsync(r,ct) is { } pasted)return pasted;
        if(await _recipes.TryExecuteAsync(r,ExecuteAsync,ct) is { } recipe)return Readable(recipe);
        if(await _expansion.TryExecuteAsync(r,ct) is { } expanded)return Readable(expanded);
        try{if(await SkillAsync(r,ct) is { } skill)return skill;}
        // Recusa por segurança (sem Lixeira no local, pasta dentro dela mesma): nada foi feito; "bloqueada", não "falhou".
        catch(BlockedException refused){return new(refused.Message,Success:false,Status:DesktopStatus.Blocked);}
        if(r.Action=="list_windows")return ListWindowsText();
        if(r.Action=="observe_foreground")
        {
            Refresh();var foreground=GetForegroundWindow();
            if(!_windows.ContainsKey(foreground.ToInt64()))
                foreground=_windows.Values.FirstOrDefault()?.Hwnd??throw new InvalidOperationException("Nenhuma janela de aplicativo aberta.");
            return Observe(foreground,r.Image==true);
        }
        if(r.Action is "launch" or "run")
        {
            if(string.IsNullOrWhiteSpace(r.Application))throw new InvalidOperationException("Informe o aplicativo ou executável.");
            if(r.Action=="launch")
            {
                var psi=new ProcessStartInfo(r.Application){UseShellExecute=true};foreach(var arg in r.Arguments??[])psi.ArgumentList.Add(arg);
                return await OpenAndWaitAsync(psi,ct);
            }
            return await RunProcessAsync(r.Application,r.Arguments??[],ct);
        }
        IntPtr hwnd=ResolveWindow(r);
        if(r.Action=="observe")return Observe(hwnd,r.Image==true);
        if(r.Action=="uia")
        {
            if(r.ControlAction is not ("fill" or "click" or "toggle" or "select"))throw new InvalidOperationException("controlAction deve ser fill, click, toggle ou select.");
            var snapshot=ContextActions.Capture(hwnd);
            int? id=null;
            if(r.ControlId is int held)
            {
                // O número é a posição no retrato em que foi mostrado (observe/find). A tela pode ter mudado desde então:
                // vale o MESMO controle (identidade + nome) no retrato de agora, nunca "o que está nessa posição agora".
                if(!_shown.TryGetValue(hwnd.ToInt64(),out var shown))
                    throw new InvalidOperationException("controlId só vale para o último observe/find desta janela (os números mudam quando a tela muda). Nada foi acionado; observe de novo ou use \"name\".");
                id=ContextActions.Rebind(shown,snapshot,held)
                    ??throw new InvalidOperationException($"O controle #{held} mudou ou não existe mais em {Label(hwnd)}. Nada foi acionado; observe de novo ou use \"name\".");
            }
            else if(r.Name is { Length: >0 } name)
                id=(snapshot.Controls.FirstOrDefault(c=>c.Name.Equals(name,StringComparison.OrdinalIgnoreCase))
                    ??snapshot.Controls.FirstOrDefault(c=>c.Name.Contains(name,StringComparison.OrdinalIgnoreCase)))?.Id;
            if(id is null)throw new InvalidOperationException("Controle não encontrado"+(r.Name is null?"":" com o nome \""+r.Name+"\"")+" em "+Label(hwnd)+". Use find para ver os nomes.");
            string done=ContextActions.Execute(snapshot,[new(id.Value,r.ControlAction,r.Text,null)],ct,requireForeground:false);
            return new(done+" ("+Label(hwnd)+")",Success:!done.Contains("Interrompido:"),Method:"UIA/padrão, sem foco");
        }
        if(r.Action=="activate"){Activate(hwnd);return new("Janela ativada: "+Label(hwnd)+".");}
        // Observação informada e ainda válida: a janela precisa ser do mesmo processo. Sem ela, bastam o processo e a
        // geometria de agora (ler todos os controles da janela aqui só atrasava a entrada); as observações antigas caem.
        GetWindowThreadProcessId(hwnd,out uint pid);GetWindowRect(hwnd,out var bounds);
        if(r.Observation is not null&&_observations.Remove(r.Observation,out var given)&&given.Hwnd==hwnd&&DateTime.UtcNow-given.Time<TimeSpan.FromMinutes(2))
        {
            if(pid!=given.Process)throw new InvalidOperationException("A janela mudou de processo; observe novamente.");
        }
        else _observations.Clear();
        Activate(hwnd);await WaitForInputFocusAsync(hwnd,ct);ct.ThrowIfCancellationRequested();
        GetWindowRect(hwnd,out bounds);
        switch(r.Action)
        {
            case "keys":
            {
                SendKeys(r.Keys??throw new InvalidOperationException("Informe keys."));
                var now=await SettleAsync(ct);
                if(now!=hwnd&&now!=IntPtr.Zero)
                {
                    Refresh();
                    return new("Atalho enviado a "+Label(hwnd)+". Agora em primeiro plano: "+Label(now)+$" [{now.ToInt64()}] — as próximas ações sem target vão para ela.");
                }
                break;
            }
            case "type":
                EnsureForeground(hwnd);
                foreach(char ch in r.Text??"")
                {
                    ct.ThrowIfCancellationRequested();EnsureForeground(hwnd);
                    Send([Key(0,ch,4),Key(0,ch,6)]);
                    await Task.Delay(4,ct);
                }
                break;
            case "click":
                PointAt(hwnd,bounds,r.X,r.Y);uint down=r.Button=="right"?8u:2u;Send([Mouse(down),Mouse(down*2)]);
                if(r.Button=="double"){await Task.Delay(40,ct);Send([Mouse(2),Mouse(4)]);}break;
            case "scroll":PointAt(hwnd,bounds,r.X,r.Y);Send([Mouse(0x800,unchecked((uint)Math.Clamp(r.Delta??0,-2400,2400)))]);break;
            case "drag":
                PointAt(hwnd,bounds,r.X,r.Y);Send([Mouse(2)]);
                try {for(int i=1;i<=12;i++){ct.ThrowIfCancellationRequested();PointAt(hwnd,bounds,r.X+(r.ToX-r.X)*i/12,r.Y+(r.ToY-r.Y)*i/12,checkCover:false);await Task.Delay(15,ct);}}
                finally{Send([Mouse(4)]);}break;
            case "move_window":
                if(r.X is null||r.Y is null||r.Width is not >=100||r.Height is not >=100)throw new InvalidOperationException("Geometria inválida.");
                if(!SetWindowPos(hwnd,IntPtr.Zero,r.X.Value,r.Y.Value,r.Width.Value,r.Height.Value,0x14))throw new System.ComponentModel.Win32Exception();break;
            default:throw new InvalidOperationException("Ação desconhecida: "+r.Action);
        }
        return new("Entrada enviada a "+Label(hwnd)+".");
    }

    /// <summary>Resultados em JSON ficam legíveis (acentos e símbolos sem \uXXXX), para a IA e para o histórico das conversas.</summary>
    private static readonly JsonSerializerOptions ReadableJson=new(){Encoder=System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping};
    private static DesktopActionResult Readable(DesktopActionResult result)
    {
        string text=result.Text.TrimStart();
        if(text.Length==0||text[0] is not ('{' or '['))return result;
        try{return result with{Text=System.Text.Json.Nodes.JsonNode.Parse(text)?.ToJsonString(ReadableJson)??result.Text};}
        catch(JsonException){return result;}
    }

    // ------------------------------------------------------------------ janelas
    private void Refresh()
    {
        _windows.Clear();
        bool AddWindow(IntPtr h,IntPtr unused)
        {
            if(_windows.ContainsKey(h.ToInt64())||!IsWindowVisible(h))return true;
            GetWindowThreadProcessId(h,out uint pid);if(pid==Environment.ProcessId)return true;
            var title=new StringBuilder(1024);GetWindowText(h,title,title.Capacity);
            var className=new StringBuilder(256);GetClassName(h,className,className.Capacity);
            string name="";try{using var p=Process.GetProcessById((int)pid);name=p.ProcessName;}catch{}
            if(name.Equals("WinStatusBar",StringComparison.OrdinalIgnoreCase))return true;
            if(!GetWindowRect(h,out var b)||b.Right<=b.Left||b.Bottom<=b.Top)return true;
            if(DwmGetWindowAttribute(h,14,out int cloaked,sizeof(int))==0&&cloaked!=0)return true;
            if(title.Length==0&&h!=GetForegroundWindow())return true;
            _windows[h.ToInt64()]=new(h,title.ToString(),className.ToString(),name,b);return true;
        }
        EnumWindows(AddWindow,IntPtr.Zero);
        // EnumWindows omits some immersive/UWP surfaces (including SearchHost CoreWindow).
        AddWindow(GetForegroundWindow(),IntPtr.Zero);
    }

    private DesktopActionResult ListWindowsText()
    {
        Refresh();var fg=GetForegroundWindow().ToInt64();
        var text=new StringBuilder();
        foreach(var w in _windows.Values)text.Append(w.Hwnd.ToInt64()==fg?"► ":"• ").Append('[').Append(w.Hwnd.ToInt64()).Append("] ").Append(w.Process).Append(" — ").Append(w.Title.Length==0?"(sem título)":w.Title).AppendLine();
        return new(text.Length==0?"Nenhuma janela visível.":"Janelas abertas (► = primeiro plano):\n"+text.ToString().TrimEnd());
    }

    /// <summary>Janela da operação: "window" (id), "target" (parte do título ou processo) ou a que está em primeiro plano.</summary>
    private IntPtr ResolveWindow(DesktopActionRequest r)
    {
        if(r.Window!=0)
        {
            var h=new IntPtr(r.Window);
            if(!IsWindow(h))throw new InvalidOperationException("A janela "+r.Window+" não existe mais. Use list_windows.");
            if(!_windows.ContainsKey(r.Window))Refresh();
            return h;
        }
        if(r.Target is { Length: >0 } target)return FindWindow(target)??throw new InvalidOperationException("Nenhuma janela com \""+target+"\" no título ou no processo. Use list_windows ou open.");
        Refresh();var fg=GetForegroundWindow();
        if(fg!=IntPtr.Zero&&_windows.ContainsKey(fg.ToInt64()))return fg;
        // A barra do WinStatusBar (omitida da lista) costuma estar em foco quando o pedido sai dela: vale a janela de cima.
        return _windows.Values.FirstOrDefault()?.Hwnd??throw new InvalidOperationException("Nenhuma janela de aplicativo aberta; use open.");
    }

    private IntPtr? FindWindow(string target)
    {
        Refresh();
        var all=_windows.Values.ToList();
        // Igual → processo que contém → título com a palavra inteira → título que contém ("Word" é o Word, não "password").
        int found=DesktopSafety.PickWindow(all.Select(w=>(w.Process,w.Title)).ToList(),target);
        return found<0?null:all[found].Hwnd;
    }

    private string Label(IntPtr hwnd)
    {
        if(!_windows.TryGetValue(hwnd.ToInt64(),out var w)){Refresh();_windows.TryGetValue(hwnd.ToInt64(),out w);}
        return w is null?"janela "+hwnd.ToInt64():$"{w.Process} — \"{(w.Title.Length>70?w.Title[..69]+"…":w.Title)}\"";
    }

    // ------------------------------------------------------------------ observação (texto; imagem só quando pedida ou sem controles)
    private (Seen Seen,string? Image,string? Error) Capture(IntPtr hwnd,bool image)
    {
        GetWindowRect(hwnd,out var b);GetWindowThreadProcessId(hwnd,out uint pid);
        ContextActions.Snapshot? controls=null;string? error=null;
        try{controls=ContextActions.Capture(hwnd);}catch(Exception ex){error=ex.Message;}
        string? path=image||controls is null||controls.Controls.Count==0?CaptureImage(hwnd,b):null;
        var seen=new Seen(hwnd,pid,b,DateTime.UtcNow,controls);
        _observations.Clear();_observations[Guid.NewGuid().ToString("N")]=seen;
        return (seen,path,error);
    }

    private DesktopActionResult Observe(IntPtr hwnd,bool image)
    {
        var (seen,path,error)=Capture(hwnd,image);
        Remember(seen.Controls); // os "#N" mostrados abaixo valem neste retrato
        string token=_observations.Keys.First();
        var text=new StringBuilder();
        text.Append(Label(hwnd)).Append($" · [{hwnd.ToInt64()}] · {seen.Bounds.Right-seen.Bounds.Left}×{seen.Bounds.Bottom-seen.Bounds.Top}");
        var named=seen.Controls?.Controls.Where(c=>c.Name.Length>0||c.Value.Length>0).Take(60).ToList()??[];
        if(named.Count>0)
        {
            text.Append("\nControles (use \"name\" ou \"controlId\" em uia): ");
            text.Append(string.Join("; ",named.Select(c=>$"#{c.Id} {Shorten(c.Name,40)}"+(c.Value.Length>0?$" = \"{Shorten(c.Value,60)}\"":"")+(c.Actions.Length>0?" ("+string.Join("/",c.Actions)+")":""))));
            int more=(seen.Controls?.Controls.Count??0)-named.Count;if(more>0)text.Append($"; +{more} sem nome");
        }
        else text.Append("\nSem controles acessíveis"+(error is null?"":" ("+error+")")+(path is null?".":"; imagem anexada (coordenadas relativas à janela)."));
        if(path is not null&&named.Count>0)text.Append("\nImagem anexada (coordenadas relativas à janela).");
        if(DialogNote(hwnd) is { } dialog)text.Append('\n').Append(dialog);
        if(OfficeContext(hwnd) is { } office)text.Append('\n').Append(office);
        text.Append("\nobservation: ").Append(token);
        return new(text.ToString(),path);
    }
    private static string Shorten(string s,int n)=>s.Length<=n?s:s[..(n-1)]+"…";

    private static string? CaptureImage(IntPtr hwnd,Rect bounds)
    {
        int w=bounds.Right-bounds.Left,h=bounds.Bottom-bounds.Top;if(w<=0||h<=0||w>8192||h>8192)return null;
        using var bitmap=new Bitmap(w,h,PixelFormat.Format32bppArgb);using var graphics=Graphics.FromImage(bitmap);
        IntPtr dc=graphics.GetHdc();bool captured;try{captured=PrintWindow(hwnd,dc,2);}finally{graphics.ReleaseHdc(dc);}
        if(!captured)return null;
        string folder=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"WinStatusBar","cache","desktop");Directory.CreateDirectory(folder);
        string path=Path.Combine(folder,$"observation-{Environment.ProcessId}.png");bitmap.Save(path,ImageFormat.Png);return path;
    }

    // ------------------------------------------------------------------ processos
    private static async Task<DesktopActionResult> RunProcessAsync(string file,IEnumerable<string> args,CancellationToken ct)
    {
        var psi=new ProcessStartInfo(file){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true,
            StandardOutputEncoding=Encoding.UTF8,StandardErrorEncoding=Encoding.UTF8};
        foreach(var arg in args)psi.ArgumentList.Add(arg);
        using var process=Process.Start(psi)??throw new IOException("Processo não iniciado.");
        using var cancel=ct.Register(()=>{try{process.Kill(true);}catch{}});
        var stdout=ReadBoundedAsync(process.StandardOutput,ct);var stderr=ReadBoundedAsync(process.StandardError,ct);
        await process.WaitForExitAsync(ct);
        string o=(await stdout).Trim(),e=(await stderr).Trim();
        var text=new StringBuilder($"código de saída {process.ExitCode}");
        if(o.Length>0)text.Append("\nsaída:\n").Append(o.Length>12000?o[..12000]+"\n…(truncado)":o);
        if(e.Length>0)text.Append("\nerros:\n").Append(e.Length>4000?e[..4000]+"\n…(truncado)":e);
        return new(text.ToString(),Success:process.ExitCode==0);
    }
    private static async Task<string> ReadBoundedAsync(StreamReader reader,CancellationToken ct)
    {
        var text=new StringBuilder();var buffer=new char[4096];int n;
        while((n=await reader.ReadAsync(buffer.AsMemory(),ct))>0)if(text.Length<64000)text.Append(buffer,0,Math.Min(n,64000-text.Length));
        return text.ToString();
    }

    /// <summary>Abre (arquivo, pasta, URL ou programa) e espera uma janela nova ficar em primeiro plano (até 12 s).</summary>
    private async Task<DesktopActionResult> OpenAndWaitAsync(ProcessStartInfo psi,CancellationToken ct)
    {
        var before=GetForegroundWindow();
        Process.Start(psi)?.Dispose();
        for(int i=0;i<60;i++)
        {
            await Task.Delay(200,ct);
            var fg=GetForegroundWindow();
            if(fg!=IntPtr.Zero&&fg!=before)
            {
                await Task.Delay(300,ct);Refresh();fg=GetForegroundWindow();
                GetWindowThreadProcessId(fg,out uint openedPid);string openedBy="";try{using var p=Process.GetProcessById((int)openedPid);openedBy=p.ProcessName;}catch{}
                if(openedBy.Equals("OpenWith",StringComparison.OrdinalIgnoreCase))
                {
                    // Sem app padrão para o tipo: o Windows perguntou "Abrir com". Fecha a pergunta e devolve o caminho certo.
                    PostMessage(fg,0x0010,IntPtr.Zero,IntPtr.Zero);
                    return new("O Windows não tem app padrão para este tipo de arquivo e abriu \"Abrir com\" (fechado). Use open com \"application\" (ex.: \"notepad.exe\") e o arquivo em \"arguments\".",
                        Success:false,Status:DesktopStatus.Blocked);
                }
                return new("Aberto: "+Label(fg)+" (agora em primeiro plano).");
            }
        }
        return new("Solicitação aceita pelo Windows, mas nenhuma janela nova veio para frente em 12 s. Use list_windows para conferir.");
    }

    // ------------------------------------------------------------------ foco e entrada
    /// <summary>
    /// Traz a janela à frente e CONFERE (a troca de foco entre processos é assíncrona: espera até 1 s). Se a janela estiver
    /// bloqueada por um diálogo modal dela, avisa com o título do diálogo em vez de digitar no lugar errado.
    /// </summary>
    private static void Activate(IntPtr hwnd)
    {
        if(IsIconic(hwnd))ShowWindow(hwnd,9);
        for(int round=0;round<2;round++)
        {
            if(GetForegroundWindow()==hwnd)break;
            if(!SetForegroundWindow(hwnd)||round==1)
            {
                // Truque padrão do Windows para trazer outra janela à frente: um Alt "vazio" libera a troca de foco.
                Send([Key(0x12,'\0',0),Key(0x12,'\0',2)]);
                SetForegroundWindow(hwnd);
            }
            for(int i=0;i<25&&GetForegroundWindow()!=hwnd;i++)Thread.Sleep(40);
        }
        var fg=GetForegroundWindow();
        if(fg==hwnd)return;
        if(fg!=IntPtr.Zero&&(GetWindowRel(fg,4)==hwnd||GetAncestor(fg,3)==hwnd)) // GW_OWNER / GA_ROOTOWNER
            throw new InvalidOperationException("A janela tem um diálogo aberto na frente: "+Describe(fg)+". Mande a ação para o diálogo (\"target\" com o título dele) ou resolva-o com uia (ex.: botão Cancelar).");
        throw new InvalidOperationException("A janela não veio para frente (Windows bloqueou a troca de foco). Em primeiro plano: "+Describe(fg)+". Use uia (funciona sem foco) ou peça ao usuário para clicar na janela.");
    }
    private static void EnsureForeground(IntPtr hwnd)
    {
        var fg=GetForegroundWindow();
        if(fg!=hwnd)throw new InvalidOperationException("O foco mudou para "+Describe(fg)+". A entrada foi interrompida e pode estar parcial. Confira (observe) antes de continuar; não repita texto cegamente.");
    }
    /// <summary>Título e processo de qualquer janela (para mensagens de diagnóstico).</summary>
    private static string Describe(IntPtr h)
    {
        if(h==IntPtr.Zero)return "nenhuma janela";
        var title=new StringBuilder(256);GetWindowText(h,title,title.Capacity);
        GetWindowThreadProcessId(h,out uint pid);string name="?";try{using var p=Process.GetProcessById((int)pid);name=p.ProcessName;}catch{}
        return $"{name} — \"{(title.Length>60?title.ToString(0,59)+"…":title.ToString())}\" [{h.ToInt64()}]";
    }
    /// <summary>Depois de atalhos (Ctrl+O, Enter, Alt…), espera a janela em primeiro plano ficar estável (até 1,5 s).</summary>
    private static async Task<IntPtr> SettleAsync(CancellationToken ct)
    {
        await Task.Delay(120,ct);
        IntPtr last=GetForegroundWindow();int stable=0;
        for(int i=0;i<24&&stable<3;i++){await Task.Delay(60,ct);var now=GetForegroundWindow();stable=now==last?stable+1:0;last=now;}
        return last;
    }
    /// <summary>Executável com interface gráfica (subsistema GUI no cabeçalho PE): caminho completo, ou null.</summary>
    private static string? GuiExecutable(string? application)
    {
        if(string.IsNullOrWhiteSpace(application))return null;
        string name=Environment.ExpandEnvironmentVariables(application.Trim('"'));
        string? path=null;
        if(Path.IsPathRooted(name))path=File.Exists(name)?name:null;
        else
        {
            string file=Path.HasExtension(name)?name:name+".exe";
            foreach(var dir in new[]{Environment.SystemDirectory,Environment.GetFolderPath(Environment.SpecialFolder.Windows)}.Concat((Environment.GetEnvironmentVariable("PATH")??"").Split(';',StringSplitOptions.RemoveEmptyEntries)))
            {
                try{var candidate=Path.Combine(dir.Trim(),file);if(File.Exists(candidate)){path=candidate;break;}}catch{}
            }
            if(path is null)
                try{using var key=Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\"+file);if(key?.GetValue("") is string reg&&File.Exists(reg.Trim('"')))path=reg.Trim('"');}catch{}
        }
        if(path is null||!path.EndsWith(".exe",StringComparison.OrdinalIgnoreCase))return null;
        try
        {
            using var f=File.OpenRead(path);using var br=new BinaryReader(f);
            f.Position=0x3C;int pe=br.ReadInt32();if(pe<=0||pe>f.Length-0x60)return null;
            f.Position=pe+0x5C;return br.ReadUInt16()==2?path:null; // IMAGE_SUBSYSTEM_WINDOWS_GUI
        }
        catch{return null;}
    }
    private static async Task WaitForInputFocusAsync(IntPtr hwnd,CancellationToken ct)
    {
        IntPtr last=IntPtr.Zero;int stable=0;
        for(int attempt=0;attempt<40;attempt++)
        {
            EnsureForeground(hwnd);var info=new GuiThreadInfo{Size=(uint)Marshal.SizeOf<GuiThreadInfo>()};
            uint thread=GetWindowThreadProcessId(hwnd,out _);
            if(GetGUIThreadInfo(thread,ref info)&&info.Focus!=IntPtr.Zero)
            {
                stable=info.Focus==last?stable+1:0;last=info.Focus;
                if(stable>=2)return;
            }
            else if(attempt>=8)return; // janelas sem foco de teclado (ex.: algumas UWP) não confirmam foco: segue após 200 ms
            await Task.Delay(25,ct);
        }
    }
    private static void PointAt(IntPtr hwnd,Rect b,int? x,int? y,bool checkCover=true)
    {
        EnsureForeground(hwnd);if(x is null||y is null||x<0||y<0||x>=b.Right-b.Left||y>=b.Bottom-b.Top)throw new InvalidOperationException("Coordenadas fora da janela.");
        if(!SetCursorPos(b.Left+x.Value,b.Top+y.Value))throw new System.ComponentModel.Win32Exception();
        if(!checkCover)return;
        var hit=WindowFromPoint(new Point(b.Left+x.Value,b.Top+y.Value));if(GetAncestor(hit,2)!=hwnd)throw new InvalidOperationException("Outra janela cobre o ponto. Observe novamente.");
    }
    private static void SendKeys(string chord)
    {
        foreach(var part in chord.Split(',',StringSplitOptions.TrimEntries|StringSplitOptions.RemoveEmptyEntries))
        {
            var keys=part.Split('+',StringSplitOptions.TrimEntries|StringSplitOptions.RemoveEmptyEntries).Select(KeyCode).ToArray();
            if(keys.Length==0||keys.Length>5)throw new InvalidOperationException("Atalho inválido: "+part);
            int down=0;try{foreach(var key in keys){Send([Key(key,'\0',0)]);down++;}}finally{for(int i=down-1;i>=0;i--)Send([Key(keys[i],'\0',2)]);}
            Thread.Sleep(40);
        }
    }
    private static ushort KeyCode(string key)
    {
        if(key.Length==1&&char.IsAsciiLetterOrDigit(key[0]))return char.ToUpperInvariant(key[0]);
        if(key.StartsWith('F')&&int.TryParse(key.AsSpan(1),out int f)&&f>=1&&f<=24)return (ushort)(111+f);
        return key.ToLowerInvariant() switch{"win"=>0x5b,"ctrl"=>0x11,"control"=>0x11,"alt"=>0x12,"shift"=>0x10,"enter"=>0x0d,"return"=>0x0d,"tab"=>9,"escape"=>0x1b,"esc"=>0x1b,"backspace"=>8,"delete"=>0x2e,"del"=>0x2e,"insert"=>0x2d,
            "space"=>0x20,"home"=>0x24,"end"=>0x23,"pageup"=>0x21,"pagedown"=>0x22,"left"=>0x25,"up"=>0x26,"right"=>0x27,"down"=>0x28,"printscreen"=>0x2c,"apps"=>0x5d,"menu"=>0x5d,
            "volumeup"=>0xaf,"volumedown"=>0xae,"volumemute"=>0xad,"mediaplay"=>0xb3,"medianext"=>0xb0,"mediaprev"=>0xb1,
            "-"=>0xbd,"="=>0xbb,","=>0xbc,"."=>0xbe,";"=>0xba,"/"=>0xbf,_=>throw new InvalidOperationException("Tecla desconhecida: "+key)};
    }
    private static Input Key(ushort vk,char scan,uint flags)=>new(){Type=1,Data=new(){Keyboard=new(){Vk=vk,Scan=scan,Flags=flags | (vk is >=0x21 and <=0x2e or 0x5b ? 1u : 0u)}}};
    private static Input Mouse(uint flags,uint data=0)=>new(){Type=0,Data=new(){Mouse=new(){Flags=flags,Data=data}}};
    private static void Send(Input[] inputs){if(SendInput((uint)inputs.Length,inputs,Marshal.SizeOf<Input>())!=inputs.Length)throw new InvalidOperationException("Entrada bloqueada pelo Windows (provavelmente uma janela de administrador). Use \"elevated\":true ou request_elevation; não contorne a proteção.");}
    [StructLayout(LayoutKind.Sequential)]private struct Input{public uint Type;public Union Data;}
    [StructLayout(LayoutKind.Explicit)]private struct Union{[FieldOffset(0)]public Keyboard Keyboard;[FieldOffset(0)]public MouseInput Mouse;}
    [StructLayout(LayoutKind.Sequential)]private struct Keyboard{public ushort Vk,Scan;public uint Flags,Time;public UIntPtr Extra;}
    [StructLayout(LayoutKind.Sequential)]private struct MouseInput{public int X,Y;public uint Data,Flags,Time;public UIntPtr Extra;}
    [StructLayout(LayoutKind.Sequential)]private struct Rect{public int Left,Top,Right,Bottom;}
    [StructLayout(LayoutKind.Sequential)]private struct GuiThreadInfo{public uint Size,Flags;public IntPtr Active,Focus,Capture,MenuOwner,MoveSize,Caret;public Rect CaretRect;}
    [DllImport("user32.dll")]private static extern bool GetGUIThreadInfo(uint thread,ref GuiThreadInfo info);
    private delegate bool EnumProc(IntPtr hwnd,IntPtr p);
    [DllImport("user32.dll")]private static extern bool EnumWindows(EnumProc callback,IntPtr p);
    [DllImport("user32.dll")]private static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")]private static extern bool IsWindow(IntPtr h);
    [DllImport("user32.dll")]private static extern bool IsIconic(IntPtr h);
    [DllImport("user32.dll")]private static extern bool ShowWindow(IntPtr h,int command);
    [DllImport("user32.dll")]private static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")]private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")]private static extern uint GetWindowThreadProcessId(IntPtr h,out uint pid);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)]private static extern int GetWindowText(IntPtr h,StringBuilder text,int count);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)]private static extern int GetClassName(IntPtr h,StringBuilder text,int count);
    [DllImport("dwmapi.dll")]private static extern int DwmGetWindowAttribute(IntPtr h,int attribute,out int value,int size);
    [DllImport("user32.dll")]private static extern bool GetWindowRect(IntPtr h,out Rect r);
    [DllImport("user32.dll",SetLastError=true)]private static extern uint SendInput(uint count,Input[] input,int size);
    [DllImport("user32.dll")]private static extern bool SetCursorPos(int x,int y);
    [DllImport("user32.dll")]private static extern IntPtr WindowFromPoint(Point point);
    [DllImport("user32.dll")]private static extern IntPtr GetAncestor(IntPtr h,uint flags);
    [DllImport("user32.dll",EntryPoint="GetWindow")]private static extern IntPtr GetWindowRel(IntPtr h,uint cmd);
    [DllImport("user32.dll")]private static extern bool SetWindowPos(IntPtr h,IntPtr after,int x,int y,int w,int height,uint flags);
    [DllImport("user32.dll")]private static extern bool PrintWindow(IntPtr h,IntPtr dc,uint flags);
    [DllImport("user32.dll")]private static extern bool PostMessage(IntPtr h,uint msg,IntPtr w,IntPtr l);
    [DllImport("user32.dll")]private static extern bool LockWorkStation();
}
