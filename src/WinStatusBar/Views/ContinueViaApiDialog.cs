using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WinStatusBar.Core.Ai.Codex;
using WinStatusBar.Core.Ai.Codex.ApiResume;
using WinStatusBar.Services;

namespace WinStatusBar.Views;

/// <summary>
/// Janela "Continuar pela API" em dois passos na mesma janela (a troca usa args.Cancel = true, para não abrir uma janela sobre
/// a outra): 1) marcar as conversas paradas pela cota, todas desmarcadas; 2) conferir modelo, nível, chave e gasto e confirmar.
/// </summary>
internal static class ContinueViaApiDialog
{
    private static int _open;

    /// <summary>Esta janela, ou uma confirmação da seção, está aberta: o menu da cota não se refaz por baixo dela.</summary>
    public static bool IsOpen => Volatile.Read(ref _open) > 0;

    internal static async Task<ContentDialogResult> ShowModalAsync(ContentDialog dialog)
    {
        Interlocked.Increment(ref _open);
        try { return await dialog.ShowAsync(); }
        finally { Interlocked.Decrement(ref _open); }
    }

    public static async Task ShowAsync(XamlRoot root, ElementTheme theme, CodexApiResumeService svc)
    {
        if (IsOpen) return;
        var body = new StackPanel { Spacing = 10 };
        var dialog = new ContentDialog
        {
            Title = Localization.T("Continuar pela API"),
            Content = new ScrollViewer { Content = body, Width = 520, MaxHeight = 520 },
            CloseButtonText = Localization.T("Cancelar"),
            DefaultButton = ContentDialogButton.Primary,
            IsPrimaryButtonEnabled = false,
            XamlRoot = root,
            RequestedTheme = theme,
        };
        var loading = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        loading.Children.Add(new ProgressRing { IsActive = true, Width = 20, Height = 20 });
        loading.Children.Add(Text(Localization.T("Conferindo as conversas…")));
        body.Children.Add(loading);

        ApiResumeChoices? choices = null;
        var checks = new List<(CheckBox Box, ApiResumeCandidate Candidate)>();
        RadioButtons? mode = null;
        int step = 1;

        List<QuotaStop> Marked() => checks.Where(c => c.Box.IsChecked == true).Select(c => c.Candidate.Stop).ToList();

        void ShowStep1()
        {
            step = 1;
            body.Children.Clear();
            bool firstTime = checks.Count == 0;
            foreach (var c in choices!.Candidates)
            {
                var row = new StackPanel { Spacing = 2 };
                CheckBox box;
                if (firstTime)
                {
                    box = new CheckBox { Content = c.Stop.Title, IsChecked = false, IsEnabled = c.CanMark };
                    Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(box, c.Stop.Title);
                    box.Checked += (_, _) => dialog.IsPrimaryButtonEnabled = Marked().Count > 0;
                    box.Unchecked += (_, _) => dialog.IsPrimaryButtonEnabled = Marked().Count > 0;
                    checks.Add((box, c));
                }
                else box = checks.First(x => ReferenceEquals(x.Candidate, c)).Box;
                (box.Parent as Panel)?.Children.Remove(box);
                row.Children.Add(box);
                foreach (var line in Lines(c)) row.Children.Add(Caption(line));
                body.Children.Add(row);
            }
            body.Children.Add(Caption(ApiResumeText.ExcludedFooter(choices.Excluded)));
            if (choices.UsedFallback)
                body.Children.Add(Caption(Localization.T("A lista oficial do Codex não respondeu; a barra usou os registros locais, e conversas arquivadas podem aparecer.")));
            dialog.PrimaryButtonText = Localization.T("Revisar");
            dialog.SecondaryButtonText = "";
            dialog.IsPrimaryButtonEnabled = Marked().Count > 0;
        }

        void ShowStep2()
        {
            step = 2;
            body.Children.Clear();
            var marked = Marked();
            foreach (var stop in marked)
            {
                body.Children.Add(Text(stop.Title, bold: true));
                body.Children.Add(Caption(ApiResumeText.ConfirmModel(stop.Model ?? "", stop.Effort ?? "")));
            }
            body.Children.Add(Text(ApiResumeText.MessageSent));
            body.Children.Add(Text(ApiResumeText.KeySource(choices!.KeySource)));
            body.Children.Add(Text(ApiResumeText.Organization(choices.Organization, choices.DashboardAccount)));
            body.Children.Add(Text(ApiResumeText.NoSpendLimit));
            mode = null;
            if (ApiResumeText.ShowParallelChoice(marked.Count))
            {
                mode = new RadioButtons { SelectedIndex = App.Current.Settings.Current.CodexApiParallel ? 1 : 0 };
                mode.Items.Add(ApiResumeText.QueueOption);
                mode.Items.Add(ApiResumeText.ParallelOption);
                body.Children.Add(mode);
            }
            body.Children.Add(Text(ApiResumeText.DontOpenInApp, bold: true));
            dialog.PrimaryButtonText = Localization.T("Confirmar e continuar pela API");
            dialog.SecondaryButtonText = Localization.T("Voltar");
            dialog.IsPrimaryButtonEnabled = marked.Count > 0;
        }

        dialog.PrimaryButtonClick += (_, args) =>
        {
            if (step == 1) { args.Cancel = true; ShowStep2(); return; }
            int added = svc.Confirm(Marked(), mode?.SelectedIndex == 1);
            App.Log($"Continuar pela API: confirmadas {added} conversa(s)");
        };
        dialog.SecondaryButtonClick += (_, args) =>
        {
            if (step != 2) return;
            args.Cancel = true; // volta ao passo 1 mantendo as marcas
            ShowStep1();
        };

        var shown = ShowModalAsync(dialog);
        try
        {
            choices = await svc.PrepareAsync(CancellationToken.None);
            if (step == 1) ShowStep1();
        }
        catch (Exception e)
        {
            body.Children.Clear();
            body.Children.Add(Text(Localization.F("Não foi possível conferir as conversas: {0}", ApiResumeErrors.Redact(e.Message))));
        }
        await shown;
        dialog.Content = null;
    }

    private static IEnumerable<string> Lines(ApiResumeCandidate c)
    {
        yield return ApiResumeText.StoppedLine(c.Stop);
        if (ApiResumeText.ContextLine(c.Stop.ContextTokens) is { Length: > 0 } context) yield return context;
        if (ApiResumeText.RemarkedLine(c.Stop) is { } remarked) yield return remarked;
        if (c.OpenInApp) yield return ApiResumeText.OpenInApp;
        if (c.AlreadyQueued) yield return ApiResumeText.AlreadyQueued;
        if (c.Unavailable is { } e) { yield return ApiResumeText.Unavailable(e); yield return e.Message; }
        foreach (var dir in c.MissingExtraDirs) yield return ApiResumeErrors.For(ApiResumeProblem.ExtraFolderMissing, folder: dir).Message;
    }

    private static TextBlock Text(string text, bool bold = false) => new()
    {
        Text = text, TextWrapping = TextWrapping.Wrap, MaxWidth = 500,
        Style = (Style)Application.Current.Resources[bold ? "BodyStrongTextBlockStyle" : "BodyTextBlockStyle"],
    };

    private static TextBlock Caption(string text) => new()
    {
        Text = text, TextWrapping = TextWrapping.Wrap, MaxWidth = 480, Opacity = 0.78, Margin = new Thickness(28, 0, 0, 0),
        Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
    };
}
