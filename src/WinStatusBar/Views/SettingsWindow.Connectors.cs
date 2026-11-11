using System.Diagnostics;
using System.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WinStatusBar.Core.Ai;
using WinStatusBar.Core.Ai.Codex;
using WinStatusBar.Core.Ai.Gemini;

namespace WinStatusBar.Views;

/// <summary>
/// Conectores e plugins de cada IA no chat da barra: quais existem neste PC (lidos dos comandos oficiais de cada
/// serviço, sob demanda) e quais conectores do Claude estão sempre liberados.
/// </summary>
public sealed partial class SettingsWindow
{
    /// <summary>Chat da barra › Permissões: conectores e plugins sem pedido de autorização.</summary>
    private Grid ConnectorsAccessRow() =>
        Toggle("Acesso total a plugins e conectores", "Todas as IAs usam seus plugins e conectores no chat da barra sem pedir autorização (Claude, Codex e Qira). Comandos no PC e edição de arquivos continuam dependendo do modo Editar.",
            s => s.ConnectorsFullAccess, (s, on) => s.ConnectorsFullAccess = on);

    /// <summary>Chat da barra › Permissões: conectores do Claude marcados como "Sempre permitir" no chat, com "Remover".</summary>
    private Border AllowedConnectorsGroup()
    {
        var allowed = new StackPanel { Spacing = 6, Margin = new Thickness(16, 12, 16, 12) };
        string? detectNote = null;
        bool detecting = false;
        void RenderAllowed()
        {
            allowed.Children.Clear();
            var head = new Grid { ColumnSpacing = 8 };
            head.ColumnDefinitions.Add(new ColumnDefinition());
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var title = new StackPanel { Spacing = 2 };
            title.Children.Add(new TextBlock { Text = "Conectores do Claude sempre permitidos", FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
            title.Children.Add(Help("Entram aqui sozinhos: com \"Acesso total\" ligado, cada conector que o Claude usar numa resposta; sem ele, os que você marcar com \"Sempre permitir\". \"Detectar\" acrescenta de uma vez todos os conectados na sua conta."));
            head.Children.Add(title);
            // Lê a lista oficial ("claude mcp list") e libera os que estão conectados; os que pedem login ficam de fora.
            var detect = SmallButton(detecting ? "Detectando…" : "Detectar", async () =>
            {
                detecting = true; detectNote = null; RenderAllowed();
                try
                {
                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(90));
                    string output = await Task.Run(() => RunListAsync(AgentPrompt.FindClaudeExecutable(), ["mcp", "list"], cts.Token, _ => true));
                    var servers = ConnectorPermissions.ParseMcpList(output);
                    var fresh = servers.Where(x => x.State == ConnectorPermissions.ServerState.Connected)
                        .Select(x => ConnectorPermissions.RuleForServer(x.Name)).Where(r => !S.AllowedConnectors.Contains(r)).Distinct().ToList();
                    if (fresh.Count > 0) Commit(s => s.AllowedConnectors.AddRange(fresh));
                    int pending = servers.Count(x => x.State == ConnectorPermissions.ServerState.NeedsSignIn);
                    detectNote = servers.Count == 0 ? "Nenhum conector encontrado no Claude Code deste PC."
                        : (fresh.Count == 0 ? "Nenhum conector novo: os conectados já estavam na lista." : fresh.Count == 1 ? "1 conector acrescentado." : fresh.Count + " conectores acrescentados.")
                          + (pending > 0 ? $" Fora da lista por pedir login no Claude: {pending}." : "");
                }
                catch (Exception ex) { detectNote = "Não foi possível ler os conectores: " + ex.Message; }
                finally { detecting = false; RenderAllowed(); }
            });
            detect.IsEnabled = !detecting;
            detect.VerticalAlignment = VerticalAlignment.Top;
            Grid.SetColumn(detect, 1);
            head.Children.Add(detect);
            allowed.Children.Add(head);
            if (detectNote is not null) allowed.Children.Add(Help(detectNote));
            if (S.AllowedConnectors.Count == 0) { allowed.Children.Add(Help("Nenhum por enquanto.")); return; }
            foreach (var rule in S.AllowedConnectors.ToList())
            {
                var line = new Grid { ColumnSpacing = 8 };
                line.ColumnDefinitions.Add(new ColumnDefinition());
                line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                line.Children.Add(new TextBlock { Text = ConnectorPermissions.DisplayName(rule), VerticalAlignment = VerticalAlignment.Center });
                var remove = SmallButton("Remover", () => { Commit(s => s.AllowedConnectors.Remove(rule)); RenderAllowed(); });
                Grid.SetColumn(remove, 1);
                line.Children.Add(remove);
                allowed.Children.Add(line);
            }
        }
        RenderAllowed();
        // As regras são acrescentadas pelo chat ("Sempre permitir"): a lista é relida quando a página aparece.
        CommandPage.RegisterPropertyChangedCallback(UIElement.VisibilityProperty, (_, _) => { if (CommandPage.Visibility == Visibility.Visible) RenderAllowed(); });
        return Group(allowed);
    }

    /// <summary>IAs e contas › Conectores e plugins instalados: o que cada IA tem neste PC, lido sob demanda.</summary>
    private void BuildConnectors(StackPanel accounts)
    {
        accounts.Children.Add(GroupTitle(WinStatusBar.Services.Localization.T("Conectores e plugins instalados")));
        accounts.Children.Add(Help("Cada IA usa no chat da barra os conectores e plugins que você ativou no app dela. Claude: conectores da conta claude.ai e plugins; na primeira vez que um conector for usado, a barra pede autorização. Codex: plugins, também com @ na caixa de mensagem. Qira: conectores ativados no Qira, usados por ele automaticamente. Copilot e Gemini (conta Google): recursos da conta, dentro da página oficial."));

        var rows = new List<FrameworkElement>();
        rows.Add(ConnectorRow("Claude Code", "Conectores da conta claude.ai, plugins e servidores MCP",
            ct => RunListAsync(AgentPrompt.FindClaudeExecutable(), ["mcp", "list"], ct, l => !l.StartsWith("Checking", StringComparison.OrdinalIgnoreCase)),
            text => ConnectorPermissions.ParseMcpList(text) is { Count: > 0 } servers ? ServerList(servers) : null));
        rows.Add(ConnectorRow("Codex", "Plugins instalados no Codex", async ct =>
        {
            var plugins = await CodexComposer.PluginsAsync(ct);
            return plugins.Count == 0 ? "Nenhum plugin instalado." : string.Join("\n", plugins.Select(p => "@" + p.Name + " · " + p.DisplayName));
        }));
        rows.Add(ConnectorRow("Gemini CLI", "Servidores MCP e extensões do Gemini CLI", async ct =>
        {
            var g = GeminiCli.Find();
            if (g is null) return "Gemini CLI não encontrado neste PC.";
            string mcp = await RunListAsync(g.Node, [g.Script, "mcp", "list"], ct, _ => true);
            string ext = await RunListAsync(g.Node, [g.Script, "extensions", "list"], ct, _ => true);
            return mcp + "\n" + ext;
        }));
        var qira = new StackPanel { Spacing = 2, Margin = new Thickness(16, 12, 16, 12) };
        qira.Children.Add(new TextBlock { Text = "Lenovo Qira", FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        qira.Children.Add(Help("Os conectores ativados no Qira (Configurações › Personalização › Conectores) e as permissões de cada ferramenta valem também no chat da barra; o Qira decide quando usá-los."));
        rows.Add(qira);
        accounts.Children.Add(Group(rows.ToArray()));
    }

    /// <summary>Linha com título, descrição e "Verificar", que mostra a lista lida do comando oficial da IA.</summary>
    /// <param name="render">Opcional: mostra o que foi lido como uma lista montada (estado, nome, origem) em vez do texto cru; null = texto.</param>
    private FrameworkElement ConnectorRow(string title, string description, Func<CancellationToken, Task<string>> read, Func<string, FrameworkElement?>? render = null)
    {
        var panel = new StackPanel { Spacing = 4, Margin = new Thickness(16, 12, 16, 12) };
        var head = new Grid { ColumnSpacing = 8 };
        head.ColumnDefinitions.Add(new ColumnDefinition());
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var text = new StackPanel();
        text.Children.Add(new TextBlock { Text = title, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        text.Children.Add(Help(description));
        head.Children.Add(text);
        var output = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 12, IsTextSelectionEnabled = true, Visibility = Visibility.Collapsed };
        var pretty = new ContentControl { HorizontalContentAlignment = HorizontalAlignment.Stretch, Visibility = Visibility.Collapsed };
        Button? check = null;
        check = SmallButton("Verificar", async () =>
        {
            check!.IsEnabled = false;
            output.Visibility = Visibility.Visible; pretty.Visibility = Visibility.Collapsed;
            output.Text = "Lendo…";
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(90));
                string text = await Task.Run(() => read(cts.Token));
                output.Text = text;
                if (render?.Invoke(text) is { } list) { pretty.Content = list; pretty.Visibility = Visibility.Visible; output.Visibility = Visibility.Collapsed; }
            }
            catch (Exception ex) { output.Text = "Não foi possível ler: " + ex.Message; }
            finally { check.IsEnabled = true; }
        });
        check.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(check, 1);
        head.Children.Add(check);
        panel.Children.Add(head);
        panel.Children.Add(output);
        panel.Children.Add(pretty);
        return panel;
    }

    /// <summary>
    /// Lista de conectores com o estado à vista: bolinha colorida, nome limpo ("Google Drive"), origem (conta, plugin) e o
    /// estado por extenso à direita. O endereço ou comando fica na dica ao passar o mouse.
    /// </summary>
    private static FrameworkElement ServerList(IReadOnlyList<ConnectorPermissions.Server> servers)
    {
        static Microsoft.UI.Xaml.Media.Brush Brush(string key) => WinStatusBar.Services.ThemeBrushes.Get(key);
        var list = new StackPanel { Spacing = 4, Margin = new Thickness(0, 6, 0, 0) };
        foreach (var server in servers.OrderBy(x => x.State).ThenBy(x => x.Display, StringComparer.CurrentCultureIgnoreCase))
        {
            var (color, label) = server.State switch
            {
                ConnectorPermissions.ServerState.Connected => ("SystemFillColorSuccessBrush", "Conectado"),
                ConnectorPermissions.ServerState.NeedsSignIn => ("SystemFillColorCautionBrush", "Precisa de login"),
                ConnectorPermissions.ServerState.Failed => ("SystemFillColorCriticalBrush", "Falhou"),
                _ => ("TextFillColorSecondaryBrush", "Estado desconhecido"),
            };
            var row = new Grid { ColumnSpacing = 10, Padding = new Thickness(10, 7, 10, 7), CornerRadius = new CornerRadius(6), Background = Brush("SubtleFillColorSecondaryBrush") };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition());
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.Children.Add(new Microsoft.UI.Xaml.Shapes.Ellipse { Width = 8, Height = 8, Fill = Brush(color), VerticalAlignment = VerticalAlignment.Center });
            var names = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
            names.Children.Add(new TextBlock { Text = server.Display, FontSize = 13 });
            names.Children.Add(new TextBlock { Text = server.Origin, FontSize = 12, Foreground = Brush("TextFillColorSecondaryBrush"), VerticalAlignment = VerticalAlignment.Center });
            Grid.SetColumn(names, 1); row.Children.Add(names);
            var state = new TextBlock { Text = label, FontSize = 12, Foreground = Brush(color), VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(state, 2); row.Children.Add(state);
            ToolTipService.SetToolTip(row, server.Detail);
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(row, $"{server.Display} · {server.Origin} · {label}");
            list.Children.Add(row);
        }
        return list;
    }

    /// <summary>Executa o comando oficial de listagem (sem janela) e devolve as linhas úteis.</summary>
    private static async Task<string> RunListAsync(string? exe, IReadOnlyList<string> args, CancellationToken ct, Func<string, bool> keep)
    {
        if (exe is null || !File.Exists(exe)) return "Não encontrado neste PC.";
        bool viaCmd = exe.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase);
        var psi = new ProcessStartInfo(viaCmd ? Path.Combine(Environment.SystemDirectory, "cmd.exe") : exe)
        {
            RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        };
        if (viaCmd) { psi.ArgumentList.Add("/d"); psi.ArgumentList.Add("/c"); psi.ArgumentList.Add(exe); }
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi) ?? throw new InvalidOperationException("não iniciou");
        var stdout = p.StandardOutput.ReadToEndAsync(ct);
        var stderr = p.StandardError.ReadToEndAsync(ct);
        try { await p.WaitForExitAsync(ct); }
        catch (OperationCanceledException) { try { p.Kill(true); } catch { } throw; }
        string all = (await stdout) + "\n" + (await stderr);
        var lines = all.Split('\n').Select(l => l.TrimEnd('\r').Trim()).Where(l => l.Length > 0 && keep(l)).ToList();
        return lines.Count == 0 ? "Nenhum." : string.Join("\n", lines);
    }
}
