using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using WinStatusBar.Views.Panels;
using WinStatusBar.Core.Ai;

namespace WinStatusBar.Views;

public sealed partial class BarWindow
{
    private Flyout? _activityFlyout;
    private DispatcherQueueTimer? _activityTimer;

    private void SetupActivityFlyout()
    {
        var heading = new TextBlock { FontSize = 15, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap };
        var task = new TextBlock { FontSize = 13, TextWrapping = TextWrapping.Wrap };
        var state = PanelKit.Caption("", 11);
        // Somente as etapas registradas pela IA, dentro do cartão claro, como a lista de tarefas do app Claude.
        // O resumo em texto gerado por IA foi removido (pedido de 07/11/2026).
        var plan = new StackPanel { Spacing = 8 };
        var planCard = new Border { Child = plan, Padding = new Thickness(12), CornerRadius = new CornerRadius(8),
            Background = PanelKit.Res("ControlFillColorDefaultBrush"), BorderBrush = PanelKit.Res("SurfaceStrokeColorDefaultBrush"), BorderThickness = new Thickness(1) };
        // Continuar pela API: faixa quando outra conversa roda pela API, e o cartão da conversa mostrada.
        var apiBannerText = new TextBlock { FontSize = 12, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
        var apiBannerShow = new Button { Content = global::WinStatusBar.Services.Localization.T("Mostrar"), Padding = new Thickness(8, 3, 8, 3) };
        var apiBannerGrid = new Grid { ColumnSpacing = 8 };
        apiBannerGrid.ColumnDefinitions.Add(new ColumnDefinition());
        apiBannerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        apiBannerGrid.Children.Add(apiBannerText);
        Grid.SetColumn(apiBannerShow, 1); apiBannerGrid.Children.Add(apiBannerShow);
        var apiBanner = new Border { Child = apiBannerGrid, Padding = new Thickness(10, 6, 10, 6), CornerRadius = new CornerRadius(6),
            Background = PanelKit.Res("SystemFillColorAttentionBackgroundBrush"), Visibility = Visibility.Collapsed };
        var apiCard = new ContentControl { HorizontalContentAlignment = HorizontalAlignment.Stretch };
        string? apiOtherThread = null;
        apiBannerShow.Click += (_, _) => { if (apiOtherThread is { } other) _hub.SelectAgent(other); };
        // Largura no viewport, não no conteúdo rolável: o texto sempre mede dentro da área visível.
        var body = new StackPanel { Spacing = 10, HorizontalAlignment = HorizontalAlignment.Stretch };
        body.Children.Add(apiBanner);
        body.Children.Add(heading); body.Children.Add(task); body.Children.Add(state);
        body.Children.Add(apiCard);
        body.Children.Add(planCard);
        var scroll = new ScrollViewer { Content = body, Width = 440, MaxHeight = 540, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, HorizontalScrollMode = ScrollMode.Disabled };
        _activityFlyout = new Flyout { Content = scroll, Placement = Microsoft.UI.Xaml.Controls.Primitives.FlyoutPlacementMode.BottomEdgeAlignedLeft,
            ShouldConstrainToRootBounds = false, SystemBackdrop = new DesktopAcrylicBackdrop() };
        var style = new Style(typeof(FlyoutPresenter));
        style.Setters.Add(new Setter(FlyoutPresenter.BackgroundProperty, new SolidColorBrush(Microsoft.UI.Colors.Transparent)));
        style.Setters.Add(new Setter(FlyoutPresenter.PaddingProperty, new Thickness(16)));
        style.Setters.Add(new Setter(FlyoutPresenter.CornerRadiusProperty, new CornerRadius(8)));
        style.Setters.Add(new Setter(FlyoutPresenter.MaxWidthProperty, 500d));
        _activityFlyout.FlyoutPresenterStyle = style;
        TaskModule.Flyout = _activityFlyout;
        string planKey = "", apiKey = "";
        void Update()
        {
            if (_closing) return;
            var selected = _hub.Priority;
            heading.Text = selected is null ? "Acompanhamento da tarefa" : "Acompanhamento · " + selected.DisplayAgent;
            task.Text = selected?.DisplayTitle ?? "Nenhuma tarefa selecionada";
            var brief = _hub.Briefing.Current;
            string id = selected is null ? "" : selected.Kind + ":" + selected.Id;
            string elapsed = "não informado";
            // Início observado pela barra; depois de um reinício dela, o início registrado no transcript.
            if ((selected?.StartedAt ?? (brief.Session == id ? brief.StartedAt : null)) is { } started && selected is not null)
            {
                var end = selected.State is AgentState.Completed or AgentState.Error or AgentState.Idle or AgentState.Interrupted
                    ? selected.LastActivity : DateTimeOffset.Now;
                var duration = end > started ? end - started : TimeSpan.Zero;
                elapsed = duration.TotalHours >= 1 ? $"{(int)duration.TotalHours}h {duration.Minutes:00}min {duration.Seconds:00}s"
                    : $"{(int)duration.TotalMinutes}min {duration.Seconds:00}s";
            }
            state.Text = (brief.Session == id ? brief.Status : "Preparando o acompanhamento…") + "\nTempo decorrido: " + elapsed;
            var steps = brief.Session == id ? brief.Steps : [];
            bool inferred = brief.Session == id && brief.Inferred;
            string key = id + inferred + selected?.TurnId + selected?.State + selected?.ProgressPercent + _settings.Current.EstimateProgressWithoutPlan + string.Join("\n", steps.Select(s => s.Status + ":" + s.Text));
            var svc = App.Current.ApiResume;
            if (svc is not null)
            {
                var job = svc.JobFor(selected);
                var other = svc.ActiveElsewhere(selected);
                var now = DateTimeOffset.Now;
                if (job is not null) heading.Text = WinStatusBar.Core.Ai.Codex.ApiResume.ApiResumeText.CardTitle;
                string nextApiKey = $"{job?.Id}|{job?.State}|{job?.Detail}|{job?.Tokens}|{job?.Spend}|{job?.ForceOffered}|{(job?.StartedAt is { } s ? (int)(now - s).TotalMinutes : -1)}|{other?.Id}|{(other?.StartedAt is { } o ? (int)(now - o).TotalMinutes : -1)}";
                if (nextApiKey != apiKey)
                {
                    apiKey = nextApiKey;
                    apiCard.Content = job is null ? null : CodexApiResumeView.JobCard(job, svc);
                    apiOtherThread = other?.ThreadId;
                    apiBannerText.Text = other is null ? "" : WinStatusBar.Core.Ai.Codex.ApiResume.ApiResumeText.Banner(other, now);
                    apiBanner.Visibility = other is null ? Visibility.Collapsed : Visibility.Visible;
                }
            }
            if (planKey == key) return;
            planKey = key;
            plan.Children.Clear();
            var progress = ActivityProgress.From(steps);
            var line = new Grid();
            line.ColumnDefinitions.Add(new ColumnDefinition());
            line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            line.Children.Add(new TextBlock { Text = inferred ? "Etapas da tarefa · deduzidas pela IA" : "Etapas da tarefa", FontSize = 13, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
            if (progress.Percent is { } percent)
            {
                var number = new TextBlock { Text = $"{Math.Min(selected?.State.IsActive() == true ? 99 : 100, percent):0}%", FontSize = 12, Foreground = PanelKit.Res("AccentTextFillColorPrimaryBrush") };
                Grid.SetColumn(number, 1); line.Children.Add(number);
            }
            plan.Children.Add(line);
            if (progress.Percent is null)
            {
                plan.Children.Add(PanelKit.Caption(selected is { ProgressIsPhaseEstimate: true, ProgressPercent: { } stagePercent } && selected.State.IsActive() && _settings.Current.EstimateProgressWithoutPlan
                    ? $"{stagePercent:0}% pela etapa observada. Estimativa de baixa confiança; a IA ainda não registrou um plano. O valor pode diminuir se surgir retrabalho."
                    : selected?.ExecutionEnded == true ? "A resposta foi encerrada sem um plano de etapas. O término da resposta não confirma a conclusão do pedido."
                    : "A IA ainda não registrou um plano de etapas para esta tarefa.", 12));
                return;
            }
            plan.Children.Add(new ProgressBar { Minimum = 0, Maximum = 100, Value = progress.Percent.Value, Height = 4 });
            plan.Children.Add(PanelKit.Caption($"{progress.Completed} de {progress.Total} etapas concluídas", 11));
            foreach (var step in steps)
            {
                var row = new Grid { ColumnSpacing = 10, Margin = new Thickness(0, 2, 0, 2) };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(22) });
                row.ColumnDefinitions.Add(new ColumnDefinition());
                string status = step.Status == "completed" ? "Concluída" : step.Status == "in_progress" ? "Em andamento" : "Pendente";
                Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(row, status + ": " + step.Text);
                FrameworkElement marker = step.Status == "in_progress"
                    ? new ProgressRing { Width = 16, Height = 16, MinWidth = 0, MinHeight = 0, IsActive = true }
                    : new FontIcon { Glyph = step.Status == "completed" ? "\uE73E" : "\uEA3A", FontSize = 14,
                        Foreground = PanelKit.Res(step.Status == "completed" ? "AccentTextFillColorPrimaryBrush" : "TextFillColorSecondaryBrush") };
                marker.VerticalAlignment = VerticalAlignment.Top;
                marker.Margin = new Thickness(0, 2, 0, 0);
                row.Children.Add(marker);
                // Lista compacta, como no app Claude: o ícone já indica o estado (sem legenda por etapa).
                var text = new TextBlock { Text = step.Text, FontSize = 13, TextWrapping = TextWrapping.Wrap,
                    Foreground = PanelKit.Res(step.Status == "completed" ? "TextFillColorSecondaryBrush" : "TextFillColorPrimaryBrush") };
                Grid.SetColumn(text, 1); row.Children.Add(text);
                plan.Children.Add(row);
            }
        }
        _activityTimer = DispatcherQueue.CreateTimer();
        _activityTimer.Interval = TimeSpan.FromSeconds(1);
        _activityTimer.Tick += (_, _) => Update();
        _activityFlyout.Opening += (_, _) =>
        {
            // Os pincéis em C# seguem o tema do aplicativo, não a cor temporária da janela sob a barra.
            body.RequestedTheme = App.IsDarkTheme ? ElementTheme.Dark : ElementTheme.Light;
            _hub.Briefing.Start(); Update(); _activityTimer.Start();
        };
        _activityFlyout.Closed += (_, _) => _activityTimer.Stop();
        Closed += (_, _) => _activityTimer.Stop();
    }
}
