using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Xml.Linq;

namespace WinStatusBar.Core.Integration;

/// <summary>Permissão persistente e limitada ao leitor de temperatura. Nenhum driver ou serviço é instalado.</summary>
public static class SensorPermissionTask
{
    private const string TaskPrefix = "WinStatusBar CPU Sensor - ";
    public const string ExecutableName = "WinStatusBar.Sensors.exe";
    public static string ProtectedRoot => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "WinStatusBar", "Sensors");
    public static string CurrentSid => WindowsIdentity.GetCurrent().User!.Value;
    public static string TaskName => TaskPrefix + CurrentSid;
    public static bool IsAdministrator()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }
    internal static bool IsProtectedExecutable(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        try
        {
            string full = Path.GetFullPath(path);
            return full.StartsWith(Path.GetFullPath(ProtectedRoot) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                && Path.GetFileName(full).Equals(ExecutableName, StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }
    private static dynamic Connect()
    {
        dynamic service = Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service")!)!;
        service.Connect();
        return service;
    }
    private static void Release(object? value) { if (value is not null && Marshal.IsComObject(value)) Marshal.ReleaseComObject(value); }
    public static bool IsEnabled()
    {
        object? service = null, folder = null, task = null;
        try
        {
            service = Connect(); folder = ((dynamic)service).GetFolder("\\"); task = ((dynamic)folder).GetTask(TaskName);
            dynamic definition = ((dynamic)task).Definition;
            return (bool)((dynamic)task).Enabled && definition.Actions.Count == 1
                && IsProtectedExecutable((string)definition.Actions[1].Path) && File.Exists((string)definition.Actions[1].Path)
                && (string)definition.Actions[1].Arguments == "--worker";
        }
        catch { return false; }
        finally { Release(task); Release(folder); Release(service); }
    }
    public static bool EnsureRunning()
    {
        object? service = null, folder = null, task = null;
        try
        {
            if (!IsEnabled()) return false;
            service = Connect(); folder = ((dynamic)service).GetFolder("\\"); task = ((dynamic)folder).GetTask(TaskName);
            if ((int)((dynamic)task).State != 4) ((dynamic)task).Run(null);
            return true;
        }
        catch { return false; }
        finally { Release(task); Release(folder); Release(service); }
    }
    internal static string BuildTaskXml(string executable, string sid)
    {
        if (!IsProtectedExecutable(executable)) throw new ArgumentException("O leitor deve estar na pasta protegida do WinStatusBar.");
        _ = new SecurityIdentifier(sid);
        XNamespace ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";
        XElement E(string name, object? value) => new(ns + name, value);
        return new XDocument(new XElement(ns + "Task", new XAttribute("version", "1.2"),
            E("RegistrationInfo", E("Description", global::WinStatusBar.Core.LocalizedText.T("Leitura da temperatura da CPU Lenovo para o WinStatusBar. Somente dados; não aceita comandos."))),
            E("Triggers", new XElement(ns + "LogonTrigger", E("Enabled", "true"), E("UserId", sid), E("Delay", "PT3S"))),
            E("Principals", new XElement(ns + "Principal", new XAttribute("id", "SensorUser"), E("UserId", sid), E("LogonType", "InteractiveToken"), E("RunLevel", "HighestAvailable"))),
            E("Settings", new object[] { E("MultipleInstancesPolicy", "IgnoreNew"), E("DisallowStartIfOnBatteries", "false"), E("StopIfGoingOnBatteries", "false"),
                E("AllowHardTerminate", "true"), E("StartWhenAvailable", "true"), E("RunOnlyIfNetworkAvailable", "false"),
                new XElement(ns + "IdleSettings", E("StopOnIdleEnd", "false"), E("RestartOnIdle", "false")),
                E("AllowStartOnDemand", "true"), E("Enabled", "true"), E("Hidden", "false"), E("RunOnlyIfIdle", "false"),
                E("WakeToRun", "false"), E("ExecutionTimeLimit", "PT0S"), E("Priority", "7"),
                new XElement(ns + "RestartOnFailure", E("Interval", "PT1M"), E("Count", "3")) }),
            new XElement(ns + "Actions", new XAttribute("Context", "SensorUser"), new XElement(ns + "Exec", E("Command", executable), E("Arguments", "--worker"), E("WorkingDirectory", Path.GetDirectoryName(executable)))))).ToString();
    }
    private static void SecureDirectory(string path)
    {
        var directory = Directory.CreateDirectory(path);
        if ((directory.Attributes & FileAttributes.ReparsePoint) != 0) throw new IOException(global::WinStatusBar.Core.LocalizedText.T("Pasta de sensores inválida."));
        var acl = new DirectorySecurity();
        var admins = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        acl.SetOwner(admins);
        acl.SetAccessRuleProtection(true, false);
        foreach (var sid in new[] { admins, new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null) })
            acl.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        acl.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null), FileSystemRights.ReadAndExecute,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        directory.SetAccessControl(acl);
    }
    public static void EnableFromCurrentExecutable()
    {
        if (!IsAdministrator()) throw new UnauthorizedAccessException(global::WinStatusBar.Core.LocalizedText.T("É necessária autorização de administrador."));
        string source = Environment.ProcessPath!;
        string parent = Path.GetDirectoryName(ProtectedRoot)!;
        if (Directory.Exists(parent) && (File.GetAttributes(parent) & FileAttributes.ReparsePoint) != 0) throw new IOException(global::WinStatusBar.Core.LocalizedText.T("Pasta inválida."));
        SecureDirectory(parent);
        SecureDirectory(ProtectedRoot);
        string sourceDirectory = Path.GetDirectoryName(source)!;
        var files = Directory.GetFiles(sourceDirectory, "*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint })
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToArray();
        using var manifest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (string file in files)
        {
            manifest.AppendData(System.Text.Encoding.UTF8.GetBytes(Path.GetRelativePath(sourceDirectory, file).ToLowerInvariant()));
            using var stream = File.OpenRead(file); manifest.AppendData(SHA256.HashData(stream));
        }
        string version = Path.Combine(ProtectedRoot, Convert.ToHexString(manifest.GetHashAndReset())[..16]);
        SecureDirectory(version);
        foreach (string file in files)
        {
            string destination = Path.Combine(version, Path.GetRelativePath(sourceDirectory, file));
            SecureDirectory(Path.GetDirectoryName(destination)!);
            if (!File.Exists(destination)) File.Copy(file, destination);
            using var original = File.OpenRead(file); using var copy = File.OpenRead(destination);
            if (!SHA256.HashData(original).SequenceEqual(SHA256.HashData(copy))) throw new IOException(global::WinStatusBar.Core.LocalizedText.T("Falha na verificação do leitor."));
        }
        string target = Path.Combine(version, ExecutableName);
        object? service = null, folder = null, old = null, registered = null;
        try
        {
            service = Connect(); folder = ((dynamic)service).GetFolder("\\");
            try { old = ((dynamic)folder).GetTask(TaskName); ((dynamic)old).Stop(0); } catch (Exception ex) when (ex is FileNotFoundException || (ex is COMException && (uint)ex.HResult == 0x80070002)) { }
            string descriptor = $"O:BAG:BAD:P(A;;GA;;;SY)(A;;GA;;;BA)(A;;GRGX;;;{CurrentSid})";
            // Create/update + DONT_ADD_PRINCIPAL_ACE: o usuário pode ler/iniciar, mas não editar a tarefa elevada.
            registered = ((dynamic)folder).RegisterTask(TaskName, BuildTaskXml(target, CurrentSid), 22, CurrentSid, null, 3, descriptor);
            ((dynamic)registered).Run(null);
        }
        finally { Release(registered); Release(old); Release(folder); Release(service); }
    }
    public static void Disable()
    {
        if (!IsAdministrator()) throw new UnauthorizedAccessException();
        object? service = null, folder = null, task = null;
        try
        {
            service = Connect(); folder = ((dynamic)service).GetFolder("\\");
            try { task = ((dynamic)folder).GetTask(TaskName); ((dynamic)task).Stop(0); ((dynamic)folder).DeleteTask(TaskName, 0); }
            catch (Exception ex) when (ex is FileNotFoundException || (ex is COMException && (uint)ex.HResult == 0x80070002)) { }
        }
        finally { Release(task); Release(folder); Release(service); }
        // Somente arquivos do próprio leitor, sem seguir links e sem remover componentes usados por outras contas.
        if (!Directory.Exists(ProtectedRoot)) return;
        service = null; folder = null;
        try
        {
            service = Connect(); folder = ((dynamic)service).GetFolder("\\");
            foreach (dynamic other in ((dynamic)folder).GetTasks(1))
                if (((string)other.Name).StartsWith(TaskPrefix, StringComparison.Ordinal)) return;
        }
        finally { Release(folder); Release(service); }
        if (IsProtectedExecutable(Environment.ProcessPath)) return;
        if ((File.GetAttributes(ProtectedRoot) & FileAttributes.ReparsePoint) != 0) return;
        if (Directory.GetDirectories(ProtectedRoot, "*", SearchOption.AllDirectories)
            .Any(d => (File.GetAttributes(d) & FileAttributes.ReparsePoint) != 0)) return;
        string removalTarget = Path.GetFullPath(ProtectedRoot);
        string expected = Path.GetFullPath(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "WinStatusBar", "Sensors"));
        if (!removalTarget.Equals(expected, StringComparison.OrdinalIgnoreCase)) throw new IOException(global::WinStatusBar.Core.LocalizedText.T("Destino inválido."));
        try { Directory.Delete(removalTarget, recursive: true); } catch (IOException) { }
    }
}
