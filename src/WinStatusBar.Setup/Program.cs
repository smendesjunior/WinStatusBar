using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32;
using WinStatusBar.Core.Integration;

namespace WinStatusBar.Setup;

/// <summary>
/// Instalador/desinstalador por usuário (sem administrador).
///   WinStatusBar.Setup.exe              → instala/atualiza e inicia
///   WinStatusBar.Setup.exe /uninstall   → desinstalação limpa
///   /quiet                              → sem janelas (atualizações automáticas, winget); sem /quiet abre o assistente
///   /keepdata                           → (desinstalação) mantém configurações em %LOCALAPPDATA%\WinStatusBar
/// </summary>
internal static class Program
{
    private const string AppName = "WinStatusBar";
    private const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\WinStatusBar";
    private static readonly StringBuilder Log = new();

    private static string LocalAppData => Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
    private static string InstallDir => Path.Combine(LocalAppData, "Programs", AppName);
    private static string DataDir => Path.Combine(LocalAppData, AppName);
    private static string ShortcutPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), AppName + ".lnk");
    private static string AppExe => Path.Combine(InstallDir, "WinStatusBar.exe");
    private static string CollectorExe => Path.Combine(InstallDir, "WinStatusBar.Collector.exe");

    [STAThread]
    private static int Main(string[] args)
    {
        bool quiet = args.Any(a => a.Equals("/quiet", StringComparison.OrdinalIgnoreCase));
        bool uninstall = args.Any(a => a.Equals("/uninstall", StringComparison.OrdinalIgnoreCase));
        bool keepData = args.Any(a => a.Equals("/keepdata", StringComparison.OrdinalIgnoreCase));
        string sid = WindowsIdentity.GetCurrent().User?.Value ?? Environment.UserName;
        using var setupMutex = new Mutex(false, "Local\\WinStatusBar.Setup." + sid);
        bool ownsMutex;
        try { ownsMutex = setupMutex.WaitOne(0); }
        catch (AbandonedMutexException) { ownsMutex = true; }
        if (!ownsMutex) return 3; // another installer/uninstaller owns this user's installation
        int code;
        try
        {
            if (uninstall)
            {
                // O desinstalador fica dentro da pasta de instalação: copia-se para %TEMP% para poder apagá-la.
                string self = Environment.ProcessPath!;
                if (self.StartsWith(InstallDir, StringComparison.OrdinalIgnoreCase) && !args.Contains("/fromtemp"))
                {
                    string temp = Path.Combine(Path.GetTempPath(), $"WinStatusBar.Uninstall.{Guid.NewGuid():N}.exe");
                    File.Copy(self, temp, true);
                    var psi = new ProcessStartInfo(temp) { UseShellExecute = false };
                    foreach (var a in args) psi.ArgumentList.Add(a);
                    psi.ArgumentList.Add("/fromtemp");
                    setupMutex.ReleaseMutex();
                    ownsMutex = false;
                    Process.Start(psi);
                    return 0;
                }
                if (!quiet && !Confirm("Remover o WinStatusBar?\n\nA barra será fechada, a área da tela será liberada e as integrações adicionadas ao Claude Code serão removidas (as configurações originais do Codex e do Claude são preservadas).")) return 1;
                Uninstall(keepData);
                if (!quiet) Info("WinStatusBar removido.\n\n" + Log);
            }
            else if (!quiet)
            {
                // Dois cliques no EXE: assistente de 3 etapas. Ele grava o próprio registro (setup.log).
                return RunWizard();
            }
            else Install(args.Any(a => a.Equals("/update", StringComparison.OrdinalIgnoreCase)), launch: true, step: null);
            code = 0;
        }
        catch (Exception ex)
        {
            Log.AppendLine("ERRO: " + ex.Message);
            if (!quiet) Error("Falha: " + ex.Message + "\n\n" + Log);
            code = 2;
        }
        finally { if (ownsMutex) setupMutex.ReleaseMutex(); }
        WriteLog(uninstall ? "uninstall" : "install");
        return code;
    }

    /// <summary>Assistente (sem /quiet). 0 = instalado, 1 = cancelado, 2 = falhou.</summary>
    private static int RunWizard()
    {
        System.Windows.Forms.Application.SetHighDpiMode(System.Windows.Forms.HighDpiMode.PerMonitorV2);
        System.Windows.Forms.Application.EnableVisualStyles();
        System.Windows.Forms.Application.SetCompatibleTextRenderingDefault(false);
        string version = typeof(Program).Assembly.GetName().Version?.ToString(3) ?? "1.0.0";
        int result = 1;
        using var wizard = new SetupWizard(version, InstalledVersion(), step =>
        {
            try { Install(updateOnly: false, launch: false, step); result = 0; }
            catch (Exception ex) { Log.AppendLine("ERRO: " + ex.Message); result = 2; throw; }
            finally { WriteLog("install"); }
        }, Path.Combine(DataDir, "setup.log"));
        System.Windows.Forms.Application.Run(wizard);
        if (result == 0 && wizard.LaunchRequested)
            try { Process.Start(new ProcessStartInfo(AppExe) { UseShellExecute = true, WorkingDirectory = InstallDir }); } catch { }
        return result;
    }

    /// <summary>Versão registrada em Aplicativos instalados, se a barra já estiver instalada.</summary>
    private static string? InstalledVersion()
    {
        try
        {
            if (!File.Exists(AppExe)) return null;
            using var key = Registry.CurrentUser.OpenSubKey(UninstallKey);
            return key?.GetValue("DisplayVersion") as string;
        }
        catch { return null; }
    }

    private static void Install(bool updateOnly, bool launch, Action<string>? step)
    {
        using var payload = typeof(Program).Assembly.GetManifestResourceStream("payload.zip")
                           ?? throw new InvalidOperationException("Pacote do aplicativo ausente no instalador.");
        step?.Invoke("Copiando os arquivos…");
        try { InstallationFiles.Replace(payload, InstallDir, () => { step?.Invoke("Fechando a barra…"); StopRunningApp(); }); }
        catch
        {
            // A failed update must not leave the previous, preserved installation closed.
            if (File.Exists(AppExe))
                try { Process.Start(new ProcessStartInfo(AppExe) { UseShellExecute = true, WorkingDirectory = InstallDir }); } catch { }
            throw;
        }
        File.Copy(Environment.ProcessPath!, Path.Combine(InstallDir, "Uninstall.exe"), true);
        Log.AppendLine($"Arquivos: {InstallDir}");

        step?.Invoke("Criando o atalho no menu Iniciar…");
        CreateShortcut(ShortcutPath, AppExe, InstallDir, "Barra de status e monitoramento do Windows 11");
        Log.AppendLine("Atalho no menu Iniciar criado.");

        step?.Invoke("Registrando em Aplicativos instalados…");
        using (var key = Registry.CurrentUser.CreateSubKey(UninstallKey))
        {
            key.SetValue("DisplayName", AppName);
            key.SetValue("DisplayVersion", typeof(Program).Assembly.GetName().Version?.ToString(3) ?? "1.0.0");
            // Editor e página do projeto: o winget relaciona a instalação ao pacote WinStatusBar.WinStatusBar por estes dados.
            key.SetValue("Publisher", "Sidney Junior");
            key.SetValue("URLInfoAbout", "https://github.com/smendesjunior/WinStatusBar");
            key.SetValue("DisplayIcon", AppExe + ",0");
            key.SetValue("InstallLocation", InstallDir);
            key.SetValue("UninstallString", $"\"{Path.Combine(InstallDir, "Uninstall.exe")}\" /uninstall");
            key.SetValue("QuietUninstallString", $"\"{Path.Combine(InstallDir, "Uninstall.exe")}\" /uninstall /quiet");
            key.SetValue("NoModify", 1, RegistryValueKind.DWord);
            key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
            key.SetValue("EstimatedSize", (int)(DirSize(InstallDir) / 1024), RegistryValueKind.DWord);
            key.SetValue("InstallDate", DateTime.Now.ToString("yyyyMMdd"));
        }
        Log.AppendLine("Registrado em Aplicativos instalados.");

        // Toda instalação/atualização migra para a tarefa de logon (sem o atraso do Run; sem administrador).
        if (ReadStartWithWindows()) { StartupService.Set(true, AppExe); Log.AppendLine("Inicialização automática ativada (" + StartupService.LastMethod + ")."); }
        // Dependências em toda instalação e atualização (decisão de 07/11/2026: os PCs da rede recebem pelo fluxo
        // automático). Só o WebView2 oficial da Microsoft, por usuário, sem administrador; presente = só consulta ao registro.
        step?.Invoke("Conferindo o Microsoft Edge WebView2…");
        EnsureWebView2();

        step?.Invoke("Ligando ao Claude Code…");
        try
        {
            if (!updateOnly && (Directory.Exists(ClaudeIntegrationInstaller.ClaudeConfigDir()) || File.Exists(ClaudeIntegrationInstaller.SettingsFile)))
                Log.AppendLine("Claude Code: " + ClaudeIntegrationInstaller.Install(CollectorExe));
            else Log.AppendLine("Claude Code não encontrado: integração não instalada.");
        }
        catch (Exception ex) { Log.AppendLine("Claude Code: integração não aplicada (" + ex.Message + ")"); }

        if (!launch) return; // no assistente, a barra abre ao clicar em Concluir (se a caixa estiver marcada)
        Process.Start(new ProcessStartInfo(AppExe) { UseShellExecute = true, WorkingDirectory = InstallDir });
        Log.AppendLine("WinStatusBar iniciado.");
    }

    // ------------------------------------------------------------------ dependências
    private const string WebView2Client = @"Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}";

    /// <summary>Microsoft Edge WebView2 Runtime instalado (por máquina ou por usuário).</summary>
    private static bool WebView2Installed()
    {
        foreach (var (hive, path) in new[] { (Registry.LocalMachine, @"SOFTWARE\WOW6432Node\" + WebView2Client), (Registry.LocalMachine, @"SOFTWARE\" + WebView2Client), (Registry.CurrentUser, @"Software\" + WebView2Client) })
        {
            try
            {
                using var key = hive.OpenSubKey(path);
                if (key?.GetValue("pv") is string pv && pv.Length > 0 && pv != "0.0.0.0") return true;
            }
            catch { }
        }
        return false;
    }

    /// <summary>
    /// O WebView2 Runtime (Copilot, conta Google do Gemini, cotas e painel da OpenAI) já vem no Windows 11. Se faltar,
    /// instala o componente oficial da Microsoft (Evergreen Bootstrapper), por usuário e sem pedir administrador.
    /// </summary>
    private static void EnsureWebView2()
    {
        if (WebView2Installed()) { Log.AppendLine("Microsoft Edge WebView2 Runtime: presente."); return; }
        const string Bootstrapper = "https://go.microsoft.com/fwlink/p/?LinkId=2124703";
        string file = Path.Combine(Path.GetTempPath(), "MicrosoftEdgeWebview2Setup.exe");
        try
        {
            using (var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromMinutes(2) })
            using (var source = http.GetStreamAsync(Bootstrapper).GetAwaiter().GetResult())
            using (var target = File.Create(file))
                source.CopyTo(target);
            using var process = Process.Start(new ProcessStartInfo(file, "/silent /install") { UseShellExecute = false, CreateNoWindow = true })
                                ?? throw new InvalidOperationException("instalador não iniciou");
            if (!process.WaitForExit(TimeSpan.FromMinutes(10))) throw new TimeoutException("tempo esgotado");
            Log.AppendLine(WebView2Installed() ? "Microsoft Edge WebView2 Runtime: instalado." : "Microsoft Edge WebView2 Runtime: instalação não confirmada (código " + process.ExitCode + ").");
        }
        catch (Exception ex)
        {
            Log.AppendLine("Microsoft Edge WebView2 Runtime ausente e não instalado (" + ex.Message + "). Copilot, conta Google do Gemini e cotas por site precisam dele: " + Bootstrapper);
        }
        finally { TryDelete(file); }
    }

    private static void Uninstall(bool keepData)
    {
        // IAs da barra no Codex ligadas: o Codex volta à configuração de antes (senão ficaria apontando para o
        // encaminhador, que sai junto com a barra).
        string codexIas = Path.Combine(InstallDir, "Automation", WinStatusBar.Core.Ai.Codex.CodexIntegration.ExeName);
        if (WinStatusBar.Core.Ai.Codex.CodexIntegration.LoadState().Ligado && File.Exists(codexIas))
        {
            try
            {
                using var off = Process.Start(new ProcessStartInfo(codexIas, "--desligar") { UseShellExecute = false, CreateNoWindow = true });
                bool done = off?.WaitForExit(60_000) == true;
                Log.AppendLine(done && off!.ExitCode == 0 ? "Codex: IAs da barra desligadas (config.toml devolvido)." : "Codex: não foi possível desligar as IAs da barra; confira o config.toml do Codex.");
            }
            catch (Exception ex) { Log.AppendLine("Codex: " + ex.Message); }
        }
        StopRunningApp();
        if (SensorPermissionTask.IsEnabled())
        {
            var permission = new ProcessStartInfo(Path.Combine(InstallDir, "Sensors", SensorPermissionTask.ExecutableName))
                { UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden };
            permission.ArgumentList.Add("--disable");
            using var process = Process.Start(permission);
            process?.WaitForExit();
            if (process is null || process.ExitCode != 0 || SensorPermissionTask.IsEnabled())
                throw new InvalidOperationException("Não foi possível remover a autorização permanente de sensores. A desinstalação foi interrompida.");
        }
        Log.AppendLine("Barra encerrada (registro da AppBar removido).");
        try { Log.AppendLine("Claude Code: " + ClaudeIntegrationInstaller.Uninstall()); }
        catch (Exception ex) { Log.AppendLine("Claude Code: " + ex.Message); }
        try { StartupService.Set(false, AppExe); Log.AppendLine("Inicialização automática removida."); } catch { }
        TryDelete(ShortcutPath);
        Registry.CurrentUser.DeleteSubKeyTree(UninstallKey, throwOnMissingSubKey: false);
        for (int i = 0; i < 10 && Directory.Exists(InstallDir); i++)
        {
            try { Directory.Delete(InstallDir, true); } catch { Thread.Sleep(500); }
        }
        Log.AppendLine(Directory.Exists(InstallDir) ? $"Não foi possível remover totalmente {InstallDir}." : "Arquivos do programa removidos.");
        if (!keepData)
        {
            try { if (Directory.Exists(DataDir)) Directory.Delete(DataDir, true); Log.AppendLine("Configurações do WinStatusBar removidas."); } catch { }
        }
    }

    /// <summary>Pede encerramento limpo (evento nomeado → a barra faz ABM_REMOVE); só força se não responder.</summary>
    private static void StopRunningApp()
    {
        string sid = WindowsIdentity.GetCurrent().User?.Value ?? Environment.UserName;
        if (EventWaitHandle.TryOpenExisting("WinStatusBar.Exit." + sid, out var ev)) { ev.Set(); ev.Dispose(); }
        var deadline = DateTime.UtcNow.AddSeconds(8);
        while (DateTime.UtcNow < deadline && Process.GetProcessesByName("WinStatusBar").Length > 0) Thread.Sleep(200);
        foreach (var p in Process.GetProcessesByName("WinStatusBar"))
        {
            try { p.Kill(); p.WaitForExit(3000); Log.AppendLine("Processo encerrado à força."); } catch { }
        }
        StopCodexComponent();
    }

    /// <summary>
    /// Encaminhador das IAs no Codex (pasta Automation): roda sem a barra, então é encerrado à parte antes de trocar os
    /// arquivos. A barra o inicia de novo ao abrir (se a ligação com o Codex estiver ligada).
    /// </summary>
    private static void StopCodexComponent()
    {
        string name = WinStatusBar.Core.Ai.Codex.CodexIntegration.ProcessName;
        if (Process.GetProcessesByName(name).Length == 0) return;
        if (EventWaitHandle.TryOpenExisting(WinStatusBar.Core.Ai.Codex.CodexIntegration.QuitEventName, out var quit)) { quit.Set(); quit.Dispose(); }
        var deadline = DateTime.UtcNow.AddSeconds(6);
        while (DateTime.UtcNow < deadline && Process.GetProcessesByName(name).Length > 0) Thread.Sleep(200);
        foreach (var p in Process.GetProcessesByName(name))
        {
            try
            {
                if (p.MainModule?.FileName?.StartsWith(InstallDir, StringComparison.OrdinalIgnoreCase) == false) continue;
                p.Kill(); p.WaitForExit(3000); Log.AppendLine("Encaminhador do Codex encerrado à força.");
            }
            catch { }
        }
        Log.AppendLine("Encaminhador do Codex encerrado para a troca dos arquivos.");
    }

    private static bool ReadStartWithWindows()
    {
        try
        {
            string f = Path.Combine(DataDir, "settings.json");
            if (!File.Exists(f)) return true;
            using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(f));
            return !doc.RootElement.TryGetProperty("StartWithWindows", out var v) || v.ValueKind != System.Text.Json.JsonValueKind.False;
        }
        catch { return true; }
    }

    private static long DirSize(string dir) => Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length);

    private static void TryDelete(string file) { try { if (File.Exists(file)) File.Delete(file); } catch { } }

    private static void WriteLog(string kind)
    {
        try
        {
            Directory.CreateDirectory(Path.GetTempPath());
            File.WriteAllText(Path.Combine(Path.GetTempPath(), $"WinStatusBar.{kind}.log"), $"{DateTime.Now:s}\n{Log}");
            if (kind == "install") { Directory.CreateDirectory(DataDir); File.WriteAllText(Path.Combine(DataDir, "setup.log"), $"{DateTime.Now:s}\n{Log}"); }
        }
        catch { }
    }

    // ---------------- Atalho (IShellLinkW) ----------------
    [ComImport, Guid("00021401-0000-0000-C000-000000000046")] private class ShellLink;

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("000214F9-0000-0000-C000-000000000046")]
    private interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder f, int cch, IntPtr fd, int flags);
        void GetIDList(out IntPtr ppidl);
        void SetIDList(IntPtr pidl);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder name, int cch);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string name);
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder dir, int cch);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string dir);
        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder args, int cch);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string args);
        void GetHotkey(out short hotkey);
        void SetHotkey(short hotkey);
        void GetShowCmd(out int cmd);
        void SetShowCmd(int cmd);
        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder path, int cch, out int icon);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string path, int icon);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string path, int reserved);
        void Resolve(IntPtr hwnd, int flags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string file);
    }

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("0000010b-0000-0000-C000-000000000046")]
    private interface IPersistFile
    {
        void GetClassID(out Guid id);
        [PreserveSig] int IsDirty();
        void Load([MarshalAs(UnmanagedType.LPWStr)] string file, uint mode);
        void Save([MarshalAs(UnmanagedType.LPWStr)] string file, bool remember);
        void SaveCompleted([MarshalAs(UnmanagedType.LPWStr)] string file);
        void GetCurFile([MarshalAs(UnmanagedType.LPWStr)] out string file);
    }

    private static void CreateShortcut(string lnk, string target, string workDir, string description)
    {
        var link = (IShellLinkW)new ShellLink();
        link.SetPath(target);
        link.SetWorkingDirectory(workDir);
        link.SetDescription(description);
        link.SetIconLocation(target, 0);
        Directory.CreateDirectory(Path.GetDirectoryName(lnk)!);
        ((IPersistFile)link).Save(lnk, true);
    }

    // ---------------- Mensagens ----------------
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int MessageBoxW(IntPtr h, string text, string caption, uint type);
    private static void Info(string text) => MessageBoxW(IntPtr.Zero, text, AppName, 0x40);
    private static void Error(string text) => MessageBoxW(IntPtr.Zero, text, AppName, 0x10);
    private static bool Confirm(string text) => MessageBoxW(IntPtr.Zero, text, AppName, 0x4 | 0x20) == 6;
}
