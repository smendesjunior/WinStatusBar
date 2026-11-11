using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.System;

namespace WinStatusBar.Views;

/// <summary>
/// Pergunta dentro do próprio menu, no lugar de um elemento dele (nunca uma janela de diálogo: a barra e os menus dela
/// ficam numa janela da altura da faixa, e um ContentDialog dela fica invisível e trava os cliques). Enquanto a pergunta
/// está aberta, o menu da cota não se refaz por baixo (<see cref="ApiResumeInline.Hold"/>).
/// </summary>
internal static class InlinePrompt
{
    /// <summary>
    /// Troca <paramref name="anchor"/> (filho de um Panel) pela pergunta e devolve true no botão principal. Cancelar, Esc ou
    /// fechar o menu devolvem false. Sem Panel por cima (caso que não deveria acontecer), devolve false sem perguntar.
    /// </summary>
    public static Task<bool> AskAsync(FrameworkElement anchor, string title, string? message, string primary,
        string cancel = "Cancelar", FrameworkElement? input = null, double maxWidth = 380)
    {
        var done = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (anchor.Parent is not Panel parent || parent.Children.IndexOf(anchor) is int at && at < 0) { done.SetResult(false); return done.Task; }
        var ask = new StackPanel { Spacing = 6, MaxWidth = maxWidth, HorizontalAlignment = HorizontalAlignment.Left };
        var release = ApiResumeInline.Hold(ask);
        ask.Children.Add(new TextBlock
        {
            Text = title, TextWrapping = TextWrapping.Wrap,
            Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"],
        });
        if (!string.IsNullOrEmpty(message))
            ask.Children.Add(new TextBlock
            {
                Text = message, TextWrapping = TextWrapping.Wrap, Opacity = 0.78,
                Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
            });
        if (input is not null) ask.Children.Add(input);
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        var ok = new Button { Content = primary, Style = (Style)Application.Current.Resources["AccentButtonStyle"] };
        var no = new Button { Content = cancel };
        row.Children.Add(ok); row.Children.Add(no);
        ask.Children.Add(row);

        void Finish(bool answer)
        {
            if (done.Task.IsCompleted) return;
            int i = parent.Children.IndexOf(ask);
            if (i >= 0) parent.Children[i] = anchor;
            release();
            done.TrySetResult(answer);
        }
        ok.Click += (_, _) => Finish(true);
        no.Click += (_, _) => Finish(false);
        ask.KeyDown += (_, e) =>
        {
            if (e.Key == VirtualKey.Escape) { e.Handled = true; Finish(false); }
            else if (e.Key == VirtualKey.Enter && input is TextBox) { e.Handled = true; Finish(true); }
        };
        // Menu fechado com a pergunta aberta: vale como cancelar.
        ask.Unloaded += (_, _) => done.TrySetResult(false);
        parent.Children[at] = ask;
        ask.Loaded += (_, _) => { if (input is Control c) c.Focus(FocusState.Programmatic); else ok.Focus(FocusState.Programmatic); };
        return done.Task;
    }
}
