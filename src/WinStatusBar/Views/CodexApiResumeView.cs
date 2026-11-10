using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WinStatusBar.Core.Ai.Codex.ApiResume;
using WinStatusBar.Services;

namespace WinStatusBar.Views;

/// <summary>
/// Seção "Conversas paradas pela cota" do menu da cota do Codex e os cartões das continuações pela API.
/// Não guarda estado próprio: o menu é refeito a cada 5 s (e pausa enquanto a janela ou uma confirmação está aberta).
/// </summary>
internal static class CodexApiResumeView
{
    private const double Width = QuotaDetailsView.ContentWidth;

    public static UIElement? Section(CodexApiResumeService svc, ElementTheme theme)
    {
        if (!ApiResumeText.ShowSection(svc.CodexBlocked, svc.Supervisor.HasUnfinished)) return null;
        svc.SectionShown();
        var panel = new StackPanel { Spacing = 8, Width = Width };
        panel.Children.Add(new Border
        {
            Height = 1, Margin = new Thickness(0, 2, 0, 2),
            Background = ThemeBrushes.ForTheme("DividerStrokeColorDefaultBrush", theme),
        });
        panel.Children.Add(Text(ApiResumeText.SectionTitle, bold: true));
        var stops = svc.LastScan?.Stops;
        panel.Children.Add(Text(stops is null ? Localization.T("Procurando conversas paradas pela cota…") : ApiResumeText.Summary(stops)));

        if (stops is { Count: > 0 })
        {
            var button = new Button
            {
                Content = ApiResumeText.ContinueButton,
                IsEnabled = svc.HasKey && !svc.IsStoreBuild,
            };
            button.Click += async (_, _) =>
            {
                if (button.XamlRoot is { } root) await ContinueViaApiDialog.ShowAsync(root, theme, svc);
            };
            panel.Children.Add(button);
            if (!svc.HasKey) panel.Children.Add(Caption(ApiResumeText.NoKeyHint));
            if (svc.IsStoreBuild) panel.Children.Add(Caption(ApiResumeText.StoreHint));
        }
        foreach (var job in svc.VisibleJobs) panel.Children.Add(JobCard(job, svc));
        return panel;
    }

    public static FrameworkElement JobCard(ApiResumeJob job, CodexApiResumeService svc)
    {
        var now = DateTimeOffset.Now;
        var card = new StackPanel { Spacing = 3 };
        card.Children.Add(Text(job.Title, bold: true));
        card.Children.Add(Text(ApiResumeText.State(job, now)));
        if (job.State is ApiResumeState.Starting or ApiResumeState.Running && !string.IsNullOrEmpty(job.Detail))
            card.Children.Add(Caption(job.Detail));
        if (job.StartedAt is { } started)
        {
            card.Children.Add(Caption(ApiResumeText.Tokens(job.Tokens)));
            if (job.IsActive) card.Children.Add(Caption(ApiResumeText.Spend(job.Spend, started, job.SpendAt)));
            else if (job.State is ApiResumeState.Completed or ApiResumeState.Stopped) card.Children.Add(Caption(ApiResumeText.FinalSpend(job.Spend)));
        }
        var (position, total) = ApiResumeText.QueuePosition(svc.Supervisor.Jobs, job.Id);
        if (total >= 2 && position > 0) card.Children.Add(Caption(ApiResumeText.Queue(position, total)));
        if (job.State == ApiResumeState.Completed && !string.IsNullOrWhiteSpace(job.Summary)) card.Children.Add(Caption(job.Summary));
        if (job.State is ApiResumeState.Failed or ApiResumeState.Blocked && job.Error is { } error)
        {
            card.Children.Add(Caption(error.Message));
            if (!string.IsNullOrEmpty(error.Technical))
                card.Children.Add(new TextBlock
                {
                    Text = Localization.F("Detalhe técnico: {0}", error.Technical), FontSize = 11, Opacity = 0.7,
                    TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true, MaxWidth = Width,
                });
        }
        else if (job is { State: ApiResumeState.Stopping, ForceOffered: true, Error: { } stopError })
            card.Children.Add(Caption(stopError.Message));

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        var actions = ApiResumeText.Actions(job, svc.CodexBlocked);
        if (job.State == ApiResumeState.Stopping && !job.ForceOffered)
            buttons.Children.Add(new Button { Content = ApiResumeText.Stopping, IsEnabled = false });
        foreach (var action in actions)
        {
            var b = new Button { Content = ApiResumeText.ActionLabel(action) };
            b.Click += async (_, _) => await RunAsync(action, job, svc, b.XamlRoot);
            buttons.Children.Add(b);
        }
        if (buttons.Children.Count > 0) card.Children.Add(buttons);
        return card;
    }

    private static async Task RunAsync(ApiResumeAction action, ApiResumeJob job, CodexApiResumeService svc, XamlRoot? root)
    {
        var sup = svc.Supervisor;
        switch (action)
        {
            case ApiResumeAction.Stop:
                if (await ConfirmAsync(root, Localization.T("Parar a continuação pela API?"), ApiResumeText.StopConfirm, Localization.T("Parar")))
                    sup.Stop(job.Id);
                break;
            case ApiResumeAction.ForceKill:
                if (await ConfirmAsync(root, Localization.T("Encerrar à força?"), ApiResumeErrors.For(ApiResumeProblem.StopNotAnswered).Message,
                        ApiResumeText.ActionLabel(ApiResumeAction.ForceKill)))
                    sup.ForceKill(job.Id);
                break;
            case ApiResumeAction.AllowWhenQuotaBack: sup.AllowWhenQuotaBack(job.Id); break;
            case ApiResumeAction.RemoveFromQueue: sup.RemoveFromQueue(job.Id); break;
            case ApiResumeAction.Retry:
            case ApiResumeAction.ContinueAgain: sup.Retry(job.Id); break;
            case ApiResumeAction.OpenInCodex: WindowsLauncherService.CodexThread(job.ThreadId); break;
            case ApiResumeAction.Dismiss: svc.Acknowledge(job.Id); break;
        }
    }

    private static async Task<bool> ConfirmAsync(XamlRoot? root, string title, string message, string primary)
    {
        if (root is null) return false;
        var dialog = new ContentDialog
        {
            Title = title, Content = message, PrimaryButtonText = primary, CloseButtonText = Localization.T("Cancelar"),
            DefaultButton = ContentDialogButton.Close, XamlRoot = root,
        };
        return await ContinueViaApiDialog.ShowModalAsync(dialog) == ContentDialogResult.Primary;
    }

    private static TextBlock Text(string text, bool bold = false) => new()
    {
        Text = text, TextWrapping = TextWrapping.Wrap, MaxWidth = Width,
        Style = (Style)Application.Current.Resources[bold ? "BodyStrongTextBlockStyle" : "BodyTextBlockStyle"],
    };

    private static TextBlock Caption(string text) => new()
    {
        Text = text, TextWrapping = TextWrapping.Wrap, MaxWidth = Width, Opacity = 0.78,
        Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
    };
}
