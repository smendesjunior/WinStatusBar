using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using Localization = WinStatusBar.Services.Localization;
namespace WinStatusBar.Views;

/// <summary>
/// Página "Sobre e suporte" (rodapé do menu): atualizações, suporte (contato e informações para o relato), diagnóstico
/// e a identificação do aplicativo com autor e créditos. Reúne as antigas páginas Atualizações, Diagnóstico e Sobre.
/// </summary>
public sealed partial class SettingsWindow
{
    private const string AboutAuthor = "Sidney Junior";
    private const string AboutEmail = "s.mendesjunior@outlook.com";

    private void BuildAboutPage()
    {
        var page = AddPage("about", Localization.T("Sobre e suporte"), Localization.T("Atualizações, ajuda para relatar um problema e informações sobre o WinStatusBar."), "", footer: true);
        var assembly = typeof(SettingsWindow).Assembly;
        string version = assembly.GetName().Version is { } v ? $"{v.Major}.{v.Minor}.{v.Build}" : "1.0.0";
        string? build = assembly.GetCustomAttributes<AssemblyMetadataAttribute>().FirstOrDefault(a => a.Key == "NetworkUpdateBuild")?.Value;
        string buildText = build is { Length: 14 } && DateTime.TryParseExact(build, "yyyyMMddHHmmss", null,
            System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal, out var built)
            ? $"{build} · {built.ToLocalTime():dd/MM/yyyy HH:mm}" : build ?? "compilação local";
        string windows = $"{RuntimeInformation.OSDescription} ({RuntimeInformation.OSArchitecture})";
        string runtime = $".NET {Environment.Version} · {RuntimeInformation.ProcessArchitecture}";

        // ---- Atualizações.
        BuildUpdatesSection(page);

        // ---- Suporte: contato e o que enviar junto do relato.
        var mail = new HyperlinkButton { Content = AboutEmail, NavigateUri = new Uri("mailto:" + AboutEmail + "?subject=WinStatusBar%20" + version), Padding = new Thickness(0) };
        var copied = Help("");
        copied.Visibility = Visibility.Collapsed;
        copied.Margin = new Thickness(16, 0, 16, 12);
        string report = $"WinStatusBar {version} (compilação {build ?? "local"})\nWindows: {windows}\nRuntime: {runtime}\nPasta: {AppContext.BaseDirectory}";
        var copy = ActionButton("Copiar informações", () =>
        {
            var data = new Windows.ApplicationModel.DataTransfer.DataPackage();
            data.SetText(report);
            Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(data);
            copied.Text = "Informações copiadas. Cole no e-mail ao relatar um problema.";
            copied.Visibility = Visibility.Visible;
        });
        string dataFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WinStatusBar");
        // Um botão só para a pasta de dados (a página Diagnóstico tinha outro igual).
        var openData = ActionButton("Abrir pasta de dados", () =>
        {
            try { Process.Start(new ProcessStartInfo("explorer.exe", "\"" + dataFolder + "\"") { UseShellExecute = true }); } catch { }
        });
        page.Children.Add(GroupTitle(Localization.T("Suporte")));
        page.Children.Add(Group(
            Row("Contato", "Dúvidas, sugestões e relatos de problemas.", mail),
            Row(Localization.T("Relatar um problema"), "Copie a versão e o sistema para enviar junto do relato, ou abra a pasta com o registro (log) do aplicativo.", Buttons(copy, openData)),
            copied));

        // ---- Diagnóstico: o grupo vem do XAML (por causa da caixa do relatório) e passa a morar nesta página.
        SettingsPages.Children.Remove(DiagnosticsPage);
        DiagnosticsPage.Visibility = Visibility.Visible;
        page.Children.Add(DiagnosticsPage);

        // ---- Sobre: ícone, nome, versão e descrição; autor; sistema; créditos.
        page.Children.Add(GroupTitle(Localization.T("Sobre")));
        var header = new Grid { ColumnSpacing = 16, Padding = new Thickness(16) };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition());
        string png = Path.Combine(AppContext.BaseDirectory, "Assets", "WinStatusBar.png");
        FrameworkElement icon = File.Exists(png)
            ? new Image { Source = new BitmapImage(new Uri(png)), Width = 56, Height = 56 }
            : new FontIcon { Glyph = "", FontSize = 40 };
        icon.VerticalAlignment = VerticalAlignment.Top;
        header.Children.Add(icon);
        var title = new StackPanel { Spacing = 4 };
        title.Children.Add(new TextBlock { Text = "WinStatusBar", FontSize = 20, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        title.Children.Add(Help($"Versão {version} · compilação {buildText}"));
        title.Children.Add(new TextBlock { Text = "Barra de status para o Windows 11: hardware, rede, bateria, cotas e tarefas de IA, área de transferência e um chat que conversa com Claude, Codex, Gemini, Copilot, Lenovo Qira, IA local ou API própria.",
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) });
        Grid.SetColumn(title, 1); header.Children.Add(title);
        page.Children.Add(Group(header));

        page.Children.Add(Group(
            Row("Desenvolvedor", null, new TextBlock { Text = AboutAuthor, VerticalAlignment = VerticalAlignment.Center }),
            Row("Direitos autorais", null, new TextBlock { Text = $"© {DateTime.Now.Year} {AboutAuthor}. Todos os direitos reservados.", TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center }),
            Row("Sistema", null, new TextBlock { Text = windows, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center }),
            Row("Runtime", null, new TextBlock { Text = runtime, VerticalAlignment = VerticalAlignment.Center })));

        // Créditos.
        page.Children.Add(Group(
            Row("Tecnologias", null, new TextBlock { Text = "Windows App SDK (WinUI 3) e .NET", TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center }),
            Row("Ícones", "Segoe Fluent Icons e Fluent UI System Icons (licença MIT).", new TextBlock { Text = "Microsoft", VerticalAlignment = VerticalAlignment.Center }),
            Row("Marcas", "Claude e Anthropic, ChatGPT, Codex e OpenAI, Ollama, Windows e demais nomes e logotipos pertencem aos respectivos proprietários. O WinStatusBar não é afiliado a essas empresas.", new TextBlock { Text = "Avisos legais", VerticalAlignment = VerticalAlignment.Center })));
    }

    private static StackPanel Buttons(params Button[] buttons)
    {
        // Empilhados: a coluna de controles tem 240 px e dois botões lado a lado não cabem.
        var row = new StackPanel { Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
        foreach (var b in buttons) { b.HorizontalAlignment = HorizontalAlignment.Stretch; row.Children.Add(b); }
        return row;
    }
}
