using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace WinStatusBar.Services;

/// <summary>Explicit per-machine driver operations. No driver is installed during startup or updates.</summary>
internal static class SensorDriverSetup
{
    internal sealed record Candidate(string Id, string Title, string DeviceClass, string Manufacturer);
    private static string Folder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WinStatusBar", "SensorSetup");
    public static string LastReport => Path.Combine(Folder, "last-result.txt");
    private static void Release(object? value) { if (value is not null && Marshal.IsComObject(value)) Marshal.FinalReleaseComObject(value); }

    public static Task<List<Candidate>> SearchAsync() => Task.Run(() =>
    {
        object? session = null, searcher = null, result = null, updates = null;
        try
        {
            session = Activator.CreateInstance(Type.GetTypeFromProgID("Microsoft.Update.Session")!);
            searcher = ((dynamic)session!).CreateUpdateSearcher();
            // Keep the machine's managed update source. Never bypass an organization's policy.
            result = ((dynamic)searcher).Search("IsInstalled=0 and IsHidden=0 and Type='Driver'");
            if ((int)((dynamic)result).ResultCode != 2) throw new InvalidOperationException("O Windows não concluiu a pesquisa de drivers. Tente novamente nas Configurações do Windows.");
            updates = ((dynamic)result).Updates;
            var list = new List<Candidate>();
            for (int i = 0; i < (int)((dynamic)updates).Count; i++)
            {
                object item = ((dynamic)updates).Item(i);
                try
                {
                    dynamic driver = item;
                    string kind = driver.DriverClass;
                    if (kind is not ("System" or "Processor" or "Display" or "SoftwareComponent" or "Sensor")) continue;
                    string id = driver.Identity.UpdateID;
                    if (Guid.TryParse(id, out _)) list.Add(new(id, (string)driver.Title, kind, (string)driver.DriverManufacturer));
                }
                finally { Release(item); }
            }
            return list;
        }
        finally { Release(updates); Release(result); Release(searcher); Release(session); }
    });

    // Data is encoded separately; it can never become PowerShell syntax.
    private static string Data(string value) => "[Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('" + Convert.ToBase64String(Encoding.UTF8.GetBytes(value)) + "'))";
    public static Task<string> InstallInfAsync(string path)
    {
        path = Path.GetFullPath(path);
        if (!File.Exists(path) || !Path.GetExtension(path).Equals(".inf", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Selecione o arquivo .inf do pacote original, mantendo os arquivos .cat e .sys na mesma pasta.");
        return RunElevatedAsync("$inf = " + Data(path) + "\n" + """
            $output = & "$env:SystemRoot\System32\pnputil.exe" /add-driver $inf /install 2>&1 | Out-String
            $code = $LASTEXITCODE
            if ($code -notin @(0,3010)) { throw "O Windows recusou ou não concluiu o pacote (código $code).`n$output" }
            $message = "Pacote processado pelo Windows. Isso não garante que um sensor esteja disponível.`n$output"
            if ($code -eq 3010) { $message += "`nReinicialização necessária. Reinicie quando for conveniente." }
            """);
    }
    public static Task<string> InstallUpdatesAsync(IEnumerable<Candidate> selected)
    {
        var ids = selected.Select(c => Guid.Parse(c.Id).ToString()).Distinct().ToArray();
        if (ids.Length == 0) throw new InvalidOperationException("Selecione pelo menos um componente.");
        return RunElevatedAsync("$ids = ConvertFrom-Json (" + Data(JsonSerializer.Serialize(ids)) + ")\n" + """
            $session = New-Object -ComObject Microsoft.Update.Session
            $search = $session.CreateUpdateSearcher().Search("IsInstalled=0 and IsHidden=0 and Type='Driver'")
            if ($search.ResultCode -ne 2) { throw 'A pesquisa não foi concluída pelo Windows.' }
            $items = New-Object -ComObject Microsoft.Update.UpdateColl
            foreach ($update in $search.Updates) {
                if ($ids -contains $update.Identity.UpdateID -and $update.DriverClass -in @('System','Processor','Display','SoftwareComponent','Sensor')) {
                    if (-not $update.EulaAccepted) { throw "O componente exige aceite de licença no Windows Update: $($update.Title)" }
                    [void]$items.Add($update)
                }
            }
            if ($items.Count -ne $ids.Count) { throw 'A lista de componentes mudou. Faça uma nova pesquisa antes de instalar.' }
            $download = $session.CreateUpdateDownloader(); $download.Updates = $items
            $downloadResult = $download.Download()
            if ($downloadResult.ResultCode -ne 2) { throw "Download não concluído. Código: $($downloadResult.ResultCode). Nenhuma instalação iniciada." }
            $installer = $session.CreateUpdateInstaller(); $installer.Updates = $items
            if ($installer.RebootRequiredBeforeInstallation) { throw 'O Windows precisa ser reiniciado antes de instalar. Nenhuma instalação iniciada.' }
            $installed = $installer.Install()
            $lines = @()
            for ($i=0; $i -lt $items.Count; $i++) {
                $r = $installed.GetUpdateResult($i)
                $state = if ($r.ResultCode -eq 2) { 'Instalado' } else { "Não concluído (resultado $($r.ResultCode), erro $($r.HResult))" }
                $lines += "$($items.Item($i).Title): $state"
            }
            $message = $lines -join "`n"
            if ($installed.RebootRequired) { $message += "`nReinicialização necessária. Reinicie quando for conveniente." }
            """);
    }
    private static async Task<string> RunElevatedAsync(string operation)
    {
        Directory.CreateDirectory(Folder);
        string pipeName = "WinStatusBar.SensorSetup." + Guid.NewGuid().ToString("N");
        using var pipe = new NamedPipeServerStream(pipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        string script = "$ErrorActionPreference='Stop'\ntry {\n" + operation + "\n} catch { $message = 'Não concluído: ' + $_.Exception.Message }\n" +
            "$pipe = New-Object IO.Pipes.NamedPipeClientStream('.', '" + pipeName + "', [IO.Pipes.PipeDirection]::Out)\ntry { $pipe.Connect(5000); $writer = New-Object IO.StreamWriter($pipe); $writer.Write($message); $writer.Flush(); $writer.Dispose() } finally { $pipe.Dispose() }";
        var info = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe"))
        {
            UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden,
            Arguments = "-NoProfile -NonInteractive -EncodedCommand " + Convert.ToBase64String(Encoding.Unicode.GetBytes(script))
        };
        using var process = Process.Start(info) ?? throw new InvalidOperationException("Não foi possível iniciar a instalação.");
        using var reportWait = new CancellationTokenSource();
        var connection = pipe.WaitForConnectionAsync(reportWait.Token);
        var exit = process.WaitForExitAsync();
        string result;
        if (await Task.WhenAny(connection, exit) == connection)
        {
            await connection;
            using var reader = new StreamReader(pipe, Encoding.UTF8);
            result = await reader.ReadToEndAsync();
            await exit;
        }
        else
        {
            reportWait.Cancel();
            try { await connection; } catch (OperationCanceledException) { }
            result = "O instalador terminou sem retornar um relatório à sessão atual. Isso pode ocorrer ao autorizar com outra conta de administrador. Confira o Windows Update antes de repetir.";
        }
        await File.WriteAllTextAsync(LastReport, DateTimeOffset.Now.ToString("g") + "\n" + result);
        return result;
    }
}
