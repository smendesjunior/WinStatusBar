using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WinStatusBar.Core.Ai.Codex;
using WinStatusBar.Core.Ai.Codex.ApiResume;
using WinStatusBar.Services;

namespace WinStatusBar.Views;

/// <summary>
/// "Continuar pela API" dentro do menu da cota, em dois passos no mesmo lugar: 1) marcar as conversas paradas pela cota,
/// todas desmarcadas; 2) conferir modelo, nível, chave e gasto e confirmar. O menu não se refaz enquanto isso.
/// </summary>
internal static class ContinueViaApiPanel
{
    private const double Width = QuotaDetailsView.ContentWidth;

    /// <param name="close">Chamado ao cancelar ou confirmar: a seção volta ao botão.</param>
    public static FrameworkElement Build(CodexApiResumeService svc, Action close)
    {
        var root = new StackPanel { Spacing = 8, Width = Width };
        var release = ApiResumeInline.Hold(root);
        void Close() { release(); close(); }

        root.Children.Add(Text(Localization.T("Continuar pela API"), bold: true));
        var body = new StackPanel { Spacing = 8 };
        root.Children.Add(body);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        root.Children.Add(buttons);

        var loading = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        loading.Children.Add(new ProgressRing { IsActive = true, Width = 20, Height = 20 });
        loading.Children.Add(Text(Localization.T("Conferindo as conversas…")));
        body.Children.Add(loading);
        var cancel = new Button { Content = Localization.T("Cancelar") };
        cancel.Click += (_, _) => Close();
        buttons.Children.Add(cancel);

        ApiResumeChoices? choices = null;
        var checks = new List<(CheckBox Box, ApiResumeCandidate Candidate)>();
        RadioButtons? mode = null;

        List<QuotaStop> Marked() => checks.Where(c => c.Box.IsChecked == true).Select(c => c.Candidate.Stop).ToList();

        void ShowStep1()
        {
            body.Children.Clear();
            buttons.Children.Clear();
            var review = new Button { Content = Localization.T("Revisar"), Style = (Style)Application.Current.Resources["AccentButtonStyle"] };
            bool firstTime = checks.Count == 0;
            foreach (var c in choices!.Candidates)
            {
                var row = new StackPanel { Spacing = 2 };
                CheckBox box;
                if (firstTime)
                {
                    box = new CheckBox { Content = c.Stop.Title, IsChecked = false, IsEnabled = c.CanMark };
                    Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(box, c.Stop.Title);
                    checks.Add((box, c));
                }
                else box = checks.First(x => ReferenceEquals(x.Candidate, c)).Box;
                box.Checked += (_, _) => review.IsEnabled = Marked().Count > 0;
                box.Unchecked += (_, _) => review.IsEnabled = Marked().Count > 0;
                (box.Parent as Panel)?.Children.Remove(box);
                row.Children.Add(box);
                foreach (var line in Lines(c)) row.Children.Add(Caption(line));
                body.Children.Add(row);
            }
            body.Children.Add(Caption(ApiResumeText.ExcludedFooter(choices.Excluded), indent: false));
            if (choices.UsedFallback)
                body.Children.Add(Caption(Localization.T("A lista oficial do Codex não respondeu; a barra usou os registros locais, e conversas arquivadas podem aparecer."), indent: false));
            review.IsEnabled = Marked().Count > 0;
            review.Click += (_, _) => ShowStep2();
            buttons.Children.Add(review);
            buttons.Children.Add(cancel);
        }

        void ShowStep2()
        {
            body.Children.Clear();
            buttons.Children.Clear();
            var marked = Marked();
            foreach (var stop in marked)
            {
                body.Children.Add(Text(stop.Title, bold: true));
                body.Children.Add(Caption(ApiResumeText.ConfirmModel(stop.Model ?? "", stop.Effort ?? ""), indent: false));
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
            var confirm = new Button
            {
                Content = Localization.T("Confirmar e continuar pela API"), IsEnabled = marked.Count > 0,
                Style = (Style)Application.Current.Resources["AccentButtonStyle"],
            };
            confirm.Click += (_, _) =>
            {
                confirm.IsEnabled = false;
                int added = svc.Confirm(Marked(), mode?.SelectedIndex == 1);
                App.Log($"Continuar pela API: confirmadas {added} conversa(s)");
                Close();
            };
            var back = new Button { Content = Localization.T("Voltar") };
            back.Click += (_, _) => ShowStep1(); // mantém as marcas
            buttons.Children.Add(confirm);
            buttons.Children.Add(back);
            buttons.Children.Add(cancel);
        }

        _ = LoadAsync();
        async Task LoadAsync()
        {
            try
            {
                choices = await svc.PrepareAsync(CancellationToken.None);
                ShowStep1();
            }
            catch (Exception e)
            {
                body.Children.Clear();
                body.Children.Add(Text(Localization.F("Não foi possível conferir as conversas: {0}", ApiResumeErrors.Redact(e.Message))));
            }
        }
        return root;
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
        Text = text, TextWrapping = TextWrapping.Wrap, MaxWidth = Width,
        Style = (Style)Application.Current.Resources[bold ? "BodyStrongTextBlockStyle" : "BodyTextBlockStyle"],
    };

    private static TextBlock Caption(string text, bool indent = true) => new()
    {
        Text = text, TextWrapping = TextWrapping.Wrap, MaxWidth = Width - (indent ? 28 : 0), Opacity = 0.78,
        Margin = new Thickness(indent ? 28 : 0, 0, 0, 0),
        Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
    };
}
