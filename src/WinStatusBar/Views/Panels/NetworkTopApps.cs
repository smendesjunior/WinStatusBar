using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WinStatusBar.Core.Ipc;
using WinStatusBar.Services;

namespace WinStatusBar.Views.Panels;

/// <summary>
/// Os 5 aplicativos que mais usam a rede agora (recebido e enviado por segundo), atualizado a cada 2 s com o painel
/// aberto. Os números vêm do auxiliar com permissão dos sensores (eventos de rede do Windows, como no Gerenciador de
/// Tarefas); processos do mesmo programa (ex.: as abas do Edge) são somados numa linha.
/// </summary>
internal sealed class NetworkTopApps
{
    private readonly StackPanel _rows = new() { Spacing = 0 };
    private readonly TextBlock _note = PanelKit.Caption("", 11);
    private readonly Dictionary<uint, (string Key, string Name, string? Icon)> _names = new();
    private bool _busy;
    private readonly Button _enable = new() { FontSize = 12, Padding = new Thickness(10, 4, 10, 5), Margin = new Thickness(0, 6, 0, 0), Visibility = Visibility.Collapsed };

    public StackPanel Root { get; } = new() { Spacing = 1, Margin = new Thickness(0, 12, 0, 0) };

    public NetworkTopApps()
    {
        Root.Children.Add(PanelKit.Title(Localization.T("Aplicativos que mais usam a rede")));
        _note.TextWrapping = TextWrapping.Wrap;
        Root.Children.Add(_note);
        Root.Children.Add(_enable);
        Root.Children.Add(_rows);
        _note.Text = Localization.T("Lendo…");
        _enable.Click += async (_, _) => await EnableAsync();
    }

    /// <summary>Autoriza (ou renova, depois de uma atualização) o auxiliar dos sensores: pede o UAC do Windows uma vez.</summary>
    private async Task EnableAsync()
    {
        _enable.IsEnabled = false;
        try
        {
            string helper = Path.Combine(AppContext.BaseDirectory, "Sensors", WinStatusBar.Core.Integration.SensorPermissionTask.ExecutableName);
            if (!File.Exists(helper)) { _note.Text = Localization.T("Componente de sensores ausente nesta instalação."); return; }
            var info = new ProcessStartInfo(helper) { UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden };
            info.ArgumentList.Add("--enable");
            using var process = Process.Start(info);
            if (process is null) return;
            await process.WaitForExitAsync();
            _note.Text = Localization.T("Lendo…");
            await Task.Delay(2500);
            await RefreshAsync();
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223) { _note.Text = Localization.T("Autorização cancelada."); }
        catch (Exception ex) { _note.Text = Localization.T("Não foi possível autorizar: ") + ex.Message; }
        finally { _enable.IsEnabled = true; }
    }

    /// <summary>Chamado a cada segundo com o painel aberto; lê a cada 2.</summary>
    public void Tick(int ticks) { if (ticks % 2 == 0) _ = RefreshAsync(); }

    public async Task RefreshAsync()
    {
        if (_busy) return;
        _busy = true;
        try
        {
            var reading = await NetUsagePipe.ReadAsync(CancellationToken.None);
            if (reading is null)
            {
                _rows.Children.Clear();
                bool enabled = await Task.Run(WinStatusBar.Core.Integration.SensorPermissionTask.IsEnabled);
                _note.Text = enabled
                    ? Localization.T("O auxiliar de sensores desta instalação ainda é o da versão anterior, sem a leitura da rede. Renove a autorização uma vez (o Windows pede confirmação).")
                    : Localization.T("O Windows só mostra o uso da rede por aplicativo com permissão de administrador. Autorize o auxiliar de sensores da barra uma vez (o mesmo da temperatura da CPU).");
                _enable.Content = enabled ? Localization.T("Renovar autorização…") : Localization.T("Autorizar…");
                _note.Visibility = _enable.Visibility = Visibility.Visible;
                return;
            }
            var groups = await Task.Run(() => reading.Top
                .Select(e => (Info: Identify(e.Pid), e))
                .GroupBy(x => x.Info.Key, StringComparer.OrdinalIgnoreCase)
                .Select(g => (g.First().Info.Name, g.First().Info.Icon, Recv: g.Sum(x => x.e.RecvPerSec), Send: g.Sum(x => x.e.SendPerSec), Count: g.Count()))
                .Where(g => g.Recv + g.Send > 0)
                .OrderByDescending(g => g.Recv + g.Send).Take(5).ToList());
            _rows.Children.Clear();
            _enable.Visibility = Visibility.Collapsed;
            _note.Visibility = groups.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            _note.Text = Localization.T("Nenhum aplicativo usando a rede agora.");
            foreach (var g in groups) _rows.Children.Add(Row(g.Name, g.Icon, g.Recv, g.Send, g.Count));
        }
        catch (Exception ex) { App.Log("Painel rede (aplicativos): " + ex.Message); }
        finally { _busy = false; }
    }

    /// <summary>Nome do programa (descrição do executável, como no Gerenciador de Tarefas) e ícone; guardado por processo.</summary>
    private (string Key, string Name, string? Icon) Identify(uint pid)
    {
        lock (_names)
            if (_names.TryGetValue(pid, out var known)) return known;
        (string, string, string?) info;
        try
        {
            using var p = Process.GetProcessById((int)pid);
            string? path = null;
            try { path = p.MainModule?.FileName; } catch { }
            string? description = null;
            try { if (path is not null) description = FileVersionInfo.GetVersionInfo(path).FileDescription; } catch { }
            string name = !string.IsNullOrWhiteSpace(description) ? description.Trim() : p.ProcessName;
            info = (p.ProcessName, name, path);
        }
        catch { info = ($"pid{pid}", pid == 4 ? "Sistema" : $"Processo {pid}", null); }
        lock (_names) { if (_names.Count > 500) _names.Clear(); _names[pid] = info; }
        return info;
    }

    private static FrameworkElement Row(string name, string? icon, long recv, long send, int count)
    {
        var row = new Grid { Height = 30, Padding = new Thickness(2, 0, 6, 0), ColumnSpacing = 8 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(18) });
        row.ColumnDefinitions.Add(new ColumnDefinition());
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var image = icon is not null ? IconService.ShellIcon(icon, 32) : null;
        FrameworkElement glyph = image is not null ? new Image { Source = image, Width = 18, Height = 18 } : new FontIcon { Glyph = "", FontSize = 15 };
        row.Children.Add(glyph);
        var label = new TextBlock { Text = count > 1 ? $"{name} ({count})" : name, FontSize = 13, TextTrimming = TextTrimming.CharacterEllipsis, MaxLines = 1 };
        Grid.SetColumn(label, 1); row.Children.Add(label);
        var down = new TextBlock { Text = "↓ " + PanelKit.Bits(recv), FontSize = 12, MinWidth = 92, TextAlignment = TextAlignment.Right };
        var up = new TextBlock { Text = "↑ " + PanelKit.Bits(send), FontSize = 12, MinWidth = 92, TextAlignment = TextAlignment.Right, Foreground = PanelKit.Res("TextFillColorSecondaryBrush") };
        ToolTipService.SetToolTip(down, Localization.T("Recebendo (média dos últimos 3 s)"));
        ToolTipService.SetToolTip(up, Localization.T("Enviando (média dos últimos 3 s)"));
        Grid.SetColumn(down, 2); Grid.SetColumn(up, 3);
        row.Children.Add(down); row.Children.Add(up);
        foreach (var child in row.Children.OfType<FrameworkElement>()) child.VerticalAlignment = VerticalAlignment.Center;
        return row;
    }
}
