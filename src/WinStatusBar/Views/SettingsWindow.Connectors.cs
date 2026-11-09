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
    private void BuildConnectors(StackPanel accounts)
    {
        accounts.Children.Add(GroupTitle("Conectores e plugins"));
        accounts.Children.Add(Help("Cada IA usa no chat da barra os conectores e plugins que você ativou no app dela. Claude: conectores da conta claude.ai e plugins; na primeira vez que um conector for usado, a barra pede autorização. Codex: plugins, também com @ na caixa de mensagem. Qira: conectores ativados no Qira, usados por ele automaticamente. Copilot e Gemini (conta Google): recursos da conta, dentro da página oficial."));

        var rows = new List<FrameworkElement>();
        rows.Add(Toggle("Acesso total a plugins e conectores", "Todas as IAs usam seus plugins e conectores no chat da barra sem pedir autorização (Claude, Codex e Qira). Comandos no PC e edição de arquivos continuam dependendo do modo Editar.",
            s => s.ConnectorsFullAccess, (s, on) => s.ConnectorsFullAccess = on));
        rows.Add(ConnectorRow("Claude Code", "Conectores da conta claude.ai, plugins e servidores MCP",
            ct => RunListAsync(AgentPrompt.FindClaudeExecutable(), ["mcp", "list"], ct, l => !l.StartsWith("Checking", StringComparison.OrdinalIgnoreCase))));
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

        // Conectores do Claude sempre liberados.
        var allowed = new StackPanel { Spacing = 6, Margin = new Thickness(16, 12, 16, 12) };
        void RenderAllowed()
        {
            allowed.Children.Clear();
            allowed.Children.Add(new TextBlock { Text = "Conectores do Claude sempre permitidos", FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
            if (S.AllowedConnectors.Count == 0) { allowed.Children.Add(Help("Nenhum. Marque \"Sempre permitir\" no pedido de autorização do chat.")); return; }
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
        accounts.Children.Add(Group(allowed));
    }

    /// <summary>Linha com título, descrição e "Verificar", que mostra a lista lida do comando oficial da IA.</summary>
    private FrameworkElement ConnectorRow(string title, string description, Func<CancellationToken, Task<string>> read)
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
        Button? check = null;
        check = SmallButton("Verificar", async () =>
        {
            check!.IsEnabled = false;
            output.Visibility = Visibility.Visible;
            output.Text = "Lendo…";
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(90));
                output.Text = await Task.Run(() => read(cts.Token));
            }
            catch (Exception ex) { output.Text = "Não foi possível ler: " + ex.Message; }
            finally { check.IsEnabled = true; }
        });
        check.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(check, 1);
        head.Children.Add(check);
        panel.Children.Add(head);
        panel.Children.Add(output);
        return panel;
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
