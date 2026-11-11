using System.Diagnostics;
using System.Security;
using Microsoft.Win32;
using Windows.ApplicationModel;

namespace WinStatusBar.Core.Integration;

/// <summary>
/// Inicialização automática sem privilégios administrativos. Instalação tradicional: tarefa agendada "ao fazer
/// logon" da própria conta (o Windows dispara junto com a área de trabalho, sem o atraso que aplica aos itens de
/// HKCU\...\Run). Se a tarefa não puder ser criada, usa HKCU\...\Run como antes. MSIX/Store: StartupTask do manifesto.
/// </summary>
public static class StartupService
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "WinStatusBar";
    public const string TaskName = "WinStatusBar";

    public static bool IsEnabled()
    {
        if (PackageContext.IsPackaged)
        {
            var state = PackagedTask().State;
            return state is StartupTaskState.Enabled or StartupTaskState.EnabledByPolicy;
        }
        if (TaskExists()) return true;
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        return key?.GetValue(ValueName) is string;
    }

    /// <summary>Último método usado ("tarefa de logon" ou "Run"), para o log do instalador.</summary>
    public static string LastMethod { get; private set; } = "";

    public static void Set(bool enabled, string exePath)
    {
        if (PackageContext.IsPackaged)
        {
            var task = PackagedTask();
            // Sem diálogo em apps de área de trabalho; se o usuário desativou em Configurações › Inicialização, respeita.
            if (enabled && task.State == StartupTaskState.Disabled) Task.Run(async () => await task.RequestEnableAsync()).GetAwaiter().GetResult();
            else if (!enabled && task.State == StartupTaskState.Enabled) task.Disable();
            return;
        }
        using var key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
        if (!enabled)
        {
            DeleteTask();
            if (key.GetValue(ValueName) is not null) key.DeleteValue(ValueName);
            LastMethod = "";
            return;
        }
        if (CreateTask(exePath))
        {
            // Só um mecanismo: sem a entrada Run, não há segunda tentativa de abrir a barra segundos depois.
            if (key.GetValue(ValueName) is not null) key.DeleteValue(ValueName);
            LastMethod = "tarefa de logon";
        }
        else
        {
            key.SetValue(ValueName, $"\"{exePath}\" --startup");
            LastMethod = "Run";
        }
    }

    /// <summary>XML da tarefa: logon da própria conta, sem atraso, prioridade normal, sem limite de tempo, sem elevação.</summary>
    public static string TaskXml(string user, string exePath) => $"""
        <?xml version="1.0" encoding="UTF-16"?>
        <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
          <RegistrationInfo><Description>Inicia a barra WinStatusBar junto com a área de trabalho.</Description></RegistrationInfo>
          <Triggers><LogonTrigger><Enabled>true</Enabled><UserId>{SecurityElement.Escape(user)}</UserId></LogonTrigger></Triggers>
          <Principals><Principal id="Author"><UserId>{SecurityElement.Escape(user)}</UserId><LogonType>InteractiveToken</LogonType><RunLevel>LeastPrivilege</RunLevel></Principal></Principals>
          <Settings>
            <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
            <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
            <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
            <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
            <Priority>4</Priority>
            <StartWhenAvailable>false</StartWhenAvailable>
          </Settings>
          <Actions Context="Author"><Exec><Command>{SecurityElement.Escape(exePath)}</Command><Arguments>--startup</Arguments><WorkingDirectory>{SecurityElement.Escape(Path.GetDirectoryName(exePath) ?? "")}</WorkingDirectory></Exec></Actions>
        </Task>
        """;

    private static bool CreateTask(string exePath)
    {
        string file = Path.Combine(Path.GetTempPath(), $"winstatusbar-startup-{Environment.ProcessId}.xml");
        try
        {
            string user = System.Security.Principal.WindowsIdentity.GetCurrent().Name;
            File.WriteAllText(file, TaskXml(user, exePath), System.Text.Encoding.Unicode);
            return Schtasks(["/Create", "/TN", TaskName, "/XML", file, "/F"]) == 0;
        }
        catch { return false; }
        finally { try { File.Delete(file); } catch { } }
    }
    private static void DeleteTask() { try { if (TaskExists()) Schtasks(["/Delete", "/TN", TaskName, "/F"]); } catch { } }
    private static bool TaskExists() { try { return Schtasks(["/Query", "/TN", TaskName]) == 0; } catch { return false; } }

    private static int Schtasks(string[] args)
    {
        var psi = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "schtasks.exe")) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        // As duas saídas são lidas ao mesmo tempo e sem segurar esta thread: lidas uma depois da outra, antes da espera,
        // o limite de 15 s nunca valia e o schtasks podia travar com a saída de erro cheia.
        var output = p.StandardOutput.ReadToEndAsync();
        var error = p.StandardError.ReadToEndAsync();
        bool exited = p.WaitForExit(15000);
        if (!exited) { try { p.Kill(entireProcessTree: true); } catch { } }
        // Espera curta pelo fim das leituras (o processo já saiu ou foi encerrado), só para não deixar erro sem dono.
        try { Task.WaitAll([output, error], 2000); } catch { }
        return exited ? p.ExitCode : -1;
    }

    private static StartupTask PackagedTask()
        => Task.Run(async () => await StartupTask.GetAsync(PackageContext.StartupTaskId)).GetAwaiter().GetResult();
}
