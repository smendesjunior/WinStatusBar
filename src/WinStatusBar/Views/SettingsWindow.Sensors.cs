using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WinStatusBar.Services;

namespace WinStatusBar.Views;

public sealed partial class SettingsWindow
{
    private bool _driverSetupBusy;
    private TextBlock? _driverSetupStatus;
    private void BuildSensorSetup()
    {
        var manual = new Button { Content = Localization.T("Instalar driver de um arquivo…") };
        var automatic = new Button { Content = Localization.T("Procurar no Windows Update…") };
        var check = new Button { Content = "Verificar sensores" };
        var report = new Button { Content = "Último resultado" };
        _driverSetupStatus = Help("Configuração exclusiva deste computador. Os drivers não são enviados aos outros PCs.");
        _driverSetupStatus.MaxLines = 5; _driverSetupStatus.TextTrimming = TextTrimming.CharacterEllipsis;
        var actions = new Microsoft.UI.Xaml.Controls.StackPanel { Spacing = 8, Margin = new Thickness(16, 12, 16, 12) };
        var first = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        first.Children.Add(manual); first.Children.Add(automatic);
        var second = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        second.Children.Add(check); second.Children.Add(report);
        actions.Children.Add(first); actions.Children.Add(second); actions.Children.Add(_driverSetupStatus);
        SensorDriversHost.Children.Add(GroupTitle(Localization.T("Drivers de sensores")));
        SensorDriversHost.Children.Add(Help("Alguns sensores dependem do driver do fabricante; outros não são expostos pelo hardware. Selecione um pacote .inf original ou procure componentes compatíveis pelo Windows Update. A instalação solicita autorização do Windows e não reinicia o computador automaticamente."));
        SensorDriversHost.Children.Add(Group(actions));
        async Task ShowResult(string title, string message) => await new ContentDialog { XamlRoot = GpuPage.XamlRoot, RequestedTheme = GpuPage.ActualTheme, Title = title, Content = new ScrollViewer { MaxHeight = 380, Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true } }, CloseButtonText = "Fechar" }.ShowAsync();
        string Readings()
        {
            var r = _app.Hub.LastTemperature;
            return $"CPU: {(r?.Cpu is double c ? $"{c:0} °C" : "indisponível")} · GPU: {(r?.Gpu is double g ? $"{g:0} °C" : "indisponível")}\nFonte CPU: {r?.CpuSource ?? "não informada"}";
        }
        async Task Run(Func<Task<string>> action)
        {
            if (_driverSetupBusy) return;
            _driverSetupBusy = true; manual.IsEnabled = automatic.IsEnabled = false;
            _driverSetupStatus.Text = "Aguardando o Windows… A pesquisa e instalação podem levar alguns minutos.";
            string before = Readings();
            try
            {
                string result = await action();
                _driverSetupStatus.Text = result;
                await Task.Delay(11000); // Wait for the existing sensor sampler, without polling or starting another collector.
                await ShowResult("Resultado neste computador", result + "\n\nAntes:\n" + before + "\n\nLeitura mais recente:\n" + Readings() + "\n\nInstalar um driver não garante uma leitura. Se foi solicitado reinício, verifique novamente depois de reiniciar.");
            }
            catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223) { _driverSetupStatus.Text = "Instalação cancelada na autorização do Windows."; }
            catch (Exception ex) { _driverSetupStatus.Text = "Não concluído: " + ex.Message; }
            finally { _driverSetupBusy = false; manual.IsEnabled = automatic.IsEnabled = true; }
        }
        manual.Click += async (_, _) =>
        {
            var picker = new Windows.Storage.Pickers.FileOpenPicker(); picker.FileTypeFilter.Add(".inf");
            WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
            var file = await picker.PickSingleFileAsync(); if (file is null) return;
            var confirm = new ContentDialog { XamlRoot = GpuPage.XamlRoot, RequestedTheme = GpuPage.ActualTheme, Title = "Instalar este pacote neste PC?", Content = file.Path + "\n\nMantenha os arquivos .cat e .sys originais junto ao .inf. O Windows validará a assinatura e aplicabilidade do pacote. Nenhuma proteção de drivers será desativada.", PrimaryButtonText = "Solicitar instalação", CloseButtonText = "Cancelar", DefaultButton = ContentDialogButton.Close };
            if (await confirm.ShowAsync() == ContentDialogResult.Primary) await Run(() => SensorDriverSetup.InstallInfAsync(file.Path));
        };
        automatic.Click += async (_, _) =>
        {
            var notice = new ContentDialog { XamlRoot = GpuPage.XamlRoot, RequestedTheme = GpuPage.ActualTheme, Title = "Tentar localizar componentes?", Content = "O Windows tentará consultar sua fonte de atualizações autorizada na internet ou na organização. Procuraremos drivers de sistema, processador, vídeo e sensores compatíveis com este PC. Você escolherá os componentes antes de baixar e instalar. A leitura dos sensores não é garantida.", PrimaryButtonText = "Pesquisar", CloseButtonText = "Cancelar", DefaultButton = ContentDialogButton.Close };
            if (await notice.ShowAsync() != ContentDialogResult.Primary) return;
            await Run(async () =>
            {
                var found = await SensorDriverSetup.SearchAsync();
                if (found.Count == 0) return "O Windows não ofereceu componentes compatíveis nesta consulta. O sensor pode não ser suportado ou depender de um pacote do fabricante. Nenhum driver foi instalado.";
                var options = found.Select(c => (Driver: c, Box: new CheckBox { Content = new TextBlock { Text = c.Title + "\n" + c.Manufacturer + " · " + c.DeviceClass, TextWrapping = TextWrapping.Wrap } })).ToList();
                var list = new StackPanel { Spacing = 10 }; foreach (var option in options) list.Children.Add(option.Box);
                var dialog = new ContentDialog { XamlRoot = GpuPage.XamlRoot, RequestedTheme = GpuPage.ActualTheme, Title = "Componentes oferecidos pelo Windows", Content = new ScrollViewer { Content = list, MaxHeight = 340 }, PrimaryButtonText = "Baixar e instalar selecionados", IsPrimaryButtonEnabled = false, CloseButtonText = "Cancelar", DefaultButton = ContentDialogButton.Close };
                foreach (var option in options) option.Box.Click += (_, _) => dialog.IsPrimaryButtonEnabled = options.Any(o => o.Box.IsChecked == true);
                if (await dialog.ShowAsync() != ContentDialogResult.Primary) return "Pesquisa concluída. Nenhum componente foi instalado.";
                return await SensorDriverSetup.InstallUpdatesAsync(options.Where(o => o.Box.IsChecked == true).Select(o => o.Driver));
            });
        };
        check.Click += async (_, _) => await ShowResult("Leitura atual dos sensores", Readings() + "\n\nAs leituras são atualizadas a cada 10 segundos quando as temperaturas estão ativadas.");
        report.Click += async (_, _) => await ShowResult("Última instalação neste PC", File.Exists(SensorDriverSetup.LastReport) ? await File.ReadAllTextAsync(SensorDriverSetup.LastReport) : "Nenhuma instalação registrada neste computador.");
    }
}
