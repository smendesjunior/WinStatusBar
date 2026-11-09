using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using WinStatusBar.Core.Integration;
using WinStatusBar.Services;

namespace WinStatusBar.Views.Panels;

public sealed partial class InstalledAppsPanel : UserControl
{
    private InstalledAppRow[] _apps = [];
    private InstalledApplication? _selected;
    private bool _busy;
    public InstalledAppsPanel() => InitializeComponent();

    public async Task RefreshAsync()
    {
        if (_busy) return;
        _busy = true; RefreshButton.IsEnabled = false;
        CountText.Text = "Carregando aplicativos…";
        try
        {
            _apps = (await Task.Run(InstalledApplications.Read)).Select(a => new InstalledAppRow(a)).ToArray();
            Filter();
        }
        catch (Exception ex) { Feedback.Text = "Não foi possível carregar os aplicativos."; App.Log(ex.ToString()); }
        finally { _busy = false; RefreshButton.IsEnabled = true; }
    }
    private void Filter()
    {
        if (Search is null || AppsList is null) return;
        string query = Search.Text.Trim();
        var found = _apps.Where(a => a.Name.Contains(query, StringComparison.CurrentCultureIgnoreCase)
            || a.App.Publisher.Contains(query, StringComparison.CurrentCultureIgnoreCase)).ToArray();
        AppsList.ItemsSource = found;
        CountText.Text = $"{found.Length} de {_apps.Length} aplicativos";
    }
    private void SearchChanged(object sender, TextChangedEventArgs e) => Filter();
    private async void RefreshClick(object sender, RoutedEventArgs e) => await RefreshAsync();
    private void UninstallClick(object sender, RoutedEventArgs e)
    {
        if (_busy || (sender as Button)?.Tag is not InstalledAppRow row) return;
        _selected = row.App;
        ConfirmationText.Text = $"Desinstalar {row.Name}?";
        Confirmation.Visibility = Visibility.Visible;
    }
    private void CancelClick(object sender, RoutedEventArgs e)
    { _selected = null; Confirmation.Visibility = Visibility.Collapsed; }
    private async void ConfirmClick(object sender, RoutedEventArgs e)
    {
        if (_busy || _selected is not { } selected) return;
        _busy = true; AppsList.IsEnabled = false; RefreshButton.IsEnabled = false;
        Confirmation.Visibility = Visibility.Collapsed; _selected = null;
        Feedback.Text = "Aguardando o desinstalador…";
        try
        {
            await InstalledApplications.UninstallAsync(selected);
            Feedback.Text = "Desinstalador executado. A lista será atualizada; conclua as etapas que o Windows apresentar.";
        }
        catch (Exception ex) { Feedback.Text = ex.Message; }
        finally
        {
            _busy = false; AppsList.IsEnabled = true; RefreshButton.IsEnabled = true;
            await RefreshAsync();
        }
    }
}

public sealed class InstalledAppRow(InstalledApplication app)
{
    public InstalledApplication App { get; } = app;
    public string Name => App.Name;
    public string Details => App.Details;
    public bool CanUninstall => App.Removable && (App.PackageName is not null || InstalledApplications.ParseUninstallCommand(App.UninstallCommand) is not null);
    private bool _iconLoaded;
    private ImageSource? _icon;
    public ImageSource? Icon
    {
        get
        {
            if (!_iconLoaded) { _iconLoaded = true; if (App.IconPath is { } path) _icon = IconService.ShellIcon(path, 32); }
            return _icon;
        }
    }
}
