using System.Numerics;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Hosting;

namespace WinStatusBar.Services;

/// <summary>
/// Animação discreta do logotipo de uma IA enquanto ela trabalha, sem deformar o desenho: nada de esticar ou encolher.
/// "turn": um quarto de volta suave e uma pausa (logos com simetria de 90°, como a estrela do Gemini, voltam ao mesmo
/// desenho); "breathe": a opacidade desce e sobe devagar. Roda no compositor e respeita a opção de animações do Windows.
/// Codex, Claude e IA local usam as animações próprias (OpenAiLogo, ClaudeWorkingDots, OllamaLogo).
/// </summary>
public static class AgentMotion
{
    public static readonly DependencyProperty KindProperty = DependencyProperty.RegisterAttached(
        "Kind", typeof(string), typeof(AgentMotion), new PropertyMetadata("", (d, _) => Hook(d)));
    public static readonly DependencyProperty ActiveProperty = DependencyProperty.RegisterAttached(
        "Active", typeof(bool), typeof(AgentMotion), new PropertyMetadata(false, (d, _) => Hook(d)));
    private static readonly DependencyProperty HookedProperty = DependencyProperty.RegisterAttached(
        "Hooked", typeof(bool), typeof(AgentMotion), new PropertyMetadata(false));

    public static string GetKind(DependencyObject d) => (string)d.GetValue(KindProperty);
    public static void SetKind(DependencyObject d, string value) => d.SetValue(KindProperty, value);
    public static bool GetActive(DependencyObject d) => (bool)d.GetValue(ActiveProperty);
    public static void SetActive(DependencyObject d, bool value) => d.SetValue(ActiveProperty, value);

    /// <summary>Gemini gira um quarto de volta; as demais IAs respiram.</summary>
    public static string KindFor(string key) => key is "gemini" or "ai:gemini" ? "turn" : "breathe";

    private static bool AnimationsEnabled()
    {
        try { return new Windows.UI.ViewManagement.UISettings().AnimationsEnabled; } catch { return true; }
    }

    private static void Hook(DependencyObject d)
    {
        if (d is not FrameworkElement fe) return;
        if (!(bool)fe.GetValue(HookedProperty))
        {
            fe.SetValue(HookedProperty, true);
            fe.Loaded += (_, _) => Apply(fe);
            fe.SizeChanged += (_, _) => Apply(fe);
            fe.Unloaded += (_, _) => Stop(ElementCompositionPreview.GetElementVisual(fe));
        }
        Apply(fe);
    }

    private static void Stop(Visual visual)
    {
        visual.StopAnimation("RotationAngleInDegrees"); visual.StopAnimation("Opacity");
        visual.RotationAngleInDegrees = 0; visual.Opacity = 1;
    }

    private static void Apply(FrameworkElement fe)
    {
        var visual = ElementCompositionPreview.GetElementVisual(fe);
        Stop(visual);
        if (!GetActive(fe) || !fe.IsLoaded || fe.ActualWidth <= 0 || !AnimationsEnabled()) return;
        var c = visual.Compositor;
        var ease = c.CreateCubicBezierEasingFunction(new Vector2(0.65f, 0f), new Vector2(0.35f, 1f));
        if (GetKind(fe) == "turn")
        {
            visual.CenterPoint = new Vector3((float)fe.ActualWidth / 2, (float)fe.ActualHeight / 2, 0);
            var turn = c.CreateScalarKeyFrameAnimation();
            turn.InsertKeyFrame(0f, 0f); turn.InsertKeyFrame(0.45f, 90f, ease); turn.InsertKeyFrame(1f, 90f);
            turn.Duration = TimeSpan.FromSeconds(2.2); turn.IterationBehavior = AnimationIterationBehavior.Forever;
            visual.StartAnimation("RotationAngleInDegrees", turn);
        }
        else
        {
            var breathe = c.CreateScalarKeyFrameAnimation();
            breathe.InsertKeyFrame(0f, 1f); breathe.InsertKeyFrame(0.5f, 0.45f, ease); breathe.InsertKeyFrame(1f, 1f, ease);
            breathe.Duration = TimeSpan.FromSeconds(2.2); breathe.IterationBehavior = AnimationIterationBehavior.Forever;
            visual.StartAnimation("Opacity", breathe);
        }
    }
}
