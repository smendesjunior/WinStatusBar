using System.Reflection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WinStatusBar.Core.Integration;
using WinStatusBar.Services;
namespace WinStatusBar.Views;

/// <summary>Aba "Atualizações": versão instalada, verificação manual (mesma rotina da automática) e o que mudou em cada versão.</summary>
public sealed partial class SettingsWindow
{
    private void BuildUpdatesPage()
    {
        var page = AddPage("updates", "Atualizações", "Versão instalada, novas versões e o que mudou em cada uma.", "");
        var assembly = typeof(SettingsWindow).Assembly;
        string version = assembly.GetName().Version is { } v ? $"{v.Major}.{v.Minor}.{v.Build}" : "1.0.0";
        string? build = assembly.GetCustomAttributes<AssemblyMetadataAttribute>().FirstOrDefault(a => a.Key == "NetworkUpdateBuild")?.Value;

        var status = Help("");
        var check = ActionButton("Verificar atualizações", () => _ = AppUpdater.CheckNowAsync());
        check.HorizontalAlignment = HorizontalAlignment.Stretch;
        void Show(UpdateProgress? progress)
        {
            status.Text = AppUpdater.IsEnabled ? progress?.Text ?? "" : "Compilação local: atualização automática desligada.";
            status.Visibility = status.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
            check.IsEnabled = AppUpdater.IsEnabled && progress?.Stage is not (UpdateStage.Checking or UpdateStage.Downloading or UpdateStage.Installing);
        }
        Show(AppUpdater.LastStatus);
        Action<UpdateProgress> onChanged = progress => DispatcherQueue.TryEnqueue(() => Show(progress));
        AppUpdater.StatusChanged += onChanged;
        Closed += (_, _) => AppUpdater.StatusChanged -= onChanged;
        var panel = new StackPanel { Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
        panel.Children.Add(check);
        panel.Children.Add(status);
        string help = AppUpdater.SourceKind == UpdateSourceKind.Local
            ? "Procura novas versões sozinha a cada 15 minutos, pela pasta local Instalador, e se atualiza sem pedir administrador."
            : "Procura novas versões sozinha a cada 15 minutos, pelo GitHub, e se atualiza sem pedir administrador.";
        page.Children.Add(Group(
            Row("Versão instalada", null, new TextBlock { Text = build is { Length: 14 } ? $"{version} · compilação {build}" : $"{version} · compilação local", TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center }),
            Row("Novas versões", help, panel)));

        // O que mudou: CHANGELOG.md que acompanha o app (mais recente primeiro, como no arquivo).
        page.Children.Add(GroupTitle("O que mudou"));
        string file = Path.Combine(AppContext.BaseDirectory, "CHANGELOG.md");
        IReadOnlyList<ChangelogVersion> versions = [];
        try { if (File.Exists(file)) versions = ChangelogParser.Parse(File.ReadAllText(file)); } catch { }
        if (versions.Count == 0) { page.Children.Add(Help("O histórico de mudanças não está disponível nesta compilação.")); return; }
        foreach (var entry in versions) page.Children.Add(Group(ChangelogCard(entry, entry.Version == version)));
    }

    private FrameworkElement ChangelogCard(ChangelogVersion entry, bool installed)
    {
        var card = new StackPanel { Spacing = 4, Padding = new Thickness(16, 12, 16, 14) };
        card.Children.Add(new TextBlock { Text = "Versão " + entry.Version, Style = (Style)Root.Resources["SettingTitle"] });
        string details = string.Join(" · ", new[] { entry.DateText, installed ? "instalada" : null }.Where(s => !string.IsNullOrEmpty(s)));
        if (details.Length > 0) card.Children.Add(Help(details));
        foreach (var section in entry.Sections)
        {
            if (section.Title.Length > 0)
                card.Children.Add(new TextBlock { Text = section.Title, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Margin = new Thickness(0, 8, 0, 0) });
            foreach (var item in section.Items)
                card.Children.Add(new TextBlock
                {
                    Text = (item.Bullet ? "• " : "") + item.Text,
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(item.Level * 18 + (item.Bullet ? 0 : 10), 0, 0, 0),
                });
        }
        return card;
    }
}
