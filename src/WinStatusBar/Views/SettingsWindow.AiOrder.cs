using System.Collections.ObjectModel;
using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;
using WinStatusBar.Core.Settings;
using WinStatusBar.Services;

namespace WinStatusBar.Views;

/// <summary>Linha da lista "Ordem das IAs": arrastável, com interruptor de cota na barra.</summary>
[Microsoft.UI.Xaml.Data.Bindable]
public sealed class AiOrderItem : INotifyPropertyChanged
{
    private bool _quotaOn;
    private ImageSource? _icon;
    public string Key { get; set; } = "";
    public string Name { get; set; } = "";
    public string Detail { get; set; } = "";
    public bool QuotaEnabled { get; set; }
    public ImageSource? Icon { get => _icon; set { _icon = value; PropertyChanged?.Invoke(this, new(nameof(Icon))); } }
    public bool QuotaOn { get => _quotaOn; set { if (_quotaOn == value) return; _quotaOn = value; PropertyChanged?.Invoke(this, new(nameof(QuotaOn))); } }
    public event PropertyChangedEventHandler? PropertyChanged;
}

public sealed partial class SettingsWindow
{
    private const string AiOrderTemplate = """
        <DataTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation">
          <Grid ColumnSpacing="12" Padding="0,6">
            <Grid.ColumnDefinitions>
              <ColumnDefinition Width="Auto" /><ColumnDefinition Width="24" /><ColumnDefinition Width="*" /><ColumnDefinition Width="Auto" />
            </Grid.ColumnDefinitions>
            <FontIcon Glyph="&#xE700;" FontSize="14" Opacity="0.6" VerticalAlignment="Center" />
            <Image Grid.Column="1" Source="{Binding Icon}" Width="20" Height="20" VerticalAlignment="Center" />
            <StackPanel Grid.Column="2" VerticalAlignment="Center" Spacing="1">
              <TextBlock Text="{Binding Name}" FontSize="14" />
              <TextBlock Text="{Binding Detail}" FontSize="12" Opacity="0.7" />
            </StackPanel>
            <ToggleSwitch Grid.Column="3" IsOn="{Binding QuotaOn, Mode=TwoWay}" IsEnabled="{Binding QuotaEnabled}" OnContent="Cota na barra" OffContent="Sem cota na barra" MinWidth="0" />
          </Grid>
        </DataTemplate>
        """;

    /// <summary>
    /// Ordem das IAs (arrastar): vale para o menu de agentes e para os módulos de cota da barra. O interruptor escolhe
    /// quais cotas aparecem na barra; o tamanho dos módulos se ajusta ao conteúdo.
    /// </summary>
    private void BuildAiOrder(StackPanel host)
    {
        var names = new Dictionary<string, (string Name, string Logo, string Detail)>
        {
            ["codex"] = ("Codex", "chatgpt-estatico.svg", "Cota 5h e 7d da assinatura"),
            ["claude"] = ("Claude Code", "", "Cota 5h e 7d da assinatura"),
            ["gemini"] = ("Gemini", "gemini.svg", "Uso atual e semanal do plano (conta Google)"),
            ["copilot"] = ("Microsoft Copilot", "copilot.svg", "Créditos de IA da assinatura Microsoft"),
            ["qira"] = ("Lenovo Qira", "qira.svg", "Modo Híbrido · créditos da nuvem do Qira"),
            ["local"] = ("IA local", "ollama-estatico.svg", "Sem cota (roda no PC)"),
            ["deepseek"] = ("DeepSeek", "deepseek.svg", "Saldo da API no menu de agentes"),
            ["grok"] = ("Grok", "grok.svg", "Sem cota informada pela chave"),
        };
        var items = new ObservableCollection<AiOrderItem>();
        foreach (string key in S.AiOrder.Where(names.ContainsKey))
        {
            var n = names[key];
            var item = new AiOrderItem { Key = key, Name = n.Name, Detail = n.Detail, QuotaEnabled = AppSettings.QuotaCapable.Contains(key), QuotaOn = S.BarQuotaServices.Contains(key), Icon = n.Logo.Length > 0 ? AccountLogo(n.Logo) : null };
            if (key == "claude") _ = SetClaudeIconAsync(item);
            item.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName != nameof(AiOrderItem.QuotaOn)) return;
                Commit(s =>
                {
                    s.BarQuotaServices.Remove(item.Key);
                    if (item.QuotaOn) s.BarQuotaServices.Add(item.Key);
                });
            };
            items.Add(item);
        }
        var list = new ListView
        {
            ItemsSource = items, SelectionMode = ListViewSelectionMode.None, CanReorderItems = true, CanDragItems = true, AllowDrop = true,
            ItemTemplate = (DataTemplate)XamlReader.Load(AiOrderTemplate), Padding = new Thickness(8, 4, 8, 4),
        };
        list.DragItemsCompleted += (_, _) => Commit(s => s.AiOrder = items.Select(i => i.Key).Concat(AppSettings.DefaultAiOrder).Distinct().ToList());
        host.Children.Add(GroupTitle("Ordem das IAs e cotas na barra"));
        host.Children.Add(Help("Arraste para mudar a ordem no menu de agentes e nos módulos de cota da barra. Ligue \"Cota na barra\" nas IAs cujas cotas devem aparecer ao lado do agente; o tamanho dos módulos se ajusta sozinho."));
        host.Children.Add(new Border { Style = (Style)Root.Resources["SettingsGroup"], Child = list });
    }

    private static async Task SetClaudeIconAsync(AiOrderItem item)
    {
        try { item.Icon = await IconService.ClaudeIconAsync() ?? AccountLogo("estrela-vetorial-animada.svg"); } catch { }
    }
}
