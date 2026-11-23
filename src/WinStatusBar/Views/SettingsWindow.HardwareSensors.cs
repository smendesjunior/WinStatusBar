using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WinStatusBar.Core.Integration;
using WinStatusBar.Core.Ipc;
using WinStatusBar.Core.Metrics;
using WinStatusBar.Services;

namespace WinStatusBar.Views;

/// <summary>
/// Hardware › Sensores de hardware: LibreHardwareMonitor com o driver PawnIO (o mesmo conjunto do Lenovo Legion Toolkit),
/// lido pelo leitor protegido. Ao ligar, confere o que falta e oferece, nesta ordem: instalar só o driver PawnIO
/// (se outro programa já o instalou, é reaproveitado) e autorizar ou renovar o leitor protegido.
/// </summary>
public sealed partial class SettingsWindow
{
    private ToggleSwitch? _hwSwitch;
    private TextBlock? _hwStatus, _hwDetail;
    private Button? _hwPawnButton, _hwAuthorizeButton;
    private bool _hwBusy;
    private string? _pawnVersion; // lido do registro ao abrir e depois de instalar (não a cada 2 s)

    private void BuildHardwareSensors()
    {
        _hwSwitch = new ToggleSwitch { OnContent = Localization.T("Ativado"), OffContent = Localization.T("Desativado"), HorizontalAlignment = HorizontalAlignment.Right, IsOn = S.HardwareSensors };
        _hwSwitch.Toggled += async (_, _) =>
        {
            if (_extrasLoading || _loading || _hwSwitch.IsOn == S.HardwareSensors) return;
            Commit(s => s.HardwareSensors = _hwSwitch.IsOn);
            if (_hwSwitch.IsOn) await PrepareHardwareSensorsAsync();
            UpdateHardwareSensorsStatus();
        };
        _extras.Add((_hwSwitch, s => s.HardwareSensors));
        _hwStatus = new TextBlock { Style = (Style)Root.Resources["SettingTitle"], TextWrapping = TextWrapping.Wrap };
        _hwDetail = Help(string.Empty);
        _hwPawnButton = new Button { Content = Localization.T("Instalar driver PawnIO…") };
        _hwAuthorizeButton = new Button { Content = Localization.T("Autorizar leitor protegido…") };
        _hwPawnButton.Click += async (_, _) => { await InstallPawnIoAsync(); UpdateHardwareSensorsStatus(); };
        _hwAuthorizeButton.Click += async (_, _) => { await AuthorizeHardwareReaderAsync(); UpdateHardwareSensorsStatus(); };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        buttons.Children.Add(_hwPawnButton); buttons.Children.Add(_hwAuthorizeButton);
        var status = new StackPanel { Spacing = 8, Padding = new Thickness(16, 12, 16, 12) };
        status.Children.Add(_hwStatus); status.Children.Add(_hwDetail); status.Children.Add(buttons);

        HardwareSensorsHost.Children.Add(GroupTitle(Localization.T("Sensores de hardware")));
        HardwareSensorsHost.Children.Add(Group(
            Row(Localization.T("Usar LibreHardwareMonitor"),
                Localization.T("Lê CPU, memória, GPU e temperaturas diretamente do hardware, como o Lenovo Legion Toolkit, e substitui as leituras do Windows. A frequência mostrada é a média dos núcleos, e o pico é o núcleo mais rápido. O que não puder ser lido continua vindo do Windows."),
                _hwSwitch),
            status));
        UpdateHardwareSensorsStatus();
    }

    private void UpdateHardwareSensorsStatus()
    {
        if (_hwStatus is null || _hwDetail is null || _hwPawnButton is null || _hwAuthorizeButton is null) return;
        bool pawn = PawnIoDriver.IsInstalled();
        _hwPawnButton.Visibility = pawn || PackageContext.IsPackaged ? Visibility.Collapsed : Visibility.Visible;
        _hwAuthorizeButton.Content = _persistentSensorEnabled ? Localization.T("Renovar leitor protegido…") : Localization.T("Autorizar leitor protegido…");
        _hwPawnButton.IsEnabled = _hwAuthorizeButton.IsEnabled = !_hwBusy;
        string driver = pawn
            ? Localization.F("Driver PawnIO presente ({0}); é reaproveitado se outro programa o instalou.", new object?[] { (_pawnVersion ??= PawnIoDriver.InstalledVersion() ?? string.Empty) is { Length: > 0 } v ? v : Localization.T("versão não informada") })
            : Localization.T("Driver PawnIO ausente: sem ele, frequências e temperatura da CPU continuam vindo do Windows.");
        if (PackageContext.IsPackaged)
        {
            _hwStatus.Text = Localization.T("Indisponível na versão da Microsoft Store");
            _hwDetail.Text = Localization.T("O leitor protegido precisa de permissão de administrador, que a versão da Store não usa.");
            _hwAuthorizeButton.Visibility = Visibility.Collapsed;
            return;
        }
        if (!S.HardwareSensors)
        {
            _hwStatus.Text = Localization.T("Desligado: as leituras vêm do Windows");
            _hwDetail.Text = driver;
            _hwAuthorizeButton.Visibility = Visibility.Collapsed;
            return;
        }
        var hw = _app.Hub.LastHardware;
        if (hw is null)
        {
            _hwStatus.Text = Localization.T("Aguardando o leitor protegido…");
            _hwDetail.Text = (_persistentSensorEnabled
                ? Localization.T("O leitor protegido não respondeu. Depois de uma atualização do WinStatusBar, renove-o para que ele passe a incluir o LibreHardwareMonitor.")
                : Localization.T("O LibreHardwareMonitor roda no leitor protegido, que precisa de uma autorização de administrador uma única vez.")) + "\n" + driver;
            _hwAuthorizeButton.Visibility = Visibility.Visible;
            return;
        }
        if (hw.Starting)
        {
            _hwStatus.Text = Localization.T("Iniciando o LibreHardwareMonitor…");
            _hwDetail.Text = driver;
            _hwAuthorizeButton.Visibility = Visibility.Collapsed;
            return;
        }
        var gpu = HardwareSensors.Match(hw, _app.Hub.LastGpu?.Adapter);
        var parts = new List<string>();
        if (hw.CpuAverageGHz is double avg) parts.Add(Localization.F("CPU {0} (pico {1})", new object?[] { $"{avg:0.00} GHz", hw.CpuPeakGHz is double pk ? $"{pk:0.00} GHz" : "—" }));
        if (hw.CpuCelsius is double c) parts.Add($"CPU {c:0} °C");
        if (hw.RamUsedGB is double ram) parts.Add($"RAM {ram:0.0} GB");
        if (gpu?.Celsius is double g) parts.Add($"GPU {g:0} °C");
        _hwStatus.Text = parts.Count > 0 ? Localization.T("Ativo") + " · " + string.Join(" · ", parts) : Localization.T("Ativo, mas sem leituras neste computador");
        _hwDetail.Text = driver + (hw.CpuAverageGHz is null && pawn ? "\n" + Localization.T("O processador não informou as frequências por núcleo; a barra usa as do Windows.") : string.Empty);
        _hwAuthorizeButton.Visibility = Visibility.Collapsed;
    }

    /// <summary>Fluxo ao ligar: PawnIO (se faltar), depois o leitor protegido (autorizar, ou renovar se ainda não responde).</summary>
    private async Task PrepareHardwareSensorsAsync()
    {
        if (PackageContext.IsPackaged) return;
        if (!PawnIoDriver.IsInstalled()) await InstallPawnIoAsync();
        _persistentSensorEnabled = SensorPermissionTask.IsEnabled();
        if (!_persistentSensorEnabled) { await AuthorizeHardwareReaderAsync(); return; }
        // Leitor autorizado numa versão anterior (sem o LibreHardwareMonitor): não responde neste canal; renova.
        // Espera até ~12 s antes (a barra também usa o canal, e o leitor pode estar iniciando).
        for (int attempt = 0; attempt < 4; attempt++)
        {
            if (await HardwareSensorPipe.ReadAsync(CancellationToken.None, connectMs: 2500) is not null || _app.Hub.LastHardware is not null) return;
            await Task.Delay(500);
        }
        await AuthorizeHardwareReaderAsync();
    }

    private async Task AuthorizeHardwareReaderAsync()
    {
        if (_hwBusy) return;
        _hwBusy = true;
        try { await ChangeSensorPermissionAsync(true); }
        finally { _hwBusy = false; }
    }

    /// <summary>Instala só o driver PawnIO, pelo pacote oficial do winget (o Windows pede autorização de administrador).</summary>
    private async Task InstallPawnIoAsync()
    {
        if (_hwBusy || PawnIoDriver.IsInstalled() || _hwDetail is null) return;
        var confirm = new ContentDialog
        {
            XamlRoot = GpuPage.XamlRoot, RequestedTheme = GpuPage.ActualTheme,
            Title = Localization.T("Instalar o driver PawnIO?"),
            Content = new TextBlock
            {
                TextWrapping = TextWrapping.Wrap,
                Text = Localization.T("O PawnIO é o driver usado pelo LibreHardwareMonitor e pelo Lenovo Legion Toolkit para ler os sensores da CPU. Só o driver é instalado, pelo pacote oficial do winget (namazso.PawnIO). O Windows pedirá autorização de administrador. Ao continuar, você aceita a licença do PawnIO."),
            },
            PrimaryButtonText = Localization.T("Instalar"), CloseButtonText = Localization.T("Cancelar"), DefaultButton = ContentDialogButton.Primary,
        };
        if (await confirm.ShowAsync() != ContentDialogResult.Primary) return;
        _hwBusy = true;
        _hwDetail.Text = Localization.T("Instalando o driver PawnIO… Confirme a autorização do Windows.");
        try
        {
            string winget = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WindowsApps", "winget.exe");
            if (!File.Exists(winget)) throw new FileNotFoundException(Localization.T("O winget não está disponível neste Windows. Instale o PawnIO pela página oficial: https://github.com/namazso/PawnIO.Setup/releases"));
            var info = new ProcessStartInfo(winget) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (string arg in new[] { "install", "--id", PawnIoDriver.WingetId, "--exact", "--source", "winget", "--silent",
                         "--accept-package-agreements", "--accept-source-agreements", "--disable-interactivity" })
                info.ArgumentList.Add(arg);
            using var process = Process.Start(info) ?? throw new InvalidOperationException(Localization.T("Não foi possível iniciar o winget."));
            var output = process.StandardOutput.ReadToEndAsync();
            var errors = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            string log = (await output + "\n" + await errors).Trim();
            App.Log("PawnIO (winget): código " + process.ExitCode + " · " + log.Split('\n').LastOrDefault(l => l.Trim().Length > 0)?.Trim());
            _pawnVersion = null;
            _hwDetail.Text = PawnIoDriver.IsInstalled()
                ? Localization.T("Driver PawnIO instalado.")
                : Localization.F("O driver PawnIO não foi instalado (código {0}). Nada foi alterado.", new object?[] { process.ExitCode });
        }
        catch (Exception ex) { _hwDetail.Text = Localization.T("Não foi possível instalar o driver PawnIO: ") + ex.Message; }
        finally { _hwBusy = false; }
    }
}
